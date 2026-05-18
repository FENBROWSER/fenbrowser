using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Network
{
    public class SecureDnsResolverTests
    {
        [Fact]
        public async Task ResolveAsync_CanceledToken_ThrowsOperationCanceledException()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => SecureDnsResolver.ResolveAsync("example.com", cts.Token));
        }
    }
}
