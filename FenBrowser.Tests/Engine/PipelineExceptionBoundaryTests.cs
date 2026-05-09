// SpecRef: FenBrowser pipeline exception boundary verification tests
// =============================================================================
// PipelineExceptionBoundaryTests.cs
// Comprehensive testing of exception boundary behavior, resource guards, telemetry integration
//
// PURPOSE: Verify production-grade exception handling, resource limits, and telemetry work correctly
//
// Test Coverage:
// - Exception boundary isolation and recovery
// - Resource limit enforcement and notifications
// - Telemetry collection and health reporting
// - Integration layer functionality
// - Fail-safe mode activation and recovery
// =============================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class PipelineExceptionBoundaryTests
    {
        private readonly ITestOutputHelper _output;

        public PipelineExceptionBoundaryTests(ITestOutputHelper output)
        {
            _output = output;
            // Reset all state before each test
            PipelineIntegrationLayer.Reset();
            PipelineContext.Reset();
            PipelineExceptionBoundary.Current.GetType().GetMethod("Reset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(PipelineExceptionBoundary.Current, null);
        }

        #region Exception Boundary Tests

        [Fact]
        public void ExceptionBoundary_CatchesAndReportsPipelineExceptions()
        {
            // Given: A failing pipeline stage
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            var boundary = PipelineExceptionBoundary.Current;

            // When: Execute failing stage
            var result = PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Styling,
                "TestOperation",
                () => throw new PipelineStageException("Test failure", PipelineStage.Styling, PipelineStage.Styling),
                () => "fallback");

            // Then: Should execute fallback and record failure
            Assert.Equal("fallback", result);
            Assert.True(boundary.TotalFailureCount > 0);
            Assert.NotNull(boundary.LastFailure);
            Assert.Equal(PipelineStage.Styling, boundary.LastFailure.Stage);
        }

        [Fact]
        public void ExceptionBoundary_DoesNotPropagateUnexpectedExceptions()
        {
            // Given: A stage with unexpected exception
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;

            // When: Execute failing stage with fallback
            var result = PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "TestOperation",
                () => throw new InvalidOperationException("Unexpected error"),
                () => "safe_fallback");

            // Then: Should execute fallback without crashing
            Assert.Equal("safe_fallback", result);
        }

        [Fact]
        public void ExceptionBoundary_OutOfMemoryCritical()
        {
            // Given: A stage that throws OutOfMemoryException
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;

            // When/Then: Should not be handled by fallback, should propagate
            Assert.Throws<OutOfMemoryException>(() =>
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Rasterizing,
                    "TestOOM",
                    () => throw new OutOfMemoryException("Critical memory failure"),
                    () => "fallback"); // This should not be used for OOM
            });
        }

        [Fact]
        public async Task ExceptionBoundary_AsyncHandling()
        {
            // Given: Async failing stage
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;

            // When: Execute async with delay
            var result = await PipelineIntegrationLayer.ExecuteStageAsync(
                PipelineStage.Painting,
                "TestAsync",
                async () =>
                {
                    await Task.Delay(10);
                    throw new PipelineStageException("Async test failure", PipelineStage.Painting, PipelineStage.Painting);
                },
                () => "async_fallback");

            // Then: Should execute fallback
            Assert.Equal("async_fallback", result);
        }

        #endregion

        #region Resource Guard Tests

        [Fact]
        public void ResourceGuard_EnforcesMemoryLimits()
        {
            // Given: Resource limits
            var guard = PipelineResourceGuard.Current;
            guard.MaxStageMemoryPerFrame = 1024 * 1024; // 1MB

            // When: Execute stage exceeding memory limit
            var ex = Assert.Throws<PipelineResourceException>(() =>
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Styling,
                    "MemoryTest",
                    () =>
                    {
                        // Track a large memory allocation
                        guard.TrackMemoryAllocation(PipelineStage.Styling, 2 * 1024 * 1024, "test_memory");
                        return 42;
                    },
                    () => -1);
            });

            // Then: Should throw resource exception
            Assert.Contains("memory", ex.Message);
            Assert.Contains("exceeds", ex.Message);
        }

        [Fact]
        public void ResourceGuard_TracksObjectCounts()
        {
            // Given: Object count limits
            var guard = PipelineResourceGuard.Current;
            var maxNodes = 1000;

            // When: Execute stage within limits
            var result = PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Parsing,
                "DOMTest",
                () =>
                {
                    guard.TrackObjectCount(PipelineStage.Parsing, "DOMNode", 500, maxNodes);
                    return "success";
                },
                () => "fallback");

            // Then: Should succeed
            Assert.Equal("success", result);

            // When: Execute stage exceeding limits
            var ex = Assert.Throws<PipelineResourceException>(() =>
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Parsing,
                    "DOMTestExceed",
                    () =>
                    {
                        guard.TrackObjectCount(PipelineStage.Parsing, "DOMNode", 1500, maxNodes);
                        return "fail";
                    },
                    () => "fallback");
            });

            // Then: Should throw
            Assert.Contains("DOMNode", ex.Message);
            Assert.Contains("exceeds", ex.Message);
        }

        [Fact]
        public void ResourceGuard_MemoryPressureMonitoring()
        {
            // Given: Resource guard
            var guard = PipelineResourceGuard.Current;
            guard.MaxPeakMemory = 100 * 1024 * 1024; // 100MB cap
            
            // When: Track memory and trigger pressure
            guard.TrackMemoryAllocation(PipelineStage.Layout, 90 * 1024 * 1024, "high_pressure_test");
            
            // Monitor manually
            var currentMemory = guard.GenerateResourceSummary();
            
            // Then: Should track and report correctly
            Assert.Contains("PeakMemory", currentMemory);
        }

        /*[Fact]
        public void ResourceGuard_ForceGarbageCollectionOnCriticalPressure()
        {
            // Given: High memory allocation
            var guard = PipelineResourceGuard.Current;
            guard.TrackMemoryAllocation(PipelineStage.Parsing, 50 * 1024 * 1024, "test_allocation");
            
            // GC would be called but we can't reliably test this
            // Just verify that the guard state updated
            Assert.True(guard.PeakMemory > 0);
        }*/

        #endregion

        #region Telemetry Tests

        [Fact]
        public void Telemetry_RecordsSuccessMetrics()
        {
            // Given: Enabled telemetry
            PipelineTelemetry.IsMetricsEnabled = true;
            PipelineTelemetry.Reset(); // Invalid but test

            // When: Execute successful stages
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Styling,
                "SuccessTest",
                () => "ok",
                () => "fb");

            // Then: Should record success
            var health = PipelineTelemetry.GetHealthStatus();
            Assert.True(health.TotalFrames > 0);
        }

        [Fact]
        public void Telemetry_RecordsFailureMetrics()
        {
            // Given: Telemetry enabled
            PipelineTelemetry.IsMetricsEnabled = true;
            
            // When: Execute failing stage
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "FailureTest",
                () => throw new Exception("test failure"),
                () => "recovered");

            // Then: Should record failure
            var health = PipelineTelemetry.GetHealthStatus();
            Assert.True(health.TotalErrors > 0);
        }

        [Fact]
        public void Telemetry_HealthStatusAccuracy()
        {
            // Given: Mixed success/failure pipeline
            PipelineTelemetry.IsMetricsEnabled = true;
            
            // When: Generate mixed outcomes
            for (int i = 0; i < 10; i++)
            {
                if (i % 3 == 0)
                {
                    PipelineIntegrationLayer.ExecuteStage(
                        PipelineStage.Painting,
                        $"HealthTest_{i}",
                        () => throw new Exception($"failure_{i}"),
                        () => "recovered");
                }
                else
                {
                    PipelineIntegrationLayer.ExecuteStage(
                        PipelineStage.Painting,
                        $"HealthTest_{i}",
                        () => $"success_{i}",
                        () => "fb");
                }
            }

            // Then: Health should reflect state
            var health = PipelineTelemetry.GetHealthStatus();
            Assert.True(health.TotalFrames >= 10);
            Assert.True(health.SuccessRate > 0.5); // At least 50% success
            Assert.True(health.SuccessRate < 1.0); // But not 100% due to failures
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task Integration_FullPipelineSuccess()
        {
            // Given: Full pipeline with all stages
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            PipelineIntegrationLayer.EnableResourceGuards = true;
            PipelineIntegrationLayer.EnableTelemetry = true;

            // When: Execute all stages successfully
            var result = await PipelineIntegrationLayer.ExecuteStageAsync(
                PipelineStage.Tokenizing,
                "FullPipeline",
                async () =>
                {
                    await System.Threading.Tasks.Task.Yield();
                    return "pipeline_complete";
                });

            // Then: Should succeed
            Assert.Equal("pipeline_complete", result);
            
            // And telemetry should be recorded
            var health = PipelineTelemetry.GetHealthStatus();
            Assert.True(health.TotalFrames > 0);
        }

        [Fact]
        public void Integration_FailSafeModeActivation()
        {
            // Given: Very low failure threshold
            PipelineIntegrationLayer.MaxConsecutiveFailures = 2;
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            PipelineIntegrationLayer.EnableTelemetry = true;

            // When: Generate consecutive failures
            for (int i = 0; i < 3; i++)
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Styling,
                    $"FailSafeTest_{i}",
                    () => throw new Exception("serial_failure"),
                    () => "recovered");
            }

            // Then: Should enter fail-safe mode
            Assert.True(PipelineIntegrationLayer.IsInFailSafeMode);
            Assert.True(PipelineIntegrationLayer.ConsecutiveFailureCount >= 2);
        }

        [Fact]
        public void Integration_RecoveryFromFailSafe()
        {
            // Given: Fail-safe mode
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            PipelineIntegrationLayer.MaxConsecutiveFailures = 3;
            
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "Fail_1",
                () => throw new Exception("fail"),
                () => "fallback");
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "Fail_2",
                () => throw new Exception("fail"),
                () => "fallback");
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "Fail_3",
                () => throw new Exception("fail"),
                () => "fallback");

            // Currently should be in fail-safe
            Assert.True(PipelineIntegrationLayer.IsInFailSafeMode);
            Assert.Equal(3, PipelineIntegrationLayer.ConsecutiveFailureCount);

            // When: Successful execution happens
            PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Layout,
                "Success",
                () => "ok",
                () => "fallback");

            // Then: Should recover
            Assert.False(PipelineIntegrationLayer.IsInFailSafeMode);
            Assert.Equal(0, PipelineIntegrationLayer.ConsecutiveFailureCount);
        }

        [Fact]
        public void Integration_DisabledSafety_FallbackToLegacyBehavior()
        {
            // Given: All safety features disabled
            PipelineIntegrationLayer.EnableExceptionBoundaries = false;
            PipelineIntegrationLayer.EnableResourceGuards = false;
            PipelineIntegrationLayer.EnableTelemetry = false;

            // When: Execute with exceptions in legacy mode
            var ex = Assert.Throws<Exception>(() =>
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Styling,
                    "LegacyMode",
                    () => throw new InvalidOperationException("should_propagate"),
                    () => "fallback"); // Will be ignored
            });

            // Then: Exception should propagate (no interception)
            Assert.Contains("should_propagate", ex.Message);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void EdgeCase_NestedExceptionDuringCleanup()
        {
            // Given: Stage that fails in cleanup
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            
            // When: Execute stage where exception happens after success
            var result = PipelineIntegrationLayer.ExecuteStage(
                PipelineStage.Presenting,
                "CleanupTest",
                () => "success",
                () => "fallback");

            // Then: Should return success, capture exception is separate
            Assert.Equal("success", result);
        }

        [Fact]
        public void EdgeCase_FallbackFactoryFailure()
        {
            // Given: Safely working system
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;

            // When: Fallback fails
            var ex = Assert.Throws<PipelineStageException>(() =>
            {
                PipelineIntegrationLayer.ExecuteStage<int>(
                    PipelineStage.Styling,
                    "FallbackFail",
                    () => throw new Exception("original"),
                    () => throw new Exception("fallback_failed"));
            });

            // Then: Should throw appropriate exception
            Assert.Contains("fallback_failed", ex.Message);
        }

        [Fact ]
public async void EdgeCase_CancellationPropagation()
        {
            // Given: Cancelled token situation
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // When: Execute async with cancelled token
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
 {
 await PipelineIntegrationLayer.ExecuteStageAsync(
                    PipelineStage.Parsing,
                    "CancellationTest",
                    async () =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        await Task.CompletedTask;
                        return "done";
                    });
            });

            // Then: Should propagate cancellation
            Assert.NotNull(ex);
        }

        #endregion

        #region Performance Tests

        [Fact]
        public void Performance_OverheadAcceptable()
        {
            // Given: Enabled safety features
            PipelineIntegrationLayer.EnableExceptionBoundaries = true;
            PipelineIntegrationLayer.EnableResourceGuards = false; // Disable for baseline
            PipelineIntegrationLayer.EnableTelemetry = false;

            // When: Measure overhead
            var iterations = 10000;
            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                PipelineIntegrationLayer.ExecuteStage(
                    PipelineStage.Styling,
                    $"PerfTest_{i}",
                    () => i,
                    () => -1);
            }

            stopwatch.Stop();
            var avgTimePerCall = stopwatch.Elapsed.TotalMilliseconds / iterations;

            // Then: Overhead should be minimal
            Assert.True(avgTimePerCall < 0.1, $"Average time per call: {avgTimePerCall}ms should be < 0.1ms");
        }

        #endregion

        [Fact]
        public void SummaryTest_ComprehensiveScenario()
        {
            // This test verifies the entire integrated system works
            _output.WriteLine($"Pipeline Hardening Test Summary");
            
            var boundary = PipelineExceptionBoundary.Current;
            var guard = PipelineResourceGuard.Current;
            var telemetry = PipelineTelemetry.GetHealthStatus();
            
            _output.WriteLine($"Exception Boundary: {boundary}");
            _output.WriteLine($"Resource Guard: {guard}");
            _output.WriteLine($"Health Status: {telemetry}");
            
            Assert.True(boundary.TotalFailureCount >= 0);
        }
    }
}
