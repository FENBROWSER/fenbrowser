using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    public class LegacyUIEvent : DomEvent
    {
        protected readonly IExecutionContext Context;
        public FenValue View { get; protected set; } = FenValue.Null;
        public int Detail { get; protected set; }

        public LegacyUIEvent(string type = "", bool bubbles = false, bool cancelable = false, IExecutionContext context = null, bool initialized = true)
            : base(type, bubbles, cancelable, false, context, initialized)
        {
            Context = context;
            InitializeUiProperties();
        }

        protected void InitializeUiProperties()
        {
            Set("view", View);
            Set("detail", FenValue.FromNumber(Detail));
            Set("initUIEvent", FenValue.FromFunction(new FenFunction("initUIEvent", InitUIEvent)));
        }

        protected virtual FenValue InitUIEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new FenTypeError("TypeError: Failed to execute 'initUIEvent': 1 argument required, but only 0 present.");
            }

            if (IsDispatching)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            View = NormalizeView(args.Length >= 4 ? args[3] : FenValue.Null);
            Detail = args.Length >= 5 ? CoerceInt32(args[4]) : 0;
            Initialized = true;

            ResetInternalStateForInitialization();
            ReinitializeCoreProperties();
            InitializeUiProperties();
            return FenValue.Undefined;
        }

        protected void ReinitializeCoreProperties()
        {
            Set("type", FenValue.FromString(Type));
            Set("bubbles", FenValue.FromBoolean(Bubbles));
            Set("cancelable", FenValue.FromBoolean(Cancelable));
            Set("defaultPrevented", FenValue.FromBoolean(false));
            Set("returnValue", FenValue.FromBoolean(true));
            Set("cancelBubble", FenValue.FromBoolean(false));
            Set("target", FenValue.Null);
            Set("currentTarget", FenValue.Null);
            Set("srcElement", FenValue.Null);
            Set("eventPhase", FenValue.FromNumber(NONE));
        }

        protected static FenValue NormalizeView(FenValue value)
        {
            return value.IsObject || value.IsNull ? value : FenValue.Null;
        }

        protected static int CoerceInt32(FenValue value)
        {
            var number = value.ToNumber();
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                return 0;
            }

            return (int)number;
        }
    }

    public class LegacyMouseEvent : LegacyUIEvent
    {
        public int ScreenX { get; protected set; }
        public int ScreenY { get; protected set; }
        public int ClientX { get; protected set; }
        public int ClientY { get; protected set; }
        public bool CtrlKey { get; protected set; }
        public bool AltKey { get; protected set; }
        public bool ShiftKey { get; protected set; }
        public bool MetaKey { get; protected set; }
        public short Button { get; protected set; }
        public FenValue RelatedTarget { get; protected set; } = FenValue.Null;

        public LegacyMouseEvent(string type = "", bool bubbles = false, bool cancelable = false, IExecutionContext context = null, bool initialized = true)
            : base(type, bubbles, cancelable, context, initialized)
        {
            InitializeMouseProperties();
        }

        protected void InitializeMouseProperties()
        {
            Set("screenX", FenValue.FromNumber(ScreenX));
            Set("screenY", FenValue.FromNumber(ScreenY));
            Set("clientX", FenValue.FromNumber(ClientX));
            Set("clientY", FenValue.FromNumber(ClientY));
            Set("ctrlKey", FenValue.FromBoolean(CtrlKey));
            Set("altKey", FenValue.FromBoolean(AltKey));
            Set("shiftKey", FenValue.FromBoolean(ShiftKey));
            Set("metaKey", FenValue.FromBoolean(MetaKey));
            Set("button", FenValue.FromNumber(Button));
            Set("relatedTarget", RelatedTarget);
            Set("initMouseEvent", FenValue.FromFunction(new FenFunction("initMouseEvent", InitMouseEvent)));
        }

        private FenValue InitMouseEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new FenTypeError("TypeError: Failed to execute 'initMouseEvent': 1 argument required, but only 0 present.");
            }

            if (IsDispatching)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            View = NormalizeView(args.Length >= 4 ? args[3] : FenValue.Null);
            Detail = args.Length >= 5 ? CoerceInt32(args[4]) : 0;
            ScreenX = args.Length >= 6 ? CoerceInt32(args[5]) : 0;
            ScreenY = args.Length >= 7 ? CoerceInt32(args[6]) : 0;
            ClientX = args.Length >= 8 ? CoerceInt32(args[7]) : 0;
            ClientY = args.Length >= 9 ? CoerceInt32(args[8]) : 0;
            CtrlKey = args.Length >= 10 && args[9].ToBoolean();
            AltKey = args.Length >= 11 && args[10].ToBoolean();
            ShiftKey = args.Length >= 12 && args[11].ToBoolean();
            MetaKey = args.Length >= 13 && args[12].ToBoolean();
            Button = (short)(args.Length >= 14 ? CoerceInt32(args[13]) : 0);
            RelatedTarget = args.Length >= 15 && (args[14].IsObject || args[14].IsNull) ? args[14] : FenValue.Null;
            Initialized = true;

            ResetInternalStateForInitialization();
            ReinitializeCoreProperties();
            InitializeUiProperties();
            InitializeMouseProperties();
            return FenValue.Undefined;
        }
    }

    public class LegacyKeyboardEvent : LegacyUIEvent
    {
        public string Key { get; protected set; } = string.Empty;
        public int Location { get; protected set; }
        public bool CtrlKey { get; protected set; }
        public bool AltKey { get; protected set; }
        public bool ShiftKey { get; protected set; }
        public bool MetaKey { get; protected set; }
        public bool Repeat { get; protected set; }
        public string Locale { get; protected set; } = string.Empty;

        public LegacyKeyboardEvent(string type = "", bool bubbles = false, bool cancelable = false, IExecutionContext context = null, bool initialized = true)
            : base(type, bubbles, cancelable, context, initialized)
        {
            InitializeKeyboardProperties();
        }

        protected void InitializeKeyboardProperties()
        {
            Set("key", FenValue.FromString(Key));
            Set("location", FenValue.FromNumber(Location));
            Set("ctrlKey", FenValue.FromBoolean(CtrlKey));
            Set("altKey", FenValue.FromBoolean(AltKey));
            Set("shiftKey", FenValue.FromBoolean(ShiftKey));
            Set("metaKey", FenValue.FromBoolean(MetaKey));
            Set("repeat", FenValue.FromBoolean(Repeat));
            Set("locale", FenValue.FromString(Locale));
            Set("initKeyboardEvent", FenValue.FromFunction(new FenFunction("initKeyboardEvent", InitKeyboardEvent)));
        }

        private FenValue InitKeyboardEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new FenTypeError("TypeError: Failed to execute 'initKeyboardEvent': 1 argument required, but only 0 present.");
            }

            if (IsDispatching)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            View = NormalizeView(args.Length >= 4 ? args[3] : FenValue.Null);
            Key = args.Length >= 5 ? args[4].ToString() : string.Empty;
            Location = args.Length >= 6 ? CoerceInt32(args[5]) : 0;
            var modifiersList = args.Length >= 7 ? args[6].ToString() : string.Empty;
            CtrlKey = modifiersList.Contains("Control", System.StringComparison.OrdinalIgnoreCase);
            AltKey = modifiersList.Contains("Alt", System.StringComparison.OrdinalIgnoreCase);
            ShiftKey = modifiersList.Contains("Shift", System.StringComparison.OrdinalIgnoreCase);
            MetaKey = modifiersList.Contains("Meta", System.StringComparison.OrdinalIgnoreCase);
            Repeat = args.Length >= 8 && args[7].ToBoolean();
            Locale = args.Length >= 9 ? args[8].ToString() : string.Empty;
            Initialized = true;

            ResetInternalStateForInitialization();
            ReinitializeCoreProperties();
            InitializeUiProperties();
            InitializeKeyboardProperties();
            return FenValue.Undefined;
        }
    }

    public class LegacyCompositionEvent : LegacyUIEvent
    {
        public string Data { get; protected set; } = string.Empty;
        public string Locale { get; protected set; } = string.Empty;

        public LegacyCompositionEvent(string type = "", bool bubbles = false, bool cancelable = false, IExecutionContext context = null, bool initialized = true)
            : base(type, bubbles, cancelable, context, initialized)
        {
            InitializeCompositionProperties();
        }

        protected void InitializeCompositionProperties()
        {
            Set("data", FenValue.FromString(Data));
            Set("locale", FenValue.FromString(Locale));
            Set("initCompositionEvent", FenValue.FromFunction(new FenFunction("initCompositionEvent", InitCompositionEvent)));
        }

        private FenValue InitCompositionEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                throw new FenTypeError("TypeError: Failed to execute 'initCompositionEvent': 1 argument required, but only 0 present.");
            }

            if (IsDispatching)
            {
                return FenValue.Undefined;
            }

            Type = args[0].ToString();
            Bubbles = args.Length >= 2 && args[1].ToBoolean();
            Cancelable = args.Length >= 3 && args[2].ToBoolean();
            View = NormalizeView(args.Length >= 4 ? args[3] : FenValue.Null);
            Data = args.Length >= 5 ? args[4].ToString() : string.Empty;
            Locale = args.Length >= 6 ? args[5].ToString() : string.Empty;
            Initialized = true;

            ResetInternalStateForInitialization();
            ReinitializeCoreProperties();
            InitializeUiProperties();
            InitializeCompositionProperties();
            return FenValue.Undefined;
        }
    }
}
