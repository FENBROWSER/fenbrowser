using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The Web Audio surface in the realm (WA1): contexts, AudioBuffer, AudioParam, the
/// connection rules, and an offline render that comes back as samples.
/// </summary>
public sealed class WebAudioRealmTests
{
    [Fact]
    public async Task AnOfflineRenderComesBackAsTheScheduledSamples()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__r = '';" +
            "var ctx = new OfflineAudioContext(1, 256, 48000);" +
            "var src = new ConstantSourceNode(ctx, { offset: 0.5 });" +
            "var gain = new GainNode(ctx, { gain: 2 });" +
            "src.connect(gain).connect(ctx.destination);" +
            "src.start(128 / 48000);" +
            "ctx.startRendering().then(function (b) { var d = b.getChannelData(0); __r = [d[0], d[127], d[128], d[255], ctx.state].join(','); });");

        // WA 1.3.4: the context is already closed when startRendering()'s promise resolves.
        Assert.Equal("0,0,1,1,closed", await WaitForAsync(engine, "__r"));
    }

    [Fact]
    public async Task AudioParamRejectsWhatWebIdlAndTheSpecRefuse()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var ctx = new OfflineAudioContext(1, 128, 8000), p = ctx.createGain().gain, out = [];" +
            "  function name(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }" +
            "  out.push(name(function () { p.setValueAtTime(NaN, 0); }));" +
            "  out.push(name(function () { p.setValueAtTime(1, -1); }));" +
            "  out.push(name(function () { p.exponentialRampToValueAtTime(0, 1); }));" +
            "  out.push(name(function () { p.setValueCurveAtTime([1], 0, 1); }));" +
            "  p.setValueCurveAtTime([0, 1], 1, 1);" +
            "  out.push(name(function () { p.setValueAtTime(3, 1.5); }));" +
            "  out.push(p.setValueAtTime(1, 3) === p);" +
            "  out.push([p.defaultValue, p.minValue === -3.4028234663852886e38, p.automationRate].join('/'));" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("TypeError,RangeError,RangeError,InvalidStateError,NotSupportedError,true,1/true/a-rate", result);
    }

    [Fact]
    public async Task DisconnectChecksWhatWasConnected()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var ctx = new OfflineAudioContext(1, 128, 8000), a = ctx.createGain(), b = ctx.createGain(), out = [];" +
            "  function name(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }" +
            "  a.connect(b);" +
            "  out.push(name(function () { a.disconnect(ctx.destination); }));" +
            "  out.push(name(function () { a.disconnect(1); }));" +
            "  out.push(name(function () { a.connect(b, 1); }));" +
            "  out.push(name(function () { a.disconnect(b); }));" +
            "  out.push(name(function () { a.disconnect(b); }));" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("InvalidAccessError,IndexSizeError,IndexSizeError,ok,InvalidAccessError", result);
    }

    [Fact]
    public async Task AudioBufferValidatesAndCopies()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var out = [];" +
            "  function name(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }" +
            "  out.push(name(function () { new AudioBuffer({ length: 0, sampleRate: 8000 }); }));" +
            "  out.push(name(function () { new AudioBuffer({ length: 1, sampleRate: 100 }); }));" +
            "  var b = new AudioBuffer({ length: 4, numberOfChannels: 2, sampleRate: 8000 });" +
            "  b.copyToChannel(new Float32Array([1, 2]), 1, 2);" +
            "  var dest = new Float32Array(4); b.copyFromChannel(dest, 1);" +
            "  out.push(Array.from(dest).join(' '));" +
            "  out.push(name(function () { b.getChannelData(2); }));" +
            "  out.push(b.duration);" +
            "  return out.join(',');" +
            "})()")?.ToString();

        Assert.Equal("NotSupportedError,NotSupportedError,0 0 1 2,IndexSizeError,0.0005", result);
    }

    [Fact]
    public async Task AbstractInterfacesCannotBeConstructed()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  function name(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }" +
            "  return [name(function () { new AudioNode(); }), name(function () { new AudioParam(); }), name(function () { new BaseAudioContext(); })," +
            "          Object.prototype.toString.call(new OfflineAudioContext(1, 1, 8000).createGain())].join(',');" +
            "})()")?.ToString();

        Assert.Equal("TypeError,TypeError,TypeError,[object GainNode]", result);
    }

    [Fact]
    public async Task AnOscillatorRendersASineFromPageScript()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__r = '';" +
            "var ctx = new OfflineAudioContext(1, 128, 44100);" +
            "var osc = new OscillatorNode(ctx, { frequency: 441 });" +
            "osc.connect(ctx.destination); osc.start();" +
            "ctx.startRendering().then(function (b) {" +
            "  var d = b.getChannelData(0), worst = 0;" +
            "  for (var i = 0; i < d.length; i++) worst = Math.max(worst, Math.abs(d[i] - Math.sin(2 * Math.PI * 441 * i / 44100)));" +
            "  __r = String(worst < 1e-4);" +
            "});");

        Assert.Equal("true", await WaitForAsync(engine, "__r"));
    }

    [Fact]
    public async Task GetFrequencyResponseUsesTheTypeJustSet()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var ctx = new OfflineAudioContext(1, 128, 44100), f = ctx.createBiquadFilter();" +
            "  f.type = 'highpass'; f.frequency.value = 1000;" +
            "  var hz = new Float32Array([10, 20000, 30000]), mag = new Float32Array(3), phase = new Float32Array(3);" +
            "  f.getFrequencyResponse(hz, mag, phase);" +
            "  return [mag[0] < 0.01, Math.abs(mag[1] - 1) < 0.01, isNaN(mag[2])].join(',');" +
            "})()")?.ToString();

        Assert.Equal("true,true,true", result);
    }

    [Fact]
    public async Task AParamValueReadsBackWithinItsNominalRange()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var ctx = new OfflineAudioContext(1, 128, 48000), p = ctx.createStereoPanner().pan;" +
            "  p.value = 3; var a = p.value; p.value = -3; return a + ',' + p.value;" +
            "})()")?.ToString();

        Assert.Equal("1,-1", result);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }
}
