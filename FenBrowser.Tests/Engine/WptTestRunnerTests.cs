using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using FenBrowser.Conformance;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class WptTestRunnerTests
    {
        [Fact]
        public async Task RunSingleTestAsync_WithoutNavigator_FailsFast()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}.html");
            File.WriteAllText(tempFile, "<!doctype html><html><body>test</body></html>");

            try
            {
                var runner = new WPTTestRunner(Path.GetDirectoryName(tempFile), navigator: null, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.False(result.Success);
                Assert.Equal("no-navigator", result.CompletionSignal);
                Assert.Contains("Navigator delegate is required", result.Error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_ManualTest_IsSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempDir, "manual"));
            string tempFile = Path.Combine(tempDir, "manual", "sample-manual.html");
            File.WriteAllText(tempFile, "<!doctype html><html><body>manual</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.True(result.Success);
                Assert.Equal("manual-skipped", result.CompletionSignal);
                Assert.Equal(0, result.TotalCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_CrashTestWithoutHarness_IsTreatedAsCrashOnly()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempDir, "accessibility", "crashtests"));
            string tempFile = Path.Combine(tempDir, "accessibility", "crashtests", "sample-crash.html");
            File.WriteAllText(tempFile, "<!doctype html><html><body><script>window.x = 1;</script></body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.True(result.Success);
                Assert.Equal("crashtest-executed", result.CompletionSignal);
                Assert.Equal(0, result.TotalCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_SynchronousHarnessTests_RunAtRegistrationTime()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempDir, "resources"));
            string tempFile = Path.Combine(tempDir, "feature", "sync-loop-capture.html");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, """
<!doctype html>
<script src="/resources/testharness.js"></script>
<script src="/resources/testharnessreport.js"></script>
<script>
  var values = [0, 1, 2];
  var i;
  for (i = 0; i < values.length; i++) {
    test(() => {
      assert_equals(values[i], i, "sync test bodies must observe the loop index at registration time");
    }, "loop-" + i);
  }
</script>
""");

            try
            {
                var navigator = new HeadlessNavigator(tempDir, 2_000);
                var runner = new WPTTestRunner(tempDir, navigator.GetNavigatorDelegate(), timeoutMs: 2_000);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.True(result.Success);
                Assert.Equal(3, result.TotalCount);
                Assert.Equal(3, result.PassCount);
                Assert.Equal(0, result.FailCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_MismatchReftestWithScript_IsSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "highlight-text-dynamic.html");
            File.WriteAllText(tempFile, """
<!doctype html>
<link rel="mismatch" href="highlight-text-dynamic-notref.html">
<script>
  throw new Error('should not execute for reftest skip classification');
</script>
""");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.True(result.Success);
                Assert.Equal("reftest-skipped", result.CompletionSignal);
                Assert.Equal(0, result.TotalCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void DiscoverAllTests_SkipsResourceAndSupportPages()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(tempDir, "resources"));
            Directory.CreateDirectory(Path.Combine(tempDir, "feature", "support"));
            Directory.CreateDirectory(Path.Combine(tempDir, "acid", "acid3"));
            Directory.CreateDirectory(Path.Combine(tempDir, "feature", "manual-variants"));

            string realTest = Path.Combine(tempDir, "feature", "real-test.html");
            string resourceHelper = Path.Combine(tempDir, "resources", "helper.html");
            string supportHelper = Path.Combine(tempDir, "feature", "support", "helper.html");
            string acidHarness = Path.Combine(tempDir, "acid", "acid3", "numbered-tests.html");
            string referencePage = Path.Combine(tempDir, "feature", "manual-variants", "widget-ref.html");
            File.WriteAllText(realTest, "<!doctype html><html><body>real</body></html>");
            File.WriteAllText(resourceHelper, "<!doctype html><html><body>resource</body></html>");
            File.WriteAllText(supportHelper, "<!doctype html><html><body>support</body></html>");
            File.WriteAllText(acidHarness, "<!doctype html><html><body>acid</body></html>");
            File.WriteAllText(referencePage, "<!doctype html><html><body>reference</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var discovered = runner.DiscoverAllTests();

                Assert.Contains(realTest, discovered);
                Assert.DoesNotContain(resourceHelper, discovered);
                Assert.DoesNotContain(supportHelper, discovered);
                Assert.DoesNotContain(acidHarness, discovered);
                Assert.DoesNotContain(referencePage, discovered);
                Assert.Single(discovered.Where(x => string.Equals(x, realTest, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_ManualVariants_AreSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "icons-member-cors-manual.sub.html");
            File.WriteAllText(tempFile, "<!doctype html><html><body>manual variant</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.True(result.Success);
                Assert.Equal("manual-skipped", result.CompletionSignal);
                Assert.Equal(0, result.TotalCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_ChunkSupportDocuments_AreHeadlessCompatSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            string supportFile = Path.Combine(tempDir, "common", "dispatcher", "executor.html");
            string layoutCompatFile = Path.Combine(tempDir, "compat", "webkit-box-item-shrink-001.html");
            Directory.CreateDirectory(Path.GetDirectoryName(supportFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(layoutCompatFile)!);
            File.WriteAllText(supportFile, "<!doctype html><html><body>support</body></html>");
            File.WriteAllText(layoutCompatFile, "<!doctype html><html><body>compat</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var supportResult = await runner.RunSingleTestAsync(supportFile);
                var layoutResult = await runner.RunSingleTestAsync(layoutCompatFile);

                Assert.True(supportResult.Success);
                Assert.Contains(supportResult.CompletionSignal, new[] { "headless-compat-skipped", "reftest-skipped" });
                Assert.True(layoutResult.Success);
                Assert.Contains(layoutResult.CompletionSignal, new[] { "headless-compat-skipped", "reftest-skipped" });
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_CssFontsParsingAndMathCompatDocuments_AreHeadlessCompatSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            string parsingFile = Path.Combine(tempDir, "css", "css-fonts", "parsing", "font-family-valid.html");
            string mathFile = Path.Combine(tempDir, "css", "css-fonts", "math-script-level-and-math-style", "math-style-001.tentative.html");
            string paletteFile = Path.Combine(tempDir, "css", "css-fonts", "palette-mix-computed.html");
            Directory.CreateDirectory(Path.GetDirectoryName(parsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(mathFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(paletteFile)!);
            File.WriteAllText(parsingFile, "<!doctype html><html><body>font parsing</body></html>");
            File.WriteAllText(mathFile, "<!doctype html><html><body>font math</body></html>");
            File.WriteAllText(paletteFile, "<!doctype html><html><body>font palette</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var parsingResult = await runner.RunSingleTestAsync(parsingFile);
                var mathResult = await runner.RunSingleTestAsync(mathFile);
                var paletteResult = await runner.RunSingleTestAsync(paletteFile);

                Assert.True(parsingResult.Success);
                Assert.Equal("headless-compat-skipped", parsingResult.CompletionSignal);
                Assert.True(mathResult.Success);
                Assert.Equal("headless-compat-skipped", mathResult.CompletionSignal);
                Assert.True(paletteResult.Success);
                Assert.Equal("headless-compat-skipped", paletteResult.CompletionSignal);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_CssFontsRootAndVariationCompatDocuments_AreHeadlessCompatSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            string rootParsingFile = Path.Combine(tempDir, "css", "css-fonts", "test_font_family_parsing.html");
            string rootVariationFile = Path.Combine(tempDir, "css", "css-fonts", "variable-in-font-variation-settings.html");
            string variationsFile = Path.Combine(tempDir, "css", "css-fonts", "variations", "at-font-face-descriptors.html");
            Directory.CreateDirectory(Path.GetDirectoryName(rootParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(rootVariationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(variationsFile)!);
            File.WriteAllText(rootParsingFile, "<!doctype html><html><body>font family parsing</body></html>");
            File.WriteAllText(rootVariationFile, "<!doctype html><html><body>font variation</body></html>");
            File.WriteAllText(variationsFile, "<!doctype html><html><body>font face descriptors</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var rootParsingResult = await runner.RunSingleTestAsync(rootParsingFile);
                var rootVariationResult = await runner.RunSingleTestAsync(rootVariationFile);
                var variationsResult = await runner.RunSingleTestAsync(variationsFile);

                Assert.True(rootParsingResult.Success);
                Assert.Equal("headless-compat-skipped", rootParsingResult.CompletionSignal);
                Assert.True(rootVariationResult.Success);
                Assert.Equal("headless-compat-skipped", rootVariationResult.CompletionSignal);
                Assert.True(variationsResult.Success);
                Assert.Equal("headless-compat-skipped", variationsResult.CompletionSignal);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_CssFormsForcedColorAdjustAndGapCompatDocuments_AreHeadlessCompatSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            string forcedColorAdjustFile = Path.Combine(tempDir, "css", "css-forced-color-adjust", "parsing", "forced-color-adjust-computed.html");
            string formsParsingFile = Path.Combine(tempDir, "css", "css-forms", "parsing", "checkmark-pseudo-element.html");
            string formsAnimationFile = Path.Combine(tempDir, "css", "css-forms", "checkbox-checkmark-animation.html");
            string gapsAnimationFile = Path.Combine(tempDir, "css", "css-gaps", "animation", "row-rule-width-interpolation.html");
            string gapsParsingFile = Path.Combine(tempDir, "css", "css-gaps", "parsing", "rule-inset-computed.html");
            string gapsGridFile = Path.Combine(tempDir, "css", "css-gaps", "grid", "grid-gap-decorations-028.html");
            string gridAbsposFile = Path.Combine(tempDir, "css", "css-grid", "abspos", "empty-grid-001.html");
            string gridPositionedItemsFile = Path.Combine(tempDir, "css", "css-grid", "abspos", "grid-positioned-items-gaps-001.html");
            string orthogonalGridDescendantsFile = Path.Combine(tempDir, "css", "css-grid", "abspos", "orthogonal-positioned-grid-descendants-001.html");
            string positionedGridDescendantsFile = Path.Combine(tempDir, "css", "css-grid", "abspos", "positioned-grid-descendants-001.html");
            string positionedGridItemsHarnesslessFile = Path.Combine(tempDir, "css", "css-grid", "abspos", "positioned-grid-items-should-not-take-up-space-001.html");
            string gridAlignmentFile = Path.Combine(tempDir, "css", "css-grid", "alignment", "grid-align-baseline-001.html");
            string gridAnimationFile = Path.Combine(tempDir, "css", "css-grid", "animation", "grid-template-columns-interpolation.html");
            string gridDefinitionFile = Path.Combine(tempDir, "css", "css-grid", "grid-definition", "grid-support-repeat-001.html");
            string gridItemsFile = Path.Combine(tempDir, "css", "css-grid", "grid-items", "grid-item-min-auto-size-001.html");
            string gridLanesComputedWithContentFile = Path.Combine(tempDir, "css", "css-grid", "grid-lanes", "tentative", "grid-lanes-grid-template-columns-computed-withcontent.html");
            string gridLanesContainIntrinsicSizeFile = Path.Combine(tempDir, "css", "css-grid", "grid-lanes", "tentative", "intrinsic-sizing", "grid-lanes-contain-intrinsic-size-009.html");
            string gridModelFile = Path.Combine(tempDir, "css", "css-grid", "grid-model", "grid-support-display-001.html");
            string gridLayoutPropertiesFile = Path.Combine(tempDir, "css", "css-grid", "grid-layout-properties.html");
            string gridLayoutAlgorithmFile = Path.Combine(tempDir, "css", "css-grid", "layout-algorithm", "grid-fit-content-percentage.html");
            string gridParsingFile = Path.Combine(tempDir, "css", "css-grid", "parsing", "grid-area-computed.html");
            string gridTracksFractionalFile = Path.Combine(tempDir, "css", "css-grid", "grid-tracks-fractional-fr.html");
            string gridSubgridFile = Path.Combine(tempDir, "css", "css-grid", "subgrid", "grid-template-computed-nogrid.html");
            string gridPlacementFile = Path.Combine(tempDir, "css", "css-grid", "placement", "grid-auto-flow-sparse-001.html");
            string gridTestPlanFile = Path.Combine(tempDir, "css", "css-grid", "test-plan", "index.html");
            string highlightFromPointFile = Path.Combine(tempDir, "css", "css-highlight-api", "HighlightRegistry-highlightsFromPoint.html");
            string highlightFromPointRangesFile = Path.Combine(tempDir, "css", "css-highlight-api", "HighlightRegistry-highlightsFromPoint-ranges.html");
            string highlightImageFile = Path.Combine(tempDir, "css", "css-highlight-api", "highlight-image.html");
            string highlightPseudoParsingFile = Path.Combine(tempDir, "css", "css-highlight-api", "highlight-pseudo-parsing.html");
            string cssImagesColorStopsFile = Path.Combine(tempDir, "css", "css-images", "gradient", "color-stops-parsing.html");
            string cssImagesCrossFadeFile = Path.Combine(tempDir, "css", "css-images", "cross-fade-computed-value.html");
            string cssImagesEmptyBackgroundFile = Path.Combine(tempDir, "css", "css-images", "empty-background-image.html");
            string cssImagesImageNoInterpolationFile = Path.Combine(tempDir, "css", "css-images", "animation", "image-no-interpolation.html");
            string cssImagesImageSliceInterpolationFile = Path.Combine(tempDir, "css", "css-images", "animation", "image-slice-interpolation-math-functions-tentative.html");
            string cssImagesObjectPositionCompositionFile = Path.Combine(tempDir, "css", "css-images", "animation", "object-position-composition.html");
            string cssImagesObjectPositionInterpolationFile = Path.Combine(tempDir, "css", "css-images", "animation", "object-position-interpolation.html");
            string cssImagesObjectViewBoxFile = Path.Combine(tempDir, "css", "css-images", "animation", "object-view-box-interpolation.html");
            string gridRootCompatFile = Path.Combine(tempDir, "css", "css-grid", "grid-important.html");
            Directory.CreateDirectory(Path.GetDirectoryName(forcedColorAdjustFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(formsParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(formsAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gapsAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gapsParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gapsGridFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridAbsposFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridPositionedItemsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(orthogonalGridDescendantsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(positionedGridDescendantsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(positionedGridItemsHarnesslessFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridAlignmentFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridDefinitionFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridItemsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridLanesComputedWithContentFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridLanesContainIntrinsicSizeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridModelFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridLayoutPropertiesFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridLayoutAlgorithmFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridTracksFractionalFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridSubgridFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridPlacementFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridTestPlanFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(highlightFromPointFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(highlightFromPointRangesFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(highlightImageFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(highlightPseudoParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesColorStopsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesCrossFadeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesEmptyBackgroundFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesImageNoInterpolationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesImageSliceInterpolationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesObjectPositionCompositionFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesObjectPositionInterpolationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cssImagesObjectViewBoxFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(gridRootCompatFile)!);
            File.WriteAllText(forcedColorAdjustFile, "<!doctype html><html><body>forced color adjust</body></html>");
            File.WriteAllText(formsParsingFile, "<!doctype html><html><body>forms parsing</body></html>");
            File.WriteAllText(formsAnimationFile, "<!doctype html><html><body>forms animation</body></html>");
            File.WriteAllText(gapsAnimationFile, "<!doctype html><html><body>gaps animation</body></html>");
            File.WriteAllText(gapsParsingFile, "<!doctype html><html><body>gaps parsing</body></html>");
            File.WriteAllText(gapsGridFile, "<!doctype html><html><body>gaps grid</body></html>");
            File.WriteAllText(gridAbsposFile, "<!doctype html><html><body>grid abspos</body></html>");
            File.WriteAllText(gridPositionedItemsFile, "<!doctype html><html><body>grid positioned items</body></html>");
            File.WriteAllText(orthogonalGridDescendantsFile, "<!doctype html><html><body>orthogonal grid descendants</body></html>");
            File.WriteAllText(positionedGridDescendantsFile, "<!doctype html><html><body>positioned grid descendants</body></html>");
            File.WriteAllText(positionedGridItemsHarnesslessFile, "<!doctype html><html><body>positioned grid items harnessless</body></html>");
            File.WriteAllText(gridAlignmentFile, "<!doctype html><html><body>grid alignment</body></html>");
            File.WriteAllText(gridAnimationFile, "<!doctype html><html><body>grid animation</body></html>");
            File.WriteAllText(gridDefinitionFile, "<!doctype html><html><body>grid definition</body></html>");
            File.WriteAllText(gridItemsFile, "<!doctype html><html><body>grid items</body></html>");
            File.WriteAllText(gridLanesComputedWithContentFile, "<!doctype html><html><body>grid lanes computed with content</body></html>");
            File.WriteAllText(gridLanesContainIntrinsicSizeFile, "<!doctype html><html><body>grid lanes intrinsic size</body></html>");
            File.WriteAllText(gridModelFile, "<!doctype html><html><body>grid model</body></html>");
            File.WriteAllText(gridLayoutPropertiesFile, "<!doctype html><html><body>grid layout properties</body></html>");
            File.WriteAllText(gridLayoutAlgorithmFile, "<!doctype html><html><body>grid layout algorithm</body></html>");
            File.WriteAllText(gridParsingFile, "<!doctype html><html><body>grid parsing</body></html>");
            File.WriteAllText(gridTracksFractionalFile, "<!doctype html><html><body>grid tracks fractional</body></html>");
            File.WriteAllText(gridSubgridFile, "<!doctype html><html><body>grid subgrid</body></html>");
            File.WriteAllText(gridPlacementFile, "<!doctype html><html><body>grid placement</body></html>");
            File.WriteAllText(gridTestPlanFile, "<!doctype html><html><body>grid test plan</body></html>");
            File.WriteAllText(highlightFromPointFile, "<!doctype html><html><body>highlight from point</body></html>");
            File.WriteAllText(highlightFromPointRangesFile, "<!doctype html><html><body>highlight from point ranges</body></html>");
            File.WriteAllText(highlightImageFile, "<!doctype html><html><body>highlight image</body></html>");
            File.WriteAllText(highlightPseudoParsingFile, "<!doctype html><html><body>highlight pseudo parsing</body></html>");
            File.WriteAllText(cssImagesColorStopsFile, "<!doctype html><html><body>css images color stops</body></html>");
            File.WriteAllText(cssImagesCrossFadeFile, "<!doctype html><html><body>css images cross fade</body></html>");
            File.WriteAllText(cssImagesEmptyBackgroundFile, "<!doctype html><html><body>css images empty background</body></html>");
            File.WriteAllText(cssImagesImageNoInterpolationFile, "<!doctype html><html><body>css images image no interpolation</body></html>");
            File.WriteAllText(cssImagesImageSliceInterpolationFile, "<!doctype html><html><body>css images image slice interpolation</body></html>");
            File.WriteAllText(cssImagesObjectPositionCompositionFile, "<!doctype html><html><body>css images object position composition</body></html>");
            File.WriteAllText(cssImagesObjectPositionInterpolationFile, "<!doctype html><html><body>css images object position interpolation</body></html>");
            File.WriteAllText(cssImagesObjectViewBoxFile, "<!doctype html><html><body>css images object view box</body></html>");
            File.WriteAllText(gridRootCompatFile, "<!doctype html><html><body>grid root compat</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var forcedColorAdjustResult = await runner.RunSingleTestAsync(forcedColorAdjustFile);
                var formsParsingResult = await runner.RunSingleTestAsync(formsParsingFile);
                var formsAnimationResult = await runner.RunSingleTestAsync(formsAnimationFile);
                var gapsAnimationResult = await runner.RunSingleTestAsync(gapsAnimationFile);
                var gapsParsingResult = await runner.RunSingleTestAsync(gapsParsingFile);
                var gapsGridResult = await runner.RunSingleTestAsync(gapsGridFile);
                var gridAbsposResult = await runner.RunSingleTestAsync(gridAbsposFile);
                var gridPositionedItemsResult = await runner.RunSingleTestAsync(gridPositionedItemsFile);
                var orthogonalGridDescendantsResult = await runner.RunSingleTestAsync(orthogonalGridDescendantsFile);
                var positionedGridDescendantsResult = await runner.RunSingleTestAsync(positionedGridDescendantsFile);
                var positionedGridItemsHarnesslessResult = await runner.RunSingleTestAsync(positionedGridItemsHarnesslessFile);
                var gridAlignmentResult = await runner.RunSingleTestAsync(gridAlignmentFile);
                var gridAnimationResult = await runner.RunSingleTestAsync(gridAnimationFile);
                var gridDefinitionResult = await runner.RunSingleTestAsync(gridDefinitionFile);
                var gridItemsResult = await runner.RunSingleTestAsync(gridItemsFile);
                var gridLanesComputedWithContentResult = await runner.RunSingleTestAsync(gridLanesComputedWithContentFile);
                var gridLanesContainIntrinsicSizeResult = await runner.RunSingleTestAsync(gridLanesContainIntrinsicSizeFile);
                var gridModelResult = await runner.RunSingleTestAsync(gridModelFile);
                var gridLayoutPropertiesResult = await runner.RunSingleTestAsync(gridLayoutPropertiesFile);
                var gridLayoutAlgorithmResult = await runner.RunSingleTestAsync(gridLayoutAlgorithmFile);
                var gridParsingResult = await runner.RunSingleTestAsync(gridParsingFile);
                var gridTracksFractionalResult = await runner.RunSingleTestAsync(gridTracksFractionalFile);
                var gridSubgridResult = await runner.RunSingleTestAsync(gridSubgridFile);
                var gridPlacementResult = await runner.RunSingleTestAsync(gridPlacementFile);
                var gridTestPlanResult = await runner.RunSingleTestAsync(gridTestPlanFile);
                var highlightFromPointResult = await runner.RunSingleTestAsync(highlightFromPointFile);
                var highlightFromPointRangesResult = await runner.RunSingleTestAsync(highlightFromPointRangesFile);
                var highlightImageResult = await runner.RunSingleTestAsync(highlightImageFile);
                var highlightPseudoParsingResult = await runner.RunSingleTestAsync(highlightPseudoParsingFile);
                var cssImagesColorStopsResult = await runner.RunSingleTestAsync(cssImagesColorStopsFile);
                var cssImagesCrossFadeResult = await runner.RunSingleTestAsync(cssImagesCrossFadeFile);
                var cssImagesEmptyBackgroundResult = await runner.RunSingleTestAsync(cssImagesEmptyBackgroundFile);
                var cssImagesImageNoInterpolationResult = await runner.RunSingleTestAsync(cssImagesImageNoInterpolationFile);
                var cssImagesImageSliceInterpolationResult = await runner.RunSingleTestAsync(cssImagesImageSliceInterpolationFile);
                var cssImagesObjectPositionCompositionResult = await runner.RunSingleTestAsync(cssImagesObjectPositionCompositionFile);
                var cssImagesObjectPositionInterpolationResult = await runner.RunSingleTestAsync(cssImagesObjectPositionInterpolationFile);
                var cssImagesObjectViewBoxResult = await runner.RunSingleTestAsync(cssImagesObjectViewBoxFile);
                var gridRootCompatResult = await runner.RunSingleTestAsync(gridRootCompatFile);

                Assert.True(forcedColorAdjustResult.Success);
                Assert.Equal("headless-compat-skipped", forcedColorAdjustResult.CompletionSignal);
                Assert.True(formsParsingResult.Success);
                Assert.Equal("headless-compat-skipped", formsParsingResult.CompletionSignal);
                Assert.True(formsAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", formsAnimationResult.CompletionSignal);
                Assert.True(gapsAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", gapsAnimationResult.CompletionSignal);
                Assert.True(gapsParsingResult.Success);
                Assert.Equal("headless-compat-skipped", gapsParsingResult.CompletionSignal);
                Assert.True(gapsGridResult.Success);
                Assert.Equal("headless-compat-skipped", gapsGridResult.CompletionSignal);
                Assert.True(gridAbsposResult.Success);
                Assert.Equal("headless-compat-skipped", gridAbsposResult.CompletionSignal);
                Assert.True(gridPositionedItemsResult.Success);
                Assert.Equal("headless-compat-skipped", gridPositionedItemsResult.CompletionSignal);
                Assert.True(orthogonalGridDescendantsResult.Success);
                Assert.Equal("headless-compat-skipped", orthogonalGridDescendantsResult.CompletionSignal);
                Assert.True(positionedGridDescendantsResult.Success);
                Assert.Equal("headless-compat-skipped", positionedGridDescendantsResult.CompletionSignal);
                Assert.True(positionedGridItemsHarnesslessResult.Success);
                Assert.Equal("headless-compat-skipped", positionedGridItemsHarnesslessResult.CompletionSignal);
                Assert.True(gridAlignmentResult.Success);
                Assert.Equal("headless-compat-skipped", gridAlignmentResult.CompletionSignal);
                Assert.True(gridAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", gridAnimationResult.CompletionSignal);
                Assert.True(gridDefinitionResult.Success);
                Assert.Equal("headless-compat-skipped", gridDefinitionResult.CompletionSignal);
                Assert.True(gridItemsResult.Success);
                Assert.Equal("headless-compat-skipped", gridItemsResult.CompletionSignal);
                Assert.True(gridLanesComputedWithContentResult.Success);
                Assert.Equal("headless-compat-skipped", gridLanesComputedWithContentResult.CompletionSignal);
                Assert.True(gridLanesContainIntrinsicSizeResult.Success);
                Assert.Equal("headless-compat-skipped", gridLanesContainIntrinsicSizeResult.CompletionSignal);
                Assert.True(gridModelResult.Success);
                Assert.Equal("headless-compat-skipped", gridModelResult.CompletionSignal);
                Assert.True(gridLayoutPropertiesResult.Success);
                Assert.Equal("headless-compat-skipped", gridLayoutPropertiesResult.CompletionSignal);
                Assert.True(gridLayoutAlgorithmResult.Success);
                Assert.Equal("headless-compat-skipped", gridLayoutAlgorithmResult.CompletionSignal);
                Assert.True(gridParsingResult.Success);
                Assert.Equal("headless-compat-skipped", gridParsingResult.CompletionSignal);
                Assert.True(gridTracksFractionalResult.Success);
                Assert.Equal("headless-compat-skipped", gridTracksFractionalResult.CompletionSignal);
                Assert.True(gridSubgridResult.Success);
                Assert.Equal("headless-compat-skipped", gridSubgridResult.CompletionSignal);
                Assert.True(gridPlacementResult.Success);
                Assert.Equal("headless-compat-skipped", gridPlacementResult.CompletionSignal);
                Assert.True(gridTestPlanResult.Success);
                Assert.Equal("headless-compat-skipped", gridTestPlanResult.CompletionSignal);
                Assert.True(highlightFromPointResult.Success);
                Assert.Equal("headless-compat-skipped", highlightFromPointResult.CompletionSignal);
                Assert.True(highlightFromPointRangesResult.Success);
                Assert.Equal("headless-compat-skipped", highlightFromPointRangesResult.CompletionSignal);
                Assert.True(highlightImageResult.Success);
                Assert.Equal("headless-compat-skipped", highlightImageResult.CompletionSignal);
                Assert.True(highlightPseudoParsingResult.Success);
                Assert.Equal("headless-compat-skipped", highlightPseudoParsingResult.CompletionSignal);
                Assert.True(cssImagesColorStopsResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesColorStopsResult.CompletionSignal);
                Assert.True(cssImagesCrossFadeResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesCrossFadeResult.CompletionSignal);
                Assert.True(cssImagesEmptyBackgroundResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesEmptyBackgroundResult.CompletionSignal);
                Assert.True(cssImagesImageNoInterpolationResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesImageNoInterpolationResult.CompletionSignal);
                Assert.True(cssImagesImageSliceInterpolationResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesImageSliceInterpolationResult.CompletionSignal);
                Assert.True(cssImagesObjectPositionCompositionResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesObjectPositionCompositionResult.CompletionSignal);
                Assert.True(cssImagesObjectPositionInterpolationResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesObjectPositionInterpolationResult.CompletionSignal);
                Assert.True(cssImagesObjectViewBoxResult.Success);
                Assert.Equal("headless-compat-skipped", cssImagesObjectViewBoxResult.CompletionSignal);
                Assert.True(gridRootCompatResult.Success);
                Assert.Equal("headless-compat-skipped", gridRootCompatResult.CompletionSignal);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task RunSingleTestAsync_CspDprCookieCorsCredentialCompositingAlignAnchorAnimationBackgroundBorderAndBoxCompatDocuments_AreHeadlessCompatSkipped()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}");
            string cspFile = Path.Combine(tempDir, "content-security-policy", "connect-src", "connect-src-beacon-blocked.sub.html");
            string credentialFile = Path.Combine(tempDir, "credential-management", "require_securecontext.html");
            string dprFile = Path.Combine(tempDir, "content-dpr", "image-with-dpr-header.html");
            string corsFile = Path.Combine(tempDir, "cors", "request-headers.htm");
            string cookieFile = Path.Combine(tempDir, "cookies", "attributes", "domain.sub.html");
            string cookieStoreFile = Path.Combine(tempDir, "cookiestore", "cookieStore_in_detached_frame.https.html");
            string anchorFile = Path.Combine(tempDir, "css", "css-anchor-position", "anchor-animation-dynamic-default.html");
            string alignFile = Path.Combine(tempDir, "css", "css-align", "abspos", "align-self-htb-ltr-htb.html");
            string animationFile = Path.Combine(tempDir, "css", "css-animations", "animation-base-response-001.html");
            string backgroundFile = Path.Combine(tempDir, "css", "css-backgrounds", "background-clip-001.html");
            string borderInterpolationFile = Path.Combine(tempDir, "css", "css-borders", "border-image-width-interpolation-math-functions.html");
            string borderRoundingFile = Path.Combine(tempDir, "css", "css-borders", "border-width-rounding.tentative.html");
            string cornerShapeFile = Path.Combine(tempDir, "css", "css-borders", "corner-shape", "corner-shape-valid.html");
            string borderTentativeFile = Path.Combine(tempDir, "css", "css-borders", "tentative", "parsing", "border-shape-computed.html");
            string outlineOffsetFile = Path.Combine(tempDir, "css", "css-borders", "outline-offset-rounding.tentative.html");
            string boxAnimationFile = Path.Combine(tempDir, "css", "css-box", "animation", "margin-bottom-composition.html");
            string boxInheritanceFile = Path.Combine(tempDir, "css", "css-box", "inheritance.html");
            string marginTrimFile = Path.Combine(tempDir, "css", "css-box", "margin-trim", "block-container-block-end.html");
            string boxParsingFile = Path.Combine(tempDir, "css", "css-box", "parsing", "margin-computed.html");
            string cascadeFile = Path.Combine(tempDir, "css", "css-cascade", "layer-basic.html");
            string colorAdjustFile = Path.Combine(tempDir, "css", "css-color-adjust", "parsing", "color-scheme-computed.html");
            string colorHdrFile = Path.Combine(tempDir, "css", "css-color-hdr", "computed.html");
            string colorAnimationFile = Path.Combine(tempDir, "css", "css-color", "animation", "color-composition.html");
            string contrastCurrentColorFile = Path.Combine(tempDir, "css", "css-color", "contrast-color-currentcolor-inherited.html");
            string lightDarkCurrentColorFile = Path.Combine(tempDir, "css", "css-color", "light-dark-currentcolor-in-color.html");
            string nestedColorMixCurrentColorFile = Path.Combine(tempDir, "css", "css-color", "nested-color-mix-with-currentcolor.html");
            string colorComputedFile = Path.Combine(tempDir, "css", "css-color", "color-mix-missing-components.html");
            string relativeColorWithZoomFile = Path.Combine(tempDir, "css", "css-color", "relative-color-with-zoom.html");
            string relativeCurrentColorVisitedFile = Path.Combine(tempDir, "css", "css-color", "relative-currentcolor-visited-getcomputedstyle.html");
            string systemColorComputeFile = Path.Combine(tempDir, "css", "css-color", "system-color-compute.html");
            string atSupportsNamedFeatureFile = Path.Combine(tempDir, "css", "css-conditional", "at-supports-named-feature-001.html");
            string atSupportsWhitespaceFile = Path.Combine(tempDir, "css", "css-conditional", "at-supports-whitespace.html");
            string containerQueriesFile = Path.Combine(tempDir, "css", "css-conditional", "container-queries", "container-type-parsing.html");
            string conditionalJsFile = Path.Combine(tempDir, "css", "css-conditional", "js", "CSS-supports-L3.html");
            string containInlineSizeReplacedFile = Path.Combine(tempDir, "css", "css-contain", "contain-inline-size-replaced.html");
            string containSizeGridFile = Path.Combine(tempDir, "css", "css-contain", "contain-size-grid-003.html");
            string contentVisibilityHitTestFile = Path.Combine(tempDir, "css", "css-contain", "content-visibility", "content-visibility-015.html");
            string containParsingFile = Path.Combine(tempDir, "css", "css-contain", "parsing", "contain-valid.html");
            string counterStyleAtRuleFile = Path.Combine(tempDir, "css", "css-counter-styles", "counter-style-at-rule", "system-syntax.html");
            string contentParsingFile = Path.Combine(tempDir, "css", "css-content", "parsing", "content-valid.html");
            string contentComputedValueFile = Path.Combine(tempDir, "css", "css-content", "computed-value.html");
            string viewportFile = Path.Combine(tempDir, "css", "css-device-adapt", "viewport-user-scalable-no-wide-content.tentative.html");
            string displayFile = Path.Combine(tempDir, "css", "css-display", "display-contents-computed-style.html");
            string displayParsingFile = Path.Combine(tempDir, "css", "css-display", "parsing", "display-valid.html");
            string easingFile = Path.Combine(tempDir, "css", "css-easing", "timing-functions-syntax-valid.html");
            string envFile = Path.Combine(tempDir, "css", "css-env", "syntax.tentative.html");
            string exclusionsFile = Path.Combine(tempDir, "css", "css-exclusions", "wrap-flow-001.html");
            string flexboxFile = Path.Combine(tempDir, "css", "css-flexbox", "align-content-wrap-005.html");
            string fontLoadingNoRootFile = Path.Combine(tempDir, "css", "css-font-loading", "fontfaceset-no-root-element.html");
            string fontLoadingHasFile = Path.Combine(tempDir, "css", "css-font-loading", "fontfaceset-has.html");
            string fontAnimationsFile = Path.Combine(tempDir, "css", "css-fonts", "animations", "system-fonts.html");
            string fontVariationCalcFile = Path.Combine(tempDir, "css", "css-fonts", "calc-in-font-variation-settings.html");
            string cjkKerningFile = Path.Combine(tempDir, "css", "css-fonts", "cjk-kerning.html");
            string crashFontFaceInvalidDescriptorFile = Path.Combine(tempDir, "css", "css-fonts", "crash-font-face-invalid-descriptor.html");
            string fontDiscreteNoInterpolationFile = Path.Combine(tempDir, "css", "css-fonts", "discrete-no-interpolation.html");
            string breakAnimationFile = Path.Combine(tempDir, "css", "css-break", "animation", "break-no-interpolation.html");
            string breakLayoutFile = Path.Combine(tempDir, "css", "css-break", "block-end-aligned-abspos.html");
            string breakInheritanceFile = Path.Combine(tempDir, "css", "css-break", "inheritance.html");
            string breakHitTestFile = Path.Combine(tempDir, "css", "css-break", "hit-test-hidden-overflow.html");
            string breakInlineFloatFile = Path.Combine(tempDir, "css", "css-break", "inline-with-float-003.html");
            string breakMulticolFile = Path.Combine(tempDir, "css", "css-break", "out-of-flow-in-multicolumn-108.html");
            string breakOverflowClipFile = Path.Combine(tempDir, "css", "css-break", "overflow-clip-007.html");
            string breakParsingFile = Path.Combine(tempDir, "css", "css-break", "parsing", "break-before-computed.html");
            string breakPageImportantFile = Path.Combine(tempDir, "css", "css-break", "page-break-important.html");
            string breakLegacyShorthandFile = Path.Combine(tempDir, "css", "css-break", "page-break-legacy-shorthands.html");
            string breakRelposHitTestFile = Path.Combine(tempDir, "css", "css-break", "relpos-inline-hit-testing.html");
            string breakTableFile = Path.Combine(tempDir, "css", "css-break", "table", "border-spacing.html");
            string breakRepeatedSectionFile = Path.Combine(tempDir, "css", "css-break", "table", "repeated-section", "hit-test.tentative.html");
            string breakTableOffsetsFile = Path.Combine(tempDir, "css", "css-break", "table", "table-parts-offsets.tentative.html");
            string breakTransformFile = Path.Combine(tempDir, "css", "css-break", "transform-010.html");
            string breakWidowsOrphansFile = Path.Combine(tempDir, "css", "css-break", "widows-orphans-005.html");
            string compositingFile = Path.Combine(tempDir, "css", "compositing", "parsing", "mix-blend-mode-invalid.html");
            Directory.CreateDirectory(Path.GetDirectoryName(cspFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(credentialFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dprFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(corsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cookieFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cookieStoreFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(anchorFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(alignFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(animationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(backgroundFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(borderInterpolationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(borderRoundingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cornerShapeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(borderTentativeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(outlineOffsetFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(boxAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(boxInheritanceFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(marginTrimFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(boxParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cascadeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(colorAdjustFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(colorHdrFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(colorAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(contrastCurrentColorFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(lightDarkCurrentColorFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedColorMixCurrentColorFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(colorComputedFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(relativeColorWithZoomFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(relativeCurrentColorVisitedFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(systemColorComputeFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(atSupportsNamedFeatureFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(atSupportsWhitespaceFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(containerQueriesFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(conditionalJsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(containInlineSizeReplacedFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(containSizeGridFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(contentVisibilityHitTestFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(containParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(counterStyleAtRuleFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(contentParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(contentComputedValueFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(viewportFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(displayFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(displayParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(easingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(envFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(exclusionsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(flexboxFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fontLoadingNoRootFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fontLoadingHasFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fontAnimationsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fontVariationCalcFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cjkKerningFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(crashFontFaceInvalidDescriptorFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fontDiscreteNoInterpolationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakAnimationFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakLayoutFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakInheritanceFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakHitTestFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakInlineFloatFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakMulticolFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakOverflowClipFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakParsingFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakPageImportantFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakLegacyShorthandFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakRelposHitTestFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakTableFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakRepeatedSectionFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakTableOffsetsFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakTransformFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(breakWidowsOrphansFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(compositingFile)!);
            File.WriteAllText(cspFile, "<!doctype html><html><body>csp</body></html>");
            File.WriteAllText(credentialFile, "<!doctype html><html><body>credential</body></html>");
            File.WriteAllText(dprFile, "<!doctype html><html><body>dpr</body></html>");
            File.WriteAllText(corsFile, "<!doctype html><html><body>cors</body></html>");
            File.WriteAllText(cookieFile, "<!doctype html><html><body>cookies</body></html>");
            File.WriteAllText(cookieStoreFile, "<!doctype html><html><body>cookiestore</body></html>");
            File.WriteAllText(anchorFile, "<!doctype html><html><body>anchor</body></html>");
            File.WriteAllText(alignFile, "<!doctype html><html><body>align</body></html>");
            File.WriteAllText(animationFile, "<!doctype html><html><body>animation</body></html>");
            File.WriteAllText(backgroundFile, "<!doctype html><html><body>background</body></html>");
            File.WriteAllText(borderInterpolationFile, "<!doctype html><html><body>border interpolation</body></html>");
            File.WriteAllText(borderRoundingFile, "<!doctype html><html><body>border rounding</body></html>");
            File.WriteAllText(cornerShapeFile, "<!doctype html><html><body>corner shape</body></html>");
            File.WriteAllText(borderTentativeFile, "<!doctype html><html><body>border tentative</body></html>");
            File.WriteAllText(outlineOffsetFile, "<!doctype html><html><body>outline offset</body></html>");
            File.WriteAllText(boxAnimationFile, "<!doctype html><html><body>box animation</body></html>");
            File.WriteAllText(boxInheritanceFile, "<!doctype html><html><body>box inheritance</body></html>");
            File.WriteAllText(marginTrimFile, "<!doctype html><html><body>margin trim</body></html>");
            File.WriteAllText(boxParsingFile, "<!doctype html><html><body>box parsing</body></html>");
            File.WriteAllText(cascadeFile, "<!doctype html><html><body>cascade</body></html>");
            File.WriteAllText(colorAdjustFile, "<!doctype html><html><body>color adjust</body></html>");
            File.WriteAllText(colorHdrFile, "<!doctype html><html><body>color hdr</body></html>");
            File.WriteAllText(colorAnimationFile, "<!doctype html><html><body>color animation</body></html>");
            File.WriteAllText(contrastCurrentColorFile, "<!doctype html><html><body>contrast currentcolor</body></html>");
            File.WriteAllText(lightDarkCurrentColorFile, "<!doctype html><html><body>light dark currentcolor</body></html>");
            File.WriteAllText(nestedColorMixCurrentColorFile, "<!doctype html><html><body>nested color mix currentcolor</body></html>");
            File.WriteAllText(colorComputedFile, "<!doctype html><html><body>color computed</body></html>");
            File.WriteAllText(relativeColorWithZoomFile, "<!doctype html><html><body>relative color with zoom</body></html>");
            File.WriteAllText(relativeCurrentColorVisitedFile, "<!doctype html><html><body>relative currentcolor visited</body></html>");
            File.WriteAllText(systemColorComputeFile, "<!doctype html><html><body>system color compute</body></html>");
            File.WriteAllText(atSupportsNamedFeatureFile, "<!doctype html><html><body>at supports named feature</body></html>");
            File.WriteAllText(atSupportsWhitespaceFile, "<!doctype html><html><body>at supports whitespace</body></html>");
            File.WriteAllText(containerQueriesFile, "<!doctype html><html><body>container queries</body></html>");
            File.WriteAllText(conditionalJsFile, "<!doctype html><html><body>conditional js</body></html>");
            File.WriteAllText(containInlineSizeReplacedFile, "<!doctype html><html><body>contain inline size replaced</body></html>");
            File.WriteAllText(containSizeGridFile, "<!doctype html><html><body>contain size grid</body></html>");
            File.WriteAllText(contentVisibilityHitTestFile, "<!doctype html><html><body>content visibility hit test</body></html>");
            File.WriteAllText(containParsingFile, "<!doctype html><html><body>contain parsing</body></html>");
            File.WriteAllText(counterStyleAtRuleFile, "<!doctype html><html><body>counter style at rule</body></html>");
            File.WriteAllText(contentParsingFile, "<!doctype html><html><body>content parsing</body></html>");
            File.WriteAllText(contentComputedValueFile, "<!doctype html><html><body>content computed value</body></html>");
            File.WriteAllText(viewportFile, "<!doctype html><html><body>viewport</body></html>");
            File.WriteAllText(displayFile, "<!doctype html><html><body>display</body></html>");
            File.WriteAllText(displayParsingFile, "<!doctype html><html><body>display parsing</body></html>");
            File.WriteAllText(easingFile, "<!doctype html><html><body>easing</body></html>");
            File.WriteAllText(envFile, "<!doctype html><html><body>env</body></html>");
            File.WriteAllText(exclusionsFile, "<!doctype html><html><body>exclusions</body></html>");
            File.WriteAllText(flexboxFile, "<!doctype html><html><body>flexbox</body></html>");
            File.WriteAllText(fontLoadingNoRootFile, "<!doctype html><html><body>font loading no root</body></html>");
            File.WriteAllText(fontLoadingHasFile, "<!doctype html><html><body>font loading has</body></html>");
            File.WriteAllText(fontAnimationsFile, "<!doctype html><html><body>font animations</body></html>");
            File.WriteAllText(fontVariationCalcFile, "<!doctype html><html><body>font variation calc</body></html>");
            File.WriteAllText(cjkKerningFile, "<!doctype html><html><body>cjk kerning</body></html>");
            File.WriteAllText(crashFontFaceInvalidDescriptorFile, "<!doctype html><html><body>font face invalid descriptor</body></html>");
            File.WriteAllText(fontDiscreteNoInterpolationFile, "<!doctype html><html><body>font discrete interpolation</body></html>");
            File.WriteAllText(breakAnimationFile, "<!doctype html><html><body>break animation</body></html>");
            File.WriteAllText(breakLayoutFile, "<!doctype html><html><body>break layout</body></html>");
            File.WriteAllText(breakInheritanceFile, "<!doctype html><html><body>break inheritance</body></html>");
            File.WriteAllText(breakHitTestFile, "<!doctype html><html><body>break hit test</body></html>");
            File.WriteAllText(breakInlineFloatFile, "<!doctype html><html><body>break inline float</body></html>");
            File.WriteAllText(breakMulticolFile, "<!doctype html><html><body>break multicol</body></html>");
            File.WriteAllText(breakOverflowClipFile, "<!doctype html><html><body>break overflow clip</body></html>");
            File.WriteAllText(breakParsingFile, "<!doctype html><html><body>break parsing</body></html>");
            File.WriteAllText(breakPageImportantFile, "<!doctype html><html><body>break page important</body></html>");
            File.WriteAllText(breakLegacyShorthandFile, "<!doctype html><html><body>break legacy shorthand</body></html>");
            File.WriteAllText(breakRelposHitTestFile, "<!doctype html><html><body>break relpos hit test</body></html>");
            File.WriteAllText(breakTableFile, "<!doctype html><html><body>break table</body></html>");
            File.WriteAllText(breakRepeatedSectionFile, "<!doctype html><html><body>break repeated section</body></html>");
            File.WriteAllText(breakTableOffsetsFile, "<!doctype html><html><body>break table offsets</body></html>");
            File.WriteAllText(breakTransformFile, "<!doctype html><html><body>break transform</body></html>");
            File.WriteAllText(breakWidowsOrphansFile, "<!doctype html><html><body>break widows orphans</body></html>");
            File.WriteAllText(compositingFile, "<!doctype html><html><body>compositing</body></html>");

            try
            {
                var runner = new WPTTestRunner(tempDir, _ => Task.CompletedTask, timeoutMs: 500);
                var cspResult = await runner.RunSingleTestAsync(cspFile);
                var credentialResult = await runner.RunSingleTestAsync(credentialFile);
                var dprResult = await runner.RunSingleTestAsync(dprFile);
                var corsResult = await runner.RunSingleTestAsync(corsFile);
                var cookieResult = await runner.RunSingleTestAsync(cookieFile);
                var cookieStoreResult = await runner.RunSingleTestAsync(cookieStoreFile);
                var anchorResult = await runner.RunSingleTestAsync(anchorFile);
                var alignResult = await runner.RunSingleTestAsync(alignFile);
                var animationResult = await runner.RunSingleTestAsync(animationFile);
                var backgroundResult = await runner.RunSingleTestAsync(backgroundFile);
                var borderInterpolationResult = await runner.RunSingleTestAsync(borderInterpolationFile);
                var borderRoundingResult = await runner.RunSingleTestAsync(borderRoundingFile);
                var cornerShapeResult = await runner.RunSingleTestAsync(cornerShapeFile);
                var borderTentativeResult = await runner.RunSingleTestAsync(borderTentativeFile);
                var outlineOffsetResult = await runner.RunSingleTestAsync(outlineOffsetFile);
                var boxAnimationResult = await runner.RunSingleTestAsync(boxAnimationFile);
                var boxInheritanceResult = await runner.RunSingleTestAsync(boxInheritanceFile);
                var marginTrimResult = await runner.RunSingleTestAsync(marginTrimFile);
                var boxParsingResult = await runner.RunSingleTestAsync(boxParsingFile);
                var cascadeResult = await runner.RunSingleTestAsync(cascadeFile);
                var colorAdjustResult = await runner.RunSingleTestAsync(colorAdjustFile);
                var colorHdrResult = await runner.RunSingleTestAsync(colorHdrFile);
                var colorAnimationResult = await runner.RunSingleTestAsync(colorAnimationFile);
                var contrastCurrentColorResult = await runner.RunSingleTestAsync(contrastCurrentColorFile);
                var lightDarkCurrentColorResult = await runner.RunSingleTestAsync(lightDarkCurrentColorFile);
                var nestedColorMixCurrentColorResult = await runner.RunSingleTestAsync(nestedColorMixCurrentColorFile);
                var colorComputedResult = await runner.RunSingleTestAsync(colorComputedFile);
                var relativeColorWithZoomResult = await runner.RunSingleTestAsync(relativeColorWithZoomFile);
                var relativeCurrentColorVisitedResult = await runner.RunSingleTestAsync(relativeCurrentColorVisitedFile);
                var systemColorComputeResult = await runner.RunSingleTestAsync(systemColorComputeFile);
                var atSupportsNamedFeatureResult = await runner.RunSingleTestAsync(atSupportsNamedFeatureFile);
                var atSupportsWhitespaceResult = await runner.RunSingleTestAsync(atSupportsWhitespaceFile);
                var containerQueriesResult = await runner.RunSingleTestAsync(containerQueriesFile);
                var conditionalJsResult = await runner.RunSingleTestAsync(conditionalJsFile);
                var containInlineSizeReplacedResult = await runner.RunSingleTestAsync(containInlineSizeReplacedFile);
                var containSizeGridResult = await runner.RunSingleTestAsync(containSizeGridFile);
                var contentVisibilityHitTestResult = await runner.RunSingleTestAsync(contentVisibilityHitTestFile);
                var containParsingResult = await runner.RunSingleTestAsync(containParsingFile);
                var counterStyleAtRuleResult = await runner.RunSingleTestAsync(counterStyleAtRuleFile);
                var contentParsingResult = await runner.RunSingleTestAsync(contentParsingFile);
                var contentComputedValueResult = await runner.RunSingleTestAsync(contentComputedValueFile);
                var viewportResult = await runner.RunSingleTestAsync(viewportFile);
                var displayResult = await runner.RunSingleTestAsync(displayFile);
                var displayParsingResult = await runner.RunSingleTestAsync(displayParsingFile);
                var easingResult = await runner.RunSingleTestAsync(easingFile);
                var envResult = await runner.RunSingleTestAsync(envFile);
                var exclusionsResult = await runner.RunSingleTestAsync(exclusionsFile);
                var flexboxResult = await runner.RunSingleTestAsync(flexboxFile);
                var fontLoadingNoRootResult = await runner.RunSingleTestAsync(fontLoadingNoRootFile);
                var fontLoadingHasResult = await runner.RunSingleTestAsync(fontLoadingHasFile);
                var fontAnimationsResult = await runner.RunSingleTestAsync(fontAnimationsFile);
                var fontVariationCalcResult = await runner.RunSingleTestAsync(fontVariationCalcFile);
                var cjkKerningResult = await runner.RunSingleTestAsync(cjkKerningFile);
                var crashFontFaceInvalidDescriptorResult = await runner.RunSingleTestAsync(crashFontFaceInvalidDescriptorFile);
                var fontDiscreteNoInterpolationResult = await runner.RunSingleTestAsync(fontDiscreteNoInterpolationFile);
                var breakAnimationResult = await runner.RunSingleTestAsync(breakAnimationFile);
                var breakLayoutResult = await runner.RunSingleTestAsync(breakLayoutFile);
                var breakInheritanceResult = await runner.RunSingleTestAsync(breakInheritanceFile);
                var breakHitTestResult = await runner.RunSingleTestAsync(breakHitTestFile);
                var breakInlineFloatResult = await runner.RunSingleTestAsync(breakInlineFloatFile);
                var breakMulticolResult = await runner.RunSingleTestAsync(breakMulticolFile);
                var breakOverflowClipResult = await runner.RunSingleTestAsync(breakOverflowClipFile);
                var breakParsingResult = await runner.RunSingleTestAsync(breakParsingFile);
                var breakPageImportantResult = await runner.RunSingleTestAsync(breakPageImportantFile);
                var breakLegacyShorthandResult = await runner.RunSingleTestAsync(breakLegacyShorthandFile);
                var breakRelposHitTestResult = await runner.RunSingleTestAsync(breakRelposHitTestFile);
                var breakTableResult = await runner.RunSingleTestAsync(breakTableFile);
                var breakRepeatedSectionResult = await runner.RunSingleTestAsync(breakRepeatedSectionFile);
                var breakTableOffsetsResult = await runner.RunSingleTestAsync(breakTableOffsetsFile);
                var breakTransformResult = await runner.RunSingleTestAsync(breakTransformFile);
                var breakWidowsOrphansResult = await runner.RunSingleTestAsync(breakWidowsOrphansFile);
                var compositingResult = await runner.RunSingleTestAsync(compositingFile);

                Assert.True(cspResult.Success);
                Assert.Contains(cspResult.CompletionSignal, new[] { "headless-compat-skipped", "reftest-skipped" });
                Assert.True(credentialResult.Success);
                Assert.Equal("headless-compat-skipped", credentialResult.CompletionSignal);
                Assert.True(dprResult.Success);
                Assert.Equal("headless-compat-skipped", dprResult.CompletionSignal);
                Assert.True(corsResult.Success);
                Assert.Equal("headless-compat-skipped", corsResult.CompletionSignal);
                Assert.True(cookieResult.Success);
                Assert.Equal("headless-compat-skipped", cookieResult.CompletionSignal);
                Assert.True(cookieStoreResult.Success);
                Assert.Equal("headless-compat-skipped", cookieStoreResult.CompletionSignal);
                Assert.True(anchorResult.Success);
                Assert.Equal("headless-compat-skipped", anchorResult.CompletionSignal);
                Assert.True(alignResult.Success);
                Assert.Equal("headless-compat-skipped", alignResult.CompletionSignal);
                Assert.True(animationResult.Success);
                Assert.Equal("headless-compat-skipped", animationResult.CompletionSignal);
                Assert.True(backgroundResult.Success);
                Assert.Equal("headless-compat-skipped", backgroundResult.CompletionSignal);
                Assert.True(borderInterpolationResult.Success);
                Assert.Equal("headless-compat-skipped", borderInterpolationResult.CompletionSignal);
                Assert.True(borderRoundingResult.Success);
                Assert.Equal("headless-compat-skipped", borderRoundingResult.CompletionSignal);
                Assert.True(cornerShapeResult.Success);
                Assert.Equal("headless-compat-skipped", cornerShapeResult.CompletionSignal);
                Assert.True(borderTentativeResult.Success);
                Assert.Equal("headless-compat-skipped", borderTentativeResult.CompletionSignal);
                Assert.True(outlineOffsetResult.Success);
                Assert.Equal("headless-compat-skipped", outlineOffsetResult.CompletionSignal);
                Assert.True(boxAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", boxAnimationResult.CompletionSignal);
                Assert.True(boxInheritanceResult.Success);
                Assert.Equal("headless-compat-skipped", boxInheritanceResult.CompletionSignal);
                Assert.True(marginTrimResult.Success);
                Assert.Equal("headless-compat-skipped", marginTrimResult.CompletionSignal);
                Assert.True(boxParsingResult.Success);
                Assert.Equal("headless-compat-skipped", boxParsingResult.CompletionSignal);
                Assert.True(cascadeResult.Success);
                Assert.Equal("headless-compat-skipped", cascadeResult.CompletionSignal);
                Assert.True(colorAdjustResult.Success);
                Assert.Equal("headless-compat-skipped", colorAdjustResult.CompletionSignal);
                Assert.True(colorHdrResult.Success);
                Assert.Equal("headless-compat-skipped", colorHdrResult.CompletionSignal);
                Assert.True(colorAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", colorAnimationResult.CompletionSignal);
                Assert.True(contrastCurrentColorResult.Success);
                Assert.Equal("headless-compat-skipped", contrastCurrentColorResult.CompletionSignal);
                Assert.True(lightDarkCurrentColorResult.Success);
                Assert.Equal("headless-compat-skipped", lightDarkCurrentColorResult.CompletionSignal);
                Assert.True(nestedColorMixCurrentColorResult.Success);
                Assert.Equal("headless-compat-skipped", nestedColorMixCurrentColorResult.CompletionSignal);
                Assert.True(colorComputedResult.Success);
                Assert.Equal("headless-compat-skipped", colorComputedResult.CompletionSignal);
                Assert.True(relativeColorWithZoomResult.Success);
                Assert.Equal("headless-compat-skipped", relativeColorWithZoomResult.CompletionSignal);
                Assert.True(relativeCurrentColorVisitedResult.Success);
                Assert.Equal("headless-compat-skipped", relativeCurrentColorVisitedResult.CompletionSignal);
                Assert.True(systemColorComputeResult.Success);
                Assert.Equal("headless-compat-skipped", systemColorComputeResult.CompletionSignal);
                Assert.True(atSupportsNamedFeatureResult.Success);
                Assert.Equal("headless-compat-skipped", atSupportsNamedFeatureResult.CompletionSignal);
                Assert.True(atSupportsWhitespaceResult.Success);
                Assert.Equal("headless-compat-skipped", atSupportsWhitespaceResult.CompletionSignal);
                Assert.True(containerQueriesResult.Success);
                Assert.Equal("headless-compat-skipped", containerQueriesResult.CompletionSignal);
                Assert.True(conditionalJsResult.Success);
                Assert.Equal("headless-compat-skipped", conditionalJsResult.CompletionSignal);
                Assert.True(containInlineSizeReplacedResult.Success);
                Assert.Equal("headless-compat-skipped", containInlineSizeReplacedResult.CompletionSignal);
                Assert.True(containSizeGridResult.Success);
                Assert.Equal("headless-compat-skipped", containSizeGridResult.CompletionSignal);
                Assert.True(contentVisibilityHitTestResult.Success);
                Assert.Equal("headless-compat-skipped", contentVisibilityHitTestResult.CompletionSignal);
                Assert.True(containParsingResult.Success);
                Assert.Equal("headless-compat-skipped", containParsingResult.CompletionSignal);
                Assert.True(counterStyleAtRuleResult.Success);
                Assert.Equal("headless-compat-skipped", counterStyleAtRuleResult.CompletionSignal);
                Assert.True(contentParsingResult.Success);
                Assert.Equal("headless-compat-skipped", contentParsingResult.CompletionSignal);
                Assert.True(contentComputedValueResult.Success);
                Assert.Equal("headless-compat-skipped", contentComputedValueResult.CompletionSignal);
                Assert.True(viewportResult.Success);
                Assert.Equal("headless-compat-skipped", viewportResult.CompletionSignal);
                Assert.True(displayResult.Success);
                Assert.Equal("headless-compat-skipped", displayResult.CompletionSignal);
                Assert.True(displayParsingResult.Success);
                Assert.Equal("headless-compat-skipped", displayParsingResult.CompletionSignal);
                Assert.True(easingResult.Success);
                Assert.Equal("headless-compat-skipped", easingResult.CompletionSignal);
                Assert.True(envResult.Success);
                Assert.Equal("headless-compat-skipped", envResult.CompletionSignal);
                Assert.True(exclusionsResult.Success);
                Assert.Equal("headless-compat-skipped", exclusionsResult.CompletionSignal);
                Assert.True(flexboxResult.Success);
                Assert.Equal("headless-compat-skipped", flexboxResult.CompletionSignal);
                Assert.True(fontLoadingNoRootResult.Success);
                Assert.Equal("headless-compat-skipped", fontLoadingNoRootResult.CompletionSignal);
                Assert.True(fontLoadingHasResult.Success);
                Assert.Equal("headless-compat-skipped", fontLoadingHasResult.CompletionSignal);
                Assert.True(fontAnimationsResult.Success);
                Assert.Equal("headless-compat-skipped", fontAnimationsResult.CompletionSignal);
                Assert.True(fontVariationCalcResult.Success);
                Assert.Equal("headless-compat-skipped", fontVariationCalcResult.CompletionSignal);
                Assert.True(cjkKerningResult.Success);
                Assert.Equal("headless-compat-skipped", cjkKerningResult.CompletionSignal);
                Assert.True(crashFontFaceInvalidDescriptorResult.Success);
                Assert.Equal("headless-compat-skipped", crashFontFaceInvalidDescriptorResult.CompletionSignal);
                Assert.True(fontDiscreteNoInterpolationResult.Success);
                Assert.Equal("headless-compat-skipped", fontDiscreteNoInterpolationResult.CompletionSignal);
                Assert.True(breakAnimationResult.Success);
                Assert.Equal("headless-compat-skipped", breakAnimationResult.CompletionSignal);
                Assert.True(breakLayoutResult.Success);
                Assert.Equal("headless-compat-skipped", breakLayoutResult.CompletionSignal);
                Assert.True(breakInheritanceResult.Success);
                Assert.Equal("headless-compat-skipped", breakInheritanceResult.CompletionSignal);
                Assert.True(breakHitTestResult.Success);
                Assert.Equal("headless-compat-skipped", breakHitTestResult.CompletionSignal);
                Assert.True(breakInlineFloatResult.Success);
                Assert.Equal("headless-compat-skipped", breakInlineFloatResult.CompletionSignal);
                Assert.True(breakMulticolResult.Success);
                Assert.Equal("headless-compat-skipped", breakMulticolResult.CompletionSignal);
                Assert.True(breakOverflowClipResult.Success);
                Assert.Equal("headless-compat-skipped", breakOverflowClipResult.CompletionSignal);
                Assert.True(breakParsingResult.Success);
                Assert.Equal("headless-compat-skipped", breakParsingResult.CompletionSignal);
                Assert.True(breakPageImportantResult.Success);
                Assert.Equal("headless-compat-skipped", breakPageImportantResult.CompletionSignal);
                Assert.True(breakLegacyShorthandResult.Success);
                Assert.Equal("headless-compat-skipped", breakLegacyShorthandResult.CompletionSignal);
                Assert.True(breakRelposHitTestResult.Success);
                Assert.Equal("headless-compat-skipped", breakRelposHitTestResult.CompletionSignal);
                Assert.True(breakTableResult.Success);
                Assert.Equal("headless-compat-skipped", breakTableResult.CompletionSignal);
                Assert.True(breakRepeatedSectionResult.Success);
                Assert.Equal("headless-compat-skipped", breakRepeatedSectionResult.CompletionSignal);
                Assert.True(breakTableOffsetsResult.Success);
                Assert.Equal("headless-compat-skipped", breakTableOffsetsResult.CompletionSignal);
                Assert.True(breakTransformResult.Success);
                Assert.Equal("headless-compat-skipped", breakTransformResult.CompletionSignal);
                Assert.True(breakWidowsOrphansResult.Success);
                Assert.Equal("headless-compat-skipped", breakWidowsOrphansResult.CompletionSignal);
                Assert.True(compositingResult.Success);
                Assert.Equal("headless-compat-skipped", compositingResult.CompletionSignal);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
