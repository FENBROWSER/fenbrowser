using System;

namespace FenBrowser.FenEngine.Rendering
{
    public static class NewTabRenderer
    {
        public static string Render()
        {
            var searchUrl = FenBrowser.Core.BrowserSettings.Instance.SearchEngineUrl;
            searchUrl = searchUrl.Replace("'", "\\'");

            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>New Tab</title>
    <style>
        html {{
            height: 100%;
        }}

        body {{
            margin: 0;
            min-height: 100vh;
            font-family: 'Segoe UI', 'Segoe UI Variable Text', Arial, sans-serif;
            color: #e2e8f0;
            background-color: #0f172a;
            overflow-x: hidden;
        }}

        .page {{
            position: relative;
            min-height: 100vh;
            padding: 72px 24px 120px;
            box-sizing: border-box;
            text-align: center;
            overflow: hidden;
        }}

        .shell {{
            display: inline-block;
            position: relative;
            width: 860px;
            max-width: 100%;
            margin-top: 6vh;
            text-align: center;
            z-index: 2;
        }}

        .hero-panel {{
            display: block;
            width: 100%;
            padding: 38px 34px 28px;
            box-sizing: border-box;
            border: 1px solid #23324f;
            border-radius: 34px;
            background: rgba(17, 27, 49, 0.94);
            text-align: center;
        }}

        .hero-rail {{
            display: block;
            width: 100%;
            max-width: 700px;
            margin: 0 auto 28px;
            padding: 8px 12px;
            box-sizing: border-box;
            border: 1px solid #253452;
            border-radius: 999px;
            background: rgba(11, 18, 32, 0.55);
        }}

        .logo-mark {{
            display: block;
            margin: 0 auto 18px;
        }}

        .logo-mark-bar {{
            display: inline-block;
            width: 18px;
            margin: 0 5px;
            border-radius: 999px;
            background: #60a5fa;
            vertical-align: bottom;
        }}

        .logo-mark-bar-primary {{
            height: 44px;
            background: #60a5fa;
        }}

        .logo-mark-bar-secondary {{
            height: 32px;
            background: #38bdf8;
        }}

        .logo-mark-bar-tertiary {{
            height: 22px;
            background: #0f766e;
        }}

        .eyebrow {{
            display: inline-block;
            padding: 0;
            box-sizing: border-box;
            color: #93c5fd;
            font-size: 12px;
            font-weight: 700;
            letter-spacing: 0.16em;
            text-transform: uppercase;
        }}

        .logo {{
            margin: 0;
            color: #f8fafc;
            font-size: 74px;
            font-weight: 700;
            letter-spacing: -0.05em;
            line-height: 1;
        }}

        .logo-accent {{
            color: #60a5fa;
        }}

        .tagline {{
            display: block;
            width: 100%;
            max-width: 620px;
            margin-top: 18px;
            margin-left: auto;
            margin-right: auto;
            color: #94a3b8;
            font-size: 19px;
            line-height: 1.6;
        }}

        .search-panel {{
            display: inline-block;
            width: 100%;
            max-width: 600px;
            margin-top: 30px;
            padding: 14px;
            box-sizing: border-box;
            border-radius: 28px;
            border: 1px solid #334155;
            background: rgba(15, 23, 42, 0.74);
            overflow: hidden;
        }}

        .search-box {{
            display: block;
            width: 100%;
            margin: 0 auto;
            height: 58px;
            padding: 0 20px;
            line-height: 56px;
            border-radius: 20px;
            border: 1px solid #3b82f6;
            background: rgba(15, 23, 42, 0.88);
            color: #f8fafc;
            font-size: 18px;
            font-family: inherit;
            box-sizing: border-box;
            text-align: left;
            overflow: hidden;
            white-space: nowrap;
            outline: none;
            appearance: none;
        }}

        .search-box::placeholder {{
            color: #94a3b8;
        }}

        .search-box.is-focused {{
            border-color: #60a5fa;
        }}

        .search-hint {{
            margin-top: 14px;
            color: #64748b;
            font-size: 12px;
            font-weight: 600;
            letter-spacing: 0.12em;
            text-transform: uppercase;
        }}

        .shortcut-row {{
            display: block;
            width: 100%;
            max-width: 700px;
            margin: 18px auto 0;
            text-align: center;
        }}

        .shortcut-chip {{
            display: inline-block;
            margin: 0 6px 10px;
            padding: 10px 14px;
            box-sizing: border-box;
            white-space: nowrap;
            border-radius: 999px;
            border: 1px solid #243552;
            background: #121c31;
            color: #a5b4fc;
            font-size: 12px;
            font-weight: 600;
            letter-spacing: 0.04em;
        }}

        .section-title {{
            display: block;
            margin-top: 28px;
            color: #7dd3fc;
            font-size: 12px;
            font-weight: 700;
            letter-spacing: 0.18em;
            text-transform: uppercase;
        }}

        .quick-links {{
            display: block;
            width: 100%;
            max-width: 700px;
            margin: 18px auto 0;
            text-align: left;
        }}

        .quick-link {{
            display: block;
            width: 100%;
            margin: 0 0 16px;
            padding: 20px 20px 18px;
            box-sizing: border-box;
            border-radius: 22px;
            border: 1px solid #334155;
            background: #1e293b;
            color: #e2e8f0;
            text-decoration: none;
            text-align: left;
        }}

        .quick-link-kicker {{
            display: inline-block;
            padding: 6px 10px;
            box-sizing: border-box;
            border-radius: 999px;
            background: rgba(15, 23, 42, 0.8);
            color: #93c5fd;
            font-size: 11px;
            font-weight: 700;
            letter-spacing: 0.12em;
            text-transform: uppercase;
        }}

        .quick-link-title {{
            display: block;
            margin-top: 16px;
            color: #f8fafc;
            font-size: 24px;
            font-weight: 600;
        }}

        .quick-link-copy {{
            display: block;
            margin-top: 10px;
            color: #94a3b8;
            font-size: 14px;
            line-height: 1.6;
        }}

        .quick-link-meta {{
            display: block;
            margin-top: 16px;
            color: #60a5fa;
            font-size: 12px;
            font-weight: 600;
            letter-spacing: 0.08em;
            text-transform: uppercase;
        }}

        .quick-link-settings {{
            background: #22304a;
        }}

        .quick-link-diagnostics {{
            background: #1b2844;
        }}

        .quick-link-mdn {{
            background: #334155;
        }}

        .quick-link-stackoverflow {{
            background: #1f2a3f;
        }}

        .footer {{
            margin-top: 20px;
            padding-bottom: 16px;
            font-size: 12px;
            color: #64748b;
            text-align: center;
        }}
    </style>
</head>
<body id='fen-newtab'>
    <div class='page'>
        <div class='shell'>
            <div class='hero-panel'>
                <div class='hero-rail'>
                    <div class='eyebrow'>Start Securely</div>
                </div>
                <div class='logo-mark'>
                    <span class='logo-mark-bar logo-mark-bar-secondary'></span>
                    <span class='logo-mark-bar logo-mark-bar-primary'></span>
                    <span class='logo-mark-bar logo-mark-bar-tertiary'></span>
                </div>
                <div class='logo'><span class='logo-accent'>Fen</span>Browser</div>
                <div class='tagline'>A focused start page built for quick search, direct navigation, and a calmer browsing surface.</div>

                <div id='newtab-form' class='search-panel'>
                    <input id='url-bar' class='search-box' type='text' autocomplete='off' spellcheck='false' placeholder='Search the web or enter a URL'>
                    <div class='search-hint'>Press Enter to search, open a site, or jump straight to a domain</div>
                </div>

                <div class='shortcut-row'>
                    <span class='shortcut-chip'>/ Focus search</span>
                    <span class='shortcut-chip'>example.com Opens direct</span>
                    <span class='shortcut-chip'>fen://settings Tune the browser</span>
                </div>
            </div>

            <div class='section-title'>Jump Back In</div>
            <div class='quick-links'>
                <a class='quick-link quick-link-settings' href='fen://settings'>
                    <span class='quick-link-kicker'>Control</span>
                    <span class='quick-link-title'>Settings</span>
                    <span class='quick-link-copy'>Review privacy, appearance, and browser behavior.</span>
                    <span class='quick-link-meta'>Open Fen settings</span>
                </a>
                <a class='quick-link quick-link-diagnostics' href='https://www.whatismybrowser.com/'>
                    <span class='quick-link-kicker'>Inspect</span>
                    <span class='quick-link-title'>Diagnostics</span>
                    <span class='quick-link-copy'>Check browser identity and surface compatibility.</span>
                    <span class='quick-link-meta'>Verify browser signals</span>
                </a>
                <a class='quick-link quick-link-mdn' href='https://developer.mozilla.org/'>
                    <span class='quick-link-kicker'>Reference</span>
                    <span class='quick-link-title'>MDN</span>
                    <span class='quick-link-copy'>Open reference docs for HTML, CSS, and JavaScript.</span>
                    <span class='quick-link-meta'>Read platform docs</span>
                </a>
                <a class='quick-link quick-link-stackoverflow' href='https://stackoverflow.com/'>
                    <span class='quick-link-kicker'>Debug</span>
                    <span class='quick-link-title'>Stack Overflow</span>
                    <span class='quick-link-copy'>Jump into implementation details and debugging threads.</span>
                    <span class='quick-link-meta'>Find working fixes</span>
                </a>
            </div>

            <div class='footer'>FenBrowser | Secure private browsing with a faster start surface</div>
        </div>
    </div>
    <script>
        function resolveInputTarget(value) {{
            if (!value) {{
                return '';
            }}

            var isUrl = value.indexOf('.') > 0 && !value.includes(' ');
            var searchBase = '{searchUrl}';
            var url = isUrl ? value : searchBase + encodeURIComponent(value);

            if (isUrl && !url.startsWith('http')) {{
                url = 'https://' + url;
            }}

            return url;
        }}

        window.onload = function() {{
            var input = document.getElementById('url-bar');
            if (!input) {{
                return;
            }}

            function focusInput() {{
                input.className = 'search-box is-focused';
                input.focus();
            }}

            function blurInput() {{
                input.className = 'search-box';
            }}

            function submitValue() {{
                var val = input.value.trim();
                if (!val) {{
                    return;
                }}

                var target = resolveInputTarget(val);
                if (target) {{
                    window.location.href = target;
                }}
            }}

            input.addEventListener('click', function() {{
                focusInput();
            }});

            input.addEventListener('focus', focusInput);
            input.addEventListener('blur', blurInput);

            input.addEventListener('keydown', function(e) {{
                if (e.key === 'Enter') {{
                    e.preventDefault();
                    submitValue();
                    return;
                }}
            }});

            document.addEventListener('keydown', function(e) {{
                if (document.activeElement !== input) {{
                    if (e.key === '/') {{
                        e.preventDefault();
                        focusInput();
                    }}
                    return;
                }}
            }});
            focusInput();
        }};
    </script>
</body>
</html>";
        }
    }
}
