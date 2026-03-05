using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;
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
        public float PageX { get; set; }
        public float PageY { get; set; }
        public float ScreenX { get; set; }
        public float ScreenY { get; set; }
        public int Buttons { get; set; }
        public int PointerId { get; set; } = 1;
        public string PointerType { get; set; } = "mouse";
        public float Pressure { get; set; } = 0.5f;
        public float TiltX { get; set; }
        public float TiltY { get; set; }
        public bool IsPrimary { get; set; } = true;
        public bool PointerCaptured { get; set; }
        public List<InputTouchPoint> TouchPoints { get; } = new();
        
        // Target element determined by HitTest
        public Element Target { get; set; }
    }

    public sealed class InputTouchPoint
    {
        public long Identifier { get; set; }
        public float ClientX { get; set; }
        public float ClientY { get; set; }
        public float PageX { get; set; }
        public float PageY { get; set; }
        public float ScreenX { get; set; }
        public float ScreenY { get; set; }
        public float RadiusX { get; set; }
        public float RadiusY { get; set; }
        public float RotationAngle { get; set; }
        public float Force { get; set; } = 0.5f;
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

        public bool ProcessEvent(InputEvent evt, Rendering.Core.RenderContext renderContext = null, IExecutionContext context = null)
        {
            // 1. Determine Target via HitTest if coordinates provided
            if (renderContext != null && (evt.Type == InputEventType.MouseDown ||
                                          evt.Type == InputEventType.MouseUp ||
                                          evt.Type == InputEventType.MouseMove ||
                                          evt.Type == InputEventType.Click ||
                                          evt.Type == InputEventType.TouchStart ||
                                          evt.Type == InputEventType.TouchMove ||
                                          evt.Type == InputEventType.TouchEnd))
            {
                // HitTester uses paint-tree reverse traversal when available (stacking-context aware).
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
                return DispatchToDom(evt, context);
            }
            return false;
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

        private bool DispatchToDom(InputEvent evt, IExecutionContext context = null)
        {
            if (evt == null || evt.Target == null) return false;

            if (!_domEventMap.TryGetValue(evt.Type, out var metadata))
                return false;

            context ??= new FenBrowser.FenEngine.Core.ExecutionContext();
            var domEvent = new DomEvent(
                metadata.Type,
                metadata.Bubbles,
                metadata.Cancelable,
                metadata.Composed,
                context);

            domEvent.Set("detail", FenValue.FromNumber(1));
            domEvent.Set("clientX", FenValue.FromNumber(evt.X));
            domEvent.Set("clientY", FenValue.FromNumber(evt.Y));
            domEvent.Set("pageX", FenValue.FromNumber(evt.X));
            domEvent.Set("pageY", FenValue.FromNumber(evt.Y));
            domEvent.Set("screenX", FenValue.FromNumber(evt.X));
            domEvent.Set("screenY", FenValue.FromNumber(evt.Y));
            domEvent.Set("button", FenValue.FromNumber(Math.Max(0, evt.Button)));
            domEvent.Set("shiftKey", FenValue.FromBoolean(evt.ShiftKey));
            domEvent.Set("ctrlKey", FenValue.FromBoolean(evt.CtrlKey));
            domEvent.Set("altKey", FenValue.FromBoolean(evt.AltKey));
            domEvent.Set("metaKey", FenValue.FromBoolean(evt.MetaKey));
            domEvent.Set("buttons", FenValue.FromNumber(evt.Buttons));
            domEvent.Set("pointerId", FenValue.FromNumber(evt.PointerId));
            domEvent.Set("pointerType", FenValue.FromString(evt.PointerType ?? "mouse"));
            domEvent.Set("pressure", FenValue.FromNumber(evt.Pressure));
            domEvent.Set("tiltX", FenValue.FromNumber(evt.TiltX));
            domEvent.Set("tiltY", FenValue.FromNumber(evt.TiltY));
            domEvent.Set("isPrimary", FenValue.FromBoolean(evt.IsPrimary));
            domEvent.Set("pointerCaptured", FenValue.FromBoolean(evt.PointerCaptured));

            if (evt.TouchPoints.Count > 0)
            {
                var touches = evt.TouchPoints
                    .Select(point => new Touch(
                        point.Identifier,
                        evt.Target,
                        point.ClientX,
                        point.ClientY,
                        point.ScreenX,
                        point.ScreenY,
                        point.PageX,
                        point.PageY,
                        point.RadiusX,
                        point.RadiusY,
                        point.RotationAngle,
                        point.Force,
                        context))
                    .ToList();

                if (touches.Count > 0)
                {
                    var touchList = new TouchList(touches);
                    domEvent.Set("touches", FenValue.FromObject(touchList));
                    domEvent.Set("targetTouches", FenValue.FromObject(touchList));
                    domEvent.Set("changedTouches", FenValue.FromObject(touchList));
                }
            }

            return FenBrowser.FenEngine.DOM.EventTarget.DispatchEvent(evt.Target, domEvent, context);
        }

        private readonly struct DomEventMetadata
        {
            public string Type { get; }
            public bool Bubbles { get; }
            public bool Cancelable { get; }
            public bool Composed { get; }

            public DomEventMetadata(string type, bool bubbles, bool cancelable, bool composed)
            {
                Type = type;
                Bubbles = bubbles;
                Cancelable = cancelable;
                Composed = composed;
            }
        }

        private static readonly Dictionary<InputEventType, DomEventMetadata> _domEventMap =
            new()
            {
                { InputEventType.MouseDown, new DomEventMetadata("mousedown", true, true, true) },
                { InputEventType.MouseUp, new DomEventMetadata("mouseup", true, true, true) },
                { InputEventType.MouseMove, new DomEventMetadata("mousemove", true, false, true) },
                { InputEventType.Click, new DomEventMetadata("click", true, true, true) },
                { InputEventType.KeyDown, new DomEventMetadata("keydown", true, true, true) },
                { InputEventType.KeyUp, new DomEventMetadata("keyup", true, true, true) },
                { InputEventType.TouchStart, new DomEventMetadata("touchstart", true, true, true) },
                { InputEventType.TouchMove, new DomEventMetadata("touchmove", true, false, true) },
                { InputEventType.TouchEnd, new DomEventMetadata("touchend", true, true, true) },
            };
    }
}




