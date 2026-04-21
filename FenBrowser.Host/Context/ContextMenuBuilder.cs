using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Interaction;

namespace FenBrowser.Host.Context
{
    public static class ContextMenuBuilder
    {
        public static List<ContextMenuItem> Build(
            HitTestResult hit,
            string currentUrl,
            bool hasSelection,
            bool canPaste,
            Action<string> onNavigate,
            Action onCopy,
            Action onPaste,
            Action onSelectAll,
            Action onReload,
            Action onBack,
            Action onForward,
            Action<string> onOpenInNewTab,
            Action<string> onCopyLink,
            Action<string> onViewPageSource,
            Action<HitTestResult> onInspectElement
        )
        {
            var items = new List<ContextMenuItem>();

            // Navigation
            items.Add(ContextMenuItem.Create("Back", onBack, "Alt+Left", onBack != null));
            items.Add(ContextMenuItem.Create("Forward", onForward, "Alt+Right", onForward != null));
            items.Add(ContextMenuItem.Create("Reload", onReload, "Ctrl+R", onReload != null));
            items.Add(ContextMenuItem.Separator());

            // Link Actions
            if (hit.IsLink && !string.IsNullOrEmpty(hit.Href))
            {
                items.Add(ContextMenuItem.Create("Open Link in New Tab", () => onOpenInNewTab?.Invoke(hit.Href), enabled: onOpenInNewTab != null));
                items.Add(ContextMenuItem.Create("Copy Link Address", () => onCopyLink?.Invoke(hit.Href), enabled: onCopyLink != null));
                items.Add(ContextMenuItem.Separator());
            }

            // Image Actions
            if (hit.TagName?.ToLowerInvariant() == "img")
            {
                items.Add(ContextMenuItem.Create("Open Image in New Tab", () =>
                {
                    if (!string.IsNullOrEmpty(hit.ImageSrc))
                        onOpenInNewTab?.Invoke(hit.ImageSrc);
                }, enabled: onOpenInNewTab != null && !string.IsNullOrEmpty(hit.ImageSrc)));
                items.Add(ContextMenuItem.Create("Save Image As...", () =>
                {
                    EngineLogBridge.Info($"[ContextMenu] Save Image As... requested: {hit.ImageSrc}", FenBrowser.Core.Logging.LogCategory.General);
                }, enabled: !string.IsNullOrEmpty(hit.ImageSrc)));
                items.Add(ContextMenuItem.Create("Copy Image Address", () =>
                {
                    if (!string.IsNullOrEmpty(hit.ImageSrc))
                        onCopyLink?.Invoke(hit.ImageSrc);
                }, enabled: onCopyLink != null && !string.IsNullOrEmpty(hit.ImageSrc)));
                items.Add(ContextMenuItem.Separator());
            }

            // Selection Actions
            if (hasSelection)
            {
                items.Add(ContextMenuItem.Create("Copy", onCopy, "Ctrl+C", onCopy != null));
                items.Add(ContextMenuItem.Separator());
            }
            
            if (canPaste)
            {
                items.Add(ContextMenuItem.Create("Paste", onPaste, "Ctrl+V", onPaste != null));
            }

            items.Add(ContextMenuItem.Create("Select All", onSelectAll, "Ctrl+A", onSelectAll != null));
            
            // Developer Tools Section
            items.Add(ContextMenuItem.Separator());
            
            // View Page Source (opens in new tab with view-source: protocol)
            items.Add(ContextMenuItem.Create("View Page Source", () => 
            {
                if (!string.IsNullOrEmpty(currentUrl) && onViewPageSource != null)
                {
                    var viewSourceUrl = $"view-source:{currentUrl}";
                    onViewPageSource(viewSourceUrl);
                }
            }, "Ctrl+U", !string.IsNullOrEmpty(currentUrl) && onViewPageSource != null));
            
            // Inspect Element (shows element details)
            items.Add(ContextMenuItem.Create("Inspect", () => 
            {
                onInspectElement?.Invoke(hit);
            }, "Ctrl+Shift+I", onInspectElement != null));


            return items;
        }
    }
}


