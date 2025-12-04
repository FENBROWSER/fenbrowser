using System;

namespace FenBrowser.FenEngine.Rendering
{
    public static class NewTabRenderer
    {
        public static string Render()
        {
            return @"
                <html>
                <head>
                    <title>New Tab</title>
                    <style>
                        body {
                            font-family: 'Segoe UI', system-ui, sans-serif;
                            background-color: #f3f3f3; /* Light gray background */
                            color: #333;
                            display: flex;
                            flex-direction: column;
                            align-items: center;
                            height: 100vh;
                            margin: 0;
                        }
                        @media (prefers-color-scheme: dark) {
                            body {
                                background-color: #202020;
                                color: #ffffff;
                            }
                        }
                        .search-container {
                            margin-top: 15vh;
                            width: 100%;
                            max-width: 600px;
                            text-align: center;
                        }
                        .logo {
                            font-size: 48px;
                            font-weight: bold;
                            margin-bottom: 32px;
                            color: #3b82f6; /* Accent color */
                        }
                        .search-box {
                            width: 100%;
                            padding: 12px 20px;
                            border-radius: 24px;
                            border: 1px solid #ccc;
                            font-size: 16px;
                            box-shadow: 0 2px 6px rgba(0,0,0,0.1);
                            outline: none;
                            transition: box-shadow 0.2s;
                        }
                        .search-box:focus {
                            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
                            border-color: #3b82f6;
                        }
                        .quick-links {
                            display: flex;
                            gap: 24px;
                            margin-top: 40px;
                            justify-content: center;
                        }
                        .link-item {
                            display: flex;
                            flex-direction: column;
                            align-items: center;
                            text-decoration: none;
                            color: inherit;
                            width: 80px;
                        }
                        .link-icon {
                            width: 48px;
                            height: 48px;
                            background-color: #e0e0e0;
                            border-radius: 50%;
                            display: flex;
                            justify-content: center;
                            align-items: center;
                            margin-bottom: 8px;
                            font-size: 20px;
                        }
                        .link-title {
                            font-size: 12px;
                            text-align: center;
                            white-space: nowrap;
                            overflow: hidden;
                            text-overflow: ellipsis;
                            width: 100%;
                        }
                    </style>
                </head>
                <body>
                    <div class='search-container'>
                        <div class='logo'>FenBrowser</div>
                        <input type='text' class='search-box' placeholder='Search the web or enter URL' onkeydown='if(event.key===""Enter"") window.location.href=""https://www.google.com/search?q=""+encodeURIComponent(this.value)'>
                    </div>

                    <div class='quick-links'>
                        <a href='https://www.google.com' class='link-item'>
                            <div class='link-icon'>G</div>
                            <div class='link-title'>Google</div>
                        </a>
                        <a href='https://www.youtube.com' class='link-item'>
                            <div class='link-icon'>Y</div>
                            <div class='link-title'>YouTube</div>
                        </a>
                        <a href='https://github.com' class='link-item'>
                            <div class='link-icon'>&#128049;</div>
                            <div class='link-title'>GitHub</div>
                        </a>
                        <a href='https://news.ycombinator.com' class='link-item'>
                            <div class='link-icon'>H</div>
                            <div class='link-title'>Hacker News</div>
                        </a>
                    </div>
                    
                    <script>
                        document.querySelector('.search-box').focus();
                    </script>
                </body>
                </html>";
        }
    }
}
