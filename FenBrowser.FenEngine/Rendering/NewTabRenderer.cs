using System;

namespace FenBrowser.FenEngine.Rendering
{
    public static class NewTabRenderer
    {
        public static string Render()
        {
            var searchUrl = FenBrowser.Core.BrowserSettings.Instance.SearchEngineUrl;
            // Ensure searchUrl is JS-safe (basic escaping)
            searchUrl = searchUrl.Replace("'", "\\'");

            return $@"<!DOCTYPE html>
<html>
<head>
    <title>New Tab</title>
    <style>
        body {{
            font-family: Segoe UI, Arial, sans-serif;
            background-color: #1e293b;
            color: #f8fafc;
            margin: 0;
            padding: 0;
            height: 100vh;
            display: flex;
            flex-direction: column; /* FIX: Column ensures width is constrained (Cross Axis) */
            align-items: center;
            justify-content: center;
        }}
        
        .container {{
            max-width: 600px;
            width: 100%;
            margin: 0 auto;
            display: flex;
            flex-direction: column;
            align-items: center;
            text-align: center; /* Fix: Restore text alignment for children */
            box-sizing: border-box;
            padding: 0 20px;
        }}
        
        .logo {{
            font-size: 42px;
            font-weight: bold;
            color: #3b82f6;
            margin-bottom: 10px;
            text-align: center; /* Fix: Explicit alignment */
            width: 100%;
        }}
        
        .tagline {{
            font-size: 14px;
            color: #94a3b8;
            margin-bottom: 40px;
            text-align: center; /* Fix: Explicit alignment */
            width: 100%;
        }}
        
        .search-box {{
            display: block;
            width: 100%;
            max-width: 400px;
            box-sizing: border-box;
            padding: 12px 20px;
            border-radius: 24px;
            background-color: #334155;
            color: #f8fafc;
            font-size: 16px;
            margin-bottom: 50px; /* Removed auto margins, relying on align-items: center */
            text-align: center; /* Center text inside input */
            height: 44px;
            appearance: none;
            border: none;
            outline: 1px solid #475569;
        }}
        
        .sites-grid {{
            display: flex;
            flex-wrap: wrap;
            justify-content: center;
            gap: 16px;
            width: 100%;
        }}
        
        .site-link {{
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center; /* Center content vertically in the box */
            width: 100px;
            height: 100px;
            box-sizing: border-box;
            padding: 10px;
            text-decoration: none;
            color: #e2e8f0; /* Brightened text color */
            background-color: #334155;
            border-radius: 12px;
            transition: background-color 0.2s, transform 0.1s;
        }}
        
        .site-link:hover {{
            background-color: #475569;
            transform: translateY(-2px); /* Subtle hover lift */
        }}
        
        .site-icon {{
            width: 44px; /* Slightly smaller to fit text better */
            height: 44px;
            border-radius: 8px; 
            margin-bottom: 8px;
            object-fit: cover;
            background-color: transparent;
        }}
        
        .site-name {{
            font-size: 12px;
            color: #e2e8f0; /* Match link color */
            margin-top: 4px;
            font-family: Segoe UI, Arial, sans-serif; /* Restore primary font */
            text-align: center;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            width: 100%;
            display: block; /* Ensure block layout */
        }}

        .footer {{
            position: fixed;
            bottom: 20px;
            left: 0;
            width: 100%;
            font-size: 12px;
            color: #475569;
            text-align: center;
            pointer-events: none; /* Let clicks pass through if overlapping */
        }}
    </style>
</head>
<body id='fen-newtab'>
    <div class='container'>
        <div class='logo'>FenBrowser</div>
        <div class='tagline'>SECURE • PRIVATE • FAST</div>
        
        <input type='text' id='url-bar' class='search-box' placeholder='Search the web or enter a URL...'>
        
        <div class='sites-grid'>
             <!-- Empty default sites -->
        </div>
        
        <div class='footer'>FenBrowser - Built for the modern web</div>
    </div>
    <script>
        document.getElementById('url-bar').addEventListener('keydown', function(e) {{
            if (e.key === 'Enter') {{
                var val = this.value;
                if (!val) return;
                console.log('NewTab Input: ' + val);
                
                var isUrl = val.indexOf('.') > 0 && !val.includes(' ');
                var searchBase = '{searchUrl}';
                var url = isUrl ? val : searchBase + encodeURIComponent(val);
                
                if (isUrl && !url.startsWith('http')) {{
                    url = 'https://' + url;
                }}
                
                window.location.href = url;
            }}
        }});
        // Autofocus
        window.onload = function() {{ 
            var el = document.getElementById('url-bar');
            if(el) el.focus(); 
        }};
    </script>
</body>
</html>";
        }
    }
}
