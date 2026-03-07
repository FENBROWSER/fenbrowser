# Audit Report Accuracy Analysis
Total files checked: 283
CSV vs JSON mismatches: 0

## Flag Accuracy Heuristics
| Flag | Total Reported | Matched Heuristic | Est. Accuracy |
|---|---|---|---|
| todo | 15 | 13 | 86.7% |
| notImplemented | 4 | 4 | 100.0% |
| genericException | 2 | 0 | 0.0% |
| asyncVoid | 1 | 0 | 0.0% |
| swallowCatch | 402 | 401 | 99.8% |
| taskRun | 51 | 51 | 100.0% |
| gcCollect | 7 | 7 | 100.0% |
| lockUsage | 234 | 234 | 100.0% |
| staticMutable | 116 | 116 | 100.0% |
| skiaNativeCtor | 196 | 196 | 100.0% |

## Samples
### todo
- `MATCH [FenRuntime.cs:11780] double now = Convert.ToDouble(DateTime.Now.Ticks) / 10000.0; // Simulated high-res time (ms)`
- `MATCH [JsTypedArray.cs:161] : FenValue.FromNumber(BitConverter.ToDouble(tmp, 0));`
- `MATCH [DocumentWrapper.cs:581] AddToDocumentListenerStore(type, callback, capture, once, passive);`
- `MATCH [DocumentWrapper.cs:623] private void AddToDocumentListenerStore(string type, FenValue callback, bool capture, bool once, bool passive)`
- `MATCH [TextWrapper.cs:28] // Todo: Implement SplitText`
### notImplemented
- `MATCH [ModuleLoader.cs:72] throw new NotSupportedException($"Default loader only supports file://. Requested: {uri}");`
- `MATCH [ModuleLoader.cs:493] catch (NotImplementedException ex)`
- `MATCH [ModuleLoader.cs:495] throw new NotSupportedException($"Bytecode-only mode: module compilation unsupported. {ex.Message}", ex);`
- `MATCH [BytecodeCompiler.cs:3353] throw new NotSupportedException($"Compiler: {nodeKind} with 'with' statement is not supported in bytecode-only function bodies.");`
### genericException
- `FALSE POSITIVE? [FenRuntime.cs:8376] throw new Exception(err);`
- `FALSE POSITIVE? [FenRuntime.cs:8396] throw new Exception($"EvalError: {ex.Message}");`
### asyncVoid
- `FALSE POSITIVE? [BrowserApi.cs:3624] // Async void is generally bad, but this is an interface method called from JS bridge`
### swallowCatch
- `MATCH [TestFenEngine.cs:119] catch (Exception ex)`
- `MATCH [TestFenEngine.cs:190] catch (Exception ex)`
- `MATCH [TestFenEngine.cs:259] catch (Exception ex)`
- `MATCH [TestFenEngine.cs:295] catch (Exception ex)`
- `MATCH [TestFenEngine.cs:335] catch (Exception ex)`
### taskRun
- `MATCH [ExecutionContext.cs:28] Task.Run(async () =>`
- `MATCH [ExecutionContext.cs:37] Task.Run(() => action());`
- `MATCH [FenRuntime.cs:6491] _ = Task.Run(async () =>`
- `MATCH [FenRuntime.cs:6518] _ = Task.Run(async () =>`
- `MATCH [FenRuntime.cs:6567] _ = Task.Run(async () =>`
### gcCollect
- `MATCH [DevToolsCore.cs:657] GC.Collect();`
- `MATCH [DevToolsCore.cs:659] GC.Collect();`
- `MATCH [Test262Runner.cs:174] GC.Collect(2, GCCollectionMode.Aggressive, true, true);`
- `MATCH [Test262Runner.cs:550] GC.Collect(2, GCCollectionMode.Aggressive, true, true);`
- `MATCH [Test262Runner.cs:589] GC.Collect(2, GCCollectionMode.Aggressive, true, true);`
### lockUsage
- `MATCH [WebCompatibilityInterventions.cs:135] lock (_gate)`
- `MATCH [WebCompatibilityInterventions.cs:144] lock (_gate)`
- `MATCH [WebCompatibilityInterventions.cs:154] lock (_gate)`
- `MATCH [WebCompatibilityInterventions.cs:173] lock (_gate)`
- `MATCH [WebCompatibilityInterventions.cs:183] lock (_gate)`
### staticMutable
- `MATCH [ISvgRenderer.cs:106] public static SvgRenderLimits Default => new SvgRenderLimits`
- `MATCH [ISvgRenderer.cs:118] public static SvgRenderLimits Strict => new SvgRenderLimits`
- `MATCH [WebCompatibilityInterventions.cs:124] public static WebCompatibilityInterventionRegistry Instance => _instance.Value;`
- `MATCH [AnimationFrameScheduler.cs:70] private static AnimationFrameScheduler _instance;`
- `MATCH [AnimationFrameScheduler.cs:75] public static AnimationFrameScheduler Instance => _instance ??= new AnimationFrameScheduler();`
### skiaNativeCtor
- `MATCH [SkiaTextMeasurer.cs:20] using var paint = new SKPaint`
- `MATCH [SkiaTextMeasurer.cs:34] using var font = new SKFont(typeface, fontSize);`
- `MATCH [InlineLayoutComputer.cs:75] using var containerFont = new SKFont(containerTypeface, containerFontSize);`
- `MATCH [InlineLayoutComputer.cs:130] using var ellPaint = new SKPaint();`
- `MATCH [InlineLayoutComputer.cs:173] using var trimPaint = new SKPaint`