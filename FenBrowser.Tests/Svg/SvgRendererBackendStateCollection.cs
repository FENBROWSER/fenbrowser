using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// Single shared collection for every test class that touches process-wide
    /// SVG/ImageLoader state.
    /// <para>
    /// The state in question is genuinely global: the
    /// <see cref="SvgRendererConfiguration"/> backend selection and its
    /// last-parse reason code (both mutated by <c>TryParse</c>,
    /// <c>DescribeValue</c>, <c>DescribeConfiguration</c> and
    /// <c>ReloadFromEnvironment</c>), the <c>FEN_SVG_RENDERER</c> environment
    /// variable they are seeded from, and the shared ImageLoader image cache,
    /// lazy-load registry and pending-load table.
    /// </para>
    /// <para>
    /// The collection is declared non-parallelizable, so xUnit serializes it
    /// against every other collection in the assembly, including the
    /// parallelizable ones. Any class that mutates or reads one of those globals
    /// must carry <c>[Collection(SvgRendererBackendStateCollection.Name)]</c>;
    /// a class that carries no collection attribute is then guaranteed never to
    /// observe another test's half-applied backend, environment value, or cache.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SvgRendererBackendStateCollection
        : ICollectionFixture<SvgRendererBackendStateFixture>
    {
        public const string Name = "SVG renderer backend state";
    }

    /// <summary>
    /// Snapshots the process-global SVG/ImageLoader state before the collection
    /// runs and restores it afterwards. This makes the collection a closed
    /// boundary: a class that leaves a mutated environment variable, backend
    /// selection, or cached bitmap behind cannot leak that state into the rest
    /// of the assembly, and state left behind by an earlier collection cannot be
    /// observed by the first SVG test that runs.
    /// </summary>
    public sealed class SvgRendererBackendStateFixture : IDisposable
    {
        private readonly string? _backendEnvironmentValue;
        private readonly SvgRendererBackend _backend;

        public SvgRendererBackendStateFixture()
        {
            _backendEnvironmentValue = Environment.GetEnvironmentVariable(
                SvgRendererConfiguration.EnvironmentVariable);
            _backend = SvgRendererConfiguration.Backend;

            ImageLoader.ClearCache();
            ImageLoader.ClearLazyRegistry();
        }

        public void Dispose()
        {
            ImageLoader.ClearLazyRegistry();
            ImageLoader.ClearCache();

            Environment.SetEnvironmentVariable(
                SvgRendererConfiguration.EnvironmentVariable, _backendEnvironmentValue);

            // The reload is what re-derives Backend and LastParseReasonCode from
            // the restored environment value; LastParseReasonCode has no public
            // setter, so this is the only way to put it back.
            SvgRendererConfiguration.ReloadFromEnvironment();
            if (SvgRendererConfiguration.Backend != _backend)
            {
                SvgRendererConfiguration.Backend = _backend;
            }
        }
    }

    /// <summary>
    /// Guards the wiring itself, because the serialization is only as good as
    /// the collection definition: a dropped <c>DisableParallelization</c> or a
    /// second competing SVG collection would silently re-open the races this
    /// collection exists to close.
    /// </summary>
    [Collection(SvgRendererBackendStateCollection.Name)]
    public sealed class SvgRendererBackendStateCollectionTests
    {
        private const string SvgNamespace = "FenBrowser.Tests.Svg";

        [Fact]
        public void Collection_IsDeclaredNonParallelizableUnderAStableName()
        {
            var definition = typeof(SvgRendererBackendStateCollection)
                .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
                .Cast<CollectionDefinitionAttribute>()
                .Single();

            Assert.True(
                definition.DisableParallelization,
                "the SVG backend-state collection must stay non-parallelizable or it "
                + "can race another collection's backend, environment, or cache state");
            Assert.Equal("SVG renderer backend state", SvgRendererBackendStateCollection.Name);
        }

        [Fact]
        public void EverySvgClassThatJoinsACollection_JoinsThisOneCollection()
        {
            string[] competing = typeof(SvgRendererBackendStateCollection)
                .Assembly
                .GetTypes()
                .Where(type => string.Equals(type.Namespace, SvgNamespace, StringComparison.Ordinal))
                .SelectMany(type => CollectionNamesOf(type)
                    .Select(name => $"{type.FullName} -> '{name}'"))
                .Where(entry => !entry.EndsWith(
                    $"-> '{SvgRendererBackendStateCollection.Name}'",
                    StringComparison.Ordinal))
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                competing.Length == 0,
                "SVG test classes must not split the shared backend state across "
                + "competing collections: " + string.Join("; ", competing));
        }

        // CollectionAttribute stores its name in the constructor argument only, so
        // the metadata (not an instantiated attribute) is the readable source.
        private static IEnumerable<string> CollectionNamesOf(Type type) =>
            type.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(CollectionAttribute))
                .Select(data => (string)data.ConstructorArguments[0].Value!);

        [Fact]
        public void Fixture_RestoresBackendEnvironmentStateAndClearsSharedCaches()
        {
            string? originalValue = Environment.GetEnvironmentVariable(
                SvgRendererConfiguration.EnvironmentVariable);

            // The last-parse reason code is derived from the environment value, and
            // an earlier test in this collection may have left a code behind that
            // came from a bare TryParse call. Baseline it from the pristine
            // environment so the assertions below compare like with like.
            SvgRendererConfiguration.ReloadFromEnvironment();
            string? originalReasonCode = SvgRendererConfiguration.LastParseReasonCode;
            SvgRendererBackend originalBackend = SvgRendererConfiguration.Backend;

            var fixture = new SvgRendererBackendStateFixture();
            try
            {
                Environment.SetEnvironmentVariable(
                    SvgRendererConfiguration.EnvironmentVariable,
                    SvgRendererConfiguration.DeprecatedLegacyValue);
                SvgRendererConfiguration.ReloadFromEnvironment();
                PolluteSharedCaches();

                Assert.Equal(
                    SvgRendererConfiguration.DeprecatedAliasReasonCode,
                    SvgRendererConfiguration.LastParseReasonCode);
                Assert.True(ImageLoader.CacheCount > 0, "image cache was not polluted");
                Assert.True(ImageLoader.LazyRegistryCount > 0, "lazy registry was not polluted");

                fixture.Dispose();

                Assert.Equal(
                    originalValue,
                    Environment.GetEnvironmentVariable(
                        SvgRendererConfiguration.EnvironmentVariable));
                Assert.Equal(originalBackend, SvgRendererConfiguration.Backend);
                Assert.Equal(
                    originalReasonCode, SvgRendererConfiguration.LastParseReasonCode);
                Assert.Equal(0, ImageLoader.CacheCount);
                Assert.Equal(0, ImageLoader.LazyRegistryCount);
            }
            finally
            {
                fixture.Dispose();
                Environment.SetEnvironmentVariable(
                    SvgRendererConfiguration.EnvironmentVariable, originalValue);
                SvgRendererConfiguration.ReloadFromEnvironment();
                if (SvgRendererConfiguration.Backend != originalBackend)
                {
                    SvgRendererConfiguration.Backend = originalBackend;
                }
            }
        }

        private static void PolluteSharedCaches()
        {
            const string svg = "<svg width='8' height='8'><rect width='8' height='8' fill='red'/></svg>";
            Assert.NotNull(ImageLoader.GetImage(
                "data:image/svg+xml;base64," + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(svg))));

            var context = new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = Guid.NewGuid().ToString("N")
            };
            using (ImageLoader.EnterRequestContext(context))
            {
                // No UpdateViewport call, so the entry stays lazy and no load starts.
                Assert.Null(ImageLoader.GetImage(
                    "https://example.test/assets/lazy-fixture.svg",
                    isLazy: true,
                    elementBounds: new SKRect(1000, 1000, 1100, 1100),
                    targetWidth: 8,
                    targetHeight: 8));
            }
        }
    }
}
