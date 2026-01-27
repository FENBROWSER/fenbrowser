using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Rendering.Interaction;

namespace FenBrowser.FenEngine.Interaction
{
    public enum InputEventType
    {
        MouseDown,
        MouseUp,
        MouseMove,
        Click,
        KeyDown,
        KeyUp,
        TouchStart,
        TouchMove,
        TouchEnd
    }

    public class InputEvent
    {
        public InputEventType Type { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Button { get; set; } // 0=Left, 1=Middle, 2=Right
        public string Key { get; set; }
        public bool ShiftKey { get; set; }
        public bool CtrlKey { get; set; }
        public bool AltKey { get; set; }
        public bool MetaKey { get; set; }
        
        // Target element determined by HitTest
        public Element Target { get; set; }
    }

    /// <summary>
    /// Unifies input streams (Mouse, Keyboard, Touch) into a consistent event pipeline.
    /// Manages HitTesting and Event Dispatching.
    /// </summary>
    public class InputManager
    {
        private readonly FocusManager _focusManager;
        
        // Current hover state for sending MouseEnter/MouseLeave
        private Element _hoveredElement;
        
        // Drag state
        private bool _isDragging;
        
        public InputManager()
        {
            _focusManager = new FocusManager();
        }

        public void ProcessEvent(InputEvent evt, Rendering.Core.RenderContext renderContext = null)
        {
            // 1. Determine Target via HitTest if coordinates provided
            if (renderContext != null && (evt.Type == InputEventType.MouseDown || evt.Type == InputEventType.MouseMove || evt.Type == InputEventType.Click || evt.Type == InputEventType.TouchStart))
            {
                // TODO: Use Stacking-Context aware HitTest
                var target = HitTester.HitTest(renderContext, evt.X, evt.Y);
                evt.Target = target;
            }
            else if (evt.Type == InputEventType.KeyDown || evt.Type == InputEventType.KeyUp)
            {
                // Keyboard events target the focused element
                evt.Target = _focusManager.FocusedElement;
            }

            // 2. Handle Focus Changes
            if (evt.Type == InputEventType.MouseDown && evt.Target != null)
            {
                _focusManager.SetFocus(evt.Target);
            }

            // 3. Handle Hover State (MouseEnter/Leave)
            if (evt.Type == InputEventType.MouseMove)
            {
                UpdateHoverState(evt.Target);
            }

            // 4. Dispatch Event to DOM
            if (evt.Target != null)
            {
                DispatchToDom(evt);
            }
        }

        private void UpdateHoverState(Element newTarget)
        {
            if (_hoveredElement != newTarget)
            {
                if (_hoveredElement != null)
                {
                    // Dispatch MouseLeave
                }
                _hoveredElement = newTarget;
                if (_hoveredElement != null)
                {
                    // Dispatch MouseEnter
                }
            }
        }

        private void DispatchToDom(InputEvent evt)
        {
            // Map InputEvent to DomEvent and dispatch
            // This would call element.DispatchEvent(...)
        }
    }
}
