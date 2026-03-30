using System;

namespace FenBrowser.Host.ProcessIsolation
{
    public enum RendererInputEventType
    {
        MouseDown,
        MouseUp,
        MouseMove,
        MouseWheel,
        KeyDown,
        TextInput
    }

    public sealed class RendererInputEvent
    {
        private float _x;
        private float _y;
        private int _button;
        private float _deltaX;
        private float _deltaY;
        private string _key = string.Empty;
        private string _text = string.Empty;

        public RendererInputEventType Type { get; init; }
        public float X
        {
            get => _x;
            init => _x = NormalizeFloat(value);
        }

        public float Y
        {
            get => _y;
            init => _y = NormalizeFloat(value);
        }

        public int Button
        {
            get => _button;
            init => _button = Math.Max(0, value);
        }

        public bool EmitClick { get; init; }
        public float DeltaX
        {
            get => _deltaX;
            init => _deltaX = NormalizeFloat(value);
        }

        public float DeltaY
        {
            get => _deltaY;
            init => _deltaY = NormalizeFloat(value);
        }

        public string Key
        {
            get => _key;
            init => _key = NormalizeText(value);
        }

        public string Text
        {
            get => _text;
            init => _text = NormalizeText(value);
        }

        public bool Ctrl { get; init; }
        public bool Shift { get; init; }
        public bool Alt { get; init; }

        public bool IsPointerEvent =>
            Type == RendererInputEventType.MouseDown ||
            Type == RendererInputEventType.MouseUp ||
            Type == RendererInputEventType.MouseMove ||
            Type == RendererInputEventType.MouseWheel;

        public bool IsKeyboardEvent =>
            Type == RendererInputEventType.KeyDown ||
            Type == RendererInputEventType.TextInput;

        public bool IsMeaningful =>
            Type switch
            {
                RendererInputEventType.KeyDown => Key.Length > 0,
                RendererInputEventType.TextInput => Text.Length > 0,
                _ => true
            };

        public bool ShouldEmitClick => Type == RendererInputEventType.MouseUp && Button == 0 && EmitClick;

        public override string ToString()
        {
            return $"{Type} @ ({X}, {Y}) button={Button} ctrl={Ctrl} shift={Shift} alt={Alt}";
        }

        private static float NormalizeFloat(float value)
        {
            return float.IsFinite(value) ? value : 0f;
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
