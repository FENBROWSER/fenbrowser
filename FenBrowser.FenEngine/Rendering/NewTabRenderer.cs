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
        }}

        .page {{
            min-height: 100vh;
            padding: 72px 24px 120px;
            box-sizing: border-box;
            text-align: center;
        }}

        .shell {{
            display: inline-block;
            width: 700px;
            max-width: 100%;
            margin-top: 10vh;
            text-align: center;
        }}

        .eyebrow {{
            display: inline-block;
            padding: 8px 14px;
            box-sizing: border-box;
            border-radius: 999px;
            border: 1px solid #334155;
            background: rgba(15, 23, 42, 0.64);
            color: #93c5fd;
            font-size: 12px;
            font-weight: 700;
            letter-spacing: 0.16em;
            text-transform: uppercase;
        }}

        .logo {{
            margin: 26px 0 0;
            color: #f8fafc;
            font-size: 68px;
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
            max-width: 560px;
            margin-top: 16px;
            margin-left: auto;
            margin-right: auto;
            color: #94a3b8;
            font-size: 18px;
            line-height: 1.6;
        }}

        .search-panel {{
            display: block;
            width: 100%;
            max-width: 580px;
            margin-top: 34px;
            margin-left: auto;
            margin-right: auto;
            padding: 12px;
            box-sizing: border-box;
            border-radius: 30px;
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

        .quick-links {{
            display: block;
            margin-top: 36px;
        }}

        .quick-link {{
            display: block;
            width: 100%;
            margin: 12px 0 0;
            padding: 18px 20px;
            box-sizing: border-box;
            border-radius: 18px;
            border: 1px solid #334155;
            background: #1e293b;
            color: #e2e8f0;
            text-decoration: none;
            text-align: left;
        }}

        .quick-link-title {{
            display: block;
            color: #f8fafc;
            font-size: 16px;
            font-weight: 600;
        }}

        .quick-link-copy {{
            display: block;
            margin-top: 8px;
            color: #94a3b8;
            font-size: 13px;
            line-height: 1.45;
        }}

        .footer {{
            margin-top: 42px;
            font-size: 12px;
            color: #64748b;
            text-align: center;
        }}
    </style>
</head>
<body id='fen-newtab'>
    <div class='page'>
        <div class='shell'>
            <div class='eyebrow'>Start Securely</div>
            <div class='logo'><span class='logo-accent'>Fen</span>Browser</div>
            <div class='tagline'>A focused start page built for quick search, direct navigation, and a calmer browsing surface.</div>

            <div id='newtab-form' class='search-panel'>
                <input id='url-bar' class='search-box' type='text' autocomplete='off' spellcheck='false' placeholder='Search the web or enter a URL'>
                <div class='search-hint'>Press Enter to search, open a site, or jump straight to a domain</div>
            </div>

            <div class='quick-links'>
                <a class='quick-link' href='fen://settings'>
                    <span class='quick-link-title'>Settings</span>
                    <span class='quick-link-copy'>Review privacy, appearance, and browser behavior.</span>
                </a>
                <a class='quick-link' href='https://www.whatismybrowser.com/'>
                    <span class='quick-link-title'>Diagnostics</span>
                    <span class='quick-link-copy'>Check browser identity and surface compatibility.</span>
                </a>
                <a class='quick-link' href='https://developer.mozilla.org/'>
                    <span class='quick-link-title'>MDN</span>
                    <span class='quick-link-copy'>Open reference docs for HTML, CSS, and JavaScript.</span>
                </a>
                <a class='quick-link' href='https://stackoverflow.com/'>
                    <span class='quick-link-title'>Stack Overflow</span>
                    <span class='quick-link-copy'>Jump into implementation details and debugging threads.</span>
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
