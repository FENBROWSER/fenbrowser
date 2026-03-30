using System;
using System.IO;
using System.Net.Security;
using FenBrowser.Core;
using FenBrowser.Core.Cache;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class ThinContractTests
    {
        [Fact]
        public void CacheKey_NormalizesWhitespace_AndDefaultsPartition()
        {
            var key = new CacheKey("  ", " https://fenbrowser.dev/resource.js ");
            var same = new CacheKey("default", "https://fenbrowser.dev/resource.js");

            Assert.Equal("default", key.PartitionKey);
            Assert.Equal("https://fenbrowser.dev/resource.js", key.Url);
            Assert.False(key.IsEmpty);
            Assert.True(key == same);
        }

        [Fact]
        public void ShardedCache_TracksHitsMissesEvictions_AndSupportsRemove()
        {
            var cache = new ShardedCache<string>(2);

            cache.Put("p1", "url1", "one");
            cache.Put("p1", "url2", "two");
            Assert.True(cache.TryGet("p1", "url1", out var first));
            Assert.Equal("one", first);

            Assert.False(cache.TryGet("p1", "missing", out _));

            cache.Put("p2", "url3", "three");

            Assert.Equal(1, cache.HitCount);
            Assert.Equal(1, cache.MissCount);
            Assert.Equal(1, cache.EvictionCount);
            Assert.Equal(2, cache.Capacity);
            Assert.False(cache.Contains("p1", "url2"));
            Assert.True(cache.TryRemove("p2", "url3", out var removed));
            Assert.Equal("three", removed);
            Assert.False(cache.Contains("p2", "url3"));
        }

        [Fact]
        public void CertificateInfo_NormalizesValues_AndExposesValidationState()
        {
            var certificate = new CertificateInfo
            {
                Subject = "  CN=fenbrowser.dev  ",
                Issuer = "  Fen CA  ",
                Thumbprint = "aa:bb cc",
                SubjectAlternativeNames = new[] { " fenbrowser.dev ", "", "FenBrowser.dev", "www.fenbrowser.dev" },
                NotBefore = DateTime.Now.AddMinutes(-5),
                NotAfter = DateTime.Now.AddMinutes(5)
            };

            Assert.Equal("CN=fenbrowser.dev", certificate.Subject);
            Assert.Equal("Fen CA", certificate.Issuer);
            Assert.Equal("AABBCC", certificate.Thumbprint);
            Assert.Equal(2, certificate.SubjectAlternativeNames.Count);
            Assert.True(certificate.IsDateRangeValid);
            Assert.True(certificate.IsCurrentlyValid);
            Assert.False(certificate.HasPolicyErrors);

            certificate.PolicyErrors = SslPolicyErrors.RemoteCertificateNameMismatch;

            Assert.True(certificate.HasPolicyErrors);
            Assert.Contains("does not match", certificate.ErrorDescription, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CornerRadius_ClampNonNegative_PreservesEqualitySurface()
        {
            var radius = new CornerRadius(-2, 4, -6, 8);
            var clamped = radius.ClampNonNegative();

            Assert.True(radius.HasNegative);
            Assert.Equal(new CornerRadius(0, 4, 0, 8), clamped);
            Assert.False(clamped.HasNegative);
            Assert.False(clamped.IsUniform);
            Assert.Equal(clamped, new CornerRadius(0, 4, 0, 8));
        }

        [Fact]
        public void Thickness_ReportsState_AndClampsNonNegative()
        {
            var thickness = new Thickness(-1, 2, -3, 4);
            var clamped = thickness.ClampNonNegative();

            Assert.True(thickness.HasNegative);
            Assert.False(thickness.IsUniform);
            Assert.Equal(new Thickness(0, 2, 0, 4), clamped);
            Assert.False(clamped.HasNegative);
            Assert.True(Thickness.Empty.IsZero);
        }

        [Fact]
        public void CssCornerRadius_ClampNonNegative_PreservesPercentSemantics()
        {
            var radius = new CssCornerRadius(
                new CssLength(-2),
                new CssLength(10, isPercent: true),
                new CssLength(4),
                new CssLength(-5, isPercent: true));

            var clamped = radius.ClampNonNegative();

            Assert.True(radius.HasNegative);
            Assert.True(radius.HasPercent);
            Assert.Equal(new CssLength(0), clamped.TopLeft);
            Assert.Equal(new CssLength(10, isPercent: true), clamped.TopRight);
            Assert.Equal(new CssLength(4), clamped.BottomRight);
            Assert.Equal(new CssLength(0, isPercent: true), clamped.BottomLeft);
            Assert.True(clamped.HasPercent);
            Assert.False(clamped.HasNegative);
        }

        [Fact]
        public void ConsoleLogger_NormalizesMessages_AndPrefixesWithLevels()
        {
            var logger = new ConsoleLogger();
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);

                logger.Log(LogLevel.Warn, "  warning text  ");
                logger.LogError("  failure  ", new InvalidOperationException("  broken  "));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string output = writer.ToString();
            Assert.Contains("[Warn] warning text", output, StringComparison.Ordinal);
            Assert.Contains("[Error] failure: InvalidOperationException: broken", output, StringComparison.Ordinal);
        }
    }
}
