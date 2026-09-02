using System.Collections.Generic;
using System.Globalization;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tooling;

/// <summary>
/// Drives a reCAPTCHA "I'm not a robot" checkbox end to end so the flow can be
/// re-measured after every engine change: navigate, wait for the anchor widget,
/// click it the way a user does, then watch the widget, the response token and
/// the page URL until the challenge resolves or the observation window expires.
/// </summary>
internal static class CaptchaFlowRunner
{
    private const string AnchorFrameSelector = "iframe[src*='recaptcha']";

    // Runs inside the anchor frame.
    private const string AnchorStateScript =
        "(function(){" +
        "var a=document.getElementById('recaptcha-anchor');" +
        "if(!a)return 'anchor=absent';" +
        "return 'anchor='+(a.className||'')+';aria-checked='+a.getAttribute('aria-checked');" +
        "})()";

    // Runs in the top document.
    private const string TopStateScript =
        "(function(){" +
        "var t=document.getElementById('g-recaptcha-response');" +
        "var tok=t&&t.value?t.value.length:0;" +
        "var frames=document.querySelectorAll('iframe');" +
        "var bframes=0;" +
        "for(var i=0;i<frames.length;i++){var s=frames[i].getAttribute('src')||'';if(s.indexOf('bframe')>=0)bframes++;}" +
        "return 'url='+location.href+';token='+tok+';iframes='+frames.length+';bframes='+bframes+" +
        "';readyState='+document.readyState;" +
        "})()";

    // Runs inside a frame: identifies the document and returns every script it holds,
    // inline text included, because that bootstrap is what the engine trips over.
    private const string FrameSourceScript =
        "(function(){" +
        "var out='HREF='+location.href+'\\nNAME='+window.name+'\\nTITLE='+document.title+'\\n';" +
        "var s=document.getElementsByTagName('script');" +
        "out+='SCRIPTS='+s.length+'\\n';" +
        "for(var i=0;i<s.length;i++){" +
        "var t=s[i].textContent||s[i].text||'';" +
        "out+='--- script '+i+' src='+(s[i].getAttribute('src')||'')+' len='+t.length+'\\n'+t+'\\n';" +
        "}" +
        "out+='=== BODY ===\\n'+(document.body?document.body.innerHTML:'<no body>');" +
        "return out;" +
        "})()";

    // The widget stalls with no request and no error, so start by establishing
    // what the realm can actually do rather than assuming: whether it has the
    // networking globals at all, whether a request really leaves (fetch
    // something same-origin and read the status back), and whether the basics
    // reCAPTCHA leans on — expando round-trip, readyState, its own namespace —
    // are intact.
    private const string FrameNetworkProbeScript =
        "(function(){" +
        "var r='xhr='+(typeof XMLHttpRequest)+' syncXhr='+(typeof __fenSyncXhr)+" +
        "' fetch='+(typeof fetch)+' syncFetch='+(typeof __fenSyncFetch);" +
        // Only once per realm: this probe's own request would otherwise drown
        // out the page's calls in the recorder below.
        "try{if(window.__fenProbedNet){r+=' probe=skipped';}else{window.__fenProbedNet=1;" +
        "var x=new XMLHttpRequest();" +
        "x.open('GET','https://www.google.com/favicon.ico',true);x.send();" +
        "r+=' probeStatus='+x.status+' readyState='+x.readyState+" +
        "' bytes='+(x.responseText?x.responseText.length:0);}}" +
        "catch(e){r+=' probeThrew='+e;}" +
        // Closure keeps its listener maps in expando properties on the element
        // (closure_lm_<id>), so if an expando does not round-trip, handlers are
        // registered and then never found again.
        "try{var d=document.createElement('div');d.closure_probe_1={a:1};" +
        "r+=' expando='+(d.closure_probe_1&&d.closure_probe_1.a===1?'ok':'lost');}" +
        "catch(e){r+=' expandoThrew='+e;}" +
        // reCAPTCHA fingerprints the client and simply stops if it decides the
        // page is automated, which looks identical to a widget that never
        // finishes: no request, no error.
        "r+=' webdriver='+navigator.webdriver+' plugins='+((navigator.plugins||[]).length)+" +
        "' langs='+((navigator.languages||[]).join(','))+' ua='+(navigator.userAgent||'').slice(0,60);" +
        "r+=' readyState='+document.readyState+' recaptcha='+(typeof recaptcha);" +
        "try{r+=' rcKeys='+(typeof recaptcha==='object'?Object.keys(recaptcha).join('|'):'-');}catch(e){}" +
        "return r;})()";

    // The verification call never reaches the network, and nothing throws. That
    // leaves two very different stories: reCAPTCHA asks and the request is
    // dropped on our side, or it never asks. Wrap every outbound call in the
    // realm and record what it was handed, so the answer is not a guess.
    private const string InstallNetworkRecorderScript =
        "(function(){" +
        "if(window.__fenNetLog)return 'already';" +
        "window.__fenNetLog=[];" +
        "var log=function(s){try{window.__fenNetLog.push(s);}catch(e){}};" +
        "try{var O=XMLHttpRequest.prototype.open,S=XMLHttpRequest.prototype.send;" +
        "XMLHttpRequest.prototype.open=function(m,u){log('open '+m+' '+u);return O.apply(this,arguments);};" +
        "XMLHttpRequest.prototype.send=function(b){log('send bodyLen='+(b==null?0:String(b).length));" +
        "return S.apply(this,arguments);};}catch(e){log('xhrHookThrew '+e);}" +
        "try{if(window.fetch){var F=window.fetch;" +
        "window.fetch=function(u){log('fetch '+u);return F.apply(this,arguments);};}}catch(e){}" +
        "try{if(navigator.sendBeacon){var B=navigator.sendBeacon;" +
        "navigator.sendBeacon=function(u){log('beacon '+u);return B.apply(this,arguments);};}}catch(e){}" +
        "try{var C=document.createElement;document.createElement=function(t){" +
        "var el=C.apply(this,arguments);" +
        "if(String(t).toLowerCase()==='form')log('createElement form');" +
        "return el;};}catch(e){}" +
        "return 'installed';})()";

    // The bootstrap call builds the widget, reports nothing and leaves no DOM
    // behind. Its own try/catch would swallow whatever went wrong, so run the
    // very same inline script again with our own catch around it and read the
    // error out.
    private const string ReplayBootstrapScript =
        "(function(){" +
        "var s=document.getElementsByTagName('script');" +
        "for(var i=0;i<s.length;i++){" +
        "var t=s[i].textContent||s[i].text||'';" +
        "if(t.indexOf('Main.init')<0)continue;" +
        "try{(0,eval)(t);}catch(e){" +
        "return 'threw '+(e&&e.name?e.name:'?')+': '+(e&&e.message?e.message:String(e))+" +
        "' @ '+((e&&e.stack)?String(e.stack).replace(/\\s+/g,' ').slice(0,300):'no stack');}" +
        "return 'ran without throwing; anchor='+(document.getElementById('recaptcha-anchor')?'built':'still missing');" +
        "}" +
        "return 'no bootstrap script in this document';})()";

    private const string ReadNetworkRecorderScript =
        "(function(){var l=window.__fenNetLog||[];" +
        "return l.length+' calls: '+l.join(' | ');})()";

    /// <summary>
    /// Writes the live source of every frame we can reach. reCAPTCHA builds the
    /// anchor and challenge documents from one-shot URLs, so the only way to see
    /// what its inline bootstrap actually ran is to read it back out of the DOM.
    /// </summary>
    private static async Task DumpFramesAsync(BrowserHost host, string tag, HashSet<string> alreadyDumped)
    {
        string[] frameIds;
        try
        {
            frameIds = await host.FindElementsAsync("css selector", "iframe").ConfigureAwait(false)
                       ?? Array.Empty<string>();
        }
        catch
        {
            return;
        }

        var dir = Path.Combine("logs", "captcha-frames");
        Directory.CreateDirectory(dir);

        for (var i = 0; i < frameIds.Length; i++)
        {
            string src;
            string html;
            string probe;
            try
            {
                src = await host.GetElementAttributeAsync(frameIds[i], "src").ConfigureAwait(false) ?? string.Empty;
                await host.SwitchToFrameAsync(frameIds[i]).ConfigureAwait(false);
                html = await EvalAsync(host, FrameSourceScript).ConfigureAwait(false);
                probe = await EvalAsync(host, FrameNetworkProbeScript).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }
            finally
            {
                await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                    .ConfigureAwait(false);
            }

            var kind = src.Contains("bframe", StringComparison.Ordinal) ? "bframe"
                : src.Contains("anchor", StringComparison.Ordinal) ? "anchor"
                : "frame" + i.ToString(CultureInfo.InvariantCulture);
            var href = html.StartsWith("HREF=", StringComparison.Ordinal)
                ? html[5..Math.Max(5, html.IndexOf('\n'))]
                : "?";
            var key = kind + ":" + href + ":" + html.Length.ToString(CultureInfo.InvariantCulture);
            if (!alreadyDumped.Add(key))
            {
                continue;
            }

            Console.WriteLine($"[captcha] net {kind}: {probe}");

            var path = Path.Combine(dir, $"{kind}-{tag}-{html.Length}.html");
            await File.WriteAllTextAsync(path, "<!-- " + src + " -->\n" + html).ConfigureAwait(false);
            Console.WriteLine($"[captcha] dumped {kind} frame ({html.Length} chars) -> {path}");
        }
    }

    public static async Task<int> RunAsync(BrowserHost host, string url, int readyMs, int observeMs)
    {
        ArgumentNullException.ThrowIfNull(host);

        var console = new List<string>();
        host.ConsoleMessage += m => { lock (console) console.Add(m); };

        // The verification call reCAPTCHA makes after a click is a network
        // request, so the request log is what says whether the widget got as
        // far as asking Google anything.
        var started = DateTime.UtcNow;
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        void Note(string text) =>
            Console.WriteLine(FormattableString.Invariant($"[net] +{(DateTime.UtcNow - started).TotalSeconds:0.0}s {text}"));

        var resources = host.ResourceManager;
        if (resources != null)
        {
            resources.NetworkRequestStarting += (id, request) =>
            {
                var url = request?.RequestUri?.AbsoluteUri ?? "?";
                lock (pending) pending[id ?? string.Empty] = url;
                Note($"-> {request?.Method?.Method ?? "?"} {Shorten(url)}");
            };
            resources.NetworkRequestCompleted += (id, response) =>
            {
                lock (pending) pending.Remove(id ?? string.Empty);
                Note($"<- {(int?)response?.StatusCode} {Shorten(response?.RequestMessage?.RequestUri?.AbsoluteUri ?? "?")}");
            };
            resources.NetworkRequestFailed += (id, error) =>
            {
                string url;
                lock (pending) url = pending.TryGetValue(id ?? string.Empty, out var known) ? known : "?";
                Note($"XX {error?.GetType().Name}: {error?.Message} {Shorten(url)}");
            };
        }

        Console.WriteLine($"[captcha] navigate {url}");
        try
        {
            await host.NavigateAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[captcha] navigate threw: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"[captcha] landed on {host.CurrentUri}");

        var anchorFrameId = await WaitForAnchorFrameAsync(host, readyMs).ConfigureAwait(false);
        if (anchorFrameId == null)
        {
            Console.WriteLine("[captcha] FAIL no reCAPTCHA anchor frame appeared.");
            DumpConsole(console);
            return 3;
        }

        var anchorId = await WaitForAnchorElementAsync(host, anchorFrameId, readyMs).ConfigureAwait(false);
        if (anchorId == null)
        {
            // The widget missing is itself a result worth keeping: reCAPTCHA
            // reports its own setup failures into #rc-anchor-alert, so dump the
            // frame rather than walking away with only "never appeared".
            Console.WriteLine("[captcha] FAIL anchor frame never produced #recaptcha-anchor.");
            await RunInFrameAsync(host, anchorFrameId, ReplayBootstrapScript, "replay anchor bootstrap")
                .ConfigureAwait(false);
            await DumpFramesAsync(host, "no-anchor", new HashSet<string>(StringComparer.Ordinal))
                .ConfigureAwait(false);
            DumpConsole(console);
            return 4;
        }

        Console.WriteLine($"[captcha] before: {await StateLineAsync(host, anchorFrameId).ConfigureAwait(false)}");

        var instrumented = new HashSet<string>(StringComparer.Ordinal);
        await EnsureNetworkRecordersAsync(host, instrumented).ConfigureAwait(false);

        Console.WriteLine("[captcha] clicking checkbox…");
        var clickStarted = DateTime.UtcNow;
        if (!await ClickAnchorAsync(host, anchorFrameId, anchorId, readyMs).ConfigureAwait(false))
        {
            DumpConsole(console);
            return 5;
        }

        var dumped = new HashSet<string>(StringComparer.Ordinal);
        await DumpFramesAsync(host, "click", dumped).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddMilliseconds(observeMs);
        string last = string.Empty;
        var solved = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            await EnsureNetworkRecordersAsync(host, instrumented).ConfigureAwait(false);
            await DumpFramesAsync(host, "observe", dumped).ConfigureAwait(false);
            var line = await StateLineAsync(host, anchorFrameId).ConfigureAwait(false);
            if (!string.Equals(line, last, StringComparison.Ordinal))
            {
                var elapsed = (DateTime.UtcNow - clickStarted).TotalSeconds;
                Console.WriteLine(FormattableString.Invariant($"[captcha] +{elapsed:0.0}s {line}"));
                last = line;
            }

            if (line.Contains("recaptcha-checkbox-checked", StringComparison.Ordinal) ||
                TokenLength(line) > 0)
            {
                solved = true;
                break;
            }
        }

        Console.WriteLine($"[captcha] final: {last}");
        await ReportNetworkRecordersAsync(host).ConfigureAwait(false);
        DumpConsole(console);
        Console.WriteLine(solved ? "[captcha] RESULT solved" : "[captcha] RESULT unsolved");
        return solved ? 0 : 1;
    }

    /// <summary>
    /// Evaluates a script inside one frame's own realm, restoring the top-level
    /// context afterwards so the caller is not left pointing at a frame.
    /// </summary>
    private static async Task<string> RunInFrameAsync(
        BrowserHost host,
        string frameId,
        string script,
        string label)
    {
        try
        {
            await host.SwitchToFrameAsync(frameId).ConfigureAwait(false);
            var result = await EvalAsync(host, script).ConfigureAwait(false);
            Console.WriteLine($"[captcha] {label}: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[captcha] {label} threw: {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Installs the recorder into every frame that does not have it yet. The
    /// challenge frame is created after the click, so this runs each tick —
    /// otherwise its very first requests happen before we are listening.
    /// </summary>
    private static async Task EnsureNetworkRecordersAsync(BrowserHost host, HashSet<string> instrumented)
    {
        string[] frameIds;
        try
        {
            frameIds = await host.FindElementsAsync("css selector", "iframe").ConfigureAwait(false)
                       ?? Array.Empty<string>();
        }
        catch
        {
            return;
        }

        foreach (var frameId in frameIds)
        {
            if (!instrumented.Add(frameId))
            {
                continue;
            }

            var src = await SafeAsync(() => host.GetElementAttributeAsync(frameId, "src")).ConfigureAwait(false)
                      ?? string.Empty;
            var kind = src.Contains("bframe", StringComparison.Ordinal) ? "bframe"
                : src.Contains("anchor", StringComparison.Ordinal) ? "anchor"
                : "frame";
            await RunInFrameAsync(host, frameId, InstallNetworkRecorderScript, "recorder " + kind)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads back what each frame recorded.
    /// </summary>
    private static async Task ReportNetworkRecordersAsync(BrowserHost host)
    {
        string[] frameIds;
        try
        {
            frameIds = await host.FindElementsAsync("css selector", "iframe").ConfigureAwait(false)
                       ?? Array.Empty<string>();
        }
        catch
        {
            return;
        }

        foreach (var frameId in frameIds)
        {
            var src = await SafeAsync(() => host.GetElementAttributeAsync(frameId, "src")).ConfigureAwait(false)
                      ?? string.Empty;
            var kind = src.Contains("bframe", StringComparison.Ordinal) ? "bframe"
                : src.Contains("anchor", StringComparison.Ordinal) ? "anchor"
                : "frame";
            await RunInFrameAsync(host, frameId, ReadNetworkRecorderScript, "netlog " + kind).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clicks the checkbox from inside the anchor frame. The element id and its
    /// geometry both belong to that browsing context, so the frame has to stay
    /// selected across measure and click; and the widget is still being laid out
    /// while its bundle runs, so an early attempt reports "element not
    /// interactable" and we retry until it has a box.
    /// </summary>
    private static async Task<bool> ClickAnchorAsync(
        BrowserHost host,
        string frameId,
        string anchorId,
        int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var attempt = 0;
        string lastError = "never attempted";

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                await host.SwitchToFrameAsync(frameId).ConfigureAwait(false);
                var rect = await host.GetElementRectAsync(anchorId).ConfigureAwait(false);
                if (rect == null || rect.Width <= 0 || rect.Height <= 0)
                {
                    lastError = FormattableString.Invariant(
                        $"anchor has no box yet ({rect?.Width ?? 0:0.#}x{rect?.Height ?? 0:0.#})");
                }
                else
                {
                    await host.ClickElementAsync(anchorId).ConfigureAwait(false);
                    Console.WriteLine(
                        FormattableString.Invariant(
                            $"[captcha] clicked on attempt {attempt} at ({rect.X:0.#},{rect.Y:0.#}) {rect.Width:0.#}x{rect.Height:0.#}"));
                    return true;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                    .ConfigureAwait(false);
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        Console.WriteLine($"[captcha] FAIL could not click after {attempt} attempts: {lastError}");
        return false;
    }

    // reCAPTCHA URLs carry multi-kilobyte tokens; the path and key are what
    // identify a request.
    private static string Shorten(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= 120)
        {
            return url;
        }

        var query = url.IndexOf('?');
        return query > 0 && query <= 120 ? url[..query] + "?…" : url[..120] + "…";
    }

    private static int TokenLength(string line)
    {
        var idx = line.IndexOf(";token=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return 0;
        }

        var rest = line[(idx + 7)..];
        var end = rest.IndexOf(';');
        if (end >= 0)
        {
            rest = rest[..end];
        }

        return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static async Task<string> StateLineAsync(BrowserHost host, string anchorFrameId)
    {
        var top = await EvalAsync(host, TopStateScript).ConfigureAwait(false);

        string anchor;
        try
        {
            await host.SwitchToFrameAsync(anchorFrameId).ConfigureAwait(false);
            anchor = await EvalAsync(host, AnchorStateScript).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            anchor = "anchor=<" + ex.GetType().Name + ">";
        }
        finally
        {
            await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                .ConfigureAwait(false);
        }

        return anchor + ";" + top;
    }

    private static async Task<string?> WaitForAnchorFrameAsync(BrowserHost host, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var id = await SafeAsync(() => host.FindElementAsync("css selector", AnchorFrameSelector))
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<string?> WaitForAnchorElementAsync(BrowserHost host, string frameId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            string? id = null;
            try
            {
                await host.SwitchToFrameAsync(frameId).ConfigureAwait(false);
                id = await host.FindElementAsync("css selector", "#recaptcha-anchor").ConfigureAwait(false);
            }
            catch
            {
                // The frame document may not be installed yet; retry until the deadline.
            }
            finally
            {
                await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<string> EvalAsync(BrowserHost host, string script)
    {
        try
        {
            var value = await host.ExecuteScriptAsync(script).ConfigureAwait(false);
            return value?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ": " + ex.Message + ">";
        }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch
        {
            return default;
        }
    }

    private static void DumpConsole(List<string> console)
    {
        List<string> snapshot;
        lock (console)
        {
            snapshot = new List<string>(console);
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        Console.WriteLine($"[captcha] console messages: {snapshot.Count}");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = 0;
        foreach (var message in snapshot)
        {
            var line = message.Replace('\n', ' ').Replace('\r', ' ');
            if (line.Length > 240)
            {
                line = line[..240] + "…";
            }

            if (!seen.Add(line) || ++shown > 40)
            {
                continue;
            }

            Console.WriteLine("   > " + line);
        }
    }
}
