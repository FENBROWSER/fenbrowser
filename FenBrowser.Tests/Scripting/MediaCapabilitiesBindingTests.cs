using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The Media Capabilities binding: <c>navigator.mediaCapabilities.decodingInfo()</c> and
/// <c>encodingInfo()</c>, their WebIDL conversions and the shape of the answer, following
/// the cases in the <c>media-capabilities</c> WPT files.
/// </summary>
[Collection("Media Engine State")]
public sealed class MediaCapabilitiesBindingTests
{
    private const string MinimalVideo = "{contentType:'video/webm; codecs=\"vp09.00.10.08\"', width:800, height:600, bitrate:3000, framerate:24}";
    private const string MinimalAudio = "{contentType:'audio/webm; codecs=\"opus\"'}";

    [Fact]
    public async Task TheInterfacesAreOnNavigator()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("function|function|object|true", engine.Evaluate("""
            [typeof MediaCapabilities, typeof MediaCapabilitiesInfo, typeof navigator.mediaCapabilities,
             String(navigator.mediaCapabilities instanceof MediaCapabilities)].join('|')
            """)?.ToString());
    }

    [Theory]
    // No configuration, an empty one, one with no type, and one with no audio or video.
    [InlineData("navigator.mediaCapabilities.decodingInfo()")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({video: V, audio: A})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file'})")]
    // Invalid decoding types.
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'foobar', video: V})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'record', video: V})")]
    // Frame rates that are not a finite number above zero.
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {framerate:-1})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {framerate:0})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {framerate:Infinity})})")]
    // Required members that are missing.
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: {contentType:'video/webm; codecs=\"vp8\"', width:800, height:600}})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', audio: {}})")]
    // Content types that are not one valid media MIME type naming one codec of the kind.
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {contentType:'fgeoa'})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {contentType:'video/webm;'})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {contentType:'audio/fgeoa'})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {contentType:'video/webm'})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', audio: {contentType:'application/ogg; codecs=theora'}})")]
    // Enum members that are not valid enum values.
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {hdrMetadataType:''})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {colorGamut:true})})")]
    [InlineData("navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {transferFunction:3})})")]
    // encodingInfo takes the encoding types only.
    [InlineData("navigator.mediaCapabilities.encodingInfo({type:'file', video: V})")]
    public async Task InvalidConfigurationsRejectWithATypeError(string expression)
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate($"globalThis.__r = ''; var V = {MinimalVideo}; var A = {MinimalAudio};");
        engine.Evaluate($"({expression}).then(function () {{ __r = 'resolved'; }}, function (e) {{ __r = e.name; }});");
        Assert.Equal("TypeError", await WaitForAsync(engine, "__r"));
    }

    [Theory]
    [InlineData("{type:'file', video: V, audio: A}")]
    [InlineData("{type:'media-source', video: V}")]
    [InlineData("{type:'file', audio: Object.assign({}, A, {spatialRendering:true})}")]
    [InlineData("{type:'webrtc', video: V}")]
    public async Task ValidConfigurationsResolveWithAMediaCapabilitiesInfo(string configuration)
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate($"globalThis.__r = ''; var V = {MinimalVideo}; var A = {MinimalAudio};");
        engine.Evaluate($$"""
            navigator.mediaCapabilities.decodingInfo({{configuration}}).then(function (info) {
                __r = [typeof info.supported, typeof info.smooth, typeof info.powerEfficient,
                       typeof info.keySystemAccess, String(info instanceof MediaCapabilitiesInfo)].join('|');
            }, function (e) { __r = 'rejected:' + e.name; });
            """);
        Assert.Equal("boolean|boolean|boolean|object|true", await WaitForAsync(engine, "__r"));
    }

    [Fact]
    public async Task TheAnswerFollowsWhatTheEngineCanDecode()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate($"globalThis.__r = ''; var V = {MinimalVideo};");
        engine.Evaluate("""
            Promise.all([
                navigator.mediaCapabilities.decodingInfo({type:'file', video: V}),
                // A VP9 string with no colour fields means BT.709, so rec2020 and PQ describe something else.
                navigator.mediaCapabilities.decodingInfo({type:'file', video: Object.assign({}, V, {colorGamut:'rec2020', transferFunction:'pq'})}),
                // Nothing here encodes.
                navigator.mediaCapabilities.encodingInfo({type:'record', video: V}),
                // No WebRTC stack.
                navigator.mediaCapabilities.decodingInfo({type:'webrtc', video: V})
            ]).then(function (r) { __r = r.map(function (i) { return String(i.supported); }).join(','); },
                    function (e) { __r = 'rejected:' + e.name; });
            """);
        Assert.Equal("true,false,false,false", await WaitForAsync(engine, "__r"));
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
