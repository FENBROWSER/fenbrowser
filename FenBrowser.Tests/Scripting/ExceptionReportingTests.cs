using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §8.1.4.6 "report an exception": an exception that escapes a listener, a timer
/// or a script reaches the window as an ErrorEvent, and window.onerror gets the special
/// five-argument call of §8.1.8.1.
/// </summary>
public sealed class ExceptionReportingTests
{
    private const string Recorder =
        "globalThis.__errors = [];" +
        "window.addEventListener('error', function (e) {" +
        "  __errors.push((e instanceof ErrorEvent) + ':' + (e.error && e.error.message) + ':' + e.cancelable);" +
        "});";

    [Fact]
    public async Task AnElementListenerExceptionReachesTheWindowAndTheNextListenerStillRuns()
    {
        var engine = await CreateEngineAsync("<div id=d></div>");
        engine.Evaluate(Recorder +
            "var d = document.getElementById('d');" +
            "d.addEventListener('x', function () { throw new Error('listener'); });" +
            "d.addEventListener('x', function () { __errors.push('second'); });" +
            "d.dispatchEvent(new Event('x'));");

        Assert.Equal("true:listener:true,second", engine.Evaluate("__errors.join(',')")?.ToString());
    }

    [Fact]
    public async Task ATimerExceptionReachesTheWindow()
    {
        var engine = await CreateEngineAsync("");
        engine.Evaluate(Recorder + "setTimeout(function () { throw new Error('timer'); }, 0);");

        Assert.Equal("true:timer:true", await WaitForAsync(engine, "__errors.join(',')"));
    }

    [Fact]
    public async Task WindowOnerrorGetsFiveArgumentsAndReturningTrueCancels()
    {
        var engine = await CreateEngineAsync("");
        var result = engine.Evaluate(
            "(function () {" +
            "  var seen;" +
            "  window.onerror = function (m, f, l, c, e) { seen = [typeof m, f, l, c, e.message].join('|'); return true; };" +
            "  var ev = new ErrorEvent('error', { message: 'm', filename: 'f.js', lineno: 3, colno: 4, error: new Error('E'), cancelable: true });" +
            "  var notCancelled = window.dispatchEvent(ev);" +
            "  window.onerror = null;" +
            "  return seen + '|' + notCancelled;" +
            "})()")?.ToString();

        Assert.Equal("string|f.js|3|4|E|false", result);
    }

    [Fact]
    public async Task AnErrorHandlerThatThrowsDoesNotLoop()
    {
        var engine = await CreateEngineAsync("<div id=d></div>");
        engine.Evaluate(
            "globalThis.__count = 0;" +
            "window.addEventListener('error', function () { __count++; throw new Error('again'); });" +
            "var d = document.getElementById('d');" +
            "d.addEventListener('x', function () { throw new Error('first'); });" +
            "d.dispatchEvent(new Event('x'));");

        Assert.Equal("1", engine.Evaluate("String(__count)")?.ToString());
    }

    [Fact]
    public async Task ReportErrorFiresTheSameEvent()
    {
        var engine = await CreateEngineAsync("");
        engine.Evaluate(Recorder + "reportError(new Error('asked'));");

        Assert.Equal("true:asked:true", engine.Evaluate("__errors.join(',')")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
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
