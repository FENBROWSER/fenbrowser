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
            items.Add(ContextMenuItem.Create("Back", onBack, "Alt+Left"));
            items.Add(ContextMenuItem.Create("Forward", onForward, "Alt+Right"));
            items.Add(ContextMenuItem.Create("Reload", onReload, "Ctrl+R"));
            items.Add(ContextMenuItem.Separator());

            // Link Actions
            if (hit.IsLink && !string.IsNullOrEmpty(hit.Href))
            {
                items.Add(ContextMenuItem.Create("Open Link in New Tab", () => onOpenInNewTab?.Invoke(hit.Href)));
                items.Add(ContextMenuItem.Create("Copy Link Address", () => onCopyLink?.Invoke(hit.Href)));
                items.Add(ContextMenuItem.Separator());
            }

            // Image Actions
            if (hit.TagName?.ToLowerInvariant() == "img")
            {
                items.Add(ContextMenuItem.Create("Open Image in New Tab", () => 
                {
                    // TODO: Get image src from hit result
                    FenBrowser.Core.FenLogger.Info("[ContextMenu] Open Image in New Tab requested", FenBrowser.Core.Logging.LogCategory.General);
                }));
                items.Add(ContextMenuItem.Create("Save Image As...", () => 
                {
                    FenBrowser.Core.FenLogger.Info("[ContextMenu] Save Image As... requested", FenBrowser.Core.Logging.LogCategory.General);
                }));
                items.Add(ContextMenuItem.Create("Copy Image", () => 
                {
                    FenBrowser.Core.FenLogger.Info("[ContextMenu] Copy Image requested", FenBrowser.Core.Logging.LogCategory.General);
                }));
                items.Add(ContextMenuItem.Separator());
            }

            // Selection Actions
            if (hasSelection)
            {
                items.Add(ContextMenuItem.Create("Copy", onCopy, "Ctrl+C"));
                items.Add(ContextMenuItem.Separator());
            }
            
            if (canPaste)
            {
                items.Add(ContextMenuItem.Create("Paste", onPaste, "Ctrl+V"));
            }

            items.Add(ContextMenuItem.Create("Select All", onSelectAll, "Ctrl+A"));
            
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
            }, "Ctrl+U"));
            
            // Inspect Element (shows element details)
            items.Add(ContextMenuItem.Create("Inspect", () => 
            {
                onInspectElement?.Invoke(hit);
            }, "Ctrl+Shift+I"));


            return items;
        }
    }
}
