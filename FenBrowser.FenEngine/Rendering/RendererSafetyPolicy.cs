namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Runtime safety budget policy for render-thread watchdog checks.
    /// </summary>
    public sealed class RendererSafetyPolicy
    {
        public static RendererSafetyPolicy Default => new RendererSafetyPolicy();

        public bool EnableWatchdog { get; set; } = true;
        public double MaxFrameBudgetMs { get; set; } = 16.67;
        public double MaxPaintStageMs { get; set; } = 12.0;
        public double MaxRasterStageMs { get; set; } = 12.0;

        /// <summary>
        /// If true, skip expensive rasterization when budget is already exceeded before raster starts.
        /// </summary>
        public bool SkipRasterWhenOverBudget { get; set; } = true;
    }
}
