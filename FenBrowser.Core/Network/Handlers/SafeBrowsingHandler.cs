using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network.Filtering;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Lightweight runtime URL safety gate for known-dangerous hosts.
    /// This is a local deny-list guard, not a full remote Safe Browsing feed.
    /// </summary>
    public sealed class SafeBrowsingHandler : INetworkHandler
    {
        private readonly Func<bool> _isEnabled;

        private static readonly SignedNetworkRuleSetStore ThreatDatabase = new(new NetworkRuleSet(
            version: 1,
            generatedAtUtc: DateTimeOffset.UnixEpoch,
            expiresAtUtc: null,
            rules: new[]
            {
                new NetworkFilterRule("testsafebrowsing.appspot.com"),
                new NetworkFilterRule("malware.testing.google.test"),
                new NetworkFilterRule("phishing.testing.google.test")
            }));

        public SafeBrowsingHandler(Func<bool> isEnabled)
        {
            _isEnabled = isEnabled ?? (() => false);
        }

        public static long ThreatDatabaseVersion => ThreatDatabase.Current.Version;

        public static bool TryInstallSignedThreatDatabase(ReadOnlySpan<byte> ruleset, ReadOnlySpan<byte> signature, ECDsa verifier, DateTimeOffset now, out string error)
            => ThreatDatabase.TryInstall(ruleset, signature, verifier, now, out error);

        public static bool TryRollbackThreatDatabase() => ThreatDatabase.TryRollback();

        public Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri == null || !_isEnabled())
            {
                return next();
            }

            if (ThreatDatabase.Current.Matches(context.Request.RequestUri))
            {
                context.IsBlocked = true;
                context.BlockReason = "SafeBrowsing";
                context.Response = new HttpResponseMessage((HttpStatusCode)451)
                {
                    ReasonPhrase = "Blocked by Safe Browsing"
                };
                return Task.CompletedTask;
            }

            return next();
        }

    }
}
