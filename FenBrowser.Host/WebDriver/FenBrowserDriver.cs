// =============================================================================
// FenBrowserDriver.cs
// Implementation of IBrowserDriver for FenBrowser.Host
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.Host.WebDriver
{
    /// <summary>
    /// Bridges the WebDriver server and the FenBrowser host.
    /// </summary>
    public class FenBrowserDriver : IBrowserDriver
    {
        private readonly BrowserIntegration _integration;
        
        public FenBrowserDriver(BrowserIntegration integration)
        {
            _integration = integration;
        }
        
        public async Task NavigateAsync(string url)
        {
            await _integration.NavigateAsync(url);
        }
        
        public Task<string> GetCurrentUrlAsync()
        {
            return Task.FromResult(_integration.CurrentUrl);
        }
        
        public Task<string> GetTitleAsync()
        {
            // Title is usually managed via events, but we can try to extract it from DOM if needed
            // For now, BrowserIntegration doesn't expose a Title property directly, 
            // but we can crawl the DOM for <title>
            var titleNode = FindTitleInDom(_integration.Document);
            return Task.FromResult(titleNode ?? "FenBrowser");
        }
        
        private string FindTitleInDom(Element root)
        {
            if (root == null) return null;
            
            // Look for <title> in <head>
            var head = root.Children.FirstOrDefault(c => (c as Element)?.TagName?.Equals("head", StringComparison.OrdinalIgnoreCase) == true) as Element;
            if (head != null)
            {
                var title = head.Children.FirstOrDefault(c => (c as Element)?.TagName?.Equals("title", StringComparison.OrdinalIgnoreCase) == true) as Element;
                if (title != null)
                {
                    return title.Children.FirstOrDefault(c => c is Text)?.Text;
                }
            }
            
            return null;
        }
        
        public async Task GoBackAsync()
        {
            await _integration.GoBackAsync();
        }
        
        public async Task GoForwardAsync()
        {
            await _integration.GoForwardAsync();
        }
        
        public async Task RefreshAsync()
        {
            await _integration.RefreshAsync();
        }
        
        public Task<object> FindElementAsync(string strategy, string selector)
        {
            var element = SearchDomByCss(_integration.Document, strategy, selector);
            return Task.FromResult<object>(element);
        }
        
        public Task<object[]> FindElementsAsync(string strategy, string selector)
        {
            var elements = SearchMultipleDomByCss(_integration.Document, strategy, selector);
            return Task.FromResult(elements.Cast<object>().ToArray());
        }
        
        private Element SearchDomByCss(Element root, string strategy, string selector)
        {
            if (root == null) return null;
            
            // Map strategy if needed (W3C maps everything to 'css selector' internally if using CSS)
            if (strategy == "id") return FindElementById(root, selector);
            if (strategy == "tag name") return FindElementByTag(root, selector);
            if (strategy == "link text") return FindElementByLinkText(root, selector);
            
            // Descendant selector support (minimal)
            if (selector.Contains(" "))
            {
                var parts = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Element current = root;
                foreach (var part in parts)
                {
                    current = SearchDomByCss(current, "css selector", part);
                    if (current == null) return null;
                }
                return current;
            }

            if (selector.StartsWith("#"))
            {
                return FindElementById(root, selector.Substring(1));
            }
            else if (selector.StartsWith("."))
            {
                return FindElementByClass(root, selector.Substring(1));
            }
            else
            {
                return FindElementByTag(root, selector);
            }
        }
        
        private List<Element> SearchMultipleDomByCss(Element root, string strategy, string selector)
        {
            var results = new List<Element>();
            if (root == null) return results;
            
            // Simple Tag selector for multiple
            CollectElementsByTag(root, selector, results);
            return results;
        }

        private Element FindElementByLinkText(Element root, string text)
        {
            if (root.TagName?.Equals("a", StringComparison.OrdinalIgnoreCase) == true)
            {
                var elText = string.Join("", root.Children.OfType<Text>().Select(t => t.Data));
                if (elText.Trim() == text) return root;
            }
                
            foreach (var child in root.Children.OfType<Element>())
            {
                var found = FindElementByLinkText(child, text);
                if (found != null) return found;
            }
            return null;
        }
        
        private Element FindElementById(Element root, string id)
        {
            if (root.Attr != null && root.Attr.TryGetValue("id", out var val) && val == id)
                return root;
                
            foreach (var child in root.Children.OfType<Element>())
            {
                var found = FindElementById(child, id);
                if (found != null) return found;
            }
            return null;
        }
        
        private Element FindElementByClass(Element root, string className)
        {
            if (root.Attr != null && root.Attr.TryGetValue("class", out var val) && val.Split(' ').Contains(className))
                return root;
                
            foreach (var child in root.Children.OfType<Element>())
            {
                var found = FindElementByClass(child, className);
                if (found != null) return found;
            }
            return null;
        }
        
        private Element FindElementByTag(Element root, string tag)
        {
            if (root.TagName?.Equals(tag, StringComparison.OrdinalIgnoreCase) == true)
                return root;
                
            foreach (var child in root.Children.OfType<Element>())
            {
                var found = FindElementByTag(child, tag);
                if (found != null) return found;
            }
            return null;
        }
        
        private void CollectElementsByTag(Element root, string tag, List<Element> results)
        {
            if (root.TagName?.Equals(tag, StringComparison.OrdinalIgnoreCase) == true)
                results.Add(root);
                
            foreach (var child in root.Children.OfType<Element>())
            {
                CollectElementsByTag(child, tag, results);
            }
        }
        
        public Task<string> GetElementTextAsync(object element)
        {
            if (element is Element el)
            {
                return Task.FromResult(_integration.EvaluateScript($"document.getElementById('{el.Id}')?.innerText")?.ToString() ?? "");
            }
            return Task.FromResult("");
        }
        
        public async Task ClickElementAsync(object element)
        {
            if (element is Element el)
            {
                var rect = _integration.GetElementRect(el);
                if (rect.HasValue)
                {
                    float centerX = rect.Value.Left + (rect.Value.Width / 2);
                    float centerY = rect.Value.Top + (rect.Value.Height / 2);
                    await _integration.HandleClick(centerX, centerY);
                }
            }
        }
        
        public async Task SendKeysAsync(object element, string text)
        {
            if (element is Element el)
            {
                _integration.FocusNode(el);
                foreach (var c in text)
                {
                    await _integration.HandleKeyPress(c.ToString());
                }
            }
        }
        
        public Task<string> GetElementAttributeAsync(object element, string name)
        {
            if (element is Element el && el.Attr != null && el.Attr.TryGetValue(name, out var val))
            {
                return Task.FromResult(val);
            }
            return Task.FromResult<string>(null);
        }
        
        public Task<object> ExecuteScriptAsync(string script, object[] args)
        {
            var result = _integration.EvaluateScript(script);
            return Task.FromResult(result);
        }
        
        public Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout)
        {
            // Fallback to sync for now
            return ExecuteScriptAsync(script, args);
        }
        
        public async Task<string> TakeScreenshotAsync()
        {
            return await _integration.CaptureScreenshotAsync();
        }
        
        public (int x, int y, int width, int height) GetWindowRect()
        {
            return (0, 0, 1024, 768); // Placeholder
        }
        
        public void SetWindowRect(int? x, int? y, int? width, int? height)
        {
            // Logic to resize window
        }
    }
}
