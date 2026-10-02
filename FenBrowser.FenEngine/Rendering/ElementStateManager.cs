using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Manages dynamic element states for CSS pseudo-class matching.
    /// Tracks :hover, :focus, :active, and other interactive states.
    /// This is a centralized state manager that can be queried during CSS cascade.
    /// </summary>
    public class ElementStateManager
    {
        #region Singleton
        private static ElementStateManager _instance;
        private static readonly object _lock = new object();
        
        public static ElementStateManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ElementStateManager();
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Reset the singleton instance (useful for testing)
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _instance = null;
            }
        }
        #endregion
        
        #region State Storage
        // Current hovered element (only one at a time - deepest in tree)
        private Element _hoveredElement;
        
        // Currently focused element (only one at a time)
        private Element _focusedElement;

        // Tracks whether the current focus was triggered by keyboard navigation
        // This is used for :focus-visible pseudo-class matching per CSS Selectors Level 4
        private bool _focusFromKeyboard;

        // Currently active (mouse down) element
        private Element _activeElement;
        
        // All elements in hover chain (from hovered to root)
        private readonly HashSet<Element> _hoverChain = new HashSet<Element>();
        
        // All elements in focus-within chain (from focused to root)
        private readonly HashSet<Element> _focusWithinChain = new HashSet<Element>();
        
        // Track checked state for checkboxes/radios (synced with DOM attribute)
        private readonly HashSet<Element> _checkedElements = new HashSet<Element>();
        private readonly HashSet<Element> _trackedCheckedElements = new HashSet<Element>();

        // Track visited URLs for :visited/:link matching.
        private readonly HashSet<string> _visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _visitedUrlsLock = new object();

        // Normalized src URLs of every iframe in a document, so :visited/:link do not
        // rescan the tree per link. Built lazily per document and dropped at the start
        // of each cascade pass (see InvalidateFrameUrlCache) — within a pass the DOM is
        // stable, which is the same freshness the per-call rescan used to give.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, HashSet<string>> _frameUrlCache = new();
        
        // Callback for when styles need to be recomputed
        public event Action<Element> OnStateChanged;

        /// <summary>
        /// Notify that an element's state has changed and its computed styles need
        /// re-evaluation.  Fires OnStateChanged, which is wired to
        /// CustomHtmlEngine.ScheduleRecascade in BrowserApi.cs.
        /// Safe to call from any thread.
        /// </summary>
        public void NotifyStateChanged(Element element)
        {
            OnStateChanged?.Invoke(element);
        }

        // Interactive pseudo-class changes can restyle large regions without changing geometry.
        // Damage-raster reuse is unsafe for that class of update, so request one conservative
        // full repaint the next time the renderer consumes interaction-state changes.
        private int _fullRepaintRequested;

        // Text caret state for the focused editable (input/textarea).
        // The compositor reads this every presented frame to draw the blinking caret,
        // so no engine invalidation is required for blink toggles.
        private Element _caretElement;
        private int _caretOffset;
        private DateTime _caretLastChangeUtc = DateTime.UtcNow;
        #endregion

        #region Text Caret State
        public void UpdateTextCaret(Element element, int offset)
        {
            lock (_caretLock)
            {
                if (_caretElement == element && _caretOffset == offset && element != null)
                    return;

                _caretElement = element;
                _caretOffset = offset < 0 ? 0 : offset;
                _caretLastChangeUtc = DateTime.UtcNow;
            }
        }

        public Element CaretElement { get { lock (_caretLock) return _caretElement; } }

        public int CaretOffset { get { lock (_caretLock) return _caretOffset; } }

        public DateTime CaretLastChangeUtc { get { lock (_caretLock) return _caretLastChangeUtc; } }

        public (Element Element, int Offset, DateTime LastChangeUtc) GetTextCaretState()
        {
            lock (_caretLock)
            {
                return (_caretElement, _caretOffset, _caretLastChangeUtc);
            }
        }

        private readonly object _caretLock = new object();
        #endregion
        
        #region Hover State
        /// <summary>
        /// Set the currently hovered element. Builds the hover chain.
        /// </summary>
        public void SetHoveredElement(Element element)
        {
            if (_hoveredElement == element)
                return;
                
            EngineLogCompat.Debug($"[ElementState] Hover changed: {_hoveredElement?.TagName ?? "null"} -> {element?.TagName ?? "null"}", LogCategory.Layout);
            
            // Build list of elements that need style update
            var toUpdate = new List<Element>();
            
            // Clear old hover chain
            foreach (var el in _hoverChain)
            {
                toUpdate.Add(el);
            }
            _hoverChain.Clear();
            
            // Set new hovered element
            var oldHovered = _hoveredElement;
            _hoveredElement = element;
            
            // Build new hover chain (all ancestors also get :hover)
            if (element != null)
            {
                var current = element;
                while (current != null)
                {
                    _hoverChain.Add(current);
                    if (!toUpdate.Contains(current))
                        toUpdate.Add(current);
                    current = current.ParentElement;
                }
            }
            
            // Hover often changes paint-only effects such as backgrounds, text color, and
            // decoration. The damage diff can miss those when the geometry is unchanged, so
            // request one conservative full repaint for the changed interaction frame.
            foreach (var el in toUpdate)
            {
                el?.MarkDirty(InvalidationKind.Style);
            }

            if (toUpdate.Count > 0)
            {
                RequestFullRepaint();
                OnStateChanged?.Invoke(element);
            }
        }
        
        /// <summary>
        /// Check if an element is currently hovered (or an ancestor of hovered element)
        /// </summary>
        public bool IsHovered(Element element)
        {
            if (element == null)
                return false;
            return _hoverChain.Contains(element);
        }
        
        /// <summary>
        /// Get the currently hovered element
        /// </summary>
        public Element HoveredElement => _hoveredElement;
        #endregion
        
        #region Focus State
        /// <summary>
        /// Set the currently focused element. Builds the focus-within chain.
        /// </summary>
        /// <param name="element">The element to focus</param>
        /// <param name="fromKeyboard">True if focus was triggered by keyboard (Tab, Enter, arrow keys), false for mouse/touch</param>
        public void SetFocusedElement(Element element, bool fromKeyboard = false)
        {
            if (_focusedElement == element && _focusFromKeyboard == fromKeyboard)
                return;

            // Update keyboard focus state
            _focusFromKeyboard = fromKeyboard;

            EngineLogCompat.Debug($"[ElementState] Focus changed: {_focusedElement?.TagName ?? "null"} -> {element?.TagName ?? "null"} (keyboard={fromKeyboard})", LogCategory.Layout);
            
            // Build list of elements that need style update
            var toUpdate = new List<Element>();
            
            // Clear old focus-within chain
            foreach (var el in _focusWithinChain)
            {
                toUpdate.Add(el);
            }
            _focusWithinChain.Clear();
            
            // Add old focused element to update list
            if (_focusedElement != null && !toUpdate.Contains(_focusedElement))
                toUpdate.Add(_focusedElement);
            
            // Set new focused element
            _focusedElement = element;
            
            // Build new focus-within chain (all ancestors get :focus-within)
            if (element != null)
            {
                var current = element;
                while (current != null)
                {
                    _focusWithinChain.Add(current);
                    if (!toUpdate.Contains(current))
                        toUpdate.Add(current);
                    current = current.ParentElement;
                }
            }
            
            // Focus state is encoded into paint-tree nodes, so focus-visible/focus-ring updates
            // can be localized by paint-tree diffing instead of falling back to full-frame repaint.
            foreach (var el in toUpdate)
            {
                el?.MarkDirty(InvalidationKind.Style);
            }

            // Sibling combinators reach past the element whose state changed: Material text
            // fields float their label with `input:focus ~ .label`, and accounts.google.com's
            // label stayed over the typed email because nothing after the input was restyled.
            foreach (var el in toUpdate)
            {
                for (var sibling = el?.NextElementSibling; sibling != null; sibling = sibling.NextElementSibling)
                {
                    sibling.MarkDirty(InvalidationKind.Style);
                }
            }

            if (toUpdate.Count > 0)
                OnStateChanged?.Invoke(element);
        }
        
        /// <summary>
        /// Check if an element is currently focused
        /// </summary>
        public bool IsFocused(Element element)
        {
            if (element == null)
                return false;
            return _focusedElement == element;
        }
        
        /// <summary>
        /// Check if an element has a focused descendant (:focus-within)
        /// </summary>
        public bool IsFocusWithin(Element element)
        {
            if (element == null)
                return false;
            return _focusWithinChain.Contains(element);
        }
        
        /// <summary>
        /// Get the currently focused element
        /// </summary>
        public Element FocusedElement => _focusedElement;

        /// <summary>
        /// Check if an element matches :focus-visible pseudo-class.
        /// Per CSS Selectors Level 4, :focus-visible matches when:
        /// 1. The element is focused, AND
        /// 2. The UA determines a visible focus indicator should be shown
        ///    (typically when focus was triggered by keyboard navigation)
        ///
        /// Additionally, certain elements like text inputs always show focus-visible
        /// when focused, regardless of how focus was triggered.
        /// Reference: https://www.w3.org/TR/selectors-4/#focus-visible-pseudo
        /// </summary>
        public bool IsFocusVisible(Element element)
        {
            if (element == null || _focusedElement != element)
                return false;

            // If focus was triggered by keyboard, always show focus-visible
            if (_focusFromKeyboard)
                return true;

            // Certain elements always show :focus-visible when focused,
            // per the UA heuristic in the spec (e.g., text inputs)
            if (element.TagName != null)
            {
                var tag = element.TagName.ToLowerInvariant();

                // Text input elements always show focus ring
                if (tag == "input")
                {
                    string type = null;
                    type = element.GetAttribute("type");
                    type = type?.ToLowerInvariant() ?? "text";

                    // Text-like inputs always show focus-visible
                    if (type == "text" || type == "password" || type == "email" ||
                        type == "url" || type == "tel" || type == "number" ||
                        type == "search" || type == "date" || type == "time" ||
                        type == "datetime-local" || type == "month" || type == "week")
                    {
                        return true;
                    }
                }

                // Textareas always show focus ring
                if (tag == "textarea")
                    return true;

                // Select elements always show focus ring
                if (tag == "select")
                    return true;

                // Elements with contenteditable always show focus ring
                if (element.HasAttribute("contenteditable"))
                {
                    string editable = element.GetAttribute("contenteditable");
                    if (editable != "false")
                        return true;
                }
            }

            // For other elements (buttons, links, etc.), only show focus-visible
            // if focus was from keyboard
            return false;
        }

        /// <summary>
        /// Check if the current focus was triggered by keyboard navigation
        /// </summary>
        public bool IsFocusFromKeyboard => _focusFromKeyboard;
        #endregion

        #region Active State
        /// <summary>
        /// Set the currently active (mouse down) element
        /// </summary>
        public void SetActiveElement(Element element)
        {
            if (_activeElement == element)
                return;
                
            var oldActive = _activeElement;
            _activeElement = element;
            
            EngineLogCompat.Debug($"[ElementState] Active changed: {oldActive?.TagName ?? "null"} -> {element?.TagName ?? "null"}", LogCategory.Layout);
            
            // Notify about both old and new
            RequestFullRepaint();
            if (oldActive != null)
                OnStateChanged?.Invoke(oldActive);
            if (element != null)
                OnStateChanged?.Invoke(element);
        }
        
        /// <summary>
        /// Check if an element is currently active (mouse down on it)
        /// Active also propagates to ancestors like :hover
        /// </summary>
        public bool IsActive(Element element)
        {
            if (element == null || _activeElement == null)
                return false;
                
            // Check if element is the active element or an ancestor
            var current = _activeElement;
            while (current != null)
            {
                if (current == element)
                    return true;
                current = current.ParentElement;
            }
            return false;
        }
        
        /// <summary>
        /// Get the currently active element
        /// </summary>
        public Element ActiveElement => _activeElement;
        #endregion
        
        #region Checked State
        /// <summary>
        /// Set checked state for a checkbox/radio
        /// </summary>
        public void SetChecked(Element element, bool isChecked)
        {
            if (element == null)
                return;

            bool wasChecked = IsChecked(element);
            if (wasChecked == isChecked)
                return;

            _trackedCheckedElements.Add(element);
            if (isChecked)
                _checkedElements.Add(element);
            else
                _checkedElements.Remove(element);

            RequestFullRepaint();
            OnStateChanged?.Invoke(element);
        }
        
        /// <summary>
        /// Check if an element is checked
        /// </summary>
        public bool IsChecked(Element element)
        {
            if (element == null)
                return false;

            if (_trackedCheckedElements.Contains(element))
                return _checkedElements.Contains(element);

            return element.HasAttribute("checked");
        }


        #region Form Control Value State
        // HTML 4.10.5.1: a form control's value is internal state, not the "value"
        // content attribute. The attribute is the *default* value; the two diverge
        // as soon as the user types or a script assigns, which the dirty value flag
        // records. Reading submissions off the attribute therefore sees the page's
        // initial markup rather than what is on screen.
        private readonly Dictionary<Element, string> _elementValues = new Dictionary<Element, string>();
        private readonly HashSet<Element> _dirtyValueElements = new HashSet<Element>();

        /// <summary>
        /// The control's current value, honouring the dirty value flag. Falls back
        /// to the default value (the content attribute, or a textarea's child text)
        /// while the control is still pristine.
        /// </summary>
        public string GetValue(Element element)
        {
            if (element == null) return string.Empty;

            lock (_valueLock)
            {
                if (_dirtyValueElements.Contains(element) &&
                    _elementValues.TryGetValue(element, out var stored))
                {
                    return stored ?? string.Empty;
                }
            }

            return GetDefaultValue(element);
        }

        /// <summary>
        /// Sets the value and raises the dirty value flag, so the control no longer
        /// tracks its content attribute.
        /// </summary>
        public void SetValue(Element element, string value)
        {
            if (element == null) return;

            lock (_valueLock)
            {
                _elementValues[element] = value ?? string.Empty;
                _dirtyValueElements.Add(element);
            }

            RequestFullRepaint();
            OnStateChanged?.Invoke(element);
        }

        /// <summary>
        /// True once the value has diverged from the default. Setting the "value"
        /// content attribute only moves a pristine control.
        /// </summary>
        public bool HasDirtyValue(Element element)
        {
            if (element == null) return false;
            lock (_valueLock)
            {
                return _dirtyValueElements.Contains(element);
            }
        }

        /// <summary>
        /// The default value: a textarea's child text content, otherwise the "value"
        /// content attribute. This is what defaultValue exposes and what a reset
        /// restores.
        /// </summary>
        public string GetDefaultValue(Element element)
        {
            if (element == null) return string.Empty;

            if (string.Equals(element.TagName, "TEXTAREA", StringComparison.OrdinalIgnoreCase))
            {
                return element.TextContent ?? string.Empty;
            }

            return element.GetAttribute("value") ?? string.Empty;
        }

        /// <summary>
        /// Drops the value and checkedness overrides for one control, returning it
        /// to its markup defaults. This is the per-control half of form reset.
        /// </summary>
        public void ResetFormControlState(Element element)
        {
            if (element == null) return;

            lock (_valueLock)
            {
                _elementValues.Remove(element);
                _dirtyValueElements.Remove(element);
            }

            _checkedElements.Remove(element);
            _trackedCheckedElements.Remove(element);
            ClearOptionSelectionState(element);

            RequestFullRepaint();
            OnStateChanged?.Invoke(element);
        }

        private readonly object _valueLock = new object();
        #endregion

        #region Option Selectedness State
        // HTML 4.10.10: an option's selectedness is internal state and the
        // "selected" content attribute is only its default (defaultSelected). The
        // dirtiness flag records that a script or the user has moved it, after
        // which the attribute no longer speaks for the control.
        private readonly HashSet<Element> _selectedOptions = new HashSet<Element>();
        private readonly HashSet<Element> _dirtySelectedOptions = new HashSet<Element>();

        /// <summary>
        /// An option's selectedness, falling back to its "selected" content
        /// attribute while the option is still pristine.
        /// </summary>
        public bool IsOptionSelected(Element option)
        {
            if (option == null) return false;

            lock (_valueLock)
            {
                if (_dirtySelectedOptions.Contains(option))
                {
                    return _selectedOptions.Contains(option);
                }
            }

            return option.HasAttribute("selected");
        }

        /// <summary>
        /// Sets an option's selectedness and raises its dirtiness flag. Callers
        /// that are honouring the single-selection rule drive every option in the
        /// list; this only records one.
        /// </summary>
        public void SetOptionSelected(Element option, bool selected)
        {
            if (option == null) return;

            lock (_valueLock)
            {
                _dirtySelectedOptions.Add(option);
                if (selected)
                {
                    _selectedOptions.Add(option);
                }
                else
                {
                    _selectedOptions.Remove(option);
                }
            }

            RequestFullRepaint();
            OnStateChanged?.Invoke(option);
        }

        /// <summary>True once selectedness has diverged from the attribute.</summary>
        public bool HasDirtySelectedness(Element option)
        {
            if (option == null) return false;
            lock (_valueLock)
            {
                return _dirtySelectedOptions.Contains(option);
            }
        }

        private void ClearOptionSelectionState(Element option)
        {
            if (option == null) return;
            lock (_valueLock)
            {
                _selectedOptions.Remove(option);
                _dirtySelectedOptions.Remove(option);
            }
        }
        #endregion

        /// <summary>
        /// Check if an element is disabled (has disabled attribute)
        /// </summary>
        public bool IsDisabled(Element element)
        {
            if (element == null) return false;
            return element.HasAttribute("disabled");
        }
        #endregion

        #region Link State
        public void RecordVisitedUrl(Uri uri)
        {
            string normalized = NormalizeVisitedUrl(uri);
            if (string.IsNullOrEmpty(normalized))
                return;

            bool added;
            lock (_visitedUrlsLock)
            {
                added = _visitedUrls.Add(normalized);
            }

            if (added)
            {
                RequestFullRepaint();
                OnStateChanged?.Invoke(null);
            }
        }

        /// <summary>
        /// The element Picture-in-Picture is showing, which is what <c>:picture-in-picture</c>
        /// matches. It is the element itself, never a shadow host it is reported through.
        /// </summary>
        public Element PictureInPictureElement { get; set; }

        public bool IsVisited(Element element)
        {
            if (element == null)
                return false;

            string tag = element.TagName?.ToLowerInvariant();
            if (tag != "a" && tag != "area")
                return false;

            string href = element.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                return false;

            Uri resolved = ResolveElementHref(element, href);
            if (resolved == null)
                return false;

            string normalized = NormalizeVisitedUrl(resolved);
            if (string.IsNullOrEmpty(normalized))
                return false;

            lock (_visitedUrlsLock)
            {
                if (_visitedUrls.Contains(normalized))
                {
                    return true;
                }
            }

            // Iframe navigations are currently synthetic in FenBrowser, so a same-document
            // browsing context can reach a URL without the network stack persisting history.
            // Treat actively loaded iframe targets as visited so :visited/:link reflect the
            // current browsing session state.
            var frameUrls = GetFrameUrls(element.OwnerDocument);
            return frameUrls.Count > 0 && frameUrls.Contains(normalized);
        }

        /// <summary>
        /// Normalized src URLs of the document's iframes.
        /// </summary>
        /// <remarks>
        /// Collected in one tree walk per document instead of one per <c>:link</c> /
        /// <c>:visited</c> test. Selector matching asks this for every anchor against
        /// every candidate rule, so rescanning here is O(anchors x rules x nodes):
        /// on a large article that is billions of node visits and the cascade never
        /// finishes.
        /// </remarks>
        private HashSet<string> GetFrameUrls(Document document)
        {
            if (document == null)
                return EmptyFrameUrls;

            return _frameUrlCache.GetValue(document, BuildFrameUrls);
        }

        private static readonly HashSet<string> EmptyFrameUrls = new(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> BuildFrameUrls(Document document)
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var documentRoot = document?.DocumentElement;
            if (documentRoot == null)
                return urls;

            foreach (var frame in documentRoot.Descendants().OfType<Element>())
            {
                if (!string.Equals(frame.TagName, "IFRAME", StringComparison.OrdinalIgnoreCase))
                    continue;

                var frameSrc = frame.GetAttribute("src");
                if (string.IsNullOrWhiteSpace(frameSrc))
                    continue;

                var frameResolved = ResolveElementHref(frame, frameSrc);
                if (frameResolved == null)
                    continue;

                var frameNormalized = NormalizeVisitedUrl(frameResolved);
                if (!string.IsNullOrEmpty(frameNormalized))
                    urls.Add(frameNormalized);
            }

            return urls;
        }

        /// <summary>
        /// Drop the cached per-document iframe URLs. Called at the start of a cascade
        /// pass so a frame added or renavigated since the last pass is picked up.
        /// </summary>
        public void InvalidateFrameUrlCache()
        {
            _frameUrlCache.Clear();
        }

        private static Uri ResolveElementHref(Element element, string href)
        {
            if (element == null || string.IsNullOrWhiteSpace(href))
                return null;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
                return absolute;

            string baseUrl =
                element.OwnerDocument?.BaseURI ??
                element.OwnerDocument?.DocumentURI ??
                element.OwnerDocument?.URL;

            if (string.IsNullOrWhiteSpace(baseUrl) ||
                !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                return null;
            }

            return Uri.TryCreate(baseUri, href, out var resolved) ? resolved : null;
        }

        private static string NormalizeVisitedUrl(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
                return null;

            var builder = new UriBuilder(uri)
            {
                Fragment = string.Empty
            };
            return builder.Uri.AbsoluteUri;
        }
        #endregion
        
        #region Target State (URL Fragment)
        // Current URL fragment (the part after #)
        private string _targetFragment;
        
        /// <summary>
        /// Set the current URL fragment for :target matching
        /// </summary>
        public void SetTargetFragment(string fragment)
        {
            if (_targetFragment == fragment)
                return;
                
            _targetFragment = fragment;
            EngineLogCompat.Debug($"[ElementState] Target fragment set: #{fragment}", LogCategory.Layout);
        }
        
        /// <summary>
        /// Get the current URL fragment
        /// </summary>
        public string TargetFragment => _targetFragment;
        
        /// <summary>
        /// Check if an element is the :target (its ID matches URL fragment)
        /// </summary>
        public bool IsTarget(Element element)
        {
            if (element == null || string.IsNullOrEmpty(_targetFragment))
                return false;
                
            string id = null;
            id = element.GetAttribute("id");
            if (id != null)
            {
                return string.Equals(id, _targetFragment, StringComparison.Ordinal);
            }
            return false;
        }
        #endregion
        
        #region Form Validation Helpers
        /// <summary>
        /// Check if an element is a form element
        /// </summary>
        public static bool IsFormElement(Element element)
        {
            if (element == null) return false;
            var tag = element.TagName?.ToLowerInvariant();
            return tag == "input" || tag == "select" || tag == "textarea" || 
                   tag == "button" || tag == "fieldset" || tag == "output";
        }
        
        /// <summary>
        /// Check if a form element is valid (basic validation)
        /// </summary>
        public static bool IsValid(Element element)
        {
            if (element == null || !IsFormElement(element)) return false;
            
            // Check required attribute
            bool required = element.HasAttribute("required");
            
            if (element.TagName?.Equals("input", StringComparison.OrdinalIgnoreCase) == true)
            {
                string type = null;
                type = element.GetAttribute("type");
                type = type?.ToLowerInvariant() ?? "text";
                
                string value = null;
                value = element.GetAttribute("value");
                
                // Required check
                if (required && string.IsNullOrEmpty(value))
                    return false;
                
                // Email pattern
                if (type == "email" && !string.IsNullOrEmpty(value))
                {
                    if (!value.Contains("@") || !value.Contains("."))
                        return false;
                }
                
                // URL pattern  
                if (type == "url" && !string.IsNullOrEmpty(value))
                {
                    if (!value.StartsWith("http://") && !value.StartsWith("https://"))
                        return false;
                }
                
                // Pattern attribute
                string pattern = null;
                pattern = element.GetAttribute("pattern");
                if (!string.IsNullOrEmpty(pattern))
                {
                    try
                    {
                        var regex = new Regex(
                            $"^(?:{pattern})$",
                            RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100));
                        if (!string.IsNullOrEmpty(value) && !regex.IsMatch(value))
                            return false;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        EngineLogCompat.Warn(
                            "[ElementStateManager] Pattern validation exceeded the regex execution budget.",
                            LogCategory.Rendering);
                        return false;
                    }
                    catch (ArgumentException ex)
                    {
                        // Invalid HTML pattern syntax disables that constraint rather than
                        // turning the control permanently invalid.
                        EngineLogCompat.Debug(
                            $"[ElementStateManager] Ignoring invalid pattern constraint: {ex.Message}",
                            LogCategory.Rendering);
                    }
                }
                
                // Min/max for number types
                if (type == "number" || type == "range")
                {
                    string minStr = null, maxStr = null;
                    minStr = element.GetAttribute("min");
                    maxStr = element.GetAttribute("max");
                    
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                        {
                            return false;
                        }

                        if (!string.IsNullOrEmpty(minStr) &&
                            double.TryParse(minStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double min) &&
                            val < min)
                        {
                            return false;
                        }

                        if (!string.IsNullOrEmpty(maxStr) &&
                            double.TryParse(maxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) &&
                            val > max)
                        {
                            return false;
                        }
                    }
                }

                if (type == "date" && !string.IsNullOrEmpty(value))
                {
                    if (!TryGetRangedInputValue(element, out var dateValue, out var dateMin, out var dateMax))
                    {
                        return false;
                    }

                    if (dateMin.HasValue && dateValue < dateMin.Value)
                    {
                        return false;
                    }

                    if (dateMax.HasValue && dateValue > dateMax.Value)
                    {
                        return false;
                    }
                }

                if (type == "datetime-local" && !string.IsNullOrEmpty(value))
                {
                    if (!TryGetRangedInputValue(element, out var dateTimeValue, out var dateTimeMin, out var dateTimeMax))
                    {
                        return false;
                    }

                    if (dateTimeMin.HasValue && dateTimeValue < dateTimeMin.Value)
                    {
                        return false;
                    }

                    if (dateTimeMax.HasValue && dateTimeValue > dateTimeMax.Value)
                    {
                        return false;
                    }
                }

                if (type == "month" && !string.IsNullOrEmpty(value))
                {
                    if (!TryGetRangedInputValue(element, out var monthValue, out var monthMin, out var monthMax))
                    {
                        return false;
                    }

                    if (monthMin.HasValue && monthValue < monthMin.Value)
                    {
                        return false;
                    }

                    if (monthMax.HasValue && monthValue > monthMax.Value)
                    {
                        return false;
                    }
                }

                if (type == "week" && !string.IsNullOrEmpty(value))
                {
                    if (!TryGetRangedInputValue(element, out var weekValue, out var weekMin, out var weekMax))
                    {
                        return false;
                    }

                    if (weekMin.HasValue && weekValue < weekMin.Value)
                    {
                        return false;
                    }

                    if (weekMax.HasValue && weekValue > weekMax.Value)
                    {
                        return false;
                    }
                }

                if (type == "time" && !string.IsNullOrEmpty(value))
                {
                    if (!TryGetRangedInputValue(element, out var timeValue, out var timeMin, out var timeMax))
                    {
                        return false;
                    }

                    if (timeMin.HasValue && timeValue < timeMin.Value)
                    {
                        return false;
                    }

                    if (timeMax.HasValue && timeValue > timeMax.Value)
                    {
                        return false;
                    }
                }
            }
            else if (element.TagName?.Equals("textarea", StringComparison.OrdinalIgnoreCase) == true ||
                     element.TagName?.Equals("select", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (required)
                {
                    string value = null;
                    value = element.GetAttribute("value");
                    // Check if element has any text content
                    string textContent = element.TextContent;
                    if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(textContent))
                        return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Check if a form element is invalid
        /// </summary>
        public static bool IsInvalid(Element element)
        {
            if (!IsFormElement(element)) return false;
            return !IsValid(element);
        }

        public static bool IsUserValid(Element element)
        {
            if (!IsFormElement(element))
            {
                return false;
            }

            if (!HasUserInteractionMarker(element))
            {
                return false;
            }

            return IsValid(element);
        }

        public static bool IsUserInvalid(Element element)
        {
            if (!IsFormElement(element))
            {
                return false;
            }

            if (!HasUserInteractionMarker(element))
            {
                return false;
            }

            return IsInvalid(element);
        }

        public static bool IsAutofilled(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (element.HasAttribute("autofilled"))
            {
                return true;
            }

            return string.Equals(
                element.GetAttribute("data-autofill"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPlayingMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "playing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitState, "buffering", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitState, "stalled", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(element.GetAttribute("data-media-paused"), "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (HasBooleanStateAttribute(element, "ended"))
            {
                return false;
            }

            return true;
        }

        public static bool IsPausedMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "paused", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(element.GetAttribute("data-media-paused"), "true", StringComparison.OrdinalIgnoreCase) ||
                   element.HasAttribute("paused");
        }

        public static bool IsSeekingMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "seeking", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasBooleanStateAttribute(element, "seeking");
        }

        public static bool IsStalledMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "stalled", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasBooleanStateAttribute(element, "stalled");
        }

        public static bool IsBufferingMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "buffering", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasBooleanStateAttribute(element, "buffering");
        }

        public static bool IsMutedMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            var explicitState = element.GetAttribute("data-media-state");
            if (string.Equals(explicitState, "muted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasBooleanStateAttribute(element, "muted");
        }

        public static bool IsVolumeLockedMedia(Element element)
        {
            if (!IsMediaElement(element))
            {
                return false;
            }

            return HasBooleanStateAttribute(element, "volume-locked");
        }

        public static bool IsTimelineCurrent(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var marker = element.GetAttribute("data-timeline-state");
            if (string.Equals(marker, "current", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var ariaCurrent = element.GetAttribute("aria-current");
            if (string.IsNullOrWhiteSpace(ariaCurrent))
            {
                return false;
            }

            ariaCurrent = ariaCurrent.Trim().ToLowerInvariant();
            return ariaCurrent != "false";
        }

        public static bool IsTimelinePast(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return string.Equals(
                element.GetAttribute("data-timeline-state"),
                "past",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTimelineFuture(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return string.Equals(
                element.GetAttribute("data-timeline-state"),
                "future",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFullscreenElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (element.HasAttribute("fullscreen"))
            {
                return true;
            }

            return string.Equals(
                element.GetAttribute("data-fullscreen"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPictureInPictureElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            // The element Picture-in-Picture is actually showing, which the script engine
            // sets as it enters and leaves. The attribute below is only a way for a test
            // page to pose as one without a real Picture-in-Picture window.
            if (ReferenceEquals(Instance.PictureInPictureElement, element))
            {
                return true;
            }

            if (!IsMediaElement(element))
            {
                return false;
            }

            return string.Equals(
                element.GetAttribute("data-picture-in-picture"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPopoverOpen(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (!element.HasAttribute("popover"))
            {
                return false;
            }

            if (element.HasAttribute("open"))
            {
                return true;
            }

            return string.Equals(
                element.GetAttribute("data-popover-open"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsActiveViewTransition(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(element.GetAttribute("data-active-view-transition"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(
                element.OwnerDocument?.DocumentElement?.GetAttribute("data-active-view-transition"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsActiveViewTransitionType(Element element, string args)
        {
            if (!IsActiveViewTransition(element) || string.IsNullOrWhiteSpace(args))
            {
                return false;
            }

            var requestedType = args.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(requestedType) || requestedType.Contains(','))
            {
                return false;
            }

            var declaredTypes = element.GetAttribute("data-active-view-transition-types");
            if (string.IsNullOrWhiteSpace(declaredTypes))
            {
                declaredTypes = element.OwnerDocument?.DocumentElement?.GetAttribute("data-active-view-transition-types");
            }

            if (string.IsNullOrWhiteSpace(declaredTypes))
            {
                return false;
            }

            return declaredTypes
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Any(type => string.Equals(type, requestedType, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsCustomState(Element element, string args)
        {
            if (element == null || string.IsNullOrWhiteSpace(args))
            {
                return false;
            }

            var localName = element.LocalName ?? string.Empty;
            if (!localName.Contains('-'))
            {
                return false;
            }

            var requestedState = args.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(requestedState) ||
                requestedState.Contains(',') ||
                requestedState.Any(char.IsWhiteSpace))
            {
                return false;
            }

            var declaredStates = element.GetAttribute("data-custom-states");
            if (string.IsNullOrWhiteSpace(declaredStates))
            {
                return false;
            }

            return declaredStates
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Any(state => string.Equals(state, requestedState, StringComparison.Ordinal));
        }
        
        /// <summary>
        /// Check if a form element has the required attribute
        /// </summary>
        public static bool IsRequired(Element element)
        {
            if (!IsFormElement(element))
            {
                return false;
            }

            return element.HasAttribute("required");
        }
        
        /// <summary>
        /// Check if a form element is optional (not required)
        /// </summary>
        public static bool IsOptional(Element element)
        {
            if (!IsFormElement(element)) return false;
            return !IsRequired(element);
        }

        public static bool IsInRange(Element element)
        {
            if (!TryGetRangedInputValue(element, out var value, out var min, out var max))
            {
                return false;
            }

            if (min.HasValue && value < min.Value)
            {
                return false;
            }

            if (max.HasValue && value > max.Value)
            {
                return false;
            }

            return true;
        }

        public static bool IsOutOfRange(Element element)
        {
            if (!TryGetRangedInputValue(element, out var value, out var min, out var max))
            {
                return false;
            }

            if (min.HasValue && value < min.Value)
            {
                return true;
            }

            if (max.HasValue && value > max.Value)
            {
                return true;
            }

            return false;
        }

        public static bool IsReadOnly(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return !IsReadWrite(element);
        }

        public static bool IsReadWrite(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (IsContentEditable(element))
            {
                return true;
            }

            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Instance.IsDisabled(element))
            {
                return false;
            }

            return !element.HasAttribute("readonly");
        }

        public static bool IsPlaceholderShown(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!element.HasAttribute("placeholder"))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(element.GetAttribute("value")))
            {
                return false;
            }

            if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(element.TextContent))
            {
                return false;
            }

            return true;
        }

        public static bool IsDefaultControl(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(element.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                return element.HasAttribute("selected");
            }

            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "checkbox" || type == "radio")
            {
                return element.HasAttribute("checked");
            }

            return false;
        }

        public static bool IsIndeterminate(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(element.TagName, "progress", StringComparison.OrdinalIgnoreCase))
            {
                return !element.HasAttribute("value");
            }

            if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
            if (type != "checkbox")
            {
                return false;
            }

            if (string.Equals(element.GetAttribute("aria-checked"), "mixed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(element.GetAttribute("indeterminate"), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOpenElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant();
            if (tag != "details" && tag != "dialog")
            {
                return false;
            }

            return element.HasAttribute("open");
        }

        public static bool IsClosedElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant();
            if (tag != "details" && tag != "dialog")
            {
                return false;
            }

            return !element.HasAttribute("open");
        }

        public static bool IsModalElement(Element element)
        {
            if (element == null || !string.Equals(element.TagName, "dialog", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!element.HasAttribute("open"))
            {
                return false;
            }

            if (string.Equals(element.GetAttribute("data-top-layer"), "modal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return element.OwnerDocument?.TopLayer?.Contains(element) == true;
        }

        public static bool IsDefinedElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var localName = element.LocalName ?? string.Empty;
            if (!localName.Contains('-'))
            {
                // Built-in HTML/SVG/MathML elements are always defined.
                return true;
            }

            return string.Equals(element.GetAttribute("data-ce-upgraded"), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLocalLinkElement(Element element, string args = null)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant();
            if (tag != "a" && tag != "area" && tag != "link")
            {
                return false;
            }

            var href = element.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                return false;
            }

            var resolved = ResolveElementHref(element, href);
            if (resolved == null)
            {
                return false;
            }

            var documentUrl = element.OwnerDocument?.BaseURI ??
                              element.OwnerDocument?.DocumentURI ??
                              element.OwnerDocument?.URL;
            if (string.IsNullOrWhiteSpace(documentUrl) ||
                !Uri.TryCreate(documentUrl, UriKind.Absolute, out var docUri))
            {
                return false;
            }

            if (!resolved.Scheme.Equals(docUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !resolved.Host.Equals(docUri.Host, StringComparison.OrdinalIgnoreCase) ||
                resolved.Port != docUri.Port)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(args))
            {
                return true;
            }

            if (!int.TryParse(args.Trim(), out var pathDepth) || pathDepth < 0)
            {
                return false;
            }

            if (pathDepth == 0)
            {
                return true;
            }

            var documentSegments = GetPathSegments(docUri.AbsolutePath);
            var targetSegments = GetPathSegments(resolved.AbsolutePath);
            if (documentSegments.Length < pathDepth || targetSegments.Length < pathDepth)
            {
                return false;
            }

            for (var i = 0; i < pathDepth; i++)
            {
                if (!string.Equals(documentSegments[i], targetSegments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsBlankInput(Element element)
        {
            if (element == null)
            {
                return false;
            }

            if (string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(element.GetAttribute("value"));
            }

            if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            {
                var value = element.GetAttribute("value");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                return string.IsNullOrWhiteSpace(element.TextContent);
            }

            return false;
        }

        public static bool IsEffectivelyDisabled(Element element)
        {
            if (element == null || !SupportsEnabledDisabledPseudoClass(element))
            {
                return false;
            }

            if (element.HasAttribute("disabled"))
            {
                return true;
            }

            var tag = element.TagName?.ToLowerInvariant();

            if (tag == "option")
            {
                var parent = element.ParentElement;
                if (parent != null &&
                    (string.Equals(parent.TagName, "optgroup", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parent.TagName, "select", StringComparison.OrdinalIgnoreCase)) &&
                    parent.HasAttribute("disabled"))
                {
                    return true;
                }
            }

            if (tag == "optgroup")
            {
                var parentSelect = element.ParentElement;
                if (parentSelect != null &&
                    string.Equals(parentSelect.TagName, "select", StringComparison.OrdinalIgnoreCase) &&
                    parentSelect.HasAttribute("disabled"))
                {
                    return true;
                }
            }

            for (var ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (!string.Equals(ancestor.TagName, "fieldset", StringComparison.OrdinalIgnoreCase) ||
                    !ancestor.HasAttribute("disabled"))
                {
                    continue;
                }

                if (IsInsideFirstLegend(ancestor, element))
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private static bool HasUserInteractionMarker(Element element)
        {
            var marker = element?.GetAttribute("data-user-interacted");
            return string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMediaElement(Element element)
        {
            if (element == null)
            {
                return false;
            }

            return string.Equals(element.TagName, "audio", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(element.TagName, "video", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasBooleanStateAttribute(Element element, string name)
        {
            if (element == null)
            {
                return false;
            }

            if (element.HasAttribute(name))
            {
                return true;
            }

            return string.Equals(element.GetAttribute("data-media-" + name), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] GetPathSegments(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Array.Empty<string>();
            }

            return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool SupportsEnabledDisabledPseudoClass(Element element)
        {
            var tag = element?.TagName?.ToLowerInvariant();
            return tag == "button" ||
                   tag == "input" ||
                   tag == "select" ||
                   tag == "textarea" ||
                   tag == "fieldset" ||
                   tag == "option" ||
                   tag == "optgroup";
        }

        private static bool IsInsideFirstLegend(Element fieldset, Element target)
        {
            var firstLegend = fieldset.Children?
                .OfType<Element>()
                .FirstOrDefault(child => string.Equals(child.TagName, "legend", StringComparison.OrdinalIgnoreCase));
            if (firstLegend == null)
            {
                return false;
            }

            if (ReferenceEquals(firstLegend, target))
            {
                return true;
            }

            return firstLegend.Descendants().OfType<Element>().Any(desc => ReferenceEquals(desc, target));
        }

        private static bool TryGetRangedInputValue(
            Element element,
            out double value,
            out double? min,
            out double? max)
        {
            value = 0;
            min = null;
            max = null;

            if (element == null || !string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string type = element.GetAttribute("type")?.Trim().ToLowerInvariant();
            if (type != "number" && type != "range" && type != "date" && type != "datetime-local" && type != "month" && type != "week" && type != "time")
            {
                return false;
            }

            if (!TryParseRangedValue(type, element.GetAttribute("value"), out value))
            {
                return false;
            }

            if (TryParseRangedValue(type, element.GetAttribute("min"), out var parsedMin))
            {
                min = parsedMin;
            }

            if (TryParseRangedValue(type, element.GetAttribute("max"), out var parsedMax))
            {
                max = parsedMax;
            }

            return true;
        }

        private static bool TryParseRangedValue(string type, string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            switch (type)
            {
                case "number":
                case "range":
                    return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                case "date":
                    if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        value = date.DayNumber;
                        return true;
                    }

                    return false;
                case "datetime-local":
                    if (DateTime.TryParseExact(
                            raw,
                            new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF" },
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var dateTime))
                    {
                        value = dateTime.ToOADate();
                        return true;
                    }

                    return false;
                case "month":
                    if (DateTime.TryParseExact(
                            raw,
                            "yyyy-MM",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var monthDateTime))
                    {
                        var monthDate = DateOnly.FromDateTime(monthDateTime);
                        value = monthDate.DayNumber;
                        return true;
                    }

                    return false;
                case "week":
                    if (TryParseIsoWeek(raw, out var weekValue))
                    {
                        value = weekValue;
                        return true;
                    }

                    return false;
                case "time":
                    if (TimeOnly.TryParseExact(
                            raw,
                            new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" },
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var time))
                    {
                        value = time.ToTimeSpan().TotalSeconds;
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        private static bool TryParseIsoWeek(string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!Regex.IsMatch(raw, @"^\d{4}-W\d{2}$"))
            {
                return false;
            }

            var parts = raw.Split("-W", StringSplitOptions.None);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var year) ||
                !int.TryParse(parts[1], out var week))
            {
                return false;
            }

            try
            {
                var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
                value = monday.ToOADate();
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool IsContentEditable(Element element)
        {
            for (var current = element; current != null; current = current.ParentElement)
            {
                var attr = current.GetAttribute("contenteditable");
                if (attr == null)
                {
                    continue;
                }

                var normalized = attr.Trim().ToLowerInvariant();
                if (normalized == "false")
                {
                    return false;
                }

                if (normalized == string.Empty || normalized == "true" || normalized == "plaintext-only")
                {
                    return true;
                }
            }

            return false;
        }
        #endregion
        
        #region Utility
        /// <summary>
        /// Clear all state (e.g., on page navigation)
        /// </summary>
        public void ClearAll()
        {
            _hoveredElement = null;
            _focusedElement = null;
            _focusFromKeyboard = false;
            _activeElement = null;
            _hoverChain.Clear();
            _focusWithinChain.Clear();
            _checkedElements.Clear();
            _trackedCheckedElements.Clear();
            lock (_valueLock)
            {
                _elementValues.Clear();
                _dirtyValueElements.Clear();
                _selectedOptions.Clear();
                _dirtySelectedOptions.Clear();
            }
            Interlocked.Exchange(ref _fullRepaintRequested, 0);
        }

        public bool ConsumeFullRepaintRequest()
            => Interlocked.Exchange(ref _fullRepaintRequested, 0) != 0;

        /// <summary>
        /// Ask the renderer to skip the damage diff and repaint everything on the
        /// next frame. Needed whenever pixels change without geometry changing -
        /// interaction states, and a late-arriving image whose bitmap simply was
        /// not available when the area was last rastered.
        /// </summary>
        public void RequestFullRepaint()
        {
            Interlocked.Exchange(ref _fullRepaintRequested, 1);
        }
        
        /// <summary>
        /// Check if an element matches a specific pseudo-class state.
        /// This is the main query method used by CSS selector matching.
        /// </summary>
        public bool MatchesPseudoClassState(Element element, string pseudoClass)
        {
            if (element == null || string.IsNullOrEmpty(pseudoClass))
                return false;
                
            switch (pseudoClass.ToLowerInvariant())
            {
                case "hover":
                    return IsHovered(element);
                case "focus":
                    return IsFocused(element);
                case "focus-within":
                    return IsFocusWithin(element);
                case "focus-visible":
                    return IsFocusVisible(element);
                case "active":
                    return IsActive(element);
                case "visited":
                    return IsVisited(element);
                case "picture-in-picture":
                    return PictureInPictureElement != null && ReferenceEquals(PictureInPictureElement, element);
                // checked, disabled, enabled are attribute-based - handled separately
                default:
                    return false;
            }
        }
        #endregion
    }
}





