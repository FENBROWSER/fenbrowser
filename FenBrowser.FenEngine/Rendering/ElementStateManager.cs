using System;
using System.Collections.Generic;
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
        private LiteElement _hoveredElement;
        
        // Currently focused element (only one at a time)
        private LiteElement _focusedElement;
        
        // Currently active (mouse down) element
        private LiteElement _activeElement;
        
        // All elements in hover chain (from hovered to root)
        private readonly HashSet<LiteElement> _hoverChain = new HashSet<LiteElement>();
        
        // All elements in focus-within chain (from focused to root)
        private readonly HashSet<LiteElement> _focusWithinChain = new HashSet<LiteElement>();
        
        // Track checked state for checkboxes/radios (synced with DOM attribute)
        private readonly HashSet<LiteElement> _checkedElements = new HashSet<LiteElement>();
        
        // Callback for when styles need to be recomputed
        public event Action<LiteElement> OnStateChanged;
        #endregion
        
        #region Hover State
        /// <summary>
        /// Set the currently hovered element. Builds the hover chain.
        /// </summary>
        public void SetHoveredElement(LiteElement element)
        {
            if (_hoveredElement == element)
                return;
                
            FenLogger.Debug($"[ElementState] Hover changed: {_hoveredElement?.Tag ?? "null"} -> {element?.Tag ?? "null"}", LogCategory.Layout);
            
            // Build list of elements that need style update
            var toUpdate = new List<LiteElement>();
            
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
                    current = current.Parent;
                }
            }
            
            // Notify about state changes
            foreach (var el in toUpdate)
            {
                OnStateChanged?.Invoke(el);
            }
        }
        
        /// <summary>
        /// Check if an element is currently hovered (or an ancestor of hovered element)
        /// </summary>
        public bool IsHovered(LiteElement element)
        {
            if (element == null)
                return false;
            return _hoverChain.Contains(element);
        }
        
        /// <summary>
        /// Get the currently hovered element
        /// </summary>
        public LiteElement HoveredElement => _hoveredElement;
        #endregion
        
        #region Focus State
        /// <summary>
        /// Set the currently focused element. Builds the focus-within chain.
        /// </summary>
        public void SetFocusedElement(LiteElement element)
        {
            if (_focusedElement == element)
                return;
                
            FenLogger.Debug($"[ElementState] Focus changed: {_focusedElement?.Tag ?? "null"} -> {element?.Tag ?? "null"}", LogCategory.Layout);
            
            // Build list of elements that need style update
            var toUpdate = new List<LiteElement>();
            
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
                    current = current.Parent;
                }
            }
            
            // Notify about state changes
            foreach (var el in toUpdate)
            {
                OnStateChanged?.Invoke(el);
            }
        }
        
        /// <summary>
        /// Check if an element is currently focused
        /// </summary>
        public bool IsFocused(LiteElement element)
        {
            if (element == null)
                return false;
            return _focusedElement == element;
        }
        
        /// <summary>
        /// Check if an element has a focused descendant (:focus-within)
        /// </summary>
        public bool IsFocusWithin(LiteElement element)
        {
            if (element == null)
                return false;
            return _focusWithinChain.Contains(element);
        }
        
        /// <summary>
        /// Get the currently focused element
        /// </summary>
        public LiteElement FocusedElement => _focusedElement;
        #endregion
        
        #region Active State
        /// <summary>
        /// Set the currently active (mouse down) element
        /// </summary>
        public void SetActiveElement(LiteElement element)
        {
            if (_activeElement == element)
                return;
                
            var oldActive = _activeElement;
            _activeElement = element;
            
            FenLogger.Debug($"[ElementState] Active changed: {oldActive?.Tag ?? "null"} -> {element?.Tag ?? "null"}", LogCategory.Layout);
            
            // Notify about both old and new
            if (oldActive != null)
                OnStateChanged?.Invoke(oldActive);
            if (element != null)
                OnStateChanged?.Invoke(element);
        }
        
        /// <summary>
        /// Check if an element is currently active (mouse down on it)
        /// Active also propagates to ancestors like :hover
        /// </summary>
        public bool IsActive(LiteElement element)
        {
            if (element == null || _activeElement == null)
                return false;
                
            // Check if element is the active element or an ancestor
            var current = _activeElement;
            while (current != null)
            {
                if (current == element)
                    return true;
                current = current.Parent;
            }
            return false;
        }
        
        /// <summary>
        /// Get the currently active element
        /// </summary>
        public LiteElement ActiveElement => _activeElement;
        #endregion
        
        #region Checked State
        /// <summary>
        /// Set checked state for a checkbox/radio
        /// </summary>
        public void SetChecked(LiteElement element, bool isChecked)
        {
            if (element == null)
                return;
                
            bool wasChecked = _checkedElements.Contains(element);
            if (wasChecked == isChecked)
                return;
                
            if (isChecked)
                _checkedElements.Add(element);
            else
                _checkedElements.Remove(element);
                
            OnStateChanged?.Invoke(element);
        }
        
        /// <summary>
        /// Check if an element is checked
        /// </summary>
        public bool IsChecked(LiteElement element)
        {
            if (element == null)
                return false;
            return _checkedElements.Contains(element);
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
            FenLogger.Debug($"[ElementState] Target fragment set: #{fragment}", LogCategory.Layout);
        }
        
        /// <summary>
        /// Get the current URL fragment
        /// </summary>
        public string TargetFragment => _targetFragment;
        
        /// <summary>
        /// Check if an element is the :target (its ID matches URL fragment)
        /// </summary>
        public bool IsTarget(LiteElement element)
        {
            if (element == null || string.IsNullOrEmpty(_targetFragment))
                return false;
                
            string id = null;
            if (element.Attr?.TryGetValue("id", out id) == true)
            {
                return string.Equals(id, _targetFragment, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        #endregion
        
        #region Form Validation Helpers
        /// <summary>
        /// Check if an element is a form element
        /// </summary>
        public static bool IsFormElement(LiteElement element)
        {
            if (element == null) return false;
            var tag = element.Tag?.ToLowerInvariant();
            return tag == "input" || tag == "select" || tag == "textarea" || 
                   tag == "button" || tag == "fieldset" || tag == "output";
        }
        
        /// <summary>
        /// Check if a form element is valid (basic validation)
        /// </summary>
        public static bool IsValid(LiteElement element)
        {
            if (element == null || !IsFormElement(element)) return false;
            
            // Check required attribute
            bool required = element.Attr?.ContainsKey("required") == true;
            
            if (element.Tag?.Equals("input", StringComparison.OrdinalIgnoreCase) == true)
            {
                string type = null;
                element.Attr?.TryGetValue("type", out type);
                type = type?.ToLowerInvariant() ?? "text";
                
                string value = null;
                element.Attr?.TryGetValue("value", out value);
                
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
                if (element.Attr?.TryGetValue("pattern", out pattern) == true && !string.IsNullOrEmpty(pattern))
                {
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex($"^{pattern}$");
                        if (!string.IsNullOrEmpty(value) && !regex.IsMatch(value))
                            return false;
                    }
                    catch { }
                }
                
                // Min/max for number types
                if (type == "number" || type == "range")
                {
                    string minStr = null, maxStr = null;
                    element.Attr?.TryGetValue("min", out minStr);
                    element.Attr?.TryGetValue("max", out maxStr);
                    
                    if (!string.IsNullOrEmpty(value) && double.TryParse(value, out double val))
                    {
                        if (!string.IsNullOrEmpty(minStr) && double.TryParse(minStr, out double min) && val < min)
                            return false;
                        if (!string.IsNullOrEmpty(maxStr) && double.TryParse(maxStr, out double max) && val > max)
                            return false;
                    }
                }
            }
            else if (element.Tag?.Equals("textarea", StringComparison.OrdinalIgnoreCase) == true ||
                     element.Tag?.Equals("select", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (required)
                {
                    string value = null;
                    element.Attr?.TryGetValue("value", out value);
                    // Check if element has any text content
                    string textContent = element.Text;
                    if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(textContent))
                        return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Check if a form element is invalid
        /// </summary>
        public static bool IsInvalid(LiteElement element)
        {
            if (!IsFormElement(element)) return false;
            return !IsValid(element);
        }
        
        /// <summary>
        /// Check if a form element has the required attribute
        /// </summary>
        public static bool IsRequired(LiteElement element)
        {
            return element?.Attr?.ContainsKey("required") == true;
        }
        
        /// <summary>
        /// Check if a form element is optional (not required)
        /// </summary>
        public static bool IsOptional(LiteElement element)
        {
            if (!IsFormElement(element)) return false;
            return !IsRequired(element);
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
            _activeElement = null;
            _hoverChain.Clear();
            _focusWithinChain.Clear();
            _checkedElements.Clear();
        }
        
        /// <summary>
        /// Check if an element matches a specific pseudo-class state.
        /// This is the main query method used by CSS selector matching.
        /// </summary>
        public bool MatchesPseudoClassState(LiteElement element, string pseudoClass)
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
                case "active":
                    return IsActive(element);
                // checked, disabled, enabled are attribute-based - handled separately
                default:
                    return false;
            }
        }
        #endregion
    }
}
