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
        public RendererInputEventType Type { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public int Button { get; init; }
        public bool EmitClick { get; init; }
        public float DeltaX { get; init; }
        public float DeltaY { get; init; }
        public string Key { get; init; }
        public string Text { get; init; }
        public bool Ctrl { get; init; }
        public bool Shift { get; init; }
        public bool Alt { get; init; }
    }
}
