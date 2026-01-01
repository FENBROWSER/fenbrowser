// =============================================================================
// InputQueue.cs
// FenBrowser Input Event Queue
// 
// SPEC REFERENCE: WHATWG HTML §8.1.7 - Event Loops
//                 https://html.spec.whatwg.org/multipage/webappapis.html#event-loops
// 
// PURPOSE: Buffer input events (keyboard, mouse, touch) for processing during
//          the event loop. Events are queued and processed in order during
//          the appropriate phase.
// 
// STATUS: ✅ Implemented
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Type of input event.
    /// </summary>
    public enum InputEventType
    {
        /// <summary>Mouse button pressed.</summary>
        MouseDown,
        /// <summary>Mouse button released.</summary>
        MouseUp,
        /// <summary>Mouse moved.</summary>
        MouseMove,
        /// <summary>Mouse wheel scrolled.</summary>
        MouseWheel,
        /// <summary>Mouse entered element.</summary>
        MouseEnter,
        /// <summary>Mouse left element.</summary>
        MouseLeave,
        /// <summary>Key pressed.</summary>
        KeyDown,
        /// <summary>Key released.</summary>
        KeyUp,
        /// <summary>Character input (after key processing).</summary>
        KeyPress,
        /// <summary>Touch started.</summary>
        TouchStart,
        /// <summary>Touch moved.</summary>
        TouchMove,
        /// <summary>Touch ended.</summary>
        TouchEnd,
        /// <summary>Touch cancelled.</summary>
        TouchCancel,
        /// <summary>Focus gained.</summary>
        Focus,
        /// <summary>Focus lost.</summary>
        Blur,
        /// <summary>Window resized.</summary>
        Resize,
        /// <summary>Scroll occurred.</summary>
        Scroll
    }

    /// <summary>
    /// Represents a queued input event.
    /// Immutable struct for efficient queueing.
    /// </summary>
    public readonly struct InputEvent
    {
        /// <summary>Type of input event.</summary>
        public readonly InputEventType Type;

        /// <summary>X coordinate (for mouse/touch events).</summary>
        public readonly float X;

        /// <summary>Y coordinate (for mouse/touch events).</summary>
        public readonly float Y;

        /// <summary>Mouse button (0=left, 1=middle, 2=right).</summary>
        public readonly int Button;

        /// <summary>Scroll delta (for wheel events).</summary>
        public readonly float Delta;

        /// <summary>Key code (for keyboard events).</summary>
        public readonly int KeyCode;

        /// <summary>Key character (for key press events).</summary>
        public readonly char KeyChar;

        /// <summary>Modifier keys (shift, ctrl, alt, meta).</summary>
        public readonly ModifierKeys Modifiers;

        /// <summary>Touch identifier (for multi-touch).</summary>
        public readonly int TouchId;

        /// <summary>Timestamp when event was created.</summary>
        public readonly long TimestampTicks;

        /// <summary>Target element ID (if known).</summary>
        public readonly int TargetElementId;

        /// <summary>
        /// Creates a new input event.
        /// </summary>
        public InputEvent(
            InputEventType type,
            float x = 0,
            float y = 0,
            int button = 0,
            float delta = 0,
            int keyCode = 0,
            char keyChar = '\0',
            ModifierKeys modifiers = ModifierKeys.None,
            int touchId = 0,
            int targetElementId = 0)
        {
            Type = type;
            X = x;
            Y = y;
            Button = button;
            Delta = delta;
            KeyCode = keyCode;
            KeyChar = keyChar;
            Modifiers = modifiers;
            TouchId = touchId;
            TargetElementId = targetElementId;
            TimestampTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Creates a mouse event.
        /// </summary>
        public static InputEvent Mouse(InputEventType type, float x, float y, int button = 0, ModifierKeys modifiers = ModifierKeys.None)
        {
            return new InputEvent(type, x, y, button, 0, 0, '\0', modifiers);
        }

        /// <summary>
        /// Creates a mouse wheel event.
        /// </summary>
        public static InputEvent Wheel(float x, float y, float delta, ModifierKeys modifiers = ModifierKeys.None)
        {
            return new InputEvent(InputEventType.MouseWheel, x, y, 0, delta, 0, '\0', modifiers);
        }

        /// <summary>
        /// Creates a keyboard event.
        /// </summary>
        public static InputEvent Keyboard(InputEventType type, int keyCode, char keyChar = '\0', ModifierKeys modifiers = ModifierKeys.None)
        {
            return new InputEvent(type, 0, 0, 0, 0, keyCode, keyChar, modifiers);
        }

        /// <summary>
        /// Creates a touch event.
        /// </summary>
        public static InputEvent Touch(InputEventType type, int touchId, float x, float y)
        {
            return new InputEvent(type, x, y, 0, 0, 0, '\0', ModifierKeys.None, touchId);
        }

        /// <summary>
        /// Creates a resize event.
        /// </summary>
        public static InputEvent Resized(float width, float height)
        {
            return new InputEvent(InputEventType.Resize, width, height);
        }

        /// <summary>
        /// Timestamp as DateTime.
        /// </summary>
        public DateTime Timestamp => new DateTime(TimestampTicks, DateTimeKind.Utc);

        /// <summary>
        /// Returns true if this is a mouse event.
        /// </summary>
        public bool IsMouse => Type >= InputEventType.MouseDown && Type <= InputEventType.MouseLeave;

        /// <summary>
        /// Returns true if this is a keyboard event.
        /// </summary>
        public bool IsKeyboard => Type >= InputEventType.KeyDown && Type <= InputEventType.KeyPress;

        /// <summary>
        /// Returns true if this is a touch event.
        /// </summary>
        public bool IsTouch => Type >= InputEventType.TouchStart && Type <= InputEventType.TouchCancel;

        public override string ToString()
        {
            return Type switch
            {
                InputEventType.MouseDown or InputEventType.MouseUp or InputEventType.MouseMove => 
                    $"{Type}({X:F1},{Y:F1} btn={Button})",
                InputEventType.MouseWheel => 
                    $"{Type}({X:F1},{Y:F1} delta={Delta:F1})",
                InputEventType.KeyDown or InputEventType.KeyUp or InputEventType.KeyPress => 
                    $"{Type}(code={KeyCode} char='{KeyChar}')",
                InputEventType.TouchStart or InputEventType.TouchMove or InputEventType.TouchEnd => 
                    $"{Type}(id={TouchId} {X:F1},{Y:F1})",
                InputEventType.Resize => 
                    $"{Type}({X:F0}x{Y:F0})",
                _ => Type.ToString()
            };
        }
    }

    /// <summary>
    /// Modifier keys state.
    /// </summary>
    [Flags]
    public enum ModifierKeys
    {
        /// <summary>No modifiers pressed.</summary>
        None = 0,
        /// <summary>Shift key pressed.</summary>
        Shift = 1,
        /// <summary>Control key pressed.</summary>
        Ctrl = 2,
        /// <summary>Alt key pressed.</summary>
        Alt = 4,
        /// <summary>Meta/Windows/Command key pressed.</summary>
        Meta = 8
    }

    /// <summary>
    /// Thread-safe queue for input events.
    /// Events are queued from the UI thread and processed during the event loop.
    /// </summary>
    public sealed class InputQueue
    {
        // Thread-safe queue for pending events
        private readonly ConcurrentQueue<InputEvent> _pendingEvents = new();

        // Optional capacity limit (0 = unlimited)
        private readonly int _maxCapacity;

        // Event statistics
        private long _totalEnqueued = 0;
        private long _totalDequeued = 0;
        private long _totalDropped = 0;

        /// <summary>
        /// Creates a new input queue with optional capacity limit.
        /// </summary>
        public InputQueue(int maxCapacity = 1000)
        {
            _maxCapacity = maxCapacity;
        }

        /// <summary>
        /// Number of pending events.
        /// </summary>
        public int Count => _pendingEvents.Count;

        /// <summary>
        /// Returns true if there are pending events.
        /// </summary>
        public bool HasPendingEvents => !_pendingEvents.IsEmpty;

        /// <summary>
        /// Total events enqueued since creation.
        /// </summary>
        public long TotalEnqueued => _totalEnqueued;

        /// <summary>
        /// Total events dequeued since creation.
        /// </summary>
        public long TotalDequeued => _totalDequeued;

        /// <summary>
        /// Total events dropped due to capacity limits.
        /// </summary>
        public long TotalDropped => _totalDropped;

        /// <summary>
        /// Queue an input event for processing.
        /// </summary>
        /// <returns>True if enqueued, false if dropped due to capacity.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Enqueue(InputEvent evt)
        {
            // Check capacity
            if (_maxCapacity > 0 && _pendingEvents.Count >= _maxCapacity)
            {
                System.Threading.Interlocked.Increment(ref _totalDropped);
                return false;
            }

            _pendingEvents.Enqueue(evt);
            System.Threading.Interlocked.Increment(ref _totalEnqueued);
            return true;
        }

        /// <summary>
        /// Try to dequeue the next event.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out InputEvent evt)
        {
            if (_pendingEvents.TryDequeue(out evt))
            {
                System.Threading.Interlocked.Increment(ref _totalDequeued);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Peek at the next event without removing it.
        /// </summary>
        public bool TryPeek(out InputEvent evt)
        {
            return _pendingEvents.TryPeek(out evt);
        }

        /// <summary>
        /// Drain all pending events into a list for batch processing.
        /// </summary>
        public List<InputEvent> DrainAll()
        {
            var result = new List<InputEvent>(_pendingEvents.Count);
            while (_pendingEvents.TryDequeue(out var evt))
            {
                result.Add(evt);
                System.Threading.Interlocked.Increment(ref _totalDequeued);
            }
            return result;
        }

        /// <summary>
        /// Drain events up to a maximum count.
        /// </summary>
        public List<InputEvent> Drain(int maxCount)
        {
            var result = new List<InputEvent>(Math.Min(maxCount, _pendingEvents.Count));
            while (result.Count < maxCount && _pendingEvents.TryDequeue(out var evt))
            {
                result.Add(evt);
                System.Threading.Interlocked.Increment(ref _totalDequeued);
            }
            return result;
        }

        /// <summary>
        /// Clear all pending events.
        /// </summary>
        public void Clear()
        {
            while (_pendingEvents.TryDequeue(out _))
            {
                System.Threading.Interlocked.Increment(ref _totalDequeued);
            }
        }

        /// <summary>
        /// Queue a mouse down event.
        /// </summary>
        public bool EnqueueMouseDown(float x, float y, int button = 0, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Mouse(InputEventType.MouseDown, x, y, button, modifiers));
        }

        /// <summary>
        /// Queue a mouse up event.
        /// </summary>
        public bool EnqueueMouseUp(float x, float y, int button = 0, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Mouse(InputEventType.MouseUp, x, y, button, modifiers));
        }

        /// <summary>
        /// Queue a mouse move event.
        /// </summary>
        public bool EnqueueMouseMove(float x, float y, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Mouse(InputEventType.MouseMove, x, y, 0, modifiers));
        }

        /// <summary>
        /// Queue a mouse wheel event.
        /// </summary>
        public bool EnqueueMouseWheel(float x, float y, float delta, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Wheel(x, y, delta, modifiers));
        }

        /// <summary>
        /// Queue a key down event.
        /// </summary>
        public bool EnqueueKeyDown(int keyCode, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Keyboard(InputEventType.KeyDown, keyCode, '\0', modifiers));
        }

        /// <summary>
        /// Queue a key up event.
        /// </summary>
        public bool EnqueueKeyUp(int keyCode, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Keyboard(InputEventType.KeyUp, keyCode, '\0', modifiers));
        }

        /// <summary>
        /// Queue a key press (character input) event.
        /// </summary>
        public bool EnqueueKeyPress(char keyChar, ModifierKeys modifiers = ModifierKeys.None)
        {
            return Enqueue(InputEvent.Keyboard(InputEventType.KeyPress, 0, keyChar, modifiers));
        }

        /// <summary>
        /// Queue a resize event.
        /// </summary>
        public bool EnqueueResize(float width, float height)
        {
            return Enqueue(InputEvent.Resized(width, height));
        }

        public override string ToString()
        {
            return $"InputQueue[Count={Count}, Enqueued={TotalEnqueued}, Dequeued={TotalDequeued}, Dropped={TotalDropped}]";
        }
    }
}
