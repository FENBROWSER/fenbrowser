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

    // The probes below cost engine time, and the engine time they cost comes
    // out of the same twenty-second budget the widget gives itself to finish
    // setting up. Dumping a frame's source concatenates the text of every
    // script in it, and reCAPTCHA's is 844KB, so one dump is a sizeable job on
    // its own: the diagnostic evaluations held the script engine for 4.5 of the
    // 20 seconds, right across the window being measured. Lean mode runs the
    // flow — navigate, click, watch the widget — and nothing else, so a timing
    // question can be asked without the asking changing the answer.
    private static readonly bool Lean = string.Equals(
        Environment.GetEnvironmentVariable("FEN_CAPTCHA_LEAN"),
        "1",
        StringComparison.Ordinal);

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
        // The widget's DOM gets built and then never wired up, and wiring is the
        // async half of setup. Ask whether async work runs in this realm at all:
        // arm the probes on the first visit, read them back on later ones.
        "try{if(window.__asyncProbe===undefined){window.__asyncProbe='armed';" +
        "window.__timerProbe='pending';window.__microProbe='pending';window.__rafProbe='pending';" +
        "setTimeout(function(){window.__timerProbe='fired';},10);" +
        "Promise.resolve().then(function(){window.__microProbe='fired';});" +
        "if(typeof requestAnimationFrame==='function')" +
        "requestAnimationFrame(function(){window.__rafProbe='fired';});" +
        "else window.__rafProbe='absent';}" +
        "r+=' timer='+window.__timerProbe+' microtask='+window.__microProbe+' raf='+window.__rafProbe;}" +
        "catch(e){r+=' asyncProbeThrew='+e;}" +
        "r+=' readyState='+document.readyState+' recaptcha='+(typeof recaptcha);" +
        "try{r+=' rcKeys='+(typeof recaptcha==='object'?Object.keys(recaptcha).join('|'):'-');}catch(e){}" +
        // The challenge frame's whole job starts with one inline call to
        // recaptcha.frame.Main.init. When it never opens a channel to the page,
        // the first thing to know is whether that entry point is even there and
        // whether the inline script that calls it ran.
        "try{r+=' frameMain='+(typeof recaptcha==='object'&&recaptcha.frame?typeof recaptcha.frame.Main:'-');" +
        "r+=' frameInit='+(typeof recaptcha==='object'&&recaptcha.frame&&recaptcha.frame.Main?" +
        "typeof recaptcha.frame.Main.init:'-');" +
        "r+=' api='+(typeof window['__recaptcha_api']);" +
        "var ss=document.getElementsByTagName('script');r+=' scripts='+ss.length;" +
        "var inl=0;for(var i=0;i<ss.length;i++){if(!ss[i].src)inl++;}r+=' inline='+inl;" +
        // A frame that cannot reach its parent cannot open the channel the
        // whole protocol runs over, and that looks identical from outside to a
        // frame that simply chose not to.
        // The challenge frame creates its channel and then never hands the port
        // to anyone, so it cannot resolve a target. These are what that
        // resolution is built from.
        "try{r+=' name='+JSON.stringify(String(window.name||''));" +
        "r+=' referrer='+JSON.stringify(String(document.referrer||'').slice(0,60));" +
        "r+=' href='+JSON.stringify(String(location.href).slice(0,70));" +
        "r+=' parentFrames='+((function(){try{return window.parent.frames.length;}catch(e){return 'threw';}})());" +
        "r+=' parentFramesType='+((function(){try{return typeof window.parent.frames;}catch(e){return 'threw';}})());" +
        "r+=' siblingByName='+((function(){try{var n=String(window.name||'').replace(/^c-/,'a-');" +
        "return typeof window.parent.frames[n];}catch(e){return 'threw';}})());" +
        "}catch(e){r+=' targetProbeThrew='+e;}" +
        "try{r+=' parentIsSelf='+(window.parent===window);" +
        "r+=' topIsSelf='+(window.top===window);" +
        "r+=' parentPost='+(window.parent?typeof window.parent.postMessage:'-');" +
        "r+=' parentOrigin='+((function(){try{return window.parent.location.origin;}catch(e){return 'blocked';}})());" +
        "}catch(e){r+=' parentProbeThrew='+e;}" +
        "}catch(e){r+=' frameProbeThrew='+e;}" +
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

    // Record every event the real click produces inside the anchor frame, on the
    // checkbox and on the document, before clicking it. If ours fire and the
    // widget still does not move, the input pipeline delivers and reCAPTCHA's own
    // handler is what is missing.
    private const string InstallClickRecorderScript =
        "(function(){" +
        "if(window.__fenClicks)return 'already';" +
        "window.__fenClicks=[];" +
        "var a=document.getElementById('recaptcha-anchor');" +
        "if(!a)return 'no anchor element';" +
        "var types=['mousedown','mouseup','click','pointerdown','pointerup','keydown'];" +
        "for(var i=0;i<types.length;i++){(function(t){" +
        "a.addEventListener(t,function(e){window.__fenClicks.push('anchor:'+t);});" +
        "document.addEventListener(t,function(e){window.__fenClicks.push('doc:'+t+'@'+" +
        "((e&&e.target&&e.target.id)||'?'));});})(types[i]);}" +
        "return 'installed';})()";

    // addEventListener is an own property of each element here rather than
    // inherited, so patching EventTarget.prototype intercepts nothing. Wrap
    // createElement instead and wrap the listener method on each element it
    // hands out, which is how the widget's own nodes are made.
    private const string InstallListenerRecorderScript =
        "(function(){" +
        "if(window.__fenListenerLog)return 'already@'+location.href;" +
        "window.__fenListenerLog=[];" +
        "var note=function(t,el){try{var who=el&&el.id?('#'+el.id):" +
        "(el&&el.tagName?String(el.tagName).toLowerCase():'?');" +
        "window.__fenListenerLog.push(String(t)+'@'+who);}catch(e){}};" +
        "var wrap=function(el){try{if(!el||el.__fenWrapped)return el;" +
        "var orig=el.addEventListener;if(typeof orig!=='function')return el;" +
        "el.__fenWrapped=1;" +
        "el.addEventListener=function(t){note(t,this);return orig.apply(this,arguments);};" +
        "}catch(e){}return el;};" +
        "try{var C=document.createElement;" +
        "document.createElement=function(){return wrap(C.apply(this,arguments));};}" +
        "catch(e){window.__fenListenerLog.push('createElementHookThrew');}" +
        "try{wrap(document.body);wrap(document.documentElement);}catch(e){}" +
        "return 'installed@'+location.href;})()";

    private const string ReadListenerLogScript =
        "(function(){var l=window.__fenListenerLog;" +
        "if(!l)return 'bundle not instrumented in this realm';" +
        "var anchor=[],counts={};" +
        "for(var i=0;i<l.length;i++){var s=l[i];" +
        "if(s.indexOf('#recaptcha-anchor')>=0)anchor.push(s);" +
        "counts[s]=(counts[s]||0)+1;}" +
        "var top=Object.keys(counts).sort(function(a,b){return counts[b]-counts[a];}).slice(0,8);" +
        "for(var j=0;j<top.length;j++)top[j]=top[j]+'x'+counts[top[j]];" +
        "return l.length+' registrations; onAnchor=['+anchor.join(',')+'] common='+top.join(',');})()";

    // The bundle catches its own start-up failures and reports them to console,
    // so without this a fatal error is indistinguishable from the widget simply
    // being slow. Read back what the prologue recorded in this realm.
    private const string ReadErrorLogScript =
        "(function(){var l=window.__fenErrorLog;" +
        "if(!l)return 'prologue not installed in this realm';" +
        "if(!l.length)return 'no errors recorded';" +
        "return l.length+' entries: '+l.join(' | ');})()";

    // Every class the anchor checkbox held, timestamped inside the frame.
    // Chrome reaches its final class 98ms in; this says where ours stops.
    private const string ReadClassLogScript =
        "(function(){var l=window.__fenClassLog;" +
        "if(!l)return 'prologue not installed in this realm';" +
        "if(!l.length)return 'checkbox never appeared';" +
        "return l.length+' states: '+l.join(' | ');})()";

    // Says whether a silent frame tried to open its channel and was not
    // heard, or never tried at all.
    private const string ReadPostLogScript =
        "(function(){var l=window.__fenPostLog;" +
        "if(!l)return 'prologue not installed in this realm';" +
        "if(!l.length)return 'the bundle posted nothing';" +
        "return l.length+' posts: '+l.join(' | ');})()";

    private const string ReadPrologueNetLogScript =
        "(function(){var l=window.__fenNetLog;" +
        "if(!l)return 'prologue not installed in this realm';" +
        "if(!l.length)return 'the bundle made no requests and created no iframes';" +
        "return l.length+' entries: '+l.join(' | ');})()";

    private const string CountFramesScript =
        "(function(){var f=document.getElementsByTagName('iframe'),o=[];" +
        "for(var i=0;i<f.length;i++){var s=f[i].getAttribute('src')||'';" +
        "o.push((f[i].id||f[i].name||'?')+' src='+(s?s.slice(0,60):'<none>')+" +
        "' doc='+(f[i].contentDocument?'yes':'no'));}" +
        "return f.length+' iframes: '+o.join(' || ');})()";

    private const string ReadClickRecorderScript =
        "(function(){var c=window.__fenClicks||[];" +
        "var a=document.getElementById('recaptcha-anchor');" +
        "return c.length+' events: '+c.join(',')+' | class='+(a?a.className:'no anchor');})()";

    // A real click that changes nothing has two very different explanations:
    // the widget never registered a handler, or it did and our input pipeline is
    // not reaching it. Clicking the same element from inside the realm tells
    // them apart.
    private const string JsClickAnchorScript =
        "(function(){var a=document.getElementById('recaptcha-anchor');" +
        "if(!a)return 'no anchor element';" +
        "var before=a.className;" +
        // Attach our own listener too: if ours fires and the widget still does
        // not react, dispatch works and reCAPTCHA simply never registered one.
        // Closure parks its listener map in a closure_lm_<uid> expando on the
        // element. If there is none, it never wired the widget up at all.
        "var own='?';" +
        "try{own=Object.getOwnPropertyNames(a).filter(function(k){" +
        "return k.indexOf('closure')===0||k.indexOf('__')===0;}).join('+')||'none';}catch(e){own='threw';}" +
        "var shape='own='+(a.hasOwnProperty?a.hasOwnProperty('addEventListener'):'?');" +
        "try{a.__fenProbeExpando=1;" +
        "shape+=' expandoEnumerable='+(Object.getOwnPropertyNames(a).indexOf('__fenProbeExpando')>=0)+" +
        "' expandoReadable='+(a.__fenProbeExpando===1);}catch(e){shape+=' expandoProbeThrew';}" +
        "try{shape+=' protoSame='+(typeof EventTarget==='function'&&" +
        "EventTarget.prototype.addEventListener===a.addEventListener);}catch(e){shape+=' protoThrew';}" +
        "try{shape+=' protoChain='+(Object.getPrototypeOf(a)?'yes':'no');}catch(e){}" +
        "var seen=[];" +
        "try{a.addEventListener('mousedown',function(){seen.push('mousedown');});" +
        "a.addEventListener('click',function(){seen.push('click');});}" +
        "catch(e){return 'addEventListener threw '+e;}" +
        "try{a.click();}catch(e){return 'clickThrew '+e;}" +
        "return 'closureProps='+own+' '+shape+' ourListenersSaw=['+seen.join(',')+'] changed='+(before!==a.className);})()";

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
            Console.WriteLine($"[captcha] net {kind}: {probe}");
            var key = kind + ":" + href + ":" + html.Length.ToString(CultureInfo.InvariantCulture);
            if (!alreadyDumped.Add(key))
            {
                continue;
            }


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

        // The frame realm does not exist the moment the element does, so keep
        // offering the recorder until it lands in the anchor document rather
        // than falling back to the top one.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var placed = await RunInFrameAsync(
                host, anchorFrameId, InstallListenerRecorderScript, "listener-recorder").ConfigureAwait(false);
            if (placed != null && placed.Contains("/anchor", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
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
        if (!Lean) await EnsureNetworkRecordersAsync(host, instrumented).ConfigureAwait(false);

        if (!Lean)
        {
            await RunInFrameAsync(host, anchorFrameId, InstallClickRecorderScript, "click-recorder").ConfigureAwait(false);
        }
        // reCAPTCHA rebuilds its anchor iframe (a fresh cb= each time), so ids
        // resolved while waiting can name a frame that is no longer the live one.
        // Re-resolve both right before clicking.
        var freshFrameId = await WaitForAnchorFrameAsync(host, 5000).ConfigureAwait(false);
        if (freshFrameId != null && !string.Equals(freshFrameId, anchorFrameId, StringComparison.Ordinal))
        {
            Console.WriteLine("[captcha] anchor frame changed since it was found; using the current one");
            anchorFrameId = freshFrameId;
        }

        var freshAnchorId = await WaitForAnchorElementAsync(host, anchorFrameId, 5000).ConfigureAwait(false);
        if (freshAnchorId != null)
        {
            anchorId = freshAnchorId;
        }

        Console.WriteLine("[captcha] clicking checkbox…");
        var clickStarted = DateTime.UtcNow;
        if (!await ClickAnchorAsync(host, anchorFrameId, anchorId, readyMs).ConfigureAwait(false))
        {
            DumpConsole(console);
            return 5;
        }

        await Task.Delay(1500).ConfigureAwait(false);
        var dumped = new HashSet<string>(StringComparer.Ordinal);
        if (!Lean)
        {
            await RunInFrameAsync(host, anchorFrameId, ReadClickRecorderScript, "clicks seen").ConfigureAwait(false);
            await RunInFrameAsync(host, anchorFrameId, ReadListenerLogScript, "listeners registered").ConfigureAwait(false);
            await RunInFrameAsync(host, anchorFrameId, ReadErrorLogScript, "errors on anchor").ConfigureAwait(false);
            await RunInFrameAsync(host, anchorFrameId, ReadClassLogScript, "checkbox states").ConfigureAwait(false);
            await RunInFrameAsync(host, anchorFrameId, ReadPostLogScript, "posts on anchor").ConfigureAwait(false);
            await RunInFrameAsync(host, anchorFrameId, ReadPrologueNetLogScript, "bundle-net on anchor").ConfigureAwait(false);
            await DumpFramesAsync(host, "click", dumped).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(observeMs);
        string last = string.Empty;
        var solved = false;
        var tileClicked = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            if (!Lean)
            {
                await EnsureNetworkRecordersAsync(host, instrumented).ConfigureAwait(false);
                await DumpFramesAsync(host, "observe", dumped).ConfigureAwait(false);
            }

            if (!tileClicked)
            {
                tileClicked = await ClickFirstTileAsync(host).ConfigureAwait(false);
            }

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
        if (!solved)
        {
            await RunInFrameAsync(host, anchorFrameId, JsClickAnchorScript, "js-click anchor").ConfigureAwait(false);
        }

        if (!Lean) await ReportNetworkRecordersAsync(host).ConfigureAwait(false);
        DumpConsole(console);
        // With FEN_FENJS_PROFILE=1 the engine counts executed opcodes; print
        // the mix so the slow path is chosen from data rather than a guess.
        if (FenBrowser.Js.Diagnostics.InterpreterProfiler.Enabled)
        {
            Console.WriteLine(FenBrowser.Js.Diagnostics.InterpreterProfiler.Report(25));
            Console.WriteLine(FenBrowser.Js.Diagnostics.InterpreterProfiler.VarReport());
            Console.WriteLine(FenBrowser.Js.Diagnostics.InterpreterProfiler.TimingReport());
        }

        if (FenBrowser.Js.Diagnostics.InterpreterProfiler.OpTimingEnabled)
        {
            Console.WriteLine(FenBrowser.Js.Diagnostics.InterpreterProfiler.OpTimeReport(30));
        }

        if (string.Equals(Environment.GetEnvironmentVariable("FEN_FENJS_LOCKPROBE"), "1", StringComparison.Ordinal))
        {
            Console.Write(FenBrowser.FenEngine.Scripting.BrowserScriptEngineDiagnostics.LockReport());
        }

        if (string.Equals(Environment.GetEnvironmentVariable("FEN_JIT_REPORT"), "1", StringComparison.Ordinal))
        {
            Console.Write(FenBrowser.Js.Bytecode.JitCompiler.Report());
        }

        if (FenBrowser.Js.Interpreter2.Interp2Options.Log)
        {
            // What fraction of the page's own JavaScript the register-window
            // loop could run, and the ranked reasons for the rest. On a
            // hand-written corpus that is 86%; on a minified bundle nobody has
            // measured it, and it is the number that decides whether the loop
            // reaches the user at all.
            Console.Write(FenBrowser.Js.Interpreter2.Interp2Stats.Report());
        }

        // Always, not behind a switch: comparing two runs of this harness is the
        // whole point of it, and the per-collection engine line only appears
        // when a collection happened to land at a safepoint.
        Console.Write(FenBrowser.FenEngine.Scripting.BrowserScriptEngineDiagnostics.EngineReport());

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

        // The widget is built by the top-level page, so the realm that creates
        // the iframes and drives the handshake is the one the per-frame loop
        // below never visits. Report it first.
        await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
            .ConfigureAwait(false);
        Console.WriteLine($"[captcha] bundle-net top: {await EvalAsync(host, ReadPrologueNetLogScript).ConfigureAwait(false)}");
        Console.WriteLine($"[captcha] errors top: {await EvalAsync(host, ReadErrorLogScript).ConfigureAwait(false)}");
        Console.WriteLine($"[captcha] frames top: {await EvalAsync(host, CountFramesScript).ConfigureAwait(false)}");

        foreach (var frameId in frameIds)
        {
            var src = await SafeAsync(() => host.GetElementAttributeAsync(frameId, "src")).ConfigureAwait(false)
                      ?? string.Empty;
            var kind = src.Contains("bframe", StringComparison.Ordinal) ? "bframe"
                : src.Contains("anchor", StringComparison.Ordinal) ? "anchor"
                : "frame";
            await RunInFrameAsync(host, frameId, ReadNetworkRecorderScript, "netlog " + kind).ConfigureAwait(false);
            await RunInFrameAsync(host, frameId, ReadListenerLogScript, "listeners " + kind).ConfigureAwait(false);
            await RunInFrameAsync(host, frameId, ReadErrorLogScript, "errors " + kind).ConfigureAwait(false);
            await RunInFrameAsync(host, frameId, ReadClassLogScript, "checkbox " + kind).ConfigureAwait(false);
            await RunInFrameAsync(host, frameId, ReadPostLogScript, "posts " + kind).ConfigureAwait(false);
            await RunInFrameAsync(host, frameId, ReadPrologueNetLogScript, "bundle-net " + kind).ConfigureAwait(false);
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

    // What a tile click is supposed to do to the challenge document: mark the
    // cell selected and relabel the button from "Skip" to "Verify". Read both
    // back, plus anything the handler threw, so "the click did nothing" can be
    // told apart from "the change was never painted".
    private const string InstallTileErrorRecorderScript =
        "(function(){if(window.__fenTileErrors)return 'already';window.__fenTileErrors=[];" +
        "window.addEventListener('error',function(e){window.__fenTileErrors.push(String(e.message)+'@'+e.lineno+':'+e.colno);});" +
        "window.addEventListener('unhandledrejection',function(e){window.__fenTileErrors.push('rejection:'+String(e.reason));});" +
        "return 'installed';})()";

    private const string ReadTileStateScript =
        "(function(){var t=document.getElementById('0');var b=document.getElementById('recaptcha-verify-button');" +
        "return 'tile0.class='+(t?t.className:'none')+' button.text='+JSON.stringify(b?b.textContent:'none')+" +
        "' button.children='+(b?b.childNodes.length:-1)+' errors=['+(window.__fenTileErrors||[]).join(' | ')+']';})()";

    // Every change to tile 0's class and to the button's text, each with the
    // JS stack that made it, so a toggle that ran twice reads differently from
    // one that never ran.
    private const string InstallTileTraceScript =
        "(function(){var t=document.getElementById('0');var b=document.getElementById('recaptcha-verify-button');" +
        "if(!t||!b)return 'no tile/button';if(window.__fenTileTrace)return 'already';var log=window.__fenTileTrace=[];" +
        "function rec(what,val){var st='';try{st=String(new Error().stack||'').split('\\n').slice(1,6).join(' <- ');}catch(e){}" +
        "log.push(Math.round(performance.now())+'ms '+what+'='+JSON.stringify(val)+(st?' stack:'+st:''));}" +
        "try{new MutationObserver(function(ms){ms.forEach(function(m){rec('tile0.class',t.className);});}).observe(t,{attributes:true,attributeFilter:['class']});" +
        "new MutationObserver(function(ms){ms.forEach(function(m){rec('button.'+m.type,b.textContent);});}).observe(b,{childList:true,characterData:true,subtree:true});}catch(e){return 'observer failed: '+e;}" +
        "return 'installed';})()";

    // Closure keeps a tile's listeners in a closure_lm_<uid> expando whose `i`
    // maps event type -> [Listener]; fireListener reads `.listener` at call
    // time, so wrapping it shows which of reCAPTCHA's own handlers a click
    // reaches, and what each returns or throws.
    private const string WrapTileListenersScript =
        "(function(){var t=document.getElementById('0');if(!t)return 'no tile';var log=window.__fenTileTrace=window.__fenTileTrace||[];" +
        "var keys=Object.getOwnPropertyNames(t).filter(function(k){return k.indexOf('closure_lm_')===0;});" +
        "if(!keys.length)return 'no closure listener map on tile 0; own='+Object.getOwnPropertyNames(t).join(',');" +
        "var lm=t[keys[0]];var map=lm&&lm.i;if(!map)return 'listener map has no i; keys='+Object.keys(lm||{}).join(',');" +
        "var out=[];Object.keys(map).forEach(function(type){map[type].forEach(function(l,i){" +
        "var orig=l.listener;if(typeof orig!=='function'){out.push(type+'#'+i+':notfn');return;}" +
        "l.listener=function(e){var tag='';try{tag=(e&&e.target&&e.target.tagName)+'/'+(e&&e.type);}catch(x){}" +
        "log.push(Math.round(performance.now())+'ms '+type+'#'+i+' fired '+tag);" +
        "try{var r=orig.apply(this,arguments);log.push('  -> returned '+r);return r;}catch(x){log.push('  -> threw '+x);throw x;}};" +
        "out.push(type+'#'+i);});});return 'wrapped '+out.join(',');})()";

    // Prototype-level hook, installed in the challenge frame before its grid
    // exists: every listener added to a TD is recorded, and each is wrapped so
    // the trace also says which of them a click actually reaches. Expandos on
    // host elements are not enumerable here, so the listener map itself cannot
    // be read back after the fact.
    private const string InstallTdListenerHookScript =
        "(function(){if(window.__fenTdHook)return 'already';var log=window.__fenTileTrace=window.__fenTileTrace||[];" +
        "var P=null;try{if(typeof EventTarget==='function'&&EventTarget.prototype.addEventListener)P=EventTarget.prototype;}catch(e){}" +
        "if(!P){try{if(Element.prototype.addEventListener)P=Element.prototype;}catch(e){}}" +
        "if(!P)return 'no prototype addEventListener';var orig=P.addEventListener;" +
        "P.addEventListener=function(t,fn,opt){var el=this;var isTd=false;try{isTd=el&&el.tagName==='TD';}catch(e){}" +
        "if(isTd&&typeof fn==='function'){log.push('reg '+t+' on td#'+el.id+' capture='+JSON.stringify(opt===undefined?false:(typeof opt==='object'&&opt?!!opt.capture:opt)));" +
        "var w=function(e){log.push(Math.round(performance.now())+'ms fire '+t+' on td#'+el.id+' target='+(e&&e.target&&e.target.tagName)+'#'+(e&&e.target&&e.target.id)+' phase='+(e&&e.eventPhase));" +
        "try{var r=fn.apply(this,arguments);log.push('  -> ok');return r;}catch(x){log.push('  -> threw '+x);throw x;}};" +
        "return orig.call(this,t,w,opt);}" +
        "return orig.apply(this,arguments);};window.__fenTdHook=1;return 'installed';})()";

    private static int _tileClickFailures;

    private const string ReadTileBlockerScript =
        "(function(){var t=document.getElementById('0');if(!t)return 'no tile';var r=t.getBoundingClientRect();" +
        "var e=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);" +
        "return 'tile0 rect='+Math.round(r.left)+','+Math.round(r.top)+' '+Math.round(r.width)+'x'+Math.round(r.height)+" +
        "' atCenter='+(e?e.tagName+'#'+e.id+'.'+e.className:'none');})()";

    // The listener map's expando key is closure_lm_<random below 1e6>, the
    // bundle is an IIFE so the variable holding it is out of reach, and host
    // expandos are readable but not enumerable. A linear scan of the key space
    // is the one way left to find it; the timing is printed with the result.
    private const string FindListenerMapScript =
        "(function(){var t=document.getElementById('0');if(!t)return 'no tile';var t0=performance.now();" +
        "for(var i=0;i<1000000;i++){var k='closure_lm_'+i;var v=t[k];if(v){window.__fenLmKey=k;" +
        "var types=[];try{types=Object.keys(v.i||{});}catch(e){}return 'found '+k+' after '+Math.round(performance.now()-t0)+'ms types='+types.join(',');}}" +
        "return 'not found in '+Math.round(performance.now()-t0)+'ms';})()";

    // With the key known, wrap every Closure listener on tile 0 so the trace
    // shows which fire on a click and what they return or throw.
    private const string WrapListenerMapScript =
        "(function(){var t=document.getElementById('0');var k=window.__fenLmKey;if(!t||!k)return 'no key';var lm=t[k];var map=lm&&lm.i;if(!map)return 'no map';" +
        "var log=window.__fenTileTrace=window.__fenTileTrace||[];var out=[];" +
        "Object.keys(map).forEach(function(type){map[type].forEach(function(l,i){var orig=l.listener;if(typeof orig!=='function'){out.push(type+'#'+i+':notfn');return;}" +
        "l.listener=function(e){var tag='';try{tag=(e&&e.target&&e.target.tagName)+'#'+(e&&e.target&&e.target.id)+'/'+(e&&e.type);}catch(x){}" +
        "log.push(Math.round(performance.now())+'ms '+type+'#'+i+' fired '+tag);" +
        "try{var r=orig.apply(this,arguments);log.push('  -> returned '+r);return r;}catch(x){log.push('  -> threw '+x);throw x;}};" +
        "out.push(type+'#'+i+' capture='+l.capture+' handler='+(l.handler?typeof l.handler:'none'));});});" +
        "return 'wrapped '+out.join(', ');})()";

    // Follows the Closure event chain out of the DOM: each DOM listener's
    // handler is a Closure EventTarget whose own listener map (an object
    // with an `i` of type -> [Listener]) holds the next hop. Wrap every hop
    // up to a few levels deep so the trace shows where the ACTION stops.
    private const string WrapListenerChainScript =
        "(function(){var t=document.getElementById('0');var k=window.__fenLmKey;if(!t||!k)return 'no key';" +
        "var log=window.__fenTileTrace=window.__fenTileTrace||[];var seen=[];var out=[];" +
        // The challenge object keeps its tile records at G.Ss.Jc (tc = records,
        // vS = selected count); those names are not obfuscated.
        "function st(h){var r='';try{var j=h&&h.G&&h.G.Ss&&h.G.Ss.Jc;if(j)r+='vS='+j.vS+' tc0.selected='+(j.tc&&j.tc[0]&&j.tc[0].selected)+' tc.len='+(j.tc&&j.tc.length);}catch(e){r+='state?';}" +
        "try{var b=document.getElementById('recaptcha-verify-button');r+=' btn='+JSON.stringify(b&&b.textContent)+'/'+(b&&b.childNodes.length)+' tile0='+JSON.stringify(t.className);}catch(e){}return r;}" +
        "function isMap(v){if(!v||typeof v!=='object'||!v.i||typeof v.i!=='object')return false;var ks=Object.keys(v.i);" +
        "return ks.length>0&&ks.every(function(x){return Array.isArray(v.i[x])&&v.i[x].every(function(l){return l&&typeof l.listener==='function';});});}" +
        "function wrapMap(map,label,depth){Object.keys(map.i).forEach(function(type){map.i[type].forEach(function(l,i){" +
        "if(l.__fenWrapped)return;l.__fenWrapped=1;var orig=l.listener;var name=label+':'+type+'#'+i;out.push(name);" +
        "l.listener=function(e){var et='';try{et=(e&&e.type)+' target='+(e&&e.target&&(e.target.tagName||e.target.constructor&&e.target.constructor.name||typeof e.target));}catch(x){}" +
        "log.push(Math.round(performance.now())+'ms '+name+' fired '+et+' | before: '+st(this));try{var r=orig.apply(this,arguments);log.push('  '+name+' -> '+r+' | after: '+st(this));return r;}catch(x){log.push('  '+name+' threw '+x+' | after: '+st(this));throw x;}};" +
        "if(depth<4&&l.handler&&typeof l.handler==='object')walk(l.handler,name+'>',depth+1);});});}" +
        "function walk(obj,label,depth){if(seen.indexOf(obj)>=0)return;seen.push(obj);var ks=[];try{ks=Object.keys(obj);}catch(e){return;}" +
        "ks.forEach(function(p){var v;try{v=obj[p];}catch(e){return;}if(isMap(v))wrapMap(v,label+p,depth);});}" +
        "walk({dom:t[k]},'',0);return 'wrapped '+out.length+': '+out.join(', ');})()";

    private const string ButtonRealmProbeScript =
        "(function(){var b=document.getElementById('recaptcha-verify-button');if(!b)return 'no button';" +
        "return 'textContent in='+('textContent' in b)+' first===last='+(b.firstChild===b.lastChild)+' first!=last='+(b.firstChild!=b.lastChild)+" +
        "' firstType='+(b.firstChild&&b.firstChild.nodeType)+' children='+b.childNodes.length+' classList='+(typeof b.classList)+" +
        "' tile0 classList='+(typeof document.getElementById('0').classList);})()";

    // Every tile's class after a round change, beside the challenge object's
    // own records, so a stale checkmark can be told apart from a stale paint.
    private const string ReadAllTilesScript =
        "(function(){var out=[];var tds=document.querySelectorAll('td.rc-imageselect-tile');" +
        "for(var i=0;i<tds.length;i++){if(tds[i].className!=='rc-imageselect-tile')out.push('#'+tds[i].id+'='+tds[i].className);}" +
        "var b=document.getElementById('recaptcha-verify-button');var st='';" +
        "try{var k=window.__fenLmKey;var t=document.getElementById('0');var lm=t&&k&&t[k];" +
        "var l=lm&&lm.i&&lm.i.click&&lm.i.click[0];var oj=l&&l.handler;" +
        "var mt=oj&&oj.j&&oj.j.i&&oj.j.i.action&&oj.j.i.action[0]&&oj.j.i.action[0].handler;" +
        "var comp=mt&&mt.j&&mt.j.i&&mt.j.i.action&&mt.j.i.action[0]&&mt.j.i.action[0].handler;" +
        "var j=comp&&comp.G&&comp.G.Ss&&comp.G.Ss.Jc;if(j){var sel=[];for(var q=0;q<j.tc.length;q++)if(j.tc[q].selected)sel.push(q);" +
        "st=' vS='+j.vS+' recordsSelected=['+sel.join(',')+'] tc.len='+j.tc.length;}}catch(e){st=' state?'+e;}" +
        "return 'selectedTiles=['+out.join(' ')+'] button='+JSON.stringify(b&&b.textContent)+st;})()";

    // Class writes on the tiles during a round change: the token-list methods
    // through their prototype (if the engine shares one) and, failing that,
    // a poll of the class attribute, so "never removed" and "removed and put
    // back" read differently.
    private const string InstallClassWriteHookScript =
        "(function(){var log=window.__fenClassLog=[];var t=document.getElementById('0');if(!t)return 'no tile';" +
        "var cl=t.classList;var same=(t.classList===cl);var proto=Object.getPrototypeOf(cl);var hasProto=proto&&typeof proto.remove==='function';" +
        "var r='classListSame='+same+' protoRemove='+hasProto+' DOMTokenList='+(typeof DOMTokenList);" +
        "function wrap(obj,name){var orig=obj[name];if(typeof orig!=='function')return false;obj[name]=function(){var el=null;try{el=this&&this.__fenOwner;}catch(e){}" +
        "log.push(Math.round(performance.now())+'ms '+name+'('+Array.prototype.join.call(arguments,',')+') on '+(el?'#'+el.id:'?'));return orig.apply(this,arguments);};return true;}" +
        "var target=hasProto?proto:cl;r+=' wrapped='+['add','remove','toggle'].map(function(n){return n+':'+wrap(target,n);}).join(',');" +
        "var tds=document.querySelectorAll('td.rc-imageselect-tile');for(var i=0;i<tds.length;i++){try{tds[i].classList.__fenOwner=tds[i];}catch(e){}}" +
        "window.__fenClassPoll=[];var last=t.className;window.__fenClassPoll.push('0ms '+last);var n=0;var iv=setInterval(function(){n++;if(t.className!==last){last=t.className;window.__fenClassPoll.push((n*50)+'ms '+last);}if(n>=120)clearInterval(iv);},50);" +
        "return r;})()";

    private const string ReadClassWriteLogScript =
        "(function(){return 'calls=['+(window.__fenClassLog||[]).join(' | ')+'] poll=['+(window.__fenClassPoll||[]).join(' | ')+']';})()";

    // Grid inventory during a round change: every image-select table, its
    // carousel classes, its tile count, and how many of its tiles are marked.
    private const string ReadGridInventoryScript =
        "(function(){var out=[];var tabs=document.querySelectorAll('table');for(var i=0;i<tabs.length;i++){var t=tabs[i];" +
        "var tds=t.querySelectorAll('td.rc-imageselect-tile');if(!tds.length)continue;var sel=0;for(var j=0;j<tds.length;j++)if(tds[j].className.indexOf('tileselected')>=0)sel++;" +
        "var p=t.parentNode;var pc=p?p.className:'';var r=t.getBoundingClientRect();" +
        "out.push('table'+i+'{tiles='+tds.length+' selected='+sel+' class='+JSON.stringify(t.className)+' parent='+JSON.stringify(pc)+' rect='+Math.round(r.left)+','+Math.round(r.top)+' '+Math.round(r.width)+'x'+Math.round(r.height)+' firstImg='+(tds[0].querySelector('img')?tds[0].querySelector('img').src.slice(-12):'none')+'}');}" +
        "var ids=document.querySelectorAll('[id=\"0\"]');return out.join(' ')+' id0count='+ids.length;})()";

    // The carousel waits for the first image of the new table to fire `load`.
    // Report the state of those images, and arm listeners of our own on them
    // plus a fresh Image() with the same URL, to see which events ever fire.
    private const string ProbeNewTableImagesScript =
        "(function(){var tabs=document.querySelectorAll('table');var q=null;for(var i=0;i<tabs.length;i++){if(tabs[i].className.indexOf('offscreen-right')>=0)q=tabs[i];}" +
        "if(!q)return 'no offscreen-right table';var imgs=q.querySelectorAll('img');if(!imgs.length)return 'no imgs';var log=window.__fenImgLog=window.__fenImgLog||[];" +
        "var out='imgs='+imgs.length;for(var i=0;i<Math.min(imgs.length,2);i++){var im=imgs[i];out+=' img'+i+'{complete='+im.complete+' w='+im.width+'x'+im.height+' nat='+im.naturalWidth+' src='+String(im.src).slice(-14)+' hasSrcAttr='+im.hasAttribute('src')+'}';" +
        "(function(k,el){el.addEventListener('load',function(){log.push('img'+k+' load');});el.addEventListener('error',function(){log.push('img'+k+' error');});})(i,im);}" +
        "try{var fresh=new Image();fresh.addEventListener('load',function(){log.push('fresh load');});fresh.addEventListener('error',function(){log.push('fresh error');});fresh.src=imgs[0].src;out+=' freshComplete='+fresh.complete;}catch(e){out+=' fresh threw '+e;}" +
        "return out;})()";

    private const string ReadImageLogScript =
        "(function(){return 'imgEvents=['+(window.__fenImgLog||[]).join(' | ')+']';})()";

    private const string ReadTileTraceScript =
        "(function(){var l=window.__fenTileTrace||[];return l.length+' changes: '+l.join(' || ');})()";

    // The dynamic (3x3) variant only ever selects; the 4x4 variant is the one
    // that toggles and shows "Skip". Failing dynamic rounds is how a session
    // gets handed a 4x4, so submit a wrong answer until the button says Skip.
    private const string ClickVerifyScript =
        "(function(){var b=document.getElementById('recaptcha-verify-button');if(!b)return 'no button';b.click();return 'clicked '+b.textContent;})()";

    /// <summary>
    /// Clicks the first image tile once the challenge grid exists and reports
    /// what the click did to the challenge document. Returns false until there
    /// is a grid to click.
    /// </summary>
    private static async Task<bool> ClickFirstTileAsync(BrowserHost host)
    {
        string[] frameIds;
        try
        {
            frameIds = await host.FindElementsAsync("css selector", "iframe").ConfigureAwait(false)
                       ?? Array.Empty<string>();
        }
        catch
        {
            return false;
        }

        foreach (var frameId in frameIds)
        {
            string src;
            try
            {
                src = await host.GetElementAttributeAsync(frameId, "src").ConfigureAwait(false) ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (!src.Contains("bframe", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await host.SwitchToFrameAsync(frameId).ConfigureAwait(false);
                var tiles = await host.FindElementsAsync("css selector", "td.rc-imageselect-tile").ConfigureAwait(false)
                            ?? Array.Empty<string>();
                if (tiles.Length == 0)
                {
                    return false;
                }

                Console.WriteLine($"[captcha] tile-recorder: {await EvalAsync(host, InstallTileErrorRecorderScript).ConfigureAwait(false)}");
                var before = await EvalAsync(host, ReadTileStateScript).ConfigureAwait(false);
                Console.WriteLine($"[captcha] tile before: {before}");

                // A grid still animating in has its button disabled; wait for
                // the next tick rather than clicking into the transition.
                if (before.Contains("button.text=\"none\"", StringComparison.Ordinal))
                {
                    return false;
                }

                var isSkipVariant = before.Contains("button.text=\"Skip\"", StringComparison.Ordinal);
                if (isSkipVariant)
                {
                    Console.WriteLine($"[captcha] tile-trace: {await EvalAsync(host, InstallTileTraceScript).ConfigureAwait(false)}");
                    Console.WriteLine($"[captcha] listener-map: {await EvalAsync(host, FindListenerMapScript).ConfigureAwait(false)}");
                    Console.WriteLine($"[captcha] listener-chain: {await EvalAsync(host, WrapListenerChainScript).ConfigureAwait(false)}");
                    Console.WriteLine($"[captcha] button-realm: {await EvalAsync(host, ButtonRealmProbeScript).ConfigureAwait(false)}");
                }

                var rect = await host.GetElementRectAsync(tiles[0]).ConfigureAwait(false);
                await host.ClickElementAsync(tiles[0]).ConfigureAwait(false);
                Console.WriteLine(FormattableString.Invariant(
                    $"[captcha] clicked tile 0 at ({rect?.X ?? 0:0.#},{rect?.Y ?? 0:0.#}) {rect?.Width ?? 0:0.#}x{rect?.Height ?? 0:0.#}"));
                await Task.Delay(1500).ConfigureAwait(false);
                Console.WriteLine($"[captcha] tile after: {await EvalAsync(host, ReadTileStateScript).ConfigureAwait(false)}");
                if (isSkipVariant)
                {
                    Console.WriteLine($"[captcha] tile-trace: {await EvalAsync(host, ReadTileTraceScript).ConfigureAwait(false)}");

                    // Now the round change: press the button and watch whether
                    // the previous round's selection is cleared from the grid.
                    var button = await host.FindElementsAsync("css selector", "#recaptcha-verify-button").ConfigureAwait(false)
                                 ?? Array.Empty<string>();
                    if (button.Length > 0)
                    {
                        Console.WriteLine($"[captcha] round before: {await EvalAsync(host, ReadAllTilesScript).ConfigureAwait(false)}");
                        Console.WriteLine($"[captcha] grids before: {await EvalAsync(host, ReadGridInventoryScript).ConfigureAwait(false)}");
                        Console.WriteLine($"[captcha] class-hook: {await EvalAsync(host, InstallClassWriteHookScript).ConfigureAwait(false)}");
                        await host.ClickElementAsync(button[0]).ConfigureAwait(false);
                        Console.WriteLine("[captcha] clicked button");
                        for (var wait = 0; wait < 4; wait++)
                        {
                            await Task.Delay(1500).ConfigureAwait(false);
                            Console.WriteLine($"[captcha] round +{(wait + 1) * 1.5:0.0}s: {await EvalAsync(host, ReadAllTilesScript).ConfigureAwait(false)}");
                            Console.WriteLine($"[captcha] grids +{(wait + 1) * 1.5:0.0}s: {await EvalAsync(host, ReadGridInventoryScript).ConfigureAwait(false)}");
                            if (wait == 0)
                            {
                                Console.WriteLine($"[captcha] new-table imgs: {await EvalAsync(host, ProbeNewTableImagesScript).ConfigureAwait(false)}");
                            }
                        }

                        Console.WriteLine($"[captcha] img-events: {await EvalAsync(host, ReadImageLogScript).ConfigureAwait(false)}");

                        Console.WriteLine($"[captcha] class-writes: {await EvalAsync(host, ReadClassWriteLogScript).ConfigureAwait(false)}");
                    }

                    return true;
                }

                // The dynamic variant answered; nothing more to learn this run.
                return true;
            }
            catch (Exception ex)
            {
                // Usually the grid animating in under an overlay; try again next
                // tick, but not forever, and say what is in the way.
                Console.WriteLine($"[captcha] tile click threw: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[captcha] tile blocker: {await SafeAsync(() => EvalAsync(host, ReadTileBlockerScript)).ConfigureAwait(false)}");
                return ++_tileClickFailures >= 5;
            }
            finally
            {
                await SafeAsync(async () => { await host.SwitchToFrameAsync(null).ConfigureAwait(false); return 0; })
                    .ConfigureAwait(false);
            }
        }

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
