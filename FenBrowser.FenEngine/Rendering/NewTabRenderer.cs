using System;

namespace FenBrowser.FenEngine.Rendering
{
    public static class NewTabRenderer
    {
        public static string Render()
        {
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
            padding: 0;
            height: 100vh;
            display: flex;
            flex-direction: column; /* FIX: Column ensures width is constrained (Cross Axis) */
            align-items: center;
            justify-content: center;
        }
        
        .container {
            max-width: 600px;
            width: 100%;
            margin: 0 auto;
            display: flex;
            flex-direction: column;
            align-items: center;
            text-align: center; /* Fix: Restore text alignment for children */
            box-sizing: border-box;
            padding: 0 20px;
        }
        
        .logo {
            font-size: 42px;
            font-weight: bold;
            color: #3b82f6;
            margin-bottom: 10px;
            text-align: center; /* Fix: Explicit alignment */
            width: 100%;
        }
        
        .tagline {
            font-size: 14px;
            color: #94a3b8;
            margin-bottom: 40px;
            text-align: center; /* Fix: Explicit alignment */
            width: 100%;
        }
        
        .search-box {
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
        }
        
        .sites-grid {
            display: flex;
            flex-wrap: wrap;
            justify-content: center;
            gap: 16px;
            width: 100%;
        }
        
        .site-link {
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
        }
        
        .site-link:hover {
            background-color: #475569;
            transform: translateY(-2px); /* Subtle hover lift */
        }
        
        .site-icon {
            width: 44px; /* Slightly smaller to fit text better */
            height: 44px;
            border-radius: 8px; 
            margin-bottom: 8px;
            object-fit: cover;
            background-color: transparent;
        }
        
        .site-name {
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
        }

        .footer {
            position: fixed;
            bottom: 20px;
            left: 0;
            width: 100%;
            font-size: 12px;
            color: #475569;
            text-align: center;
            pointer-events: none; /* Let clicks pass through if overlapping */
        }
    </style>
</head>
<body id='fen-newtab'>
    <div class='container'>
        <div class='logo'>FenBrowser</div>
        <div class='tagline'>SECURE • PRIVATE • FAST</div>
        
        <input type='text' id='url-bar' class='search-box' placeholder='Search the web or enter a URL...'>
        
        <div class='sites-grid'>
            <a href='https://www.google.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.google.com&sz=64' class='site-icon' />
                <div class='site-name'>Google</div>
            </a>
            <a href='https://www.youtube.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.youtube.com&sz=64' class='site-icon' />
                <div class='site-name'>YouTube</div>
            </a>
            <a href='https://www.facebook.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.facebook.com&sz=64' class='site-icon' />
                <div class='site-name'>Facebook</div>
            </a>
            <a href='https://github.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=github.com&sz=64' class='site-icon' />
                <div class='site-name'>GitHub</div>
            </a>
            <a href='https://www.wikipedia.org' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.wikipedia.org&sz=64' class='site-icon' />
                <div class='site-name'>Wikipedia</div>
            </a>
            <a href='https://www.reddit.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.reddit.com&sz=64' class='site-icon' />
                <div class='site-name'>Reddit</div>
            </a>
            <a href='https://twitter.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=twitter.com&sz=64' class='site-icon' />
                <div class='site-name'>X / Twitter</div>
            </a>
            <a href='https://www.amazon.com' class='site-link'>
                <img src='https://www.google.com/s2/favicons?domain=www.amazon.com&sz=64' class='site-icon' />
                <div class='site-name'>Amazon</div>
            </a>
        </div>
        
        <div class='footer'>FenBrowser - Built for the modern web</div>
    </div>
</body>
</html>";
        }
    }
}
