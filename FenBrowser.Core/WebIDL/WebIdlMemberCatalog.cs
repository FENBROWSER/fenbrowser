using FenBrowser.Core.Logging;

namespace FenBrowser.Core.WebIDL;

internal sealed record WebIdlMemberResolution(
    bool KnownMember,
    string DefinedInterface,
    bool? ReceiverMatchesDefinedInterface);

internal static class WebIdlMemberCatalog
{
    private static readonly Lazy<CatalogData> Catalog = new(
        BuildCatalog,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static WebIdlMemberResolution Resolve(string receiverInterface, string propertyName)
    {
        var receiver = receiverInterface?.Trim() ?? string.Empty;
        var property = propertyName?.Trim() ?? string.Empty;
        if (receiver.Length == 0 || property.Length == 0)
        {
            return new WebIdlMemberResolution(false, string.Empty, null);
        }

        var catalog = Catalog.Value;
        if (!catalog.MemberOwners.TryGetValue(property, out var owners) || owners.Count == 0)
        {
            return new WebIdlMemberResolution(false, string.Empty, null);
        }

        if (TryResolveAccessibleMember(catalog.Interfaces, receiver, property, new HashSet<string>(StringComparer.Ordinal), out var definedInterface))
        {
            return new WebIdlMemberResolution(true, definedInterface, true);
        }

        return new WebIdlMemberResolution(true, owners[0], false);
    }

    private static bool TryResolveAccessibleMember(
        IReadOnlyDictionary<string, InterfaceEntry> interfaces,
        string interfaceName,
        string property,
        HashSet<string> visited,
        out string definedInterface)
    {
        definedInterface = string.Empty;
        if (!visited.Add(interfaceName) || !interfaces.TryGetValue(interfaceName, out var entry))
        {
            return false;
        }

        if (entry.Members.Contains(property))
        {
            definedInterface = interfaceName;
            return true;
        }

        foreach (var mixin in entry.IncludedMixins.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (TryResolveAccessibleMember(interfaces, mixin, property, visited, out definedInterface))
            {
                return true;
            }
        }

        return entry.Inherits.Length != 0 &&
               TryResolveAccessibleMember(interfaces, entry.Inherits, property, visited, out definedInterface);
    }

    private static CatalogData BuildCatalog()
    {
        var interfaces = new Dictionary<string, InterfaceEntry>(StringComparer.Ordinal);
        var includes = new List<(string Target, string Mixin)>();
        var assembly = typeof(WebIdlMemberCatalog).Assembly;

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".WebIDL.Idl.", StringComparison.Ordinal) &&
                           name.EndsWith(".idl", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resourceNames.Length == 0)
        {
            EngineLogCompat.Warn(
                "[WebIDL] Embedded member catalog has no IDL resources; missing APIs will remain unclassified.",
                LogCategory.FeatureGaps);
        }

        foreach (var resourceName in resourceNames)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    EngineLogCompat.Warn(
                        $"[WebIDL] Embedded member catalog could not open '{resourceName}'.",
                        LogCategory.FeatureGaps);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var parseResult = new WebIdlParser().Parse(reader.ReadToEnd());
                if (!parseResult.Success)
                {
                    EngineLogCompat.Warn(
                        $"[WebIDL] Embedded member catalog skipped '{resourceName}' because parsing reported {parseResult.Errors.Count} error(s).",
                        LogCategory.FeatureGaps);
                    continue;
                }

                foreach (var definition in parseResult.Definitions)
                {
                    switch (definition)
                    {
                        case IdlInterface idlInterface when !string.IsNullOrWhiteSpace(idlInterface.Name):
                        {
                            var entry = GetOrCreateInterface(interfaces, idlInterface.Name.Trim());
                            if (!string.IsNullOrWhiteSpace(idlInterface.Inherits))
                            {
                                entry.Inherits = idlInterface.Inherits.Trim();
                            }

                            foreach (var member in idlInterface.Members)
                            {
                                if (!string.IsNullOrWhiteSpace(member.Name))
                                {
                                    entry.Members.Add(member.Name.Trim());
                                }
                            }

                            break;
                        }
                        case IdlIncludes idlIncludes
                            when !string.IsNullOrWhiteSpace(idlIncludes.Target) &&
                                 !string.IsNullOrWhiteSpace(idlIncludes.Mixin):
                            includes.Add((idlIncludes.Target.Trim(), idlIncludes.Mixin.Trim()));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn(
                    $"[WebIDL] Embedded member catalog skipped '{resourceName}': {ex.GetType().Name}: {ex.Message}",
                    LogCategory.FeatureGaps);
            }
        }

        foreach (var (target, mixin) in includes)
        {
            GetOrCreateInterface(interfaces, target).IncludedMixins.Add(mixin);
        }

        var memberOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in interfaces.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            foreach (var member in entry.Members.OrderBy(member => member, StringComparer.Ordinal))
            {
                if (!memberOwners.TryGetValue(member, out var owners))
                {
                    owners = new List<string>();
                    memberOwners[member] = owners;
                }

                owners.Add(entry.Name);
            }
        }

        return new CatalogData(interfaces, memberOwners);
    }

    private static InterfaceEntry GetOrCreateInterface(
        IDictionary<string, InterfaceEntry> interfaces,
        string name)
    {
        if (!interfaces.TryGetValue(name, out var entry))
        {
            entry = new InterfaceEntry(name);
            interfaces[name] = entry;
        }

        return entry;
    }

    private sealed record CatalogData(
        IReadOnlyDictionary<string, InterfaceEntry> Interfaces,
        IReadOnlyDictionary<string, List<string>> MemberOwners);

    private sealed class InterfaceEntry
    {
        public InterfaceEntry(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Inherits { get; set; } = string.Empty;
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IncludedMixins { get; } = new(StringComparer.Ordinal);
    }
}
