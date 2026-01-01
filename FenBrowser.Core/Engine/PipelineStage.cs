// =============================================================================
// PipelineStage.cs
// FenBrowser Rendering Pipeline Stage Definitions
// 
// SPEC REFERENCE: Custom (no external spec - internal architecture)
// PURPOSE: Defines the sequential stages of the rendering pipeline
// 
// DISTINCTION FROM EnginePhase:
//   - EnginePhase: Execution context phases (when JS/observers can run)
//   - PipelineStage: Data transformation stages (what transformation is active)
//
// Pipeline Flow:
//   InputBytes → ParsedTokens → DOMTree → StyleTree → LayoutTree → DisplayList → RasterOutput
// =============================================================================

using System;

namespace FenBrowser.Core.Engine
{
    /// <summary>
    /// Defines the stages of the rendering pipeline.
    /// Each stage produces an immutable snapshot consumed by the next stage.
    /// 
    /// INVARIANT: Stages must proceed in order. No stage may read output from a future stage.
    /// INVARIANT: Each stage's output is immutable once produced.
    /// </summary>
    public enum PipelineStage
    {
        /// <summary>
        /// No pipeline activity. Initial state or between frames.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Stage 1: Raw bytes are being tokenized into HTML/CSS tokens.
        /// Input: byte[] (raw document)
        /// Output: TokenStream
        /// </summary>
        Tokenizing = 1,

        /// <summary>
        /// Stage 2: Tokens are being parsed into DOM tree.
        /// Input: TokenStream
        /// Output: DOMTree (Node hierarchy)
        /// </summary>
        Parsing = 2,

        /// <summary>
        /// Stage 3: CSS cascade and computed style resolution.
        /// Input: DOMTree + Stylesheets
        /// Output: StyleTree (ComputedStyle per element)
        /// </summary>
        Styling = 3,

        /// <summary>
        /// Stage 4: Layout computation (box model, positioning).
        /// Input: StyleTree
        /// Output: LayoutTree (LayoutBox per element with geometry)
        /// </summary>
        Layout = 4,

        /// <summary>
        /// Stage 5: Building paint commands from layout.
        /// Input: LayoutTree
        /// Output: DisplayList (sorted draw commands)
        /// </summary>
        Painting = 5,

        /// <summary>
        /// Stage 6: Rasterizing display list to pixels.
        /// Input: DisplayList
        /// Output: RasterOutput (GPU texture or bitmap)
        /// </summary>
        Rasterizing = 6,

        /// <summary>
        /// Stage 7: Presenting to screen (buffer swap).
        /// Input: RasterOutput
        /// Output: Screen presentation complete
        /// </summary>
        Presenting = 7
    }

    /// <summary>
    /// Extension methods for PipelineStage.
    /// </summary>
    public static class PipelineStageExtensions
    {
        /// <summary>
        /// Returns true if this stage comes before the other stage in the pipeline.
        /// </summary>
        public static bool IsBefore(this PipelineStage current, PipelineStage other)
        {
            return (int)current < (int)other;
        }

        /// <summary>
        /// Returns true if this stage comes after the other stage in the pipeline.
        /// </summary>
        public static bool IsAfter(this PipelineStage current, PipelineStage other)
        {
            return (int)current > (int)other;
        }

        /// <summary>
        /// Returns true if this is an active processing stage (not Idle or Presenting).
        /// </summary>
        public static bool IsProcessing(this PipelineStage stage)
        {
            return stage != PipelineStage.Idle && stage != PipelineStage.Presenting;
        }

        /// <summary>
        /// Returns the name of the output data type for this stage.
        /// </summary>
        public static string OutputTypeName(this PipelineStage stage)
        {
            return stage switch
            {
                PipelineStage.Idle => "None",
                PipelineStage.Tokenizing => "TokenStream",
                PipelineStage.Parsing => "DOMTree",
                PipelineStage.Styling => "StyleTree",
                PipelineStage.Layout => "LayoutTree",
                PipelineStage.Painting => "DisplayList",
                PipelineStage.Rasterizing => "RasterOutput",
                PipelineStage.Presenting => "ScreenPresent",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Returns the description for logging/debugging.
        /// </summary>
        public static string GetDescription(this PipelineStage stage)
        {
            return stage switch
            {
                PipelineStage.Idle => "Pipeline idle",
                PipelineStage.Tokenizing => "Tokenizing HTML/CSS input",
                PipelineStage.Parsing => "Building DOM tree from tokens",
                PipelineStage.Styling => "Computing CSS cascade and styles",
                PipelineStage.Layout => "Computing box layout geometry",
                PipelineStage.Painting => "Building display list",
                PipelineStage.Rasterizing => "Rasterizing to GPU",
                PipelineStage.Presenting => "Presenting to screen",
                _ => "Unknown stage"
            };
        }
    }
}
