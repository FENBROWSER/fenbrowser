using Xunit;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core;
using System.Net.Http;
using System.Threading.Tasks;
using System;

namespace FenBrowser.Tests.Privacy
{
    public class CookieIsolationTests
    {
        [Fact]
        public async Task PrivacyHandler_BlocksThirdPartyCookies()
        {
            // Arrange
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();
            
            var request = new HttpRequestMessage(HttpMethod.Get, "https://tracker.com/pixel.png");
            request.Headers.Add("Cookie", "id=123");
            request.Headers.Referrer = new Uri("https://example.com"); // Different host
            
            var context = new NetworkContext(request);
            
            // Act
            await handler.HandleAsync(context, () => Task.CompletedTask, default);
            
            // Assert
            Assert.False(request.Headers.Contains("Cookie"), "Third-party cookie should be removed");
        }

        [Fact]
        public async Task PrivacyHandler_AllowsFirstPartyCookies()
        {
            // Arrange
            BrowserSettings.Instance.BlockThirdPartyCookies = true;
            var handler = new PrivacyHandler();
            
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api/data");
            request.Headers.Add("Cookie", "session=abc");
            request.Headers.Referrer = new Uri("https://example.com/page"); // Same host
            
            var context = new NetworkContext(request);
            
            // Act
            await handler.HandleAsync(context, () => Task.CompletedTask, default);
            
            // Assert
            Assert.True(request.Headers.Contains("Cookie"), "First-party cookie should be preserved");
        }
    }
}
