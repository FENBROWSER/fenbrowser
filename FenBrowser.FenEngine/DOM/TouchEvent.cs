using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Represents a single contact point on a touch-sensitive device.
    /// </summary>
    public class Touch : FenObject
    {
        public long Identifier { get; }
        public Element Target { get; }
        public double ClientX { get; }
        public double ClientY { get; }
        public double ScreenX { get; }
        public double ScreenY { get; }
        public double PageX { get; }
        public double PageY { get; }
        public double RadiusX { get; }
        public double RadiusY { get; }
        public double RotationAngle { get; }
        public double Force { get; }

        public Touch(long identifier, Element target, 
            double clientX, double clientY, 
            double screenX, double screenY,
            double pageX, double pageY,
            double radiusX = 0, double radiusY = 0, 
            double rotationAngle = 0, double force = 0)
        {
            Identifier = identifier;
            Target = target;
            ClientX = clientX;
            ClientY = clientY;
            ScreenX = screenX;
            ScreenY = screenY;
            PageX = pageX;
            PageY = pageY;
            RadiusX = radiusX;
            RadiusY = radiusY;
            RotationAngle = rotationAngle;
            Force = force;

            InitializeProperties();
        }

        private void InitializeProperties()
        {
            Set("identifier", FenValue.FromNumber(Identifier));
            // TODO: Proper Element wrapping for JS exposure
            Set("target", FenValue.Null); 
            Set("clientX", FenValue.FromNumber(ClientX));
            Set("clientY", FenValue.FromNumber(ClientY));
            Set("screenX", FenValue.FromNumber(ScreenX));
            Set("screenY", FenValue.FromNumber(ScreenY));
            Set("pageX", FenValue.FromNumber(PageX));
            Set("pageY", FenValue.FromNumber(PageY));
            Set("radiusX", FenValue.FromNumber(RadiusX));
            Set("radiusY", FenValue.FromNumber(RadiusY));
            Set("rotationAngle", FenValue.FromNumber(RotationAngle));
            Set("force", FenValue.FromNumber(Force));
        }
    }

    /// <summary>
    /// Represents a list of Touch objects.
    /// </summary>
    public class TouchList : FenObject
    {
        private readonly List<Touch> _touches;

        public int Length => _touches.Count;

        public TouchList(List<Touch> touches)
        {
            _touches = touches ?? new List<Touch>();
            InitializeProperties();
        }

        public Touch Item(int index)
        {
            if (index < 0 || index >= _touches.Count) return null;
            return _touches[index];
        }

        private void InitializeProperties()
        {
            Set("length", FenValue.FromNumber(Length));
            
            // Item access method
            Set("item", FenValue.FromFunction(new FenFunction("item", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Number)
                {
                    var index = (int)args[0].ToNumber();
                    var touch = Item(index);
                    return touch != null ? FenValue.FromObject(touch) : FenValue.Null;
                }
                return FenValue.Null;
            })));

            // Array-like access
            for (int i = 0; i < _touches.Count; i++)
            {
                Set(i.ToString(), FenValue.FromObject(_touches[i]));
            }
        }
    }

    /// <summary>
    /// Represents an event triggered by a touch interaction.
    /// </summary>
    public class TouchEvent : DomEvent
    {
        public TouchList Touches { get; }
        public TouchList TargetTouches { get; }
        public TouchList ChangedTouches { get; }
        public bool CtrlKey { get; }
        public bool ShiftKey { get; }
        public bool AltKey { get; }
        public bool MetaKey { get; }

        public TouchEvent(string type, TouchList touches, TouchList targetTouches, TouchList changedTouches,
                          bool ctrlKey = false, bool shiftKey = false, bool altKey = false, bool metaKey = false,
                          bool bubbles = true, bool cancelable = true)
            : base(type, bubbles, cancelable)
        {
            Touches = touches;
            TargetTouches = targetTouches;
            ChangedTouches = changedTouches;
            CtrlKey = ctrlKey;
            ShiftKey = shiftKey;
            AltKey = altKey;
            MetaKey = metaKey;

            InitializeDetailedProperties();
        }

        private void InitializeDetailedProperties()
        {
            Set("touches", FenValue.FromObject(Touches));
            Set("targetTouches", FenValue.FromObject(TargetTouches));
            Set("changedTouches", FenValue.FromObject(ChangedTouches));
            Set("ctrlKey", FenValue.FromBoolean(CtrlKey));
            Set("shiftKey", FenValue.FromBoolean(ShiftKey));
            Set("altKey", FenValue.FromBoolean(AltKey));
            Set("metaKey", FenValue.FromBoolean(MetaKey));
        }
    }
}
