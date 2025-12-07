using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering;

public class BrowserEngine : IBrowserEngine
{
    private readonly INetworkService _networkService;
    private readonly ILogger _logger;

    public string Title { get; private set; } = "New Tab";
    public string Url { get; private set; } = string.Empty;

    public BrowserEngine(INetworkService networkService, ILogger logger)
    {
        _networkService = networkService;
        _logger = logger;
    }

    public async Task LoadAsync(string url)
    {
        _logger.Log(LogLevel.Info, $"Loading URL: {url}");
        Url = url;
        
        try
        {
            string content = await _networkService.GetStringAsync(url);
            _logger.Log(LogLevel.Info, $"Content loaded, length: {content.Length}");
            
            // TODO: Parse HTML and Render
            Title = "Loaded: " + url; // Placeholder
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to load {url}", ex);
            Title = "Error loading page";
        }
    }
}
