using FenBrowser.Core.Dom.V2;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security.Oopif;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Errors;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Wraps a Element to expose it to JavaScript.
    /// Provides DOM manipulation methods with permission checking.
    /// </summary>
    public partial class ElementWrapper : NodeWrapper
    {
        private readonly Element _element;
        private static readonly ConditionalWeakTable<Element, FenObject> s_iframeWindowByElement = new ConditionalWeakTable<Element, FenObject>();
        private static readonly ConditionalWeakTable<Element, IframeProcessAssignment> s_iframeAssignmentByElement = new ConditionalWeakTable<Element, IframeProcessAssignment>();
        private static readonly ConditionalWeakTable<Element, ElementScrollState> s_scrollStateByElement = new ConditionalWeakTable<Element, ElementScrollState>();
        private static readonly OopifPolicy s_oopifPolicy = new OopifPolicy();
        // _context is in base

        private sealed class IframeProcessAssignment
        {
            public string ParentUrl { get; set; }
            public string TargetUrl { get; set; }
            public bool IsRemote { get; set; }
            public int RendererId { get; set; }
            public string RemoteOrigin { get; set; }
            public string Reason { get; set; }
        }

        private sealed class ElementScrollState
        {
            public double Left { get; set; }
            public double Top { get; set; }
        }

        public ElementWrapper(Element element, IExecutionContext context) : base(element, context)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            // _context set in base
        }

        public Element Element => _element;

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();
            
            switch (key.ToLowerInvariant())
            {
                case "innerhtml":
                    return GetInnerHTML();
                
                case "textcontent":
                    return GetTextContent();
                
                case "tagname":
                    return FenValue.FromString(_element.NodeName?.ToUpperInvariant() ?? "");
                
                case "id":
                    return FenValue.FromString(_element.Id ?? "");

                case "contenteditable":
                    return FenValue.FromString(GetContentEditableState());

                case "iscontenteditable":
                    return FenValue.FromBoolean(IsContentEditable());
                
                case "getattribute":
                    return FenValue.FromFunction(new FenFunction("getAttribute", GetAttribute));
                
                case "setattribute":
                    return FenValue.FromFunction(new FenFunction("setAttribute", SetAttribute));

                case "hasattribute":
                    return FenValue.FromFunction(new FenFunction("hasAttribute", HasAttribute));

                case "removeattribute":
                    return FenValue.FromFunction(new FenFunction("removeAttribute", RemoveAttribute));

                case "attributes":
                    return FenValue.FromObject(new NamedNodeMapWrapper(_element.Attributes, _context));

                case "getattributenode":
                    return FenValue.FromFunction(new FenFunction("getAttributeNode", GetAttributeNode));
                
                case "setattributenode":
                    return FenValue.FromFunction(new FenFunction("setAttributeNode", SetAttributeNode));
                
                case "removeattributenode":
                    return FenValue.FromFunction(new FenFunction("removeAttributeNode", RemoveAttributeNode));

                case "width":
                    return FenValue.FromNumber(GetDimension("width"));
                
                case "height":
                    return FenValue.FromNumber(GetDimension("height"));

                case "src":
                    return FenValue.FromString(_element.GetAttribute("src") ?? string.Empty);

                case "currentsrc":
                    return FenValue.FromString(_element.GetAttribute("src") ?? string.Empty);

                case "clientwidth":
                    // clientWidth - inner width without scrollbar (for viewport calculations)
                    // For documentElement, return viewport width
                    return FenValue.FromNumber(GetClientWidth());
                
                case "clientheight":
                    // clientHeight - inner height without scrollbar (for viewport calculations)
                    // For documentElement, return viewport height
                    return FenValue.FromNumber(GetClientHeight());

                case "offsetwidth":
                    return FenValue.FromNumber(GetOffsetDimension("width"));

                case "offsetheight":
                    return FenValue.FromNumber(GetOffsetDimension("height"));

                case "scrollwidth":
                    return FenValue.FromNumber(GetScrollWidth());

                case "scrollheight":
                    return FenValue.FromNumber(GetScrollHeight());

                case "scrolltop":
                    return FenValue.FromNumber(GetScrollTop());

                case "scrollleft":
                    return FenValue.FromNumber(GetScrollLeft());

                case "scrollto":
                    return FenValue.FromFunction(new FenFunction("scrollTo", ScrollToMethod));

                case "scrollby":
                    return FenValue.FromFunction(new FenFunction("scrollBy", ScrollByMethod));

                case "getcontext":

                    return FenValue.FromFunction(new FenFunction("getContext", GetContext));
                
                case "removechild":
                    return FenValue.FromFunction(new FenFunction("removeChild", RemoveChild));


                case "appendchild":
                    return FenValue.FromFunction(new FenFunction("appendChild", AppendChild));

                case "style":
                    return FenValue.FromObject(new CSSStyleDeclaration(_element, _context));

                case "matches":
                    return FenValue.FromFunction(new FenFunction("matches", MatchesSelector));

                case "closest":
                    return FenValue.FromFunction(new FenFunction("closest", ClosestSelector));

                case "queryselector":
                    return FenValue.FromFunction(new FenFunction("querySelector", QuerySelector));

                case "queryselectorall":
                    return FenValue.FromFunction(new FenFunction("querySelectorAll", QuerySelectorAll));

                case "getelementsbytagname":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagName", GetElementsByTagNameMethod));

                case "getelementsbytagnamens":
                    return FenValue.FromFunction(new FenFunction("getElementsByTagNameNS", GetElementsByTagNameNSMethod));

                case "getelementsbyclassname":
                    return FenValue.FromFunction(new FenFunction("getElementsByClassName", GetElementsByClassNameMethod));

                case "type":
                    return FenValue.FromString(_element.GetAttribute("type") ?? string.Empty);

                case "checked":
                    return FenValue.FromBoolean(_element.HasAttribute("checked"));

                case "disabled":
                    return FenValue.FromBoolean(_element.HasAttribute("disabled"));

                case "click":
                    return FenValue.FromFunction(new FenFunction("click", ClickMethod));

                case "classname":
                    return FenValue.FromString(_element.GetAttribute("class") ?? "");

                case "parentelement":
                case "parentnode":
                    if (_element.ParentNode != null && _element.ParentNode is Element parentEl)
                        return DomWrapperFactory.Wrap(parentEl, _context);
                    return FenValue.Null;

                case "children":
                    return FenValue.FromObject(new HTMLCollectionWrapper(
                        () => _element.ChildNodes?.OfType<Element>() ?? Enumerable.Empty<Element>(),
                        _context));

                case "firstelementchild":
                    var firstChild = _element.ChildNodes?.OfType<Element>().FirstOrDefault();
                    return firstChild != null ? DomWrapperFactory.Wrap(firstChild, _context) : FenValue.Null;

                case "lastelementchild":
                    var lastChild = _element.ChildNodes?.OfType<Element>().LastOrDefault();
                    return lastChild != null ? DomWrapperFactory.Wrap(lastChild, _context) : FenValue.Null;
                
                // DIALOG ELEMENT METHODS
                case "show":
                    return FenValue.FromFunction(new FenFunction("show", ShowDialog));
                
                case "showmodal":
                    return FenValue.FromFunction(new FenFunction("showModal", ShowModalDialog));
                
                case "close":
                    return FenValue.FromFunction(new FenFunction("close", CloseDialog));
                
                case "open":
                    // Check if dialog is open
                    if (_element.TagName?.ToUpperInvariant() == "DIALOG")
                        return FenValue.FromBoolean(_element.HasAttribute("open"));
                    return FenValue.Undefined;
                
                
                // Shadow DOM
                case "attachshadow":
                    return FenValue.FromFunction(new FenFunction("attachShadow", AttachShadow));
                
                case "shadowroot":
                    if (_element.ShadowRoot != null)
                    {
                        if (_element.ShadowRoot.Mode == ShadowRootMode.Closed) return FenValue.Null;
                        return FenValue.FromObject(new ShadowRootWrapper(_element.ShadowRoot, _context));
                        // ShadowRootWrapper needs update to V2 ShadowRoot
                    }
                    return FenValue.Null;
                
                // DOM Level 3 Events
                case "addeventlistener":
                    return FenValue.FromFunction(new FenFunction("addEventListener", AddEventListenerMethod));
                
                case "removeeventlistener":
                    return FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListenerMethod));
                
                case "dispatchevent":
                    return FenValue.FromFunction(new FenFunction("dispatchEvent", DispatchEventMethod));

                case "focus":
                    return FenValue.FromFunction(new FenFunction("focus", FocusMethod));

                case "blur":
                    return FenValue.FromFunction(new FenFunction("blur", BlurMethod));

                case "clonenode":
                    return FenValue.FromFunction(new FenFunction("cloneNode", CloneNodeMethod));

                case "dataset":
                    return FenValue.FromObject(new DOMStringMap(_element, _context));

                case "classlist":
                    return FenValue.FromObject(new DOMTokenList(_element, "class", _context));
                
                case "getboundingclientrect":
                    return FenValue.FromFunction(new FenFunction("getBoundingClientRect", GetBoundingClientRectMethod));
                
                case "getclientrects":
                    return FenValue.FromFunction(new FenFunction("getClientRects", GetClientRectsMethod));

                case "contentwindow":
                    return GetContentWindow();

                case "contentdocument":
                    return GetContentDocument();

                default:
                    return base.Get(key, context);
            }
        }

        private FenValue GetContentWindow()
        {
            if (!string.Equals(_element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return FenValue.Null;
            }

            var sandboxAttribute = _element.GetAttribute("sandbox");
            var sandboxed = FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute);
            var sandboxFlags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
            var sandboxPolicy = FenBrowser.Core.SandboxPolicy.FromIframeSandboxAttribute(sandboxAttribute);
            var allowSameOrigin = (sandboxFlags & FenBrowser.Core.IframeSandboxFlags.SameOrigin) != 0;
            var allowScripts = sandboxPolicy.Allows(FenBrowser.Core.SandboxFeature.Scripts);
            var iframeAssignment = GetIframeProcessAssignment();
            var isRemoteAssignedFrame = iframeAssignment?.IsRemote == true;

            if (!s_iframeWindowByElement.TryGetValue(_element, out var frameWindow))
            {
                frameWindow = new FenObject();

                var abortSignal = new FenObject();
                abortSignal.Set("timeout", FenValue.FromFunction(new FenFunction("timeout", (args, thisVal) =>
                {
                    int delayMs = 0;
                    if (args.Length > 0)
                    {
                        delayMs = (int)Math.Max(0, args[0].ToNumber());
                    }

                    var signal = new FenObject();
                    signal.Set("aborted", FenValue.FromBoolean(false));
                    signal.Set("reason", FenValue.Undefined);
                    signal.Set("onabort", FenValue.Undefined);

                    var listeners = new List<FenValue>();
                    signal.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (sigArgs, sigThis) =>
                    {
                        if (sigArgs.Length >= 2 && string.Equals(sigArgs[0].ToString(), "abort", StringComparison.OrdinalIgnoreCase))
                        {
                            listeners.Add(sigArgs[1]);
                        }
                        return FenValue.Undefined;
                    })));

                    signal.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (sigArgs, sigThis) =>
                    {
                        if (sigArgs.Length >= 2 && string.Equals(sigArgs[0].ToString(), "abort", StringComparison.OrdinalIgnoreCase))
                        {
                            listeners.RemoveAll(l => l.Equals(sigArgs[1]));
                        }
                        return FenValue.Undefined;
                    })));

                    _context?.ScheduleCallback?.Invoke(() =>
                    {
                        // If iframe was detached before timeout, this context is considered gone.
                        if (_element.ParentNode == null)
                        {
                            return;
                        }

                        if (signal.Get("aborted").ToBoolean())
                        {
                            return;
                        }

                        var reason = FenValue.FromString("TimeoutError");
                        signal.Set("aborted", FenValue.FromBoolean(true));
                        signal.Set("reason", reason);

                        var onAbort = signal.Get("onabort");
                        if (onAbort.IsFunction)
                        {
                            onAbort.AsFunction().Invoke(new[] { reason }, _context, FenValue.FromObject(signal));
                        }

                        foreach (var listener in listeners.ToList())
                        {
                            if (listener.IsFunction)
                            {
                                listener.AsFunction().Invoke(new[] { reason }, _context, FenValue.FromObject(signal));
                            }
                        }
                    }, delayMs);

                    return FenValue.FromObject(signal);
                })));

                frameWindow.Set("AbortSignal", FenValue.FromObject(abortSignal));
                frameWindow.Set("__fenSandboxed", FenValue.FromBoolean(sandboxed));
                frameWindow.Set("__fenSandboxScriptsAllowed", FenValue.FromBoolean(allowScripts));
                frameWindow.Set("__fenSandboxSameOrigin", FenValue.FromBoolean(allowSameOrigin));
                frameWindow.Set("__fenSandboxFormsAllowed", FenValue.FromBoolean((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Forms) != 0));
                frameWindow.Set("__fenSandboxPopupsAllowed", FenValue.FromBoolean((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Popups) != 0));
                frameWindow.Set("__fenSandboxTopNavigationAllowed", FenValue.FromBoolean(sandboxPolicy.Allows(FenBrowser.Core.SandboxFeature.Navigation)));
                frameWindow.Set("window", FenValue.FromObject(frameWindow));
                frameWindow.Set("self", FenValue.FromObject(frameWindow));
                frameWindow.Set("top", FenValue.FromObject(frameWindow));
                frameWindow.Set("parent", FenValue.FromObject(frameWindow));
                frameWindow.Set("document", FenValue.Null);
                frameWindow.Set("__fenSandboxLastBlockedAction", FenValue.Undefined);

                FenValue BlockedAction(string action)
                {
                    frameWindow.Set("__fenSandboxLastBlockedAction", FenValue.FromString(action));
                    return FenValue.Null;
                }

                bool IsRemoteFrameAccessBlocked() => GetIframeProcessAssignment()?.IsRemote == true;

                var env = _context?.Environment;
                var parentWindow = env != null ? env.Get("window") : FenValue.Undefined;
                var parentLocation = env != null ? env.Get("location") : FenValue.Undefined;

                frameWindow.Set("open", FenValue.FromFunction(new FenFunction("open", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        return BlockedAction("remote-frame");
                    }

                    if ((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Popups) == 0)
                    {
                        return BlockedAction("popup");
                    }

                    if (parentWindow.IsObject)
                    {
                        var parentOpen = parentWindow.AsObject().Get("open");
                        if (parentOpen.IsFunction)
                        {
                            return parentOpen.AsFunction().Invoke(args, _context, parentWindow);
                        }
                    }

                    return FenValue.Null;
                })));

                var frameLocation = new FenObject();
                frameLocation.Set("href", FenValue.FromString(string.Empty));
                frameLocation.Set("assign", FenValue.FromFunction(new FenFunction("assign", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        return BlockedAction("remote-frame");
                    }

                    if (!sandboxPolicy.Allows(FenBrowser.Core.SandboxFeature.Navigation))
                    {
                        return BlockedAction("top-navigation");
                    }

                    if (parentLocation.IsObject)
                    {
                        var assign = parentLocation.AsObject().Get("assign");
                        if (assign.IsFunction)
                        {
                            return assign.AsFunction().Invoke(args, _context, parentLocation);
                        }
                    }

                    return FenValue.Undefined;
                })));
                frameLocation.Set("replace", FenValue.FromFunction(new FenFunction("replace", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        return BlockedAction("remote-frame");
                    }

                    if (!sandboxPolicy.Allows(FenBrowser.Core.SandboxFeature.Navigation))
                    {
                        return BlockedAction("top-navigation");
                    }

                    if (parentLocation.IsObject)
                    {
                        var replace = parentLocation.AsObject().Get("replace");
                        if (replace.IsFunction)
                        {
                            return replace.AsFunction().Invoke(args, _context, parentLocation);
                        }
                    }

                    return FenValue.Undefined;
                })));
                frameWindow.Set("location", FenValue.FromObject(frameLocation));

                frameWindow.Set("alert", FenValue.FromFunction(new FenFunction("alert", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        return BlockedAction("remote-frame");
                    }

                    if ((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Modals) == 0)
                    {
                        return BlockedAction("modal");
                    }

                    if (parentWindow.IsObject)
                    {
                        var alert = parentWindow.AsObject().Get("alert");
                        if (alert.IsFunction)
                        {
                            return alert.AsFunction().Invoke(args, _context, parentWindow);
                        }
                    }

                    return FenValue.Undefined;
                })));

                frameWindow.Set("confirm", FenValue.FromFunction(new FenFunction("confirm", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        BlockedAction("remote-frame");
                        return FenValue.FromBoolean(false);
                    }

                    if ((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Modals) == 0)
                    {
                        BlockedAction("modal");
                        return FenValue.FromBoolean(false);
                    }

                    if (parentWindow.IsObject)
                    {
                        var confirm = parentWindow.AsObject().Get("confirm");
                        if (confirm.IsFunction)
                        {
                            return confirm.AsFunction().Invoke(args, _context, parentWindow);
                        }
                    }

                    return FenValue.FromBoolean(false);
                })));

                frameWindow.Set("prompt", FenValue.FromFunction(new FenFunction("prompt", (args, thisVal) =>
                {
                    if (IsRemoteFrameAccessBlocked())
                    {
                        return BlockedAction("remote-frame");
                    }

                    if ((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.Modals) == 0)
                    {
                        return BlockedAction("modal");
                    }

                    if (parentWindow.IsObject)
                    {
                        var prompt = parentWindow.AsObject().Get("prompt");
                        if (prompt.IsFunction)
                        {
                            return prompt.AsFunction().Invoke(args, _context, parentWindow);
                        }
                    }

                    return FenValue.Null;
                })));

                s_iframeWindowByElement.Add(_element, frameWindow);
            }

            frameWindow.Set("__fenRemoteFrame", FenValue.FromBoolean(isRemoteAssignedFrame));
            frameWindow.Set("__fenRemoteFrameRendererId", FenValue.FromNumber(iframeAssignment?.RendererId ?? 0));
            frameWindow.Set("__fenRemoteFrameOrigin", string.IsNullOrWhiteSpace(iframeAssignment?.RemoteOrigin) ? FenValue.Null : FenValue.FromString(iframeAssignment.RemoteOrigin));
            frameWindow.Set("__fenRemoteFrameUrl", string.IsNullOrWhiteSpace(iframeAssignment?.TargetUrl) ? FenValue.Null : FenValue.FromString(iframeAssignment.TargetUrl));
            frameWindow.Set("__fenRemoteFrameReason", string.IsNullOrWhiteSpace(iframeAssignment?.Reason) ? FenValue.Null : FenValue.FromString(iframeAssignment.Reason));

            var runtimeEnv = _context?.Environment;
            if (isRemoteAssignedFrame || (sandboxed && !allowSameOrigin))
            {
                frameWindow.Set("document", FenValue.Null);
            }
            else if (runtimeEnv != null)
            {
                var doc = runtimeEnv.Get("document");
                frameWindow.Set("document", doc.IsObject ? doc : FenValue.Null);
            }

            return FenValue.FromObject(frameWindow);
        }

        private FenValue GetContentDocument()
        {
            if (!string.Equals(_element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return FenValue.Null;
            }

            if (GetIframeProcessAssignment()?.IsRemote == true)
            {
                return FenValue.Null;
            }

            var sandboxAttribute = _element.GetAttribute("sandbox");
            if (FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute))
            {
                var sandboxFlags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
                if ((sandboxFlags & FenBrowser.Core.IframeSandboxFlags.SameOrigin) == 0)
                {
                    return FenValue.Null;
                }
            }

            var frameWindow = GetContentWindow();
            if (frameWindow.IsObject)
            {
                var doc = frameWindow.AsObject().Get("document");
                if (doc.IsObject)
                {
                    return doc;
                }
            }

            var env = _context?.Environment;
            if (env == null)
            {
                return FenValue.Null;
            }

            var fallbackDoc = env.Get("document");
            return fallbackDoc.IsObject ? fallbackDoc : FenValue.Null;
        }

        internal static bool IsRemoteFrameElement(Element element, string currentUrl)
        {
            return ComputeIframeProcessAssignment(element, currentUrl)?.IsRemote == true;
        }

        private IframeProcessAssignment GetIframeProcessAssignment()
        {
            var currentUrl = _context?.CurrentUrl;
            var targetUrl = _element.GetAttribute("src");
            if (!s_iframeAssignmentByElement.TryGetValue(_element, out var assignment))
            {
                assignment = ComputeIframeProcessAssignment(_element, currentUrl) ?? new IframeProcessAssignment();
                s_iframeAssignmentByElement.Add(_element, assignment);
                return assignment;
            }

            if (!string.Equals(assignment.ParentUrl, currentUrl, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assignment.TargetUrl, targetUrl, StringComparison.Ordinal))
            {
                var refreshed = ComputeIframeProcessAssignment(_element, currentUrl) ?? new IframeProcessAssignment();
                assignment.ParentUrl = refreshed.ParentUrl;
                assignment.TargetUrl = refreshed.TargetUrl;
                assignment.IsRemote = refreshed.IsRemote;
                assignment.RendererId = refreshed.RendererId;
                assignment.RemoteOrigin = refreshed.RemoteOrigin;
                assignment.Reason = refreshed.Reason;
            }

            return assignment;
        }

        private static IframeProcessAssignment ComputeIframeProcessAssignment(Element element, string currentUrl)
        {
            if (element == null || !string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var srcdoc = element.GetAttribute("srcdoc");
            var src = element.GetAttribute("src");
            var assignment = new IframeProcessAssignment
            {
                ParentUrl = currentUrl,
                TargetUrl = src ?? string.Empty,
                Reason = "same-process"
            };

            if (!string.IsNullOrWhiteSpace(srcdoc) || string.IsNullOrWhiteSpace(src))
            {
                return assignment;
            }

            if (!TryResolveIframeTargetUri(currentUrl, src, out var targetUri))
            {
                assignment.IsRemote = true;
                assignment.Reason = "unresolvable-url";
                return assignment;
            }

            assignment.TargetUrl = targetUri.AbsoluteUri;
            if (!string.Equals(targetUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                assignment.Reason = "non-http-frame";
                return assignment;
            }

            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var parentUri) || !string.Equals(parentUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(parentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                assignment.IsRemote = true;
                assignment.RemoteOrigin = $"{targetUri.Scheme}://{targetUri.Host}{(targetUri.IsDefaultPort ? string.Empty : ":" + targetUri.Port)}";
                assignment.Reason = "missing-parent-origin";
                assignment.RendererId = 2;
                return assignment;
            }

            var frameTree = new FrameTree(s_oopifPolicy);
            var parentFrame = frameTree.CreateMainFrame(parentUri.AbsoluteUri, rendererId: 1);
            var (frame, decision) = frameTree.NavigateChildFrame(parentFrame, targetUri.AbsoluteUri);
            assignment.IsRemote = decision.RequiresNewProcess;
            assignment.RendererId = frame.RendererId;
            assignment.RemoteOrigin = frame.SiteLock?.Origin;
            assignment.Reason = decision.Reason;
            return assignment;
        }

        private static bool TryResolveIframeTargetUri(string currentUrl, string src, out Uri resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(src))
            {
                return false;
            }

            var trimmed = src.Trim();
            if (string.Equals(trimmed, "about:blank", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out resolved))
            {
                return true;
            }

            return Uri.TryCreate(currentUrl, UriKind.Absolute, out var baseUri) &&
                   Uri.TryCreate(baseUri, trimmed, out resolved);
        }


        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (_context != null)
            {
                _context.CheckExecutionTimeLimit();
                if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, $"Set {key}"))
                    throw new FenSecurityError("DOM write permission required");
            }

            switch (key.ToLowerInvariant())
            {
                case "innerhtml":
                    SetInnerHTML(value);
                    break;

                case "textcontent":
                    SetTextContent(value);
                    break;

                case "style":
                {
                    var styleText = value.AsString(context ?? _context) ?? string.Empty;
                    if (styleText.Length == 0)
                    {
                        _element.RemoveAttribute("style");
                    }
                    else
                    {
                        _element.SetAttribute("style", styleText);
                    }

                    _context?.RequestRender?.Invoke();
                    break;
                }

                case "id":
                {
                    var idValue = value.AsString(context ?? _context);
                    _element.SetAttribute("id", idValue ?? string.Empty);
                    break;
                }

                case "contenteditable":
                {
                    var normalized = (value.AsString(context ?? _context) ?? string.Empty).Trim().ToLowerInvariant();
                    switch (normalized)
                    {
                        case "true":
                        case "plaintext-only":
                        case "false":
                            _element.SetAttribute("contenteditable", normalized);
                            break;
                        default:
                            _element.RemoveAttribute("contenteditable");
                            break;
                    }
                    break;
                }

                case "classname":
                {
                    var classValue = value.AsString(context ?? _context);
                    _element.SetAttribute("class", classValue ?? string.Empty);
                    break;
                }

                case "type":
                    _element.SetAttribute("type", value.ToString() ?? string.Empty);
                    break;

                case "src":
                    _element.SetAttribute("src", value.ToString() ?? string.Empty);
                    _context?.RequestRender?.Invoke();
                    break;

                case "width":
                    _element.SetAttribute("width", value.ToString() ?? string.Empty);
                    _context?.RequestRender?.Invoke();
                    break;

                case "height":
                    _element.SetAttribute("height", value.ToString() ?? string.Empty);
                    _context?.RequestRender?.Invoke();
                    break;

                case "checked":
                    if (value.ToBoolean()) _element.SetAttribute("checked", "");
                    else _element.RemoveAttribute("checked");
                    break;

                case "disabled":
                    if (value.ToBoolean()) _element.SetAttribute("disabled", "");
                    else _element.RemoveAttribute("disabled");
                    break;

                case "scrolltop":
                    SetScrollPosition(GetScrollLeft(), value.ToNumber());
                    break;

                case "scrollleft":
                    SetScrollPosition(value.ToNumber(), GetScrollTop());
                    break;

                default:
                    base.Set(key, value, context);
                    break;
            }
        }

        public override bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public override bool Delete(string key, IExecutionContext context = null) => false;

        public override System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null) 
            => new[] { "attachShadow", "shadowRoot", "innerHTML", "textContent", "tagName", "id", "className", "contentEditable", "isContentEditable", "type", "checked", "disabled", "src", "currentSrc", "attributes", "getAttribute", "setAttribute", "hasAttribute", "removeAttribute", "getAttributeNode", "setAttributeNode", "removeAttributeNode", "getElementsByTagName", "getElementsByTagNameNS", "getElementsByClassName", "querySelector", "querySelectorAll", "addEventListener", "removeEventListener", "dispatchEvent", "click", "focus", "blur", "getContext", "width", "height", "clientWidth", "clientHeight", "offsetWidth", "offsetHeight", "scrollWidth", "scrollHeight", "scrollTop", "scrollLeft", "scrollTo", "scrollBy" };

        private string GetContentEditableState()
        {
            var attributeValue = _element.GetAttribute("contenteditable");
            if (attributeValue == null)
            {
                return "inherit";
            }

            var normalized = attributeValue.Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized == "true")
            {
                return "true";
            }

            if (normalized == "plaintext-only")
            {
                return "plaintext-only";
            }

            if (normalized == "false")
            {
                return "false";
            }

            return "inherit";
        }

        private bool IsContentEditable()
        {
            var attributeValue = _element.GetAttribute("contenteditable");
            if (attributeValue == null)
            {
                return false;
            }

            var normalized = attributeValue.Trim().ToLowerInvariant();
            return normalized.Length == 0 || normalized == "true" || normalized == "plaintext-only";
        }
        
        private FenValue GetContext(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var type = args[0].ToString()?.ToLowerInvariant();
            
            // Check if element is canvas
            if (!string.Equals(_element.TagName, "canvas", StringComparison.OrdinalIgnoreCase))
                return FenValue.Null;
            
            // Get canvas dimensions
            int width = 300, height = 150; // Default canvas size per HTML spec
            // V2 Element Helper for getting int attributes
            if (int.TryParse(_element.GetAttribute("width"), out var pw)) width = pw;
            if (int.TryParse(_element.GetAttribute("height"), out var ph)) height = ph;
            
            // 2D Canvas Context
            if (type == "2d")
            {
                return FenValue.FromObject(new FenBrowser.FenEngine.Scripting.CanvasRenderingContext2D(_element, null));
            }
            
            // WebGL Context
            if (type == "webgl" || type == "experimental-webgl")
            {
                var canvasId = _element.GetAttribute("id") ?? _element.GetHashCode().ToString();
                var context = FenBrowser.FenEngine.Rendering.WebGL.WebGLContextManager.GetContext(canvasId, width, height, webgl2: false);
                if (context != null)
                {
                    // Create JavaScript wrapper object with all WebGL methods and constants
                    var wrapper = FenBrowser.FenEngine.Rendering.WebGL.WebGLJavaScriptBindings.CreateJSWrapper(context);
                    return FenValue.FromObject(new WebGLContextWrapper(context, wrapper, _element, _context));
                }
            }

            // WebGL2 Context
            if (type == "webgl2")
            {
                var canvasId = _element.GetAttribute("id") ?? _element.GetHashCode().ToString();
                var context = FenBrowser.FenEngine.Rendering.WebGL.WebGLContextManager.GetContext(canvasId, width, height, webgl2: true);
                if (context != null)
                {
                    var wrapper = FenBrowser.FenEngine.Rendering.WebGL.WebGLJavaScriptBindings.CreateJSWrapper(context);
                    return FenValue.FromObject(new WebGLContextWrapper(context, wrapper, _element, _context));
                }
            }
            
            return FenValue.Null;
        }
        
        /// <summary>
        /// Wrapper to expose WebGL context to JavaScript
        /// </summary>
        private class WebGLContextWrapper : IObject
        {
            private readonly FenBrowser.FenEngine.Rendering.WebGL.WebGLRenderingContext _context;
            private readonly Dictionary<string, object> _methods;
            private readonly FenBrowser.Core.Dom.V2.Element _canvasElement;
            private readonly IExecutionContext _execContext;
            private IObject _prototype;
            public object NativeObject { get; set; }

            public WebGLContextWrapper(FenBrowser.FenEngine.Rendering.WebGL.WebGLRenderingContext context, object methods,
                FenBrowser.Core.Dom.V2.Element canvasElement = null, IExecutionContext execContext = null)
            {
                _context = context;
                _methods = methods as Dictionary<string, object> ?? new Dictionary<string, object>();
                _canvasElement = canvasElement;
                _execContext = execContext;
            }
            
            public FenValue Get(string key, IExecutionContext context = null)
            {
                if (_methods.TryGetValue(key, out var value))
                {
                    // Convert delegates to FenFunction
                    if (value is Delegate del)
                    {
                        return FenValue.FromFunction(new FenFunction(key, (args, thisVal) =>
                        {
                            try
                            {
                                var parameters = del.Method.GetParameters();
                                var convertedArgs = new object[parameters.Length];
                                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                                {
                                    convertedArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                                }
                                var result = del.DynamicInvoke(convertedArgs);
                                return ConvertResult(result);
                            }
                            catch (Exception ex)
                            {
                                // Log error silently - FenLogger may not be available in this context
                                System.Diagnostics.Debug.WriteLine($"[WebGL] Error calling {key}: {ex.Message}");
                                return FenValue.Undefined;
                            }
                        }));
                    }
                    // Constants (uint, int, etc.)
                    if (value is uint ui) return FenValue.FromNumber(ui);
                    if (value is int i) return FenValue.FromNumber(i);
                    if (value is float f) return FenValue.FromNumber(f);
                    if (value is double d) return FenValue.FromNumber(d);
                    if (value is string s) return FenValue.FromString(s);
                    if (value is bool b) return FenValue.FromBoolean(b);
                }
                
                // Also expose properties like drawingBufferWidth, drawingBufferHeight
                if (key == "drawingBufferWidth") return FenValue.FromNumber(_context.DrawingBufferWidth);
                if (key == "drawingBufferHeight") return FenValue.FromNumber(_context.DrawingBufferHeight);
                if (key == "canvas") return _canvasElement != null
                    ? DomWrapperFactory.Wrap(_canvasElement, _execContext)
                    : FenValue.Null;
                
                return FenValue.Undefined;
            }
            
            private object ConvertArg(IValue arg, Type targetType)
            {
                if (targetType == typeof(uint)) return (uint)arg.ToNumber();
                if (targetType == typeof(int)) return (int)arg.ToNumber();
                if (targetType == typeof(float)) return (float)arg.ToNumber();
                if (targetType == typeof(double)) return arg.ToNumber();
                if (targetType == typeof(bool)) return arg.ToBoolean();
                if (targetType == typeof(string)) return arg.ToString();
                if (targetType == typeof(byte[]))
                {
                    // Convert array-like object to byte array
                    if (arg.IsObject)
                    {
                        var obj = arg.AsObject();
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            var bytes = new byte[len];
                            for (int i = 0; i < len; i++)
                            {
                                var v = obj.Get(i.ToString());
                                bytes[i] = v != null ? (byte)v.ToNumber() : (byte)0;
                            }
                            return bytes;
                        }
                    }
                    return FenValue.Null;
                }
                if (targetType == typeof(float[]))
                {
                    if (arg.IsObject)
                    {
                        var obj = arg.AsObject();
                        var lenVal = obj.Get("length");
                        if (lenVal != null && lenVal.IsNumber)
                        {
                            int len = (int)lenVal.ToNumber();
                            var floats = new float[len];
                            for (int i = 0; i < len; i++)
                            {
                                var v = obj.Get(i.ToString());
                                floats[i] = v != null ? (float)v.ToNumber() : 0f;
                            }
                            return floats;
                        }
                    }
                    return FenValue.Null;
                }
                // WebGL objects - check if it's already the correct type
                if (arg.IsObject)
                {
                    var obj = arg.AsObject();
                    if (targetType.IsAssignableFrom(obj.GetType()))
                        return obj;
                }
                return FenValue.Null;
            }
            
            private FenValue ConvertResult(object result)
            {
                if (result  == null) return FenValue.Null;
                if (result is uint ui) return FenValue.FromNumber(ui);
                if (result is int i) return FenValue.FromNumber(i);
                if (result is float f) return FenValue.FromNumber(f);
                if (result is double d) return FenValue.FromNumber(d);
                if (result is bool b) return FenValue.FromBoolean(b);
                if (result is string s) return FenValue.FromString(s);
                if (result is byte[] bytes)
                {
                    var arr = new FenObject();
                    for (int j = 0; j < bytes.Length; j++)
                        arr.Set(j.ToString(), FenValue.FromNumber(bytes[j]));
                    arr.Set("length", FenValue.FromNumber(bytes.Length));
                    return FenValue.FromObject(arr);
                }
                if (result is string[] strings)
                {
                    var arr = new FenObject();
                    for (int j = 0; j < strings.Length; j++)
                        arr.Set(j.ToString(), FenValue.FromString(strings[j]));
                    arr.Set("length", FenValue.FromNumber(strings.Length));
                    return FenValue.FromObject(arr);
                }
                // Return WebGL objects as-is (they implement IObject)
                if (result is IObject obj) return FenValue.FromObject(obj);
                // Unknown types - just convert toString
                return FenValue.FromString(result.ToString());
            }
            
            public bool Has(string key, IExecutionContext context = null) => _methods.ContainsKey(key) || key == "drawingBufferWidth" || key == "drawingBufferHeight" || key == "canvas";
            public void Set(string key, FenValue value, IExecutionContext context = null) { /* WebGL context properties are read-only */ }
            public bool Delete(string key, IExecutionContext context = null) => false;
            public IEnumerable<string> Keys(IExecutionContext context = null) => _methods.Keys;
            public IObject GetPrototype() => _prototype;
            public void SetPrototype(IObject prototype) => _prototype = prototype;
            public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
        }

        private FenValue GetInnerHTML()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "innerHTML"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectInnerHtml(_element));
        }

        private string CollectInnerHtml(Node node)
        {
            if (node  == null) return "";
            if (!(node is Element element))
                return node.NodeType == NodeType.Text ? node.NodeValue ?? "" : "";
            
            if (element.ChildNodes == null || element.ChildNodes.Length == 0)
                return element.TextContent ?? "";

            var sb = new StringBuilder();
            foreach (var child in element.ChildNodes)
            {
                // Simple reconstruction - in real app would need proper serialization
                if (child.NodeType == NodeType.Text)
                {
                    sb.Append(child.TextContent);
                }
                else if (child is Element childEl)
                {
                    sb.Append($"<{childEl.TagName}");
                    // Reconstruct attributes
                    foreach (var attr in childEl.Attributes)
                    {
                         sb.Append($" {attr.Name}=\"{attr.Value}\"");
                    }
                    sb.Append(">");
                    sb.Append(CollectInnerHtml(childEl));
                    sb.Append($"</{childEl.TagName}>");
                }
            }
            return sb.ToString();
        }

        private void SetInnerHTML(FenValue value)
        {
            var removed = _element.ChildNodes != null ? new System.Collections.Generic.List<Node>(_element.ChildNodes) : new System.Collections.Generic.List<Node>();

            // NodeList is read-only, clear via TextContent
            _element.TextContent = "";
            var htmlString = value.ToString();
            
            var added = new System.Collections.Generic.List<Node>();

            if (!string.IsNullOrEmpty(htmlString))
            {
                try
                {
                    var parsed = new FenBrowser.Core.Parsing.HtmlParser(htmlString).Parse();
                    ContainerNode source = null;
                    if (parsed != null)
                    {
                        source = parsed.GetElementsByTagName("body").FirstOrDefault();
                        source ??= parsed.FirstElementChild ?? (ContainerNode)parsed;
                    }

                    if (source?.ChildNodes != null)
                    {
                        foreach (var child in source.ChildNodes.ToArray()) // Copy to avoid modification of source collection during iteration if active
                        {
                            if (child is Node n)
                            {
                                _element.AppendChild(n);
                                added.Add(n);
                            }
                        }
                    }
                }
                catch
                {
                    var textNode = new Text(htmlString);
                    _element.AppendChild(textNode);
                    added.Add(textNode);
                }
            }

            _element.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
            _context.RequestRender?.Invoke();

            _context.OnMutation?.Invoke(new FenBrowser.Core.Dom.V2.MutationRecord
            {
                Type = FenBrowser.Core.Dom.V2.MutationRecordType.ChildList,
                Target = _element,
                AddedNodes = added,
                RemovedNodes = removed
            });
        }

        private FenValue GetTextContent()
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomRead, "textContent"))
                throw new FenSecurityError("DOM read permission required");

            return FenValue.FromString(CollectText(_element));
        }

        private string CollectText(Node node)
        {
            if (node  == null) return "";
            if (node.NodeType == NodeType.Text) return node.TextContent ?? "";
            if (node.ChildNodes  == null) return "";
            
            var sb = new StringBuilder();
            foreach (var child in node.ChildNodes)
            {
                sb.Append(CollectText(child));
            }
            return sb.ToString();
        }

        private void SetTextContent(FenValue value)
        {
            var text = value.ToString();

            // DOM textContent writes must be observable immediately by later JS in the same task.
            _element.TextContent = text;
            
            // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.TextChange,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                "textContent",
                null,
                text
            ));
        }

        private FenValue CreateElement(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attrName = args[0].ToString();
            return _element.GetAttribute(attrName) != null
                ? FenValue.FromString(_element.GetAttribute(attrName))
                : FenValue.Null;
        }

        private FenValue GetAttribute(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attrName = args[0].ToString();
            var val = _element.GetAttribute(attrName);
            return val != null ? FenValue.FromString(val) : FenValue.Null;
        }

        private FenValue SetAttribute(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttribute"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length < 2) return FenValue.Undefined;

            var name = args[0].ToString();
            var value = args[1].ToString();
            SetAttributeFromBinding(name, value);
            return FenValue.Undefined;
        }

        private FenValue HasAttribute(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.FromBoolean(false);
            return FenValue.FromBoolean(_element.HasAttribute(args[0].ToString()));
        }

        private FenValue RemoveAttribute(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeAttribute"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0) return FenValue.Undefined;
            var name = args[0].ToString();
            RemoveAttributeFromBinding(name);
            return FenValue.Undefined;
        }

        private FenValue GetAttributeNode(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var attr = _element.GetAttributeNode(args[0].ToString());
            return attr != null ? FenValue.FromObject(new AttrWrapper(attr, _context)) : FenValue.Null;
        }

        private FenValue SetAttributeNode(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttributeNode"))
                throw new FenSecurityError("DOM write permission required");

             if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
             
             var wrapper = args[0].AsObject() as AttrWrapper;
             if (wrapper  == null) throw new FenSecurityError("Argument must be an Attr node");

             try
             {
                 var old = _element.SetAttributeNode(wrapper.Attr);
                 return old != null ? FenValue.FromObject(new AttrWrapper(old, _context)) : FenValue.Null;
             }
             catch(Exception ex)
             {
                 throw new FenSecurityError(ex.Message);
             }
        }

        private FenValue RemoveAttributeNode(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeAttributeNode"))
                throw new FenSecurityError("DOM write permission required");

             if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;
             
             var wrapper = args[0].AsObject() as AttrWrapper;
             if (wrapper  == null) throw new FenSecurityError("Argument must be an Attr node");

             try
             {
                 var removed = _element.RemoveAttributeNode(wrapper.Attr);
                 return removed != null ? FenValue.FromObject(new AttrWrapper(removed, _context)) : FenValue.Null;
             }
             catch(Exception ex)
             {
                 throw new FenSecurityError(ex.Message);
             }
        }
        private double GetDimension(string attrName)
        {
            var val = _element.GetAttribute(attrName);
            if (val != null)
            {
                if (double.TryParse(val, out var d)) return d;
                if (TryParsePixels(val, out d)) return d;
            }

            var styleValue = GetInlineStyleValue(attrName);
            if (TryParsePixels(styleValue, out var stylePixels))
            {
                return stylePixels;
            }

            return 0;
        }

        private double GetClientWidth()
        {
            // For <html> element (documentElement), return viewport width
            if (string.Equals(_element.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                // Return viewport width (typical desktop width)
                return 1920;
            }
            // For other elements, use width attribute or return 0
            return GetDimension("width");
        }

        private double GetClientHeight()
        {
            // For <html> element (documentElement), return viewport height
            if (string.Equals(_element.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                // Return viewport height (typical desktop height)
                return 1080;
            }
            // For other elements, use height attribute or return 0
            return GetDimension("height");
        }

        private double GetOffsetDimension(string dimensionName)
        {
            var explicitDimension = GetDimension(dimensionName);
            if (explicitDimension > 0)
            {
                return explicitDimension;
            }

            if (TryGetComputedPixels(_element, dimensionName, out var computedDimension) && computedDimension > 0)
            {
                return computedDimension;
            }

            var parent = _element.ParentElement;
            if (parent != null)
            {
                var parentWrapper = new ElementWrapper(parent, _context);
                var parentDisplay = FenBrowser.Core.Css.NodeStyleExtensions.GetComputedStyle(parent)?.Display;
                if (!string.Equals(parentDisplay, "grid", StringComparison.OrdinalIgnoreCase))
                {
                    var inlineDisplay = parentWrapper.GetInlineStyleValue("display");
                    if (string.Equals(inlineDisplay?.Trim(), "grid", StringComparison.OrdinalIgnoreCase))
                    {
                        parentDisplay = "grid";
                    }
                    else
                    {
                        if (!TryGetStylesheetValue(parent, "display", out parentDisplay) ||
                            !string.Equals(parentDisplay, "grid", StringComparison.OrdinalIgnoreCase))
                        {
                            parentDisplay = null;
                        }
                    }
                }

                if (string.Equals(parentDisplay, "grid", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetComputedPixels(parent, dimensionName, out var gridTrackDimension) && gridTrackDimension > 0)
                    {
                        return gridTrackDimension;
                    }

                    if (TryGetStylesheetPixels(parent, dimensionName, out var stylesheetDimension) && stylesheetDimension > 0)
                    {
                        return stylesheetDimension;
                    }

                    var parentExplicitDimension = parentWrapper.GetDimension(dimensionName);
                    if (parentExplicitDimension > 0)
                    {
                        return parentExplicitDimension;
                    }
                }
            }

            return dimensionName == "width"
                ? Math.Max(GetClientWidth(), GetScrollWidth())
                : Math.Max(GetClientHeight(), GetScrollHeight());
        }

        private static bool TryGetComputedPixels(Element element, string propertyName, out double pixels)
        {
            pixels = 0;
            var computedStyle = element == null ? null : FenBrowser.Core.Css.NodeStyleExtensions.GetComputedStyle(element);
            if (computedStyle?.Map?.TryGetValue(propertyName, out var rawValue) != true ||
                string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            rawValue = rawValue.Trim();
            if (!rawValue.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return double.TryParse(
                rawValue.Substring(0, rawValue.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out pixels);
        }

        private static bool TryGetStylesheetPixels(Element element, string propertyName, out double pixels)
        {
            pixels = 0;
            return TryGetStylesheetValue(element, propertyName, out var rawValue) &&
                   rawValue.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
                   double.TryParse(
                       rawValue.Substring(0, rawValue.Length - 2),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out pixels);
        }

        private static bool TryGetStylesheetValue(Element element, string propertyName, out string value)
        {
            value = string.Empty;
            var document = element?.OwnerDocument;
            if (document?.DocumentElement == null)
            {
                return false;
            }

            foreach (var node in document.DocumentElement.DescendantsAndSelf())
            {
                if (node is not Element styleElement ||
                    !string.Equals(styleElement.TagName, "style", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cssText = styleElement.TextContent ?? string.Empty;
                foreach (Match match in Regex.Matches(cssText, @"(?s)([^{}]+)\{([^{}]*)\}"))
                {
                    var selectorText = match.Groups[1].Value.Trim();
                    var body = match.Groups[2].Value;
                    if (selectorText.StartsWith("@", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var selector in selectorText.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            if (!FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(element, selector.Trim()))
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var colonIndex = declaration.IndexOf(':');
                            if (colonIndex <= 0)
                            {
                                continue;
                            }

                            var name = declaration.Substring(0, colonIndex).Trim();
                            if (!string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            value = declaration.Substring(colonIndex + 1).Trim();
                            var importantIndex = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
                            if (importantIndex >= 0)
                            {
                                value = value.Substring(0, importantIndex).Trim();
                            }

                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private double GetScrollWidth()
        {
            return Math.Max(GetClientWidth(), MeasureContentExtent("width"));
        }

        private double GetScrollHeight()
        {
            return Math.Max(GetClientHeight(), MeasureContentExtent("height"));
        }

        private double GetScrollTop()
        {
            return s_scrollStateByElement.TryGetValue(_element, out var state) ? state.Top : 0;
        }

        private double GetScrollLeft()
        {
            return s_scrollStateByElement.TryGetValue(_element, out var state) ? state.Left : 0;
        }

        private void SetScrollPosition(double left, double top)
        {
            var state = s_scrollStateByElement.GetOrCreateValue(_element);
            state.Left = NormalizeScrollCoordinate(left, GetScrollWidth() - GetClientWidth());
            state.Top = NormalizeScrollCoordinate(top, GetScrollHeight() - GetClientHeight());
            _context?.RequestRender?.Invoke();
        }

        private static double NormalizeScrollCoordinate(double requested, double max)
        {
            if (double.IsNaN(requested) || double.IsInfinity(requested))
            {
                return 0;
            }

            if (max > 0)
            {
                return Math.Max(0, Math.Min(requested, max));
            }

            return Math.Max(0, requested);
        }

        private FenValue ScrollToMethod(FenValue[] args, FenValue thisVal)
        {
            double left = GetScrollLeft();
            double top = GetScrollTop();

            if (args.Length == 1 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                var topValue = options?.Get("top");
                if (topValue.HasValue && !topValue.Value.IsUndefined)
                {
                    top = topValue.Value.ToNumber();
                }

                var leftValue = options?.Get("left");
                if (leftValue.HasValue && !leftValue.Value.IsUndefined)
                {
                    left = leftValue.Value.ToNumber();
                }
            }
            else
            {
                if (args.Length >= 1)
                {
                    left = args[0].ToNumber();
                }

                if (args.Length >= 2)
                {
                    top = args[1].ToNumber();
                }
            }

            SetScrollPosition(left, top);
            return FenValue.Undefined;
        }

        private FenValue ScrollByMethod(FenValue[] args, FenValue thisVal)
        {
            double deltaLeft = 0;
            double deltaTop = 0;

            if (args.Length == 1 && args[0].IsObject)
            {
                var options = args[0].AsObject();
                var topValue = options?.Get("top");
                if (topValue.HasValue && !topValue.Value.IsUndefined)
                {
                    deltaTop = topValue.Value.ToNumber();
                }

                var leftValue = options?.Get("left");
                if (leftValue.HasValue && !leftValue.Value.IsUndefined)
                {
                    deltaLeft = leftValue.Value.ToNumber();
                }
            }
            else
            {
                if (args.Length >= 1)
                {
                    deltaLeft = args[0].ToNumber();
                }

                if (args.Length >= 2)
                {
                    deltaTop = args[1].ToNumber();
                }
            }

            SetScrollPosition(GetScrollLeft() + deltaLeft, GetScrollTop() + deltaTop);
            return FenValue.Undefined;
        }

        private double MeasureContentExtent(string dimension)
        {
            if (_element.ChildNodes == null || !_element.ChildNodes.Any())
            {
                return 0;
            }

            double total = 0;
            foreach (var child in _element.ChildNodes)
            {
                if (child is not Element childElement)
                {
                    continue;
                }

                var childWrapper = new ElementWrapper(childElement, _context);
                var childSize = childWrapper.GetDimension(dimension);
                if (string.Equals(dimension, "height", StringComparison.OrdinalIgnoreCase))
                {
                    total += childSize;
                }
                else
                {
                    total = Math.Max(total, childSize);
                }
            }

            return total;
        }

        private string GetInlineStyleValue(string propertyName)
        {
            var style = _element.GetAttribute("style");
            if (string.IsNullOrWhiteSpace(style))
            {
                return null;
            }

            var declarations = style.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var declaration in declarations)
            {
                var parts = declaration.Split(':', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (string.Equals(parts[0].Trim(), propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim();
                }
            }

            return null;
        }

        private static bool TryParsePixels(string value, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^2].Trim();
            }

            return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "appendChild"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;

            var argObj = args[0].AsObject();
            if (argObj is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            var childNode = (argObj as ElementWrapper)?.Element ?? (argObj as NodeWrapper)?.Node;

            if (childNode != null)
            {
                // Apply immediately for DOM-observable semantics, then notify invalidation pipeline.
                _element.AppendChild(childNode);

                DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                    MutationType.NodeInsert,
                    InvalidationKind.Layout | InvalidationKind.Paint,
                    _element,
                    null,
                    null,
                    childNode
                ));

                NotifyChildListMutation(childNode, removedNode: null);
                _context.RequestRender?.Invoke();
                return args[0];
            }

            return FenValue.Null;
        }

        private FenValue RemoveChild(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeChild"))
                throw new FenSecurityError("DOM write permission required");

            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null;

            var argObj = args[0].AsObject();
            if (argObj is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            var childNode = (argObj as ElementWrapper)?.Element ?? (argObj as NodeWrapper)?.Node;

            if (childNode != null)
            {
                // Apply immediately for DOM-observable semantics, then notify invalidation pipeline.
                _element.RemoveChild(childNode);

                DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                    MutationType.NodeRemove,
                    InvalidationKind.Layout | InvalidationKind.Paint,
                    _element,
                    null,
                    childNode,
                    null
                ));

                NotifyChildListMutation(addedNode: null, removedNode: childNode);
                _context.RequestRender?.Invoke();
                return args[0];
            }

            return FenValue.Null;
        }

        internal void SetAttributeFromBinding(string name, string value)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "setAttribute"))
                throw new FenSecurityError("DOM write permission required");

            var oldValue = _element.GetAttribute(name);

            _element.SetAttribute(name, value);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                name,
                oldValue,
                value
            ));

            NotifyAttributeMutation(name, oldValue);
            _context.RequestRender?.Invoke();
        }

        internal void RemoveAttributeFromBinding(string name)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeAttribute"))
                throw new FenSecurityError("DOM write permission required");

            var oldValue = _element.GetAttribute(name);
            _element.RemoveAttribute(name);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                name,
                oldValue,
                null
            ));

            NotifyAttributeMutation(name, oldValue);
            _context.RequestRender?.Invoke();
        }

        internal override Node AppendChildFromBinding(Node child)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "appendChild"))
                throw new FenSecurityError("DOM write permission required");

            if (child == null)
            {
                return null;
            }

            _element.AppendChild(child);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.NodeInsert,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                null,
                null,
                child
            ));

            NotifyChildListMutation(child, removedNode: null);
            _context.RequestRender?.Invoke();
            return child;
        }

        internal override Node RemoveChildFromBinding(Node child)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "removeChild"))
                throw new FenSecurityError("DOM write permission required");

            if (child == null)
            {
                return null;
            }

            _element.RemoveChild(child);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.NodeRemove,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                null,
                child,
                null
            ));

            NotifyChildListMutation(addedNode: null, removedNode: child);
            _context.RequestRender?.Invoke();
            return child;
        }

        internal override Node ReplaceChildFromBinding(Node newNode, Node oldNode)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "replaceChild"))
                throw new FenSecurityError("DOM write permission required");

            if (newNode == null || oldNode == null)
            {
                return null;
            }

            _element.ReplaceChild(newNode, oldNode);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.NodeRemove,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                null,
                oldNode,
                null
            ));
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.NodeInsert,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                null,
                null,
                newNode
            ));

            NotifyChildListMutation(newNode, oldNode);
            _context.RequestRender?.Invoke();
            return oldNode;
        }

        internal override Node InsertBeforeFromBinding(Node newNode, Node referenceNode)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "insertBefore"))
                throw new FenSecurityError("DOM write permission required");

            if (newNode == null)
            {
                return null;
            }

            var inserted = _element.InsertBefore(newNode, referenceNode);

            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.NodeInsert,
                InvalidationKind.Layout | InvalidationKind.Paint,
                _element,
                null,
                null,
                inserted
            ));

            NotifyChildListMutation(inserted, removedNode: null);
            _context.RequestRender?.Invoke();
            return inserted;
        }

        private void NotifyAttributeMutation(string attributeName, string oldValue)
        {
            _context.OnMutation?.Invoke(new FenBrowser.Core.Dom.V2.MutationRecord
            {
                Type = FenBrowser.Core.Dom.V2.MutationRecordType.Attributes,
                Target = _element,
                AttributeName = attributeName,
                OldValue = oldValue
            });
        }

        private void NotifyChildListMutation(Node addedNode, Node removedNode)
        {
            var added = addedNode != null
                ? new System.Collections.Generic.List<Node> { addedNode }
                : new System.Collections.Generic.List<Node>();
            var removed = removedNode != null
                ? new System.Collections.Generic.List<Node> { removedNode }
                : new System.Collections.Generic.List<Node>();

            _context.OnMutation?.Invoke(new FenBrowser.Core.Dom.V2.MutationRecord
            {
                Type = FenBrowser.Core.Dom.V2.MutationRecordType.ChildList,
                Target = _element,
                AddedNodes = added,
                RemovedNodes = removed
            });
        }

        /// <summary>
        /// Implements element.matches(selector) - checks if element matches a CSS selector
        /// </summary>
        private FenValue MatchesSelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return FenValue.FromBoolean(false);
            
            try
            {
                var selector = args[0].ToString();
                var result = Rendering.CssLoader.MatchesSelector(_element, selector);
                return FenValue.FromBoolean(result);
            }
            catch
            {
                return FenValue.FromBoolean(false);
            }
        }

        /// <summary>
        /// Implements element.closest(selector) - finds nearest ancestor matching selector
        /// </summary>
        private FenValue ClosestSelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsString) return FenValue.Null;
            
            try
            {
                var selector = args[0].ToString();
                var current = _element;
                
                while (current != null)
                {
                    if (DocumentWrapper.MatchesSelectorForDomQueries(current, selector))
                    {
                        return DomWrapperFactory.Wrap(current, _context);
                    }
                    current = current.ParentNode as Element;
                }
                
                return FenValue.Null;
            }
            catch
            {
                return FenValue.Null;
            }
        }

        private FenValue QuerySelector(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var selector = args[0].ToString();
            
            // Should probably use the DocumentWrapper implementation or a static helper
            // For now simplified recursive search
            var result = FindFirstDescendant(_element, selector);
            return result != null ? DomWrapperFactory.Wrap(result, _context) : FenValue.Null;
        }
        
        private FenValue QuerySelectorAll(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0)
            {
                return FenValue.FromObject(new NodeListWrapper(Array.Empty<Node>(), _context));
            }
            var selector = args[0].ToString();
            var results = new List<Node>();
            FindAllDescendants(_element, selector, results);
            return FenValue.FromObject(new NodeListWrapper(results, _context));
        }

        private Element FindFirstDescendant(Element parent, string selector)
        {
             if (parent.ChildNodes == null) return null;
             
             foreach (var child in parent.ChildNodes.OfType<Element>())
             {
                 if (DocumentWrapper.MatchesSelectorForDomQueries(child, selector)) return child;
                 var f = FindFirstDescendant(child, selector);
                 if (f != null) return f;
             }
             return null;
        }
        
        private void FindAllDescendants(Element parent, string selector, List<Node> results)
        {
             if (parent.ChildNodes == null) return;
             
             foreach (var child in parent.ChildNodes.OfType<Element>())
             {
                 if (DocumentWrapper.MatchesSelectorForDomQueries(child, selector)) results.Add(child);
                 FindAllDescendants(child, selector, results);
             }
        }

        private FenValue GetElementsByTagNameMethod(FenValue[] args, FenValue thisVal)
        {
            var tag = args.Length > 0 ? args[0].ToString() : "*";
            return FenValue.FromObject(new HTMLCollectionWrapper(() => _element.GetElementsByTagName(tag), _context));
        }

        private FenValue GetElementsByTagNameNSMethod(FenValue[] args, FenValue thisVal)
        {
            // Namespace-aware matching is not yet implemented in the core DOM; delegate by local name.
            var localName = args.Length > 1 ? args[1].ToString() : "*";
            return FenValue.FromObject(new HTMLCollectionWrapper(() => _element.GetElementsByTagName(localName), _context));
        }

        private FenValue GetElementsByClassNameMethod(FenValue[] args, FenValue thisVal)
        {
            var classNames = args.Length > 0 ? args[0].ToString() : string.Empty;
            return FenValue.FromObject(new HTMLCollectionWrapper(() => _element.GetElementsByClassName(classNames), _context));
        }
        private FenValue ShowDialog(FenValue[] args, FenValue thisVal)
        {
            if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "show"))
                throw new FenSecurityError("DOM write permission required");
                
            _element.SetAttribute("open", "");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                null,
                ""
            ));
            return FenValue.Undefined;
        }
        
        private FenValue ShowModalDialog(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "showModal"))
                throw new FenSecurityError("DOM write permission required");

            _element.SetAttribute("open", "");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                null,
                ""
            ));
            // Mark as top-layer modal so UA CSS can apply position:fixed + z-index overlay styling
            _element.SetAttribute("data-top-layer", "modal");
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "data-top-layer",
                null,
                "modal"
            ));
            return FenValue.Undefined;
        }

        private FenValue CloseDialog(FenValue[] args, FenValue thisVal)
        {
             if (!_context.Permissions.CheckAndLog(JsPermissions.DomWrite, "close"))
                throw new FenSecurityError("DOM write permission required");

            _element.RemoveAttribute("open");
            _element.RemoveAttribute("data-top-layer");
             // Enqueue mutation (Deferred)
            DomMutationQueue.Instance.EnqueueMutation(new DomMutation(
                MutationType.AttributeChange,
                InvalidationKind.Style | InvalidationKind.Layout,
                _element,
                "open",
                "",
                null
            ));
            return FenValue.Undefined;
        }

        private FenValue AttachShadow(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject) return FenValue.Null; // Needs {mode: 'open'|'closed'}
            
            var options = args[0].AsObject() as FenObject;
            var modeVal = options != null ? options.Get("mode") : FenValue.Undefined;
            var mode = modeVal.ToString() ?? "closed";
            
            var init = new ShadowRootInit
            {
                Mode = mode == "open" ? ShadowRootMode.Open : ShadowRootMode.Closed,
                DelegatesFocus = false,
                SlotAssignment = SlotAssignmentMode.Named
            };
            
            if (options != null)
            {
                var delegatesFocus = options.Get("delegatesFocus");
                if (delegatesFocus != null && delegatesFocus.IsBoolean)
                    init.DelegatesFocus = delegatesFocus.ToBoolean();
                    
                var slotAssignment = options.Get("slotAssignment");
                if (slotAssignment != null && slotAssignment.ToString() == "manual")
                    init.SlotAssignment = SlotAssignmentMode.Manual;
            }

            var shadow = _element.AttachShadow(init);
            return FenValue.FromObject(new ShadowRootWrapper(shadow, _context)); 
        }

        // --- Event Listeners Reuse ---
        private FenValue AddEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;

            var type = args[0].ToString();
            var callback = args[1];

            bool capture = false;
            bool once = false;
            bool passive = false;
            FenValue signal = FenValue.Undefined;
            if (args.Length >= 3)
            {
                if (!args[2].IsObject || args[2].IsNull)
                {
                    capture = args[2].ToBoolean();
                }
                else
                {
                    var opts = args[2].AsObject();
                    if (opts != null)
                    {
                        var captureVal = opts.Get("capture", _context);
                        if (captureVal.IsUndefined)
                        {
                            var captureGetter = opts.Get("__get_capture", _context);
                            if (captureGetter.IsFunction)
                            {
                                captureVal = captureGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        capture = captureVal.ToBoolean();

                        var onceVal = opts.Get("once", _context);
                        if (onceVal.IsUndefined)
                        {
                            var onceGetter = opts.Get("__get_once", _context);
                            if (onceGetter.IsFunction)
                            {
                                onceVal = onceGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        once = onceVal.ToBoolean();

                        var passiveVal = opts.Get("passive", _context);
                        if (passiveVal.IsUndefined)
                        {
                            var passiveGetter = opts.Get("__get_passive", _context);
                            if (passiveGetter.IsFunction)
                            {
                                passiveVal = passiveGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        passive = passiveVal.ToBoolean();

                        var sVal = opts.Get("signal", _context);
                        if (sVal.IsObject) signal = sVal;
                    }
                }
            }

            var callbackIsValid = callback.IsFunction || callback.IsObject;
            if (type == null || callbackIsValid == false)
                return FenValue.Undefined;
            if (callback.IsNull || callback.IsUndefined)
                return FenValue.Undefined;

            // If signal is already aborted, do not add the listener (per spec)
            EventTarget.Registry.Add(_element, type, callback, capture, once, passive, signal);

            return FenValue.Undefined;
        }

        private FenValue RemoveEventListenerMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var type = args[0].ToString();
            var callback = args[1];
            bool capture = false;
            if (args.Length >= 3)
            {
                if (!args[2].IsObject || args[2].IsNull)
                {
                    capture = args[2].ToBoolean();
                }
                else
                {
                    var opts = args[2].AsObject();
                    if (opts != null)
                    {
                        var captureVal = opts.Get("capture", _context);
                        if (captureVal.IsUndefined)
                        {
                            var captureGetter = opts.Get("__get_capture", _context);
                            if (captureGetter.IsFunction)
                            {
                                captureVal = captureGetter.AsFunction().Invoke(Array.Empty<FenValue>(), _context, FenValue.FromObject(opts));
                            }
                        }
                        capture = captureVal.ToBoolean();
                    }
                }
            }

            EventTarget.Registry.Remove(_element, type, callback, capture);
            return FenValue.Undefined;
        }

        private FenValue DispatchEventMethod(FenValue[] args, FenValue thisValue)
        {
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new FenTypeError("TypeError: Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
            }

            var originalEventValue = args[0];
            var eventObj = args[0].AsObject() as DomEvent;
            if (eventObj == null)
            {
                var evtLike = args[0].AsObject() as FenObject;
                if (evtLike == null) return FenValue.FromBoolean(false);

                var typeVal = evtLike.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : string.Empty;
                var bubbles = evtLike.Get("bubbles").ToBoolean();
                var cancelable = evtLike.Get("cancelable").ToBoolean();
                var composed = evtLike.Get("composed").ToBoolean();
                eventObj = new DomEvent(type, bubbles, cancelable, composed, _context);
            }

            var shouldRunMouseActivation =
                string.Equals(eventObj.Type, "click", StringComparison.OrdinalIgnoreCase) &&
                IsMouseClickEvent(originalEventValue);
            var activationTarget = shouldRunMouseActivation
                ? FindClickActivationTarget(_element, eventObj.Bubbles)
                : null;

            var checkableActivationState = activationTarget != null ? ApplyCheckablePreActivation(activationTarget) : null;
            var hadCheckablePreActivation = checkableActivationState?.Changed == true;

            var notPrevented = EventTarget.DispatchEvent(_element, eventObj, _context);

            // Legacy fallback for runtimes that do not wire EventTarget top-level dispatch.
            if (eventObj.Bubbles && EventTarget.ExternalListenerInvoker == null)
            {
                var windowVal = _context?.Environment != null ? _context.Environment.Get("window") : FenValue.Undefined;
                if (windowVal.IsObject)
                {
                    var windowDispatch = windowVal.AsObject().Get("dispatchEvent");
                    if (windowDispatch.IsFunction)
                    {
                        var windowResult = windowDispatch.AsFunction().Invoke(
                            new[] { FenValue.FromObject(eventObj) },
                            _context,
                            windowVal);
                        if (windowResult.IsBoolean)
                        {
                            notPrevented = notPrevented && windowResult.ToBoolean();
                        }
                    }
                }
            }

            if (shouldRunMouseActivation)
            {
                if (!notPrevented && hadCheckablePreActivation)
                {
                    RestoreCheckableState(checkableActivationState);
                }
                else
                {
                    if (hadCheckablePreActivation && activationTarget != null && activationTarget.IsConnected)
                    {
                        DispatchInputAndChangeEvents(activationTarget);
                    }

                    if (notPrevented && activationTarget != null)
                    {
                        RunLabelActivation(activationTarget);
                        RunSubmitActivation(activationTarget);
                    }
                }
            }

            return FenValue.FromBoolean(notPrevented);
        }
        private FenValue FocusMethod(FenValue[] args, FenValue thisVal)
        {
            if (!IsPotentiallyFocusable(_element))
            {
                return FenValue.Undefined;
            }

            var ownerDocument = _element.OwnerDocument;
            if (ownerDocument != null)
            {
                var previous = ownerDocument.ActiveElement;
                if (previous != null && !ReferenceEquals(previous, _element))
                {
                    var blurEvent = new DomEvent("blur", bubbles: false, cancelable: false, composed: true, context: _context);
                    EventTarget.DispatchEvent(previous, blurEvent, _context);
                }

                ownerDocument.ActiveElement = _element;
            }

            ElementStateManager.Instance.SetFocusedElement(_element);
            var focusEvent = new DomEvent("focus", bubbles: false, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(_element, focusEvent, _context);
            return FenValue.Undefined;
        }

        private FenValue BlurMethod(FenValue[] args, FenValue thisVal)
        {
            var ownerDocument = _element.OwnerDocument;
            if (ownerDocument != null && ReferenceEquals(ownerDocument.ActiveElement, _element))
            {
                ownerDocument.ActiveElement = null;
            }

            if (ElementStateManager.Instance.IsFocused(_element))
            {
                ElementStateManager.Instance.SetFocusedElement(null);
            }

            var blurEvent = new DomEvent("blur", bubbles: false, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(_element, blurEvent, _context);
            return FenValue.Undefined;
        }

        private FenValue ClickMethod(FenValue[] args, FenValue thisVal)
        {
            if (_element == null)
            {
                return FenValue.Undefined;
            }

            if (IsUserInteractionDisabledControl(_element))
            {
                return FenValue.Undefined;
            }

            var checkableActivationState = ApplyCheckablePreActivation(_element);
            var hadCheckablePreActivation = checkableActivationState?.Changed == true;

            var clickEvent = new DomEvent("click", bubbles: true, cancelable: true, composed: true, context: _context);
            var notPrevented = EventTarget.DispatchEvent(_element, clickEvent, _context);

            if (!notPrevented)
            {
                if (hadCheckablePreActivation)
                {
                    RestoreCheckableState(checkableActivationState);
                }
                return FenValue.Undefined;
            }

            if (hadCheckablePreActivation && _element.IsConnected)
            {
                DispatchInputAndChangeEvents(_element);
            }

            RunLabelActivation(_element);
            RunSubmitActivation(_element);
            return FenValue.Undefined;
        }

        private bool IsMouseClickEvent(FenValue eventValue)
        {
            if (!eventValue.IsObject)
            {
                return false;
            }

            var current = eventValue.AsObject();
            while (current != null)
            {
                var ctorVal = current.Get("constructor", _context);
                if (ctorVal.IsFunction && ctorVal.AsFunction() is FenFunction ctorFn)
                {
                    var ctorName = ctorFn.Name ?? string.Empty;
                    if (string.Equals(ctorName, "MouseEvent", StringComparison.Ordinal) ||
                        string.Equals(ctorName, "PointerEvent", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (string.Equals(ctorName, "Event", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                current = current.GetPrototype();
            }

            return false;
        }

        private static bool TryGetCheckableInputType(Element element, out string type)
        {
            type = string.Empty;
            if (!string.Equals(element?.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var inputType = (element.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
            if (inputType != "checkbox" && inputType != "radio")
            {
                return false;
            }

            type = inputType;
            return true;
        }

        private static Element FindClickActivationTarget(Element element, bool bubbles)
        {
            if (element == null)
            {
                return null;
            }

            if (HasClickActivationBehavior(element))
            {
                return element;
            }

            if (!bubbles)
            {
                return null;
            }

            for (var ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
            {
                if (HasClickActivationBehavior(ancestor))
                {
                    return ancestor;
                }
            }

            return null;
        }

        private static bool HasClickActivationBehavior(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            if (tag == "label" || tag == "button")
            {
                return true;
            }

            if (tag == "a")
            {
                return !string.IsNullOrEmpty(element.GetAttribute("href"));
            }

            if (tag != "input")
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
            return type == "checkbox" || type == "radio" || type == "submit" || type == "image" || type == "button";
        }

        private sealed class CheckableActivationState
        {
            public Element Element { get; init; }
            public string InputType { get; init; }
            public bool WasChecked { get; init; }
            public bool Changed { get; set; }
            public List<Element> PreviouslyCheckedRadios { get; } = new();
        }

        private static CheckableActivationState ApplyCheckablePreActivation(Element element)
        {
            if (!TryGetCheckableInputType(element, out var inputType))
            {
                return null;
            }

            var state = new CheckableActivationState
            {
                Element = element,
                InputType = inputType,
                WasChecked = element.HasAttribute("checked")
            };

            if (inputType == "checkbox")
            {
                if (state.WasChecked) element.RemoveAttribute("checked");
                else element.SetAttribute("checked", "");
                state.Changed = element.HasAttribute("checked") != state.WasChecked;
                return state;
            }

            if (state.WasChecked)
            {
                return state;
            }

            foreach (var peer in EnumerateRadioGroup(element))
            {
                if (peer.HasAttribute("checked"))
                {
                    state.PreviouslyCheckedRadios.Add(peer);
                    peer.RemoveAttribute("checked");
                }
            }

            element.SetAttribute("checked", "");
            state.Changed = true;
            return state;
        }

        private static void RestoreCheckableState(CheckableActivationState state)
        {
            if (state == null || state.Element == null)
            {
                return;
            }

            if (state.InputType == "checkbox")
            {
                if (state.WasChecked) state.Element.SetAttribute("checked", "");
                else state.Element.RemoveAttribute("checked");
                return;
            }

            state.Element.RemoveAttribute("checked");
            foreach (var peer in EnumerateRadioGroup(state.Element))
            {
                if (!ReferenceEquals(peer, state.Element))
                {
                    peer.RemoveAttribute("checked");
                }
            }

            if (state.WasChecked)
            {
                state.Element.SetAttribute("checked", "");
            }

            foreach (var peer in state.PreviouslyCheckedRadios)
            {
                peer.SetAttribute("checked", "");
            }
        }

        private static IEnumerable<Element> EnumerateRadioGroup(Element element)
        {
            if (element == null || !string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            var root = element.OwnerDocument?.DocumentElement;
            if (root == null)
            {
                yield return element;
                yield break;
            }

            var groupName = element.GetAttribute("name") ?? string.Empty;
            var ownerForm = FindAncestorForm(element);

            foreach (var candidate in root.Descendants().OfType<Element>())
            {
                if (!string.Equals(candidate.TagName, "input", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateType = (candidate.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
                if (candidateType != "radio")
                {
                    continue;
                }

                if (!string.Equals(candidate.GetAttribute("name") ?? string.Empty, groupName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!ReferenceEquals(FindAncestorForm(candidate), ownerForm))
                {
                    continue;
                }

                yield return candidate;
            }
        }

        private static Element FindAncestorForm(Element element)
        {
            for (var current = element?.ParentElement; current != null; current = current.ParentElement)
            {
                if (string.Equals(current.TagName, "form", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }

            return null;
        }

        private void DispatchInputAndChangeEvents(Element element)
        {
            var inputEvt = new DomEvent("input", bubbles: true, cancelable: false, composed: true, context: _context);
            EventTarget.DispatchEvent(element, inputEvt, _context);

            var changeEvt = new DomEvent("change", bubbles: true, cancelable: false, composed: false, context: _context);
            EventTarget.DispatchEvent(element, changeEvt, _context);
        }

        private static bool IsUserInteractionDisabledControl(Element element)
        {
            if (element == null || !element.HasAttribute("disabled"))
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            return tag == "input" || tag == "button" || tag == "select" || tag == "textarea" || tag == "option" || tag == "optgroup";
        }

        private void RunSubmitActivation(Element control)
        {
            if (control == null || !control.IsConnected || IsUserInteractionDisabledControl(control))
            {
                return;
            }

            var tag = control.TagName?.ToLowerInvariant() ?? string.Empty;
            var type = (control.GetAttribute("type") ?? string.Empty).ToLowerInvariant();

            var isSubmit = false;
            if (tag == "button")
            {
                isSubmit = string.IsNullOrEmpty(type) || type == "submit";
            }
            else if (tag == "input")
            {
                isSubmit = type == "submit" || type == "image";
            }

            if (!isSubmit)
            {
                return;
            }

            var current = control.ParentNode;
            while (current != null)
            {
                if (current is Element formElement && string.Equals(formElement.TagName, "form", StringComparison.OrdinalIgnoreCase))
                {
                    if (!formElement.IsConnected)
                    {
                        return;
                    }

                    if (!IsIframeSandboxFormSubmissionAllowed(formElement))
                    {
                        return;
                    }

                    var submitEvent = new DomEvent("submit", bubbles: true, cancelable: true, composed: false, context: _context);
                    EventTarget.DispatchEvent(formElement, submitEvent, _context);
                    return;
                }

                current = current.ParentNode;
            }
        }

        private void RunLabelActivation(Element element)
        {
            if (element == null ||
                !string.Equals(element.TagName, "label", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var control = FindAssociatedLabelControl(element);
            if (control == null || ReferenceEquals(control, element) || IsUserInteractionDisabledControl(control))
            {
                return;
            }

            var wrapper = new ElementWrapper(control, _context);
            wrapper.ClickMethod(Array.Empty<FenValue>(), FenValue.Undefined);
        }

        private Element FindAssociatedLabelControl(Element label)
        {
            var forId = label.GetAttribute("for");
            if (!string.IsNullOrWhiteSpace(forId))
            {
                var root = _element.OwnerDocument?.DocumentElement;
                var byId = root != null ? FindDescendantById(root, forId) : null;
                if (IsLabelableControl(byId))
                {
                    return byId;
                }
            }

            return FindFirstLabelableDescendant(label);
        }

        private static Element FindDescendantById(Node node, string id)
        {
            if (node is Element element &&
                string.Equals(element.GetAttribute("id"), id, StringComparison.Ordinal))
            {
                return element;
            }

            if (node?.ChildNodes == null)
            {
                return null;
            }

            foreach (var child in node.ChildNodes)
            {
                var match = FindDescendantById(child, id);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Element FindFirstLabelableDescendant(Node node)
        {
            if (node?.ChildNodes == null)
            {
                return null;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is Element childElement && IsLabelableControl(childElement))
                {
                    return childElement;
                }

                var nested = FindFirstLabelableDescendant(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool IsLabelableControl(Element element)
        {
            if (element == null)
            {
                return false;
            }

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            if (tag == "button" || tag == "meter" || tag == "output" || tag == "progress" ||
                tag == "select" || tag == "textarea")
            {
                return true;
            }

            if (tag != "input")
            {
                return false;
            }

            var type = (element.GetAttribute("type") ?? string.Empty).ToLowerInvariant();
            return type != "hidden";
        }

        private static bool IsIframeSandboxFormSubmissionAllowed(Element formElement)
        {
            var cursor = formElement?.ParentNode;
            while (cursor != null)
            {
                if (cursor is Element element &&
                    string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
                {
                    var sandboxAttribute = element.GetAttribute("sandbox");
                    if (FenBrowser.Core.SandboxPolicy.HasIframeSandboxAttribute(sandboxAttribute))
                    {
                        var flags = FenBrowser.Core.SandboxPolicy.ParseIframeSandboxFlags(sandboxAttribute);
                        return (flags & FenBrowser.Core.IframeSandboxFlags.Forms) != 0;
                    }
                }

                cursor = cursor.ParentNode;
            }

            return true;
        }
        private static bool IsPotentiallyFocusable(Element element)
        {
            if (element == null) return false;
            if (element.HasAttribute("disabled")) return false;
            if (element.GetAttribute("tabindex") != null) return true;

            var tag = element.TagName?.ToLowerInvariant() ?? string.Empty;
            switch (tag)
            {
                case "input":
                case "button":
                case "select":
                case "textarea":
                    return true;
                case "a":
                    return element.GetAttribute("href") != null;
                default:
                    return false;
            }
        }

        private FenValue CloneNodeMethod(FenValue[] args, FenValue thisVal)
        {
            bool deep = false;
            if (args.Length > 0 && args[0].IsBoolean) deep = args[0].ToBoolean();
            
            var clone = _element.CloneNode(deep) as Element;
            return clone != null ? DomWrapperFactory.Wrap(clone, _context) : FenValue.Null;
        }
        
        private FenValue GetBoundingClientRectMethod(FenValue[] args, FenValue thisVal)
        {
            if (_context == null)
            {
                return FenValue.FromObject(new DOMRectReadOnly(0, 0, 0, 0));
            }

            var engine = _context.GetLayoutEngine();
            if (engine != null)
            {
                var box = engine.GetBoxForNode(_element);
                if (box.HasValue)
                {
                    var resolved = ResolveBoundingRect(box);
                    return FenValue.FromObject(new DOMRectReadOnly(resolved.Left, resolved.Top, resolved.Width, resolved.Height));
                }
            }

            var syntheticRect = ResolveBoundingRect(null);
            if (syntheticRect.Width > 0 || syntheticRect.Height > 0)
            {
                return FenValue.FromObject(new DOMRectReadOnly(syntheticRect.Left, syntheticRect.Top, syntheticRect.Width, syntheticRect.Height));
            }

            return FenValue.FromObject(new DOMRectReadOnly(0, 0, 0, 0));
        }

        private SKRect ResolveBoundingRect(SKRect? anchorBox)
        {
            if (anchorBox.HasValue && anchorBox.Value.Width > 0 && anchorBox.Value.Height > 0)
            {
                return anchorBox.Value;
            }

            var synthetic = TryCreateSyntheticTextRect(anchorBox);
            if (synthetic.HasValue)
            {
                return synthetic.Value;
            }

            return anchorBox ?? SKRect.Create(0, 0, 0, 0);
        }

        private SKRect? TryCreateSyntheticTextRect(SKRect? anchorBox)
        {
            var textContent = _element.TextContent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(textContent))
            {
                return anchorBox;
            }

            var origin = GetSyntheticInlineOrigin(anchorBox);
            var width = Math.Max(anchorBox?.Width ?? 0, Math.Max(1, textContent.Length) * 10);
            var height = Math.Max(anchorBox?.Height ?? 0, 16);
            return SKRect.Create(origin.Left, origin.Top, width, height);
        }

        private (float Left, float Top) GetSyntheticInlineOrigin(SKRect? anchorBox)
        {
            if (anchorBox.HasValue)
            {
                return (anchorBox.Value.Left, anchorBox.Value.Top);
            }

            float left = 0;
            float top = 0;
            if (_element.ParentNode is Element parent)
            {
                foreach (var sibling in parent.ChildNodes)
                {
                    if (ReferenceEquals(sibling, _element))
                    {
                        break;
                    }

                    if (sibling is Element siblingElement)
                    {
                        if (string.Equals(siblingElement.TagName, "br", StringComparison.OrdinalIgnoreCase))
                        {
                            top += 16;
                            left = 0;
                            continue;
                        }

                        left += EstimateSyntheticWidth(siblingElement);
                    }
                    else if (sibling.NodeType == NodeType.Text)
                    {
                        left += Math.Max(1, sibling.TextContent?.Length ?? 0) * 10;
                    }
                }
            }

            return (left, top);
        }

        private static float EstimateSyntheticWidth(Element element)
        {
            if (string.Equals(element.TagName, "br", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return Math.Max(1, element.TextContent?.Length ?? 0) * 10;
        }

        private FenValue GetClientRectsMethod(FenValue[] args, FenValue thisVal)
        {
            var arr = new FenObject();
            var rect = GetBoundingClientRectMethod(args, thisVal);
            
            // For now, getClientRects just returns an array containing the single bounding rect.
            // Inline elements that wrap across lines might have multiple rects in reality.
            var rectObj = rect.AsObject();
            if (rectObj != null && rectObj.Get("width").ToNumber() > 0)
            {
                arr.Set("0", rect);
                arr.Set("length", FenValue.FromNumber(1));
            }
            else
            {
                arr.Set("length", FenValue.FromNumber(0));
            }
            
            return FenValue.FromObject(arr);
        }
    }

    /// <summary>
    /// Implements DOMRectReadOnly interface for getBoundingClientRect
    /// </summary>
    public class DOMRectReadOnly : IObject
    {
        private readonly double _x;
        private readonly double _y;
        private readonly double _width;
        private readonly double _height;
        private IObject _prototype;

        public object NativeObject { get; set; }

        public DOMRectReadOnly(double x, double y, double width, double height)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key.ToLowerInvariant())
            {
                case "x": return FenValue.FromNumber(_x);
                case "y": return FenValue.FromNumber(_y);
                case "width": return FenValue.FromNumber(_width);
                case "height": return FenValue.FromNumber(_height);
                case "top": return FenValue.FromNumber(_y);
                case "right": return FenValue.FromNumber(_x + _width);
                case "bottom": return FenValue.FromNumber(_y + _height);
                case "left": return FenValue.FromNumber(_x);
                case "tojson": return FenValue.FromFunction(new FenFunction("toJSON", ToJSONMethod));
                default: return FenValue.Undefined;
            }
        }

        private FenValue ToJSONMethod(FenValue[] args, FenValue thisVal)
        {
            var obj = new FenObject();
            obj.Set("x", FenValue.FromNumber(_x));
            obj.Set("y", FenValue.FromNumber(_y));
            obj.Set("width", FenValue.FromNumber(_width));
            obj.Set("height", FenValue.FromNumber(_height));
            obj.Set("top", FenValue.FromNumber(_y));
            obj.Set("right", FenValue.FromNumber(_x + _width));
            obj.Set("bottom", FenValue.FromNumber(_y + _height));
            obj.Set("left", FenValue.FromNumber(_x));
            return FenValue.FromObject(obj);
        }

        public void Set(string key, FenValue value, IExecutionContext context = null) { } // ReadOnly
        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null) => new[] { "x", "y", "width", "height", "top", "right", "bottom", "left", "toJSON" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }

    public class DOMTokenList : IObject
    {
        private readonly Element _element;
        private readonly string _attrName;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        private readonly Dictionary<string, FenValue> _expandos = new(StringComparer.Ordinal);
        public object NativeObject { get; set; }

        public DOMTokenList(Element element, string attrName, IExecutionContext context)
        {
            _element = element;
            _attrName = attrName;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            var val = _element.GetAttribute(_attrName) ?? "";
            var tokens = new List<string>(val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            switch (key)
            {
                case "add": return FenValue.FromFunction(new FenFunction("add", (args, _) => Modify(tokens, args, (t, list) => { if (!list.Contains(t, StringComparer.Ordinal)) list.Add(t); })));
                case "remove": return FenValue.FromFunction(new FenFunction("remove", (args, _) => Modify(tokens, args, (t, list) => list.RemoveAll(existing => string.Equals(existing, t, StringComparison.Ordinal)))));
                case "toggle": return FenValue.FromFunction(new FenFunction("toggle", (args, _) => Toggle(tokens, args)));
                case "contains": return FenValue.FromFunction(new FenFunction("contains", (args, _) => FenValue.FromBoolean(args.Length > 0 && tokens.Contains(args[0].ToString(), StringComparer.Ordinal))));
                case "item": return FenValue.FromFunction(new FenFunction("item", (args, _) =>
                {
                    if (args.Length == 0) return FenValue.Null;
                    var index = ToCollectionIndex(args[0]);
                    return index < tokens.Count ? FenValue.FromString(tokens[index]) : FenValue.Null;
                }));
                case "forEach": return FenValue.FromFunction(new FenFunction("forEach", (args, _) =>
                {
                    if (args.Length > 0 && args[0].IsFunction)
                    {
                        var callback = args[0].AsFunction();
                        for (var i = 0; i < tokens.Count; i++)
                        {
                            callback.Invoke(new[] { FenValue.FromString(tokens[i]), FenValue.FromNumber(i), FenValue.FromObject(this) }, _context);
                        }
                    }
                    return FenValue.Undefined;
                }));
                case "values": return FenValue.FromFunction(new FenFunction("values", (args, _) => FenValue.FromObject(CreateIterator(tokens, IteratorProjection.Values))));
                case "keys": return FenValue.FromFunction(new FenFunction("keys", (args, _) => FenValue.FromObject(CreateIterator(tokens, IteratorProjection.Keys))));
                case "entries": return FenValue.FromFunction(new FenFunction("entries", (args, _) => FenValue.FromObject(CreateIterator(tokens, IteratorProjection.Entries))));
                case "[Symbol.iterator]":
                case "Symbol.iterator":
                case "Symbol(Symbol.iterator)":
                    return FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, _) => FenValue.FromObject(CreateIterator(tokens, IteratorProjection.Values))));
            }

            switch (key.ToLowerInvariant())
            {
                case "length":
                    return FenValue.FromNumber(tokens.Count);
                default: 
                    if (TryParseCollectionIndex(key, out int index) && index < tokens.Count)
                        return FenValue.FromString(tokens[index]);
                    if (_expandos.TryGetValue(key, out var expando))
                        return expando;
                    return FenValue.Undefined;
            }
        }

        private FenValue Modify(List<string> tokens, FenValue[] args, Action<string, List<string>> action)
        {
            foreach (var arg in args) action(arg.ToString(), tokens);
            Update(tokens);
            return FenValue.Undefined;
        }

        private FenValue Toggle(List<string> tokens, FenValue[] args)
        {
             if (args.Length == 0) return FenValue.FromBoolean(false);
             var token = args[0].ToString();
             bool has = tokens.Contains(token, StringComparer.Ordinal);
             if (args.Length >= 2 && args[1].IsBoolean)
             {
                 var force = args[1].ToBoolean();
                 if (force && !has) tokens.Add(token);
                 if (!force && has) tokens.RemoveAll(existing => string.Equals(existing, token, StringComparison.Ordinal));
                 Update(tokens);
                 return FenValue.FromBoolean(force);
             }
             if (has) tokens.RemoveAll(existing => string.Equals(existing, token, StringComparison.Ordinal));
             else tokens.Add(token);
             Update(tokens);
             return FenValue.FromBoolean(!has);
        }

        private void Update(List<string> tokens)
        {
            _element.SetAttribute(_attrName, string.Join(" ", tokens));
            _context?.RequestRender?.Invoke();
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            var tokens = GetTokens();
            if (TryParseCollectionIndex(key, out var index) && index < tokens.Count)
            {
                return;
            }

            _expandos[key] = value;
        }
        public bool Has(string key, IExecutionContext context = null) => !Get(key, context).IsUndefined;
        public bool Delete(string key, IExecutionContext context = null)
        {
            var tokens = GetTokens();
            if (TryParseCollectionIndex(key, out var index))
            {
                return index >= tokens.Count;
            }

            return _expandos.Remove(key) || !_expandos.ContainsKey(key);
        }
        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var tokens = GetTokens();
            return Enumerable.Range(0, tokens.Count).Select(i => i.ToString()).Concat(_expandos.Keys);
        }
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (key == "length" || key == "add" || key == "remove" || key == "toggle" || key == "contains" ||
                key == "item" || key == "forEach" || key == "values" || key == "keys" || key == "entries" ||
                key == "[Symbol.iterator]" || key == "Symbol.iterator" || key == "Symbol(Symbol.iterator)")
            {
                return false;
            }

            var tokens = GetTokens();
            if (TryParseCollectionIndex(key, out var index))
            {
                return index >= tokens.Count;
            }

            if (desc.IsAccessor)
            {
                return false;
            }

            _expandos[key] = desc.Value ?? FenValue.Undefined;
            return true;
        }

        private List<string> GetTokens()
        {
            var val = _element.GetAttribute(_attrName) ?? "";
            return new List<string>(val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private enum IteratorProjection
        {
            Keys,
            Values,
            Entries
        }

        private FenObject CreateIterator(List<string> tokens, IteratorProjection projection)
        {
            var snapshot = tokens.ToList();
            var iterator = new FenObject();
            var index = 0;
            iterator.Set("next", FenValue.FromFunction(new FenFunction("next", (args, thisVal) =>
            {
                var step = new FenObject();
                if (index >= snapshot.Count)
                {
                    step.Set("value", FenValue.Undefined);
                    step.Set("done", FenValue.FromBoolean(true));
                    return FenValue.FromObject(step);
                }

                FenValue value = projection switch
                {
                    IteratorProjection.Keys => FenValue.FromNumber(index),
                    IteratorProjection.Values => FenValue.FromString(snapshot[index]),
                    _ => CreateEntry(index, snapshot[index])
                };

                step.Set("value", value);
                step.Set("done", FenValue.FromBoolean(false));
                index++;
                return FenValue.FromObject(step);
            })));
            iterator.Set("[Symbol.iterator]", FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) => FenValue.FromObject(iterator))));
            iterator.Set("Symbol.iterator", iterator.Get("[Symbol.iterator]"));
            iterator.Set("Symbol(Symbol.iterator)", iterator.Get("[Symbol.iterator]"));
            return iterator;
        }

        private static FenValue CreateEntry(int index, string token)
        {
            var pair = FenObject.CreateArray();
            pair.Set("0", FenValue.FromNumber(index));
            pair.Set("1", FenValue.FromString(token));
            pair.Set("length", FenValue.FromNumber(2));
            return FenValue.FromObject(pair);
        }

        private static bool TryParseCollectionIndex(string key, out int index)
        {
            return int.TryParse(key, out index) && index >= 0;
        }

        private static int ToCollectionIndex(FenValue value)
        {
            var number = value.ToNumber();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            {
                return int.MaxValue;
            }

            return (int)number;
        }
    }

    public class DOMStringMap : IObject
    {
        private readonly Element _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        private readonly Dictionary<string, PropertyDescriptor> _expandos = new(StringComparer.Ordinal);
        private readonly List<string> _expandoOrder = new();
        public object NativeObject { get; set; }

        public DOMStringMap(Element element, IExecutionContext context)
        {
            _element = element;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.IsAccessor)
                {
                    return expandoDesc.Getter != null
                        ? expandoDesc.Getter.Invoke(Array.Empty<FenValue>(), context ?? _context, FenValue.FromObject(this))
                        : FenValue.Undefined;
                }

                return expandoDesc.Value ?? FenValue.Undefined;
            }

            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            var val = _element.GetAttribute(attrName);
            if (val != null)
                return FenValue.FromString(val);
            return FenValue.Undefined;
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.IsAccessor)
                {
                    expandoDesc.Setter?.Invoke(new[] { value }, context ?? _context, FenValue.FromObject(this));
                    return;
                }

                if (expandoDesc.Writable == false)
                {
                    return;
                }

                expandoDesc.Value = value;
                _expandos[key] = expandoDesc;
                return;
            }

            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            _element.SetAttribute(attrName, value.ToString());
            _context?.RequestRender?.Invoke();
        }

        public bool Has(string key, IExecutionContext context = null)
        {
            return _expandos.ContainsKey(key) || EnumerateSupportedPropertyNames().Contains(key, StringComparer.Ordinal);
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                if (expandoDesc.Configurable == false)
                {
                    return false;
                }

                _expandos.Remove(key);
                _expandoOrder.Remove(key);
                return true;
            }

            var attrName = "data-" + PropertyNameToAttributeSuffix(key);
            if (_element.HasAttribute(attrName))
            {
                _element.RemoveAttribute(attrName);
                _context?.RequestRender?.Invoke();
            }
            return true;
        }

        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            foreach (var name in EnumerateSupportedPropertyNames())
            {
                yield return name;
            }

            foreach (var expando in _expandoOrder)
            {
                if (_expandos.TryGetValue(expando, out var desc) && (desc.Enumerable ?? false))
                {
                    yield return expando;
                }
            }
        }

        public IEnumerable<string> GetOwnPropertyNames(IExecutionContext context = null)
        {
            foreach (var name in EnumerateSupportedPropertyNames())
            {
                yield return name;
            }

            foreach (var expando in _expandoOrder)
            {
                if (_expandos.ContainsKey(expando))
                {
                    yield return expando;
                }
            }
        }

        public PropertyDescriptor? GetOwnPropertyDescriptor(string key)
        {
            if (_expandos.TryGetValue(key, out var expandoDesc))
            {
                return expandoDesc;
            }

            if (!Has(key))
            {
                return null;
            }

            return new PropertyDescriptor
            {
                Value = Get(key),
                Writable = true,
                Enumerable = true,
                Configurable = true,
                Getter = null,
                Setter = null
            };
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (EnumerateSupportedPropertyNames().Contains(key, StringComparer.Ordinal))
            {
                if (desc.IsAccessor)
                {
                    return false;
                }

                if (desc.Configurable == false)
                {
                    return false;
                }

                if (desc.Value.HasValue)
                {
                    Set(key, desc.Value.Value, _context);
                }

                return true;
            }

            if (_expandos.TryGetValue(key, out var current))
            {
                if (current.Configurable == false)
                {
                    if (desc.Configurable == true)
                    {
                        return false;
                    }

                    if (desc.Enumerable.HasValue && desc.Enumerable != current.Enumerable)
                    {
                        return false;
                    }

                    if (current.IsData && current.Writable == false)
                    {
                        if (desc.Writable == true)
                        {
                            return false;
                        }

                        if (desc.Value.HasValue && !desc.Value.Value.StrictEquals(current.Value))
                        {
                            return false;
                        }
                    }
                }

                if (desc.Value.HasValue) current.Value = desc.Value.Value;
                if (desc.Writable.HasValue) current.Writable = desc.Writable;
                if (desc.Enumerable.HasValue) current.Enumerable = desc.Enumerable;
                if (desc.Configurable.HasValue) current.Configurable = desc.Configurable;
                if (desc.Getter != null || desc.Setter != null)
                {
                    current.Getter = desc.Getter;
                    current.Setter = desc.Setter;
                    if (!desc.Value.HasValue)
                    {
                        current.Value = null;
                        current.Writable = null;
                    }
                }

                _expandos[key] = current;
                return true;
            }

            var normalized = desc;
            if (normalized.IsData)
            {
                if (!normalized.Value.HasValue) normalized.Value = FenValue.Undefined;
                if (!normalized.Writable.HasValue) normalized.Writable = false;
            }
            if (!normalized.Enumerable.HasValue) normalized.Enumerable = false;
            if (!normalized.Configurable.HasValue) normalized.Configurable = false;

            _expandos[key] = normalized;
            _expandoOrder.Add(key);
            return true;
        }

        private IEnumerable<string> EnumerateSupportedPropertyNames()
        {
            var yielded = new HashSet<string>(StringComparer.Ordinal);
            var attrs = _element.Attributes;
            for (int i = 0; i < attrs.Length; i++)
            {
                var attr = attrs[i];
                if (attr == null || string.IsNullOrEmpty(attr.Name))
                {
                    continue;
                }

                if (!attr.Name.StartsWith("data-", StringComparison.Ordinal))
                {
                    continue;
                }

                var suffix = attr.Name.Substring(5);
                var propName = AttributeSuffixToPropertyName(suffix);
                if (yielded.Add(propName))
                {
                    yield return propName;
                }
            }
        }

        private static string AttributeSuffixToPropertyName(string suffix)
        {
            if (suffix == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(suffix.Length);
            for (int i = 0; i < suffix.Length; i++)
            {
                var c = suffix[i];
                if (c == '-' && i + 1 < suffix.Length && suffix[i + 1] >= 'a' && suffix[i + 1] <= 'z')
                {
                    sb.Append(char.ToUpperInvariant(suffix[i + 1]));
                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string PropertyNameToAttributeSuffix(string propertyName)
        {
            if (propertyName == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(propertyName.Length * 2);
            for (int i = 0; i < propertyName.Length; i++)
            {
                var c = propertyName[i];
                if (c >= 'A' && c <= 'Z')
                {
                    sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
    public class CSSStyleDeclaration : IObject
    {
        private static readonly Dictionary<string, string> s_propertyAliases = new(StringComparer.Ordinal)
        {
            ["webkitAlignContent"] = "align-content",
            ["webkitAlignItems"] = "align-items",
            ["webkitAlignSelf"] = "align-self",
            ["webkitAnimation"] = "animation",
            ["webkitAnimationDelay"] = "animation-delay",
            ["webkitAnimationDirection"] = "animation-direction",
            ["webkitAnimationDuration"] = "animation-duration",
            ["webkitAnimationFillMode"] = "animation-fill-mode",
            ["webkitAnimationIterationCount"] = "animation-iteration-count",
            ["webkitAnimationName"] = "animation-name",
            ["webkitAnimationPlayState"] = "animation-play-state",
            ["webkitAnimationTimingFunction"] = "animation-timing-function",
            ["webkitBackfaceVisibility"] = "backface-visibility",
            ["WebKitBackgroundClip"] = "background-clip",
            ["webkitBackgroundOrigin"] = "background-origin",
            ["webkitBackgroundSize"] = "background-size",
            ["webkitBorderBottomLeftRadius"] = "border-bottom-left-radius",
            ["webkitBorderBottomRightRadius"] = "border-bottom-right-radius",
            ["webkitBorderRadius"] = "border-radius",
            ["webkitBorderTopLeftRadius"] = "border-top-left-radius",
            ["webkitBorderTopRightRadius"] = "border-top-right-radius",
            ["webkitBoxShadow"] = "box-shadow",
            ["webkitBoxSizing"] = "box-sizing",
            ["webkitFilter"] = "filter",
            ["webkitFlex"] = "flex",
            ["webkitFlexBasis"] = "flex-basis",
            ["webkitFlexDirection"] = "flex-direction",
            ["webkitFlexFlow"] = "flex-flow",
            ["webkitFlexGrow"] = "flex-grow",
            ["webkitFlexShrink"] = "flex-shrink",
            ["webkitFlexWrap"] = "flex-wrap",
            ["webkitJustifyContent"] = "justify-content",
            ["webkitMask"] = "mask",
            ["webkitMaskBoxImage"] = "mask-box-image",
            ["webkitMaskBoxImageOutset"] = "mask-box-image-outset",
            ["webkitMaskBoxImageRepeat"] = "mask-box-image-repeat",
            ["webkitMaskBoxImageSlice"] = "mask-box-image-slice",
            ["webkitMaskBoxImageSource"] = "mask-box-image-source",
            ["webkitMaskBoxImageWidth"] = "mask-box-image-width",
            ["webkitMaskClip"] = "mask-clip",
            ["webkitMaskComposite"] = "mask-composite",
            ["webkitMaskImage"] = "mask-image",
            ["webkitMaskOrigin"] = "mask-origin",
            ["webkitMaskPosition"] = "mask-position",
            ["webkitMaskRepeat"] = "mask-repeat",
            ["webkitMaskSize"] = "mask-size",
            ["webkitOrder"] = "order",
            ["webkitPerspective"] = "perspective",
            ["webkitPerspectiveOrigin"] = "perspective-origin",
            ["webkitTransform"] = "transform",
            ["webkitTransformOrigin"] = "transform-origin",
            ["webkitTransformStyle"] = "transform-style",
            ["webkitTransition"] = "transition",
            ["webkitTransitionDelay"] = "transition-delay",
            ["webkitTransitionDuration"] = "transition-duration",
            ["webkitTransitionProperty"] = "transition-property",
            ["webkitTransitionTimingFunction"] = "transition-timing-function",
        };

        private readonly Element _element;
        private readonly IExecutionContext _context;
        private IObject _prototype;
        public object NativeObject { get; set; }

        public CSSStyleDeclaration(Element element, IExecutionContext context)
        {
            _element = element;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            static string NormalizeStyleDeclarationValue(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return value ?? string.Empty;
                }

                const string importantSuffix = "!important";
                var trimmed = value.Trim();
                if (trimmed.EndsWith(importantSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - importantSuffix.Length).TrimEnd();
                }

                return trimmed;
            }

            // Get style property from element attributes (style="key:value")
            // Simplified: parsing style attribute every time is slow but works for now
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            
            if (string.Equals(key, "setProperty", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("setProperty", SetProperty));
            if (string.Equals(key, "getPropertyValue", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("getPropertyValue", GetPropertyValue));
            if (string.Equals(key, "removeProperty", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromFunction(new FenFunction("removeProperty", RemoveProperty));

            var cssKey = ResolveCssPropertyName(key);
            return styles.ContainsKey(cssKey) ? FenValue.FromString(NormalizeStyleDeclarationValue(styles[cssKey])) : FenValue.Undefined;
        }

        private string CamelToKebab(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Handle --custom-properties as-is
            if (s.StartsWith("--")) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, "(?<!^)([A-Z])", "-$1").ToLower();
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
             if (_context != null)
            {
                _context.CheckExecutionTimeLimit();
            }

            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            var cssKey = ResolveCssPropertyName(key);
            styles[cssKey] = value.ToString();
            
            // Rebuild style string
            var sb = new StringBuilder();
            foreach (var kvp in styles)
            {
                sb.Append($"{kvp.Key}:{kvp.Value};");
            }
            
            if (_element != null)
            {
                _element.SetAttribute("style", sb.ToString());
            }

            FenLogger.Debug($"[CSS] Set style {key}={value}", LogCategory.CSS);
            _context.RequestRender?.Invoke();
        }

        private FenValue SetProperty(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Undefined;
            Set(args[0].ToString(), args[1]);
            return FenValue.Undefined;
        }

        private FenValue GetPropertyValue(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.FromString("");
            var val = Get(args[0].ToString());
            return val.IsUndefined ? FenValue.FromString("") : val;
        }

        private FenValue RemoveProperty(FenValue[] args, FenValue thisVal)
        {
             if (args.Length == 0) return FenValue.FromString("");
             var key = ResolveCssPropertyName(args[0].ToString());
             
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            if (styles.ContainsKey(key))
            {
                var val = styles[key];
                styles.Remove(key);
                
                // Rebuild
                var sb = new StringBuilder();
                foreach (var kvp in styles) sb.Append($"{kvp.Key}:{kvp.Value};");
                if (_element != null) _element.SetAttribute("style", sb.ToString());
                
                 _context?.RequestRender?.Invoke();
                 return FenValue.FromString(val);
            }
            return FenValue.FromString("");
        }

        public bool Has(string key, IExecutionContext context = null)
        {
            if (string.Equals(key, "setProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "getPropertyValue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "removeProperty", StringComparison.OrdinalIgnoreCase) ||
                s_propertyAliases.ContainsKey(key))
            {
                return true;
            }

            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            return styles.ContainsKey(ResolveCssPropertyName(key));
        }
        public bool Delete(string key, IExecutionContext context = null) => false;
        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var styleStr = _element.GetAttribute("style") ?? "";
            var styles = ParseStyle(styleStr);
            return s_propertyAliases.Keys.Concat(styles.Keys).Distinct(StringComparer.Ordinal);
        }
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;

        private string ResolveCssPropertyName(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            return s_propertyAliases.TryGetValue(key, out var alias) ? alias : CamelToKebab(key);
        }

        private Dictionary<string, string> ParseStyle(string style)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(style)) return dict;

            foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length == 2)
                {
                    dict[kv[0].Trim()] = kv[1].Trim();
                }
            }
            return dict;
        }
    }
}






























