using System;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// Indicates which subsystems need invalidation after mutation.
    /// </summary>
    [Flags]
    public enum InvalidationKind
    {
        None = 0,
        Style = 1,      // CSS recalc needed
        Layout = 2,     // Layout pass needed
        Paint = 4       // Paint only (no layout change)
    }
}
