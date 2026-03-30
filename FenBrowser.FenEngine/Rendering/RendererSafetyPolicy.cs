namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Runtime safety budget policy for render-thread watchdog checks.
    /// </summary>
    public sealed class RendererSafetyPolicy
    {
        private static readonly RendererSafetyPolicy DefaultInstance = new RendererSafetyPolicy();
        private double _maxFrameBudgetMs = 16.67;
        private double _maxPaintStageMs = 12.0;
        private double _maxRasterStageMs = 12.0;

        public static RendererSafetyPolicy Default => DefaultInstance.Clone();

        public bool EnableWatchdog { get; set; } = true;
        public double MaxFrameBudgetMs
        {
            get => _maxFrameBudgetMs;
            set => _maxFrameBudgetMs = NormalizeBudget(value, 16.67);
        }

        public double MaxPaintStageMs
        {
            get => _maxPaintStageMs;
            set => _maxPaintStageMs = NormalizeBudget(value, 12.0);
        }

        public double MaxRasterStageMs
        {
            get => _maxRasterStageMs;
            set => _maxRasterStageMs = NormalizeBudget(value, 12.0);
        }

        /// <summary>
        /// If true, skip expensive rasterization when budget is already exceeded before raster starts.
        /// </summary>
        public bool SkipRasterWhenOverBudget { get; set; } = true;

        public RendererSafetyPolicy Clone()
        {
            return new RendererSafetyPolicy
            {
                EnableWatchdog = EnableWatchdog,
                MaxFrameBudgetMs = MaxFrameBudgetMs,
                MaxPaintStageMs = MaxPaintStageMs,
                MaxRasterStageMs = MaxRasterStageMs,
                SkipRasterWhenOverBudget = SkipRasterWhenOverBudget
            };
        }

        public override string ToString()
        {
            return $"watchdog={EnableWatchdog}, frame={MaxFrameBudgetMs:0.##}ms, paint={MaxPaintStageMs:0.##}ms, raster={MaxRasterStageMs:0.##}ms, skipRaster={SkipRasterWhenOverBudget}";
        }

        private static double NormalizeBudget(double value, double fallback)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
        }
    }
}
