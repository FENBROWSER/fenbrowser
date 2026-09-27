using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// WebCodecs: the encoded chunks and colour space, AudioData and VideoFrame, and the
/// AudioDecoder and VideoDecoder over the engine's own decoder registry.
/// </summary>
[Collection("Media Engine State")]
public sealed class WebCodecsTests
{
    [Fact]
    public async Task TheInterfacesAreOnTheGlobal()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("function|function|function|function|function|function|function", engine.Evaluate("""
            [typeof EncodedAudioChunk, typeof EncodedVideoChunk, typeof VideoColorSpace,
             typeof AudioData, typeof VideoFrame, typeof AudioDecoder, typeof VideoDecoder].join('|')
            """)?.ToString());
    }

    [Fact]
    public async Task AnEncodedVideoChunkCarriesItsBytes()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("key|10|300|3|10,11,12", engine.Evaluate("""
            var chunk = new EncodedVideoChunk({type:'key', timestamp:10, duration:300, data:new Uint8Array([0x0A,0x0B,0x0C])});
            var dest = new Uint8Array(3);
            chunk.copyTo(dest);
            [chunk.type, chunk.timestamp, chunk.duration, chunk.byteLength, Array.from(dest).join(',')].join('|')
            """)?.ToString());

        // A chunk with no duration reports null, and a destination that is too small throws.
        Assert.Equal("null|TypeError", engine.Evaluate("""
            var c2 = new EncodedVideoChunk({type:'delta', timestamp:100, data:new Uint8Array([0,1,2])});
            var thrown = '';
            try { c2.copyTo(new Uint8Array(2)); } catch (e) { thrown = e.name; }
            [String(c2.duration), thrown].join('|')
            """)?.ToString());
    }

    [Fact]
    public async Task VideoColorSpaceRoundTripsThroughToJson()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("bt709|pq|rgb|true", engine.Evaluate("""
            var json = new VideoColorSpace({primaries:'bt709', transfer:'pq', matrix:'rgb', fullRange:true}).toJSON();
            [json.primaries, json.transfer, json.matrix, String(json.fullRange)].join('|')
            """)?.ToString());

        // Members that were not given read back as null, and a bad enum value throws.
        Assert.Equal("null|null|null|null|TypeError", engine.Evaluate("""
            var empty = new VideoColorSpace({}).toJSON();
            var thrown = '';
            try { new VideoColorSpace({primaries:'nonsense'}); } catch (e) { thrown = e.name; }
            [String(empty.primaries), String(empty.transfer), String(empty.matrix), String(empty.fullRange), thrown].join('|')
            """)?.ToString());
    }

    [Fact]
    public async Task AudioDataCarriesItsSamples()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("f32-planar|8000|100|2|12500|1.5", engine.Evaluate("""
            var samples = new Float32Array(200);
            samples[100] = 1.5;
            var data = new AudioData({timestamp:1234, data:samples, numberOfFrames:100, numberOfChannels:2, sampleRate:8000, format:'f32-planar'});
            var dest = new Float32Array(100);
            data.copyTo(dest, {planeIndex:1});
            [data.format, data.sampleRate, data.numberOfFrames, data.numberOfChannels, data.duration, String(dest[0])].join('|')
            """)?.ToString());

        Assert.Equal("null|0|0|0", engine.Evaluate("""
            var d = new AudioData({timestamp:0, data:new Float32Array(8), numberOfFrames:4, numberOfChannels:2, sampleRate:8000, format:'f32-planar'});
            d.close();
            [String(d.format), d.sampleRate, d.numberOfFrames, d.numberOfChannels].join('|')
            """)?.ToString());
    }

    [Theory]
    [InlineData("new AudioData({timestamp:0, data:new Int16Array(200), numberOfFrames:100, numberOfChannels:2, sampleRate:8000, format:'f32-planar'})")]
    [InlineData("new AudioData({timestamp:0, data:new Float32Array(200), numberOfFrames:0, numberOfChannels:2, sampleRate:8000, format:'f32-planar'})")]
    [InlineData("new AudioData({timestamp:0, data:new Float32Array(200), numberOfFrames:100, numberOfChannels:0, sampleRate:8000, format:'f32-planar'})")]
    [InlineData("new AudioData({data:new Float32Array(200), numberOfFrames:100, numberOfChannels:2, sampleRate:8000, format:'f32-planar'})")]
    [InlineData("new AudioData({timestamp:0, numberOfFrames:100, numberOfChannels:2, sampleRate:8000, format:'f32-planar'})")]
    public async Task InvalidAudioDataThrowsATypeError(string expression)
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("TypeError", engine.Evaluate($"(function(){{ try {{ {expression}; return 'none'; }} catch (e) {{ return e.name; }} }})()")?.ToString());
    }

    [Fact]
    public async Task AVideoFrameCarriesItsPlanes()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("I420|4|2|4|2|1000|12", engine.Evaluate("""
            var bytes = new Uint8Array(4 * 2 + 2 * 1 * 2);
            var frame = new VideoFrame(bytes, {format:'I420', codedWidth:4, codedHeight:2, timestamp:1000});
            [frame.format, frame.codedWidth, frame.codedHeight, frame.displayWidth, frame.displayHeight,
             frame.timestamp, frame.allocationSize()].join('|')
            """)?.ToString());

        Assert.Equal("TypeError|null|0", engine.Evaluate("""
            var thrown = '';
            try { new VideoFrame(new Uint8Array(3), {format:'I420', codedWidth:4, codedHeight:2, timestamp:0}); } catch (e) { thrown = e.name; }
            var f = new VideoFrame(new Uint8Array(12), {format:'I420', codedWidth:4, codedHeight:2, timestamp:0});
            f.close();
            [thrown, String(f.format), f.codedWidth].join('|')
            """)?.ToString());
    }

    [Fact]
    public async Task ADecoderReportsWhichConfigurationsAreSupported()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            globalThis.__r = '';
            Promise.all([
                VideoDecoder.isConfigSupported({codec:'vp8'}),
                VideoDecoder.isConfigSupported({codec:'bogus'}),
                AudioDecoder.isConfigSupported({codec:'opus', sampleRate:48000, numberOfChannels:2}),
                AudioDecoder.isConfigSupported({codec:'bogus', sampleRate:48000, numberOfChannels:2})
            ]).then(function (r) { __r = r.map(function (i) { return String(i.supported) + ':' + i.config.codec; }).join(','); },
                    function (e) { __r = 'rejected:' + e.name; });
            """);
        Assert.Equal("true:vp8,false:bogus,true:opus,false:bogus", await WaitForAsync(engine, "__r"));
    }

    [Fact]
    public async Task ADecoderRefusesAConfigurationWithoutACodec()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            globalThis.__r = '';
            VideoDecoder.isConfigSupported({}).then(function () { __r = 'resolved'; }, function (e) { __r = e.name; });
            """);
        Assert.Equal("TypeError", await WaitForAsync(engine, "__r"));

        Assert.Equal("TypeError|TypeError|InvalidStateError", engine.Evaluate("""
            var results = [];
            var d = new VideoDecoder({output: function () {}, error: function () {}});
            try { d.configure({codec: ''}); results.push('none'); } catch (e) { results.push(e.name); }
            try { new VideoDecoder({output: function () {}}); results.push('none'); } catch (e) { results.push(e.name); }
            var d2 = new VideoDecoder({output: function () {}, error: function () {}});
            try { d2.decode(new EncodedVideoChunk({type:'key', timestamp:0, data:new Uint8Array(1)})); results.push('none'); } catch (e) { results.push(e.name); }
            results.join('|')
            """)?.ToString());
    }

    /// <summary>
    /// A codec string padded with whitespace names nothing the registry has, so it is a
    /// well formed configuration that is simply unsupported, and configure() reports it
    /// through the error callback rather than throwing.
    /// </summary>
    [Fact]
    public async Task AnUnsupportedCodecReachesTheErrorCallback()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            globalThis.__r = '';
            var d = new VideoDecoder({output: function () {}, error: function (e) { __r = e.name + ':' + d.state; }});
            d.configure({codec: '  vp8  '});
            """);
        Assert.Equal("NotSupportedError:closed", await WaitForAsync(engine, "__r"));
    }

    /// <summary>
    /// The whole path: a configured VideoDecoder takes the coded frames of a fixture and
    /// hands back VideoFrames with the engine's own decoder behind them.
    /// </summary>
    [Fact]
    public async Task AVideoDecoderDecodesRealChunks()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("""
            globalThis.__frames = [];
            globalThis.__err = '';
            globalThis.__decoder = new VideoDecoder({
                output: function (frame) { __frames.push(frame.codedWidth + 'x' + frame.codedHeight + '@' + frame.timestamp + ':' + frame.format); frame.close(); },
                error: function (e) { __err = e.name + ':' + e.message; }
            });
            __decoder.configure({codec: 'vp8', codedWidth: 64, codedHeight: 48});
            globalThis.__state = __decoder.state;
            """);
        Assert.Equal("configured", engine.Evaluate("globalThis.__state")?.ToString());
        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__err")?.ToString());
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

            await Task.Delay(20);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }
}
