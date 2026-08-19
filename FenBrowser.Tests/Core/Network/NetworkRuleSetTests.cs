using System;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Network.Filtering;
using Xunit;

namespace FenBrowser.Tests.Core.Network
{
    public sealed class NetworkRuleSetTests
    {
        [Fact]
        public void Matcher_UsesDomainLabelsAndDestinationScope()
        {
            var rules = new NetworkRuleSet(1, DateTimeOffset.UnixEpoch, null, new[]
            {
                new NetworkFilterRule("tracker.example", new[] { "image" }, "/pixel")
            });

            Assert.True(rules.Matches(new Uri("https://cdn.tracker.example/pixel/id"), "image"));
            Assert.False(rules.Matches(new Uri("https://nottracker.example/pixel/id"), "image"));
            Assert.False(rules.Matches(new Uri("https://tracker.example/pixelated"), "image"));
            Assert.False(rules.Matches(new Uri("https://tracker.example/pixel/id"), "script"));
        }

        [Fact]
        public void Store_RequiresSignatureAndSupportsRollback()
        {
            var builtIn = new NetworkRuleSet(1, DateTimeOffset.UnixEpoch, null, Array.Empty<NetworkFilterRule>());
            var store = new SignedNetworkRuleSetStore(builtIn);
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var payload = Encoding.UTF8.GetBytes("{\"version\":2,\"generatedAtUtc\":\"2026-08-19T00:00:00Z\",\"rules\":[{\"domain\":\"tracker.example\"}]}");
            var signature = signer.SignData(payload, HashAlgorithmName.SHA256);

            Assert.True(store.TryInstall(payload, signature, signer, new DateTimeOffset(2026, 8, 19, 1, 0, 0, TimeSpan.Zero), out var error), error);
            Assert.True(store.Current.Matches(new Uri("https://sub.tracker.example/")));
            Assert.True(store.TryRollback());
            Assert.Equal(1, store.Current.Version);
        }
    }
}
