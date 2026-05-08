// SpecRef: FenBrowser Process Isolation Configuration
// CapabilityId: PROCESS-ISOLATION-CONFIG-01
// Determinism: strict
namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Immutable configuration for renderer process isolation with production defaults.
    /// Thread-safe and validated on construction.
    /// </summary>
    public sealed class ProcessIsolationConfig
    {
        /// <summary>
        /// Maximum number of pre-warmed processes.
        /// Default: 3
        /// </summary>
        public int MaxPoolSize { get; }

        /// <summary>
        /// Target number of warm standby processes.
        /// Default: 2
        /// </summary>
        public int TargetWarmCount { get; }

        /// <summary>
        /// Maximum concurrent process startups.
        /// Default: 2
        /// </summary>
        public int MaxConcurrentStartup { get; }

        /// <summary>
        /// How long to wait for process startup.
        /// Default: 5 seconds
        /// </summary>
        public TimeSpan ProcessStartupTimeout { get; }

        /// <summary>
        /// Maximum lifetime of a process before retirement.
        /// Default: 30 minutes
        /// </summary>
        public TimeSpan ProcessLifetimeMax { get; }

        /// <summary>
        /// Enable automatic pool pre-warming.
        /// Default: true
        /// </summary>
        public bool EnablePreWarm { get; }

        /// <summary>
        /// Health check interval.
        /// Default: 30 seconds
        /// </summary>
        public TimeSpan HealthCheckInterval { get; }

        public ProcessIsolationConfig(
            int maxPoolSize = 3,
            int targetWarmCount = 2,
            int maxConcurrentStartup = 2,
            TimeSpan? processStartupTimeout = null,
            TimeSpan? processLifetimeMax = null,
            bool enablePreWarm = true,
            TimeSpan? healthCheckInterval = null)
        {
            MaxPoolSize = maxPoolSize;
            TargetWarmCount = targetWarmCount;
            MaxConcurrentStartup = maxConcurrentStartup;
            ProcessStartupTimeout = processStartupTimeout ?? TimeSpan.FromSeconds(5);
            ProcessLifetimeMax = processLifetimeMax ?? TimeSpan.FromMinutes(30);
            EnablePreWarm = enablePreWarm;
            HealthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(30);

            Validate();
        }

        public static ProcessIsolationConfig Default => new ProcessIsolationConfig();

        public static ProcessIsolationConfig Development => new ProcessIsolationConfig(
            maxPoolSize: 1,
            targetWarmCount: 0,
            enablePreWarm: false,
            processStartupTimeout: TimeSpan.FromSeconds(10)
        );

        public static ProcessIsolationConfig HighSecurity => new ProcessIsolationConfig(
            maxPoolSize: 5,
            targetWarmCount: 3,
            processLifetimeMax: TimeSpan.FromMinutes(15),
            healthCheckInterval: TimeSpan.FromSeconds(15)
        );

        private void Validate()
        {
            if (MaxPoolSize < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxPoolSize), "Must be at least 1");
            
            if (TargetWarmCount < 0)
                throw new ArgumentOutOfRangeException(nameof(TargetWarmCount), "Must be non-negative");
            
            if (TargetWarmCount > MaxPoolSize)
                throw new ArgumentException("TargetWarmCount cannot exceed MaxPoolSize");
            
            if (MaxConcurrentStartup < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentStartup), "Must be at least 1");
            
            if (ProcessStartupTimeout.TotalMilliseconds < 1000)
                throw new ArgumentOutOfRangeException(nameof(ProcessStartupTimeout), "Must be at least 1 second");
            
            if (ProcessLifetimeMax.TotalMinutes < 1)
                throw new ArgumentOutOfRangeException(nameof(ProcessLifetimeMax), "Must be at least 1 minute");
        }
    }
}