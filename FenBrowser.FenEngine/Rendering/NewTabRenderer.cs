using System;

namespace FenBrowser.FenEngine.Rendering
{
    public static class NewTabRenderer
    {
        public static string Render()
        {
            // Simplified HTML/CSS for FenBrowser's layout engine
            return @"<!DOCTYPE html>
<html>
<head>
    <title>New Tab</title>
    <style>
        body {
            font-family: Segoe UI, Arial, sans-serif;
            background-color: #1e293b;
            color: #f8fafc;
            margin: 0;
            padding: 60px 20px;
            text-align: center;
        }
        
        .logo {
            font-size: 42px;
            font-weight: bold;
            color: #3b82f6;
            margin-bottom: 10px;
        }
        
        .tagline {
            font-size: 14px;
            color: #94a3b8;
            margin-bottom: 40px;
        }
        
        .search-box {
            width: 500px;
            padding: 14px 20px;
            border-radius: 24px;
            border: 1px solid #475569;
            background-color: #334155;
            color: #f8fafc;
            font-size: 16px;
            margin-bottom: 50px;
        }
        
        .sites-container {
            margin: 0 auto;
            width: 600px;
        }

        .site-row {
            margin-bottom: 20px;
        }
        
        .site-link {
            display: inline-block;
            width: 120px;
            padding: 16px 8px;
            margin: 0 10px;
            text-decoration: none;
            color: #cbd5e1;
            background-color: #334155;
            border-radius: 12px;
            vertical-align: top;
        }
        
        .site-icon {
            width: 48px;
            height: 48px;
            margin: 0 auto 10px auto;
            border-radius: 10px;
            font-size: 24px;
            font-weight: bold;
            line-height: 48px;
            color: white;
        }
        
        .google-bg { background-color: #4285f4; }
        .youtube-bg { background-color: #ff0000; }
        .facebook-bg { background-color: #1877f2; }
        .github-bg { background-color: #333333; }
        .wikipedia-bg { background-color: #636466; }
        .reddit-bg { background-color: #ff4500; }
        .twitter-bg { background-color: #1da1f2; }
        .amazon-bg { background-color: #ff9900; }
        
        .site-name {
            font-size: 13px;
        }

        .footer {
            margin-top: 60px;
            font-size: 12px;
            color: #475569;
        }
    </style>
</head>
<body>
    <div class='logo'>FenBrowser</div>
    <div class='tagline'>SECURE • PRIVATE • FAST</div>
    
    <div>
        <input type='text' class='search-box' placeholder='Search the web or enter a URL...'>
    </div>
    
    <div class='sites-container'>
        <div class='site-row'>
            <a href='https://www.google.com' class='site-link'>
                <div class='site-icon google-bg'>G</div>
                <div class='site-name'>Google</div>
            </a>
            <a href='https://www.youtube.com' class='site-link'>
                <div class='site-icon youtube-bg'>Y</div>
                <div class='site-name'>YouTube</div>
            </a>
            <a href='https://www.facebook.com' class='site-link'>
                <div class='site-icon facebook-bg'>f</div>
                <div class='site-name'>Facebook</div>
            </a>
            <a href='https://github.com' class='site-link'>
                <div class='site-icon github-bg'>G</div>
                <div class='site-name'>GitHub</div>
            </a>
        </div>
        <div class='site-row'>
            <a href='https://www.wikipedia.org' class='site-link'>
                <div class='site-icon wikipedia-bg'>W</div>
                <div class='site-name'>Wikipedia</div>
            </a>
            <a href='https://www.reddit.com' class='site-link'>
                <div class='site-icon reddit-bg'>R</div>
                <div class='site-name'>Reddit</div>
            </a>
            <a href='https://twitter.com' class='site-link'>
                <div class='site-icon twitter-bg'>X</div>
                <div class='site-name'>X / Twitter</div>
            </a>
            <a href='https://www.amazon.com' class='site-link'>
                <div class='site-icon amazon-bg'>a</div>
                <div class='site-name'>Amazon</div>
            </a>
        </div>
    </div>
    
    <div class='footer'>FenBrowser - Built for the modern web</div>
</body>
</html>";
        }
    }
}
