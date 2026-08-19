using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FenBrowser.Core.Security
{
    public sealed class DocumentSecurityContext
    {
        public DocumentSecurityContext(
            Uri documentUri,
            SchemefulSite topLevelSite,
            SandboxPolicy sandbox,
            CspPolicy contentSecurityPolicy,
            PermissionsPolicy permissionsPolicy,
            CrossOriginIsolationPolicy crossOriginIsolation,
            bool isEmbedded,
            IEnumerable<string> grantedPermissions = null)
        {
            DocumentUri = documentUri ?? throw new ArgumentNullException(nameof(documentUri));
            TopLevelSite = topLevelSite;
            Sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            ContentSecurityPolicy = contentSecurityPolicy;
            PermissionsPolicy = permissionsPolicy ?? FenBrowser.Core.Security.PermissionsPolicy.None;
            CrossOriginIsolation = crossOriginIsolation ?? new CrossOriginIsolationPolicy();
            IsEmbedded = isEmbedded;
            IsSecureContext = IsPotentiallyTrustworthy(documentUri);
            GrantedPermissions = (grantedPermissions ?? Array.Empty<string>())
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public Uri DocumentUri { get; }
        public SchemefulSite TopLevelSite { get; }
        public SandboxPolicy Sandbox { get; }
        public CspPolicy ContentSecurityPolicy { get; }
        public PermissionsPolicy PermissionsPolicy { get; }
        public CrossOriginIsolationPolicy CrossOriginIsolation { get; }
        public bool IsEmbedded { get; }
        public bool IsSecureContext { get; }
        public ImmutableHashSet<string> GrantedPermissions { get; }

        public bool Allows(SandboxFeature capability)
        {
            if (!Sandbox.Allows(capability)) return false;
            if (capability is SandboxFeature.SharedArrayBuffer or SandboxFeature.CrossOriginIsolated)
                return IsSecureContext && CrossOriginIsolation.IsCrossOriginIsolated;
            return true;
        }

        public bool AllowsFeature(PolicyControlledFeature feature)
        {
            var origin = SerializeOrigin(DocumentUri);
            return PermissionsPolicy == FenBrowser.Core.Security.PermissionsPolicy.None ||
                   PermissionsPolicy.IsFeatureAllowed(feature, origin, origin);
        }

        public static DocumentSecurityContext CreateTopLevel(
            Uri documentUri,
            CspPolicy contentSecurityPolicy,
            PermissionsPolicy permissionsPolicy,
            CrossOriginIsolationPolicy crossOriginIsolation)
        {
            var isolation = crossOriginIsolation ?? new CrossOriginIsolationPolicy();
            var sandbox = isolation.IsCrossOriginIsolated
                ? SandboxPolicy.StandardPage.WithFeature(SandboxFeature.SharedArrayBuffer | SandboxFeature.CrossOriginIsolated)
                : SandboxPolicy.StandardPage;
            return new DocumentSecurityContext(
                documentUri,
                SiteIdentityService.Default.CreateSchemefulSite(documentUri),
                sandbox,
                contentSecurityPolicy,
                permissionsPolicy,
                isolation,
                isEmbedded: false);
        }

        private static string SerializeOrigin(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri) return "null";
            return uri.GetLeftPart(UriPartial.Authority);
        }

        private static bool IsPotentiallyTrustworthy(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri) return false;
            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("fen", StringComparison.OrdinalIgnoreCase)) return true;
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
            return uri.IsLoopback;
        }
    }
}
