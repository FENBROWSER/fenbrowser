using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FenBrowser.Core.WebIDL;

namespace FenBrowser.Tooling;

internal static class WebIdlInventoryRunner
{
    private const string SchemaVersion = "fenbrowser.webidl-manual-binding-inventory.v1";

    public static void Run(string[] args)
    {
        var options = ParseOptions(args);
        var report = Build(options);
        Directory.CreateDirectory(options.OutputDirectory);

        var jsonPath = Path.Combine(options.OutputDirectory, "webidl_manual_binding_inventory.json");
        var markdownPath = Path.Combine(options.OutputDirectory, "webidl_manual_binding_inventory.md");
        File.WriteAllText(jsonPath, Serialize(report), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, RenderMarkdown(report), new UTF8Encoding(false));

        Console.WriteLine($"[webidl-inventory] definitions={report.Summary.Definitions} members={report.Summary.Members}");
        Console.WriteLine($"[webidl-inventory] manual-evidence={report.Summary.MembersWithManualEvidence} generated-active={report.Summary.GeneratedOutputsCompiled}");
        Console.WriteLine($"[webidl-inventory] candidate={report.LowLifetimeRiskCandidate.Definition}");
        Console.WriteLine($"[webidl-inventory] json={jsonPath}");
        Console.WriteLine($"[webidl-inventory] markdown={markdownPath}");
    }

    internal static WebIdlInventoryReport Build(WebIdlInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(options.RepositoryRoot);
        var idlRoot = Path.GetFullPath(options.IdlDirectory);
        if (!Directory.Exists(idlRoot))
        {
            throw new DirectoryNotFoundException($"WebIDL input directory not found: {idlRoot}");
        }

        var sourceFiles = LoadActiveSourceFiles(root, "FenBrowser.Core", "FenBrowser.FenEngine");
        var testFiles = LoadActiveSourceFiles(root, "FenBrowser.Tests");
        var selectedWptFiles = LoadSelectedWptFiles(options.SelectedWptRoot, options.SelectedWptPaths);
        var definitions = ParseDefinitions(root, idlRoot);
        var generatedRemove = ReadCompileRemoves(Path.Combine(root, "FenBrowser.FenEngine", "FenBrowser.FenEngine.csproj"))
            .Any(pattern => NormalizePath(pattern).StartsWith("Bindings/Generated/", StringComparison.OrdinalIgnoreCase));

        var records = new List<WebIdlDefinitionInventory>();
        foreach (var definition in definitions)
        {
            var generatedFileName = GeneratedFileName(definition);
            var generatedRelative = generatedFileName.Length == 0
                ? "not generated independently"
                : $"FenBrowser.FenEngine/Bindings/Generated/{generatedFileName}";
            var generatedFull = generatedFileName.Length == 0
                ? string.Empty
                : Path.Combine(root, generatedRelative.Replace('/', Path.DirectorySeparatorChar));
            var members = definition.Members
                .Select(member => BuildMemberInventory(definition, member, sourceFiles, testFiles, selectedWptFiles))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ThenBy(member => member.IdlKind, StringComparer.Ordinal)
                .ThenBy(member => member.Signature, StringComparer.Ordinal)
                .ToList();

            records.Add(new WebIdlDefinitionInventory
            {
                Name = definition.Name,
                IdlKind = definition.Kind,
                Inheritance = definition.Inheritance,
                IdlSources = definition.SourceFiles,
                GeneratedOutputLocation = generatedRelative,
                GeneratedOutputExists = generatedFull.Length > 0 && File.Exists(generatedFull),
                GeneratedCompileIncluded = generatedFull.Length > 0 && File.Exists(generatedFull) && !generatedRemove,
                Members = members,
                LifetimeComplexity = ClassifyDefinitionLifetime(definition),
                MigrationRisk = ClassifyDefinitionRisk(definition)
            });
        }

        records = records
            .OrderBy(record => record.Name, StringComparer.Ordinal)
            .ThenBy(record => record.IdlKind, StringComparer.Ordinal)
            .ToList();

        var candidate = SelectLowLifetimeRiskCandidate(records);
        var idlHash = ComputeInputHash(idlRoot);
        return new WebIdlInventoryReport
        {
            SchemaVersion = SchemaVersion,
            FenBrowserRevision = TryReadGitRevision(root),
            IdlInputSha256 = idlHash,
            IdlRoot = NormalizePath(Path.GetRelativePath(root, idlRoot)),
            GeneratedBindingCompilePolicy = generatedRemove
                ? "excluded by FenBrowser.FenEngine.csproj Compile Remove"
                : "not explicitly excluded",
            RuntimeBindingPolicy = "manual FenJS host dispatch; inventory only; generated bindings not activated",
            Definitions = records,
            LowLifetimeRiskCandidate = candidate,
            Summary = new WebIdlInventorySummary
            {
                Definitions = records.Count,
                Members = records.Sum(record => record.Members.Count),
                MembersWithManualEvidence = records.Sum(record => record.Members.Count(member => member.ManualImplementationLocations.Count > 0)),
                MembersWithActiveTests = records.Sum(record => record.Members.Count(member => member.TestCoverage.Count > 0)),
                MembersWithSelectedWptCoverage = records.Sum(record => record.Members.Count(member => member.SelectedWptCoverage.Count > 0)),
                GeneratedOutputsPresent = records.Count(record => record.GeneratedOutputExists),
                GeneratedOutputsCompiled = records.Count(record => record.GeneratedCompileIncluded)
            },
            AuditWarnings = new List<string>
            {
                "Manual implementation locations are source-evidence candidates, not proof of complete WebIDL behavior.",
                "Missing source evidence means not evidenced by this offline scan; it does not prove the API is absent.",
                "Test and selected-WPT coverage are name-based correlations that require behavioral review before migration.",
                "Real-site usage is intentionally reported as not measured unless a future correlated trace input is supplied.",
                "No generated binding output was created, compiled, or activated by this command."
            }
        };
    }

    internal static string Serialize(WebIdlInventoryReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static WebIdlMemberInventory BuildMemberInventory(
        ParsedDefinition definition,
        ParsedMember member,
        IReadOnlyList<SourceFile> sourceFiles,
        IReadOnlyList<SourceFile> testFiles,
        IReadOnlyList<SourceFile> selectedWptFiles)
    {
        var evidence = FindSourceEvidence(sourceFiles, definition.Name, member.Name);
        var contexts = evidence.Select(item => item.Context).ToArray();
        return new WebIdlMemberInventory
        {
            Name = member.Name,
            IdlKind = member.Kind,
            Signature = member.Signature,
            ManualImplementationLocations = evidence.Select(item => new WebIdlSourceEvidence
            {
                Path = item.Path,
                Line = item.Line,
                Evidence = item.Evidence,
                ActiveCompileInclusion = true
            }).ToList(),
            GeneratedOutputLocation = GeneratedFileName(definition).Length == 0
                ? "not generated independently"
                : $"FenBrowser.FenEngine/Bindings/Generated/{GeneratedFileName(definition)}",
            ActiveCompileInclusion = false,
            ConversionImplementation = DescribeEvidence(contexts, "conversion", "CoerceTo", "Convert", "FromString", "FromBoolean", "ToHost"),
            BrandCheckImplementation = DescribeEvidence(contexts, "brand or receiver check", "RequireHostObject", "ResolveHostObject", "IsCheckable", "IsCheckbox", "Illegal invocation", "receiver"),
            DescriptorImplementation = DescribeEvidence(contexts, "manual callable or descriptor", "GetOrCreateHostCallable", "AllocateNativeFunction", "DefineProperty", "CreatePrototype"),
            ExceptionBehavior = DescribeEvidence(contexts, "explicit exception path", "ThrowDomException", "throw new", "JsTypeError", "TypeError"),
            TestCoverage = FindCoverage(testFiles, definition.Name, member.Name),
            SelectedWptCoverage = FindCoverage(selectedWptFiles, definition.Name, member.Name),
            RealSiteUsage = "not measured by offline source inventory",
            LifetimeComplexity = ClassifyMemberLifetime(definition.Name, member),
            MigrationRisk = ClassifyMemberRisk(definition.Name, member, evidence.Count > 0)
        };
    }

    private static List<ParsedDefinition> ParseDefinitions(string repositoryRoot, string idlRoot)
    {
        var parsed = new Dictionary<string, ParsedDefinition>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(idlRoot, "*.idl", SearchOption.AllDirectories)
                     .OrderBy(path => NormalizePath(Path.GetRelativePath(idlRoot, path)), StringComparer.Ordinal))
        {
            var result = new WebIdlParser().Parse(File.ReadAllText(file));
            if (!result.Success)
            {
                throw new InvalidDataException($"{NormalizePath(Path.GetRelativePath(repositoryRoot, file))}: {string.Join("; ", result.Errors)}");
            }

            var source = NormalizePath(Path.GetRelativePath(repositoryRoot, file));
            foreach (var definition in result.Definitions)
            {
                var key = DefinitionKind(definition) + ":" + (definition.Name ?? string.Empty);
                if (!parsed.TryGetValue(key, out var aggregate))
                {
                    aggregate = new ParsedDefinition
                    {
                        Name = definition.Name ?? DefinitionKind(definition),
                        Kind = DefinitionKind(definition),
                        Inheritance = DefinitionInheritance(definition)
                    };
                    parsed.Add(key, aggregate);
                }

                aggregate.SourceFiles.Add(source);
                aggregate.Members.AddRange(DefinitionMembers(definition));
            }
        }

        foreach (var definition in parsed.Values)
        {
            definition.SourceFiles = definition.SourceFiles.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        return parsed.Values.ToList();
    }

    private static IEnumerable<ParsedMember> DefinitionMembers(IdlDefinition definition)
    {
        if (definition is IdlInterface iface)
        {
            return iface.Members.Select(ToParsedMember);
        }

        if (definition is IdlDictionary dictionary)
        {
            return dictionary.Members.Select(member => new ParsedMember
            {
                Name = member.Name ?? "<unnamed>",
                Kind = member.Required ? "required dictionary member" : "dictionary member",
                Signature = $"{member.Type} {member.Name}" + (member.DefaultValue == null ? string.Empty : $" = {member.DefaultValue}"),
                TypeText = member.Type?.ToString() ?? string.Empty
            });
        }

        if (definition is IdlEnum idlEnum)
        {
            return idlEnum.Values.Select(value => new ParsedMember
            {
                Name = value,
                Kind = "enum value",
                Signature = $"\"{value}\"",
                TypeText = "DOMString"
            });
        }

        if (definition is IdlNamespace idlNamespace)
        {
            return idlNamespace.Members.Select(ToParsedMember);
        }

        if (definition is IdlCallback callback)
        {
            if (callback.IsFunction)
            {
                return new[]
                {
                    new ParsedMember
                    {
                        Name = "callback",
                        Kind = "callback function",
                        Signature = $"{callback.ReturnType} callback({string.Join(", ", callback.Arguments.Select(FormatArgument))})",
                        TypeText = callback.ReturnType?.ToString() ?? string.Empty
                    }
                };
            }

            return callback.Members.Select(ToParsedMember);
        }

        return Array.Empty<ParsedMember>();
    }

    private static ParsedMember ToParsedMember(IdlMember member)
    {
        var name = string.IsNullOrWhiteSpace(member.Name) ? member.Kind.ToString().ToLowerInvariant() : member.Name;
        var signature = member.Kind switch
        {
            IdlMemberKind.Attribute or IdlMemberKind.StaticAttribute => $"{(member.Readonly ? "readonly " : string.Empty)}attribute {member.Type} {name}",
            IdlMemberKind.Const => $"const {member.Type} {name} = {member.ConstValue}",
            IdlMemberKind.Iterable or IdlMemberKind.Maplike or IdlMemberKind.Setlike => $"{member.Kind.ToString().ToLowerInvariant()}<{string.Join(", ", member.IterableTypes)}>",
            _ => $"{member.Type} {name}({string.Join(", ", member.Arguments.Select(FormatArgument))})"
        };
        return new ParsedMember
        {
            Name = name ?? "<unnamed>",
            Kind = member.Kind.ToString(),
            Signature = signature,
            TypeText = string.Join(" ", new[] { member.Type?.ToString() ?? string.Empty }.Concat(member.Arguments.Select(argument => argument.Type?.ToString() ?? string.Empty)))
        };
    }

    private static string FormatArgument(IdlArgument argument) =>
        $"{(argument.Optional ? "optional " : string.Empty)}{argument.Type} {argument.Name}{(argument.Variadic ? "..." : string.Empty)}" +
        (argument.DefaultValue == null ? string.Empty : $" = {argument.DefaultValue}");

    private static string DefinitionKind(IdlDefinition definition) => definition switch
    {
        IdlInterface iface when iface.IsMixin => "interface mixin",
        IdlInterface => "interface",
        IdlDictionary => "dictionary",
        IdlEnum => "enum",
        IdlTypedef => "typedef",
        IdlIncludes => "includes",
        IdlNamespace => "namespace",
        IdlCallback callback when callback.IsFunction => "callback function",
        IdlCallback => "callback interface",
        _ => definition.GetType().Name
    };

    private static string DefinitionInheritance(IdlDefinition definition) => definition switch
    {
        IdlInterface iface => iface.Inherits ?? string.Empty,
        IdlDictionary dictionary => dictionary.Inherits ?? string.Empty,
        IdlIncludes includes => $"{includes.Target} includes {includes.Mixin}",
        _ => string.Empty
    };

    private static string GeneratedFileName(ParsedDefinition definition) => definition.Kind switch
    {
        "interface" => definition.Name + "Binding.g.cs",
        "dictionary" => definition.Name + "Binding.g.cs",
        "enum" => definition.Name + "Binding.g.cs",
        "namespace" => definition.Name + "NamespaceBinding.g.cs",
        "callback function" or "callback interface" => definition.Name + "CallbackBinding.g.cs",
        _ => string.Empty
    };

    private static List<SourceEvidenceCandidate> FindSourceEvidence(
        IReadOnlyList<SourceFile> files,
        string definitionName,
        string memberName)
    {
        var candidates = new List<SourceEvidenceCandidate>();
        var memberPattern = BuildEvidencePattern(memberName);
        var definitionPattern = new Regex($@"\b(class|record|interface)\s+{Regex.Escape(definitionName)}\b", RegexOptions.CultureInvariant);
        foreach (var file in files)
        {
            for (var index = 0; index < file.Lines.Length; index++)
            {
                var line = file.Lines[index];
                if (!memberPattern.IsMatch(line) && !definitionPattern.IsMatch(line))
                {
                    continue;
                }

                var contextStart = Math.Max(0, index - 5);
                var contextEnd = Math.Min(file.Lines.Length - 1, index + 5);
                var context = string.Join(" ", file.Lines[contextStart..(contextEnd + 1)].Select(value => value.Trim()));
                candidates.Add(new SourceEvidenceCandidate
                {
                    Path = file.Path,
                    Line = index + 1,
                    Evidence = line.Trim().Length <= 180 ? line.Trim() : line.Trim()[..180],
                    Context = context
                });
                if (candidates.Count >= 8)
                {
                    return candidates;
                }
            }
        }

        return candidates;
    }

    private static Regex BuildEvidencePattern(string memberName)
    {
        var escaped = Regex.Escape(memberName);
        var pascal = Regex.Escape(ToPascalCase(memberName));
        return new Regex(
            $"(?:case\\s+\"{escaped}\"|string\\.Equals\\([^\\r\\n]*\"{escaped}\"|(?:RegisterGlobalValue|AllocateNativeFunction)\\(\\s*\"{escaped}\"|\\b(public|internal|protected)\\s+[^;=\\r\\n]+\\b{pascal}\\s*(?:\\{{|=>|\\())",
            RegexOptions.CultureInvariant);
    }

    private static List<string> FindCoverage(IReadOnlyList<SourceFile> files, string definitionName, string memberName)
    {
        return files
            .Where(file => ContainsWord(file.Text, definitionName) || ContainsWord(file.Text, memberName))
            .Select(file => file.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(12)
            .ToList();
    }

    private static bool ContainsWord(string text, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(value)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string DescribeEvidence(IEnumerable<string> contexts, string label, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (contexts.Any(context => context.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return $"evidenced manual {label}: {marker}";
            }
        }

        return $"no {label} evidenced by bounded source scan";
    }

    private static string ClassifyDefinitionLifetime(ParsedDefinition definition)
    {
        if (definition.Kind is "enum" or "typedef")
        {
            return "low";
        }

        if (definition.Kind == "dictionary" && definition.Members.All(member => IsPrimitiveType(member.TypeText)))
        {
            return "low";
        }

        return definition.Members.Any(member => IsHighLifetimeType(member.TypeText)) ? "high" : "medium";
    }

    private static string ClassifyDefinitionRisk(ParsedDefinition definition) => ClassifyDefinitionLifetime(definition) switch
    {
        "low" => "low: value conversion only; still requires conversion and exception tests",
        "medium" => "medium: interface/prototype or composite-value behavior",
        _ => "high: wrapper, callback, collection, Promise, or cross-realm lifetime concerns"
    };

    private static string ClassifyMemberLifetime(string definitionName, ParsedMember member)
    {
        var combined = definitionName + " " + member.TypeText + " " + member.Kind;
        if (IsHighLifetimeType(combined))
        {
            return "high";
        }

        return IsPrimitiveType(member.TypeText) ? "low" : "medium";
    }

    private static string ClassifyMemberRisk(string definitionName, ParsedMember member, bool hasManualEvidence)
    {
        var lifetime = ClassifyMemberLifetime(definitionName, member);
        var evidence = hasManualEvidence ? "manual path evidenced" : "manual path not evidenced";
        return $"{lifetime}: {evidence}; generated path excluded";
    }

    private static bool IsPrimitiveType(string value)
    {
        var normalized = value.Replace("?", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 0 || Regex.IsMatch(
            normalized,
            @"^(?:(?:unsigned |unrestricted )?(?:short|long|double|float)|boolean|byte|octet|DOMString|USVString|ByteString|undefined|any)(?:\s+(?:(?:unsigned |unrestricted )?(?:short|long|double|float)|boolean|byte|octet|DOMString|USVString|ByteString|undefined|any))*$",
            RegexOptions.CultureInvariant);
    }

    private static bool IsHighLifetimeType(string value) => Regex.IsMatch(
        value ?? string.Empty,
        @"\b(Node|Element|Document|Window|EventTarget|EventListener|Callback|Promise|sequence|record|iterable|maplike|setlike|Collection|List|AbortSignal)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static WebIdlLowLifetimeCandidate SelectLowLifetimeRiskCandidate(IReadOnlyList<WebIdlDefinitionInventory> definitions)
    {
        var selected = definitions
            .Where(definition => definition.LifetimeComplexity == "low")
            .Where(definition => definition.IdlKind is "dictionary" or "enum" or "typedef")
            .OrderByDescending(definition => definition.Members.Count(member => member.ManualImplementationLocations.Count > 0))
            .ThenBy(definition => definition.IdlKind == "dictionary" ? 0 : 1)
            .ThenBy(definition => definition.Members.Count)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return selected == null
            ? new WebIdlLowLifetimeCandidate
            {
                Definition = "none",
                Rationale = "No checked-in low-lifetime definition has manual source evidence.",
                Preconditions = new List<string> { "Add or identify deterministic conversion coverage before selection." }
            }
            : new WebIdlLowLifetimeCandidate
            {
                Definition = selected.Name,
                Rationale = "Value-only definition with no wrapper identity; selected by deterministic lifetime and manual-evidence scoring.",
                Preconditions = new List<string>
                {
                    "Keep generated runtime bindings disabled.",
                    "Add side-by-side conversion, default-value, and exception tests.",
                    "Confirm the manual evidence candidate is the active call path before integration."
                }
            };
    }

    private static IReadOnlyList<SourceFile> LoadActiveSourceFiles(string root, params string[] projects)
    {
        var result = new List<SourceFile>();
        foreach (var projectName in projects)
        {
            var projectRoot = Path.Combine(root, projectName);
            if (!Directory.Exists(projectRoot))
            {
                continue;
            }

            var removes = ReadCompileRemoves(Path.Combine(projectRoot, projectName + ".csproj"));
            foreach (var file in Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !HasDirectorySegment(path, "bin") && !HasDirectorySegment(path, "obj"))
                         .OrderBy(path => NormalizePath(Path.GetRelativePath(root, path)), StringComparer.Ordinal))
            {
                var projectRelative = NormalizePath(Path.GetRelativePath(projectRoot, file));
                if (removes.Any(remove => GlobMatches(remove, projectRelative)))
                {
                    continue;
                }

                result.Add(ReadSourceFile(root, file));
            }
        }

        return result;
    }

    private static IReadOnlyList<SourceFile> LoadSelectedWptFiles(string? wptRoot, IReadOnlyList<string> selectedPaths)
    {
        if (string.IsNullOrWhiteSpace(wptRoot) || !Directory.Exists(wptRoot))
        {
            return Array.Empty<SourceFile>();
        }

        var result = new List<SourceFile>();
        foreach (var selectedPath in selectedPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(wptRoot, selectedPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var text = File.ReadAllText(fullPath);
            result.Add(new SourceFile
            {
                Path = NormalizePath(selectedPath),
                Text = text,
                Lines = SplitLines(text)
            });
        }

        return result;
    }

    private static SourceFile ReadSourceFile(string root, string path)
    {
        var text = File.ReadAllText(path);
        return new SourceFile
        {
            Path = NormalizePath(Path.GetRelativePath(root, path)),
            Text = text,
            Lines = SplitLines(text)
        };
    }

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static List<string> ReadCompileRemoves(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            return new List<string>();
        }

        return XDocument.Load(projectPath)
            .Descendants()
            .Where(element => element.Name.LocalName == "Compile")
            .Select(element => element.Attribute("Remove")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizePath(value!))
            .ToList();
    }

    private static bool GlobMatches(string pattern, string relativePath)
    {
        var normalized = NormalizePath(pattern);
        var regex = "^" + Regex.Escape(normalized)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(relativePath, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasDirectorySegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => string.Equals(value, segment, StringComparison.OrdinalIgnoreCase));

    private static string ComputeInputHash(string idlRoot)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.GetFiles(idlRoot, "*.idl", SearchOption.AllDirectories)
                     .OrderBy(path => NormalizePath(Path.GetRelativePath(idlRoot, path)), StringComparer.Ordinal))
        {
            builder.Append(NormalizePath(Path.GetRelativePath(idlRoot, file))).Append('\n');
            builder.Append(File.ReadAllText(file)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string TryReadGitRevision(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
            {
                return "unavailable";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output : "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string RenderMarkdown(WebIdlInventoryReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WebIDL Manual Binding Inventory");
        builder.AppendLine();
        builder.AppendLine($"Schema: `{report.SchemaVersion}`  ");
        builder.AppendLine($"FenBrowser revision: `{report.FenBrowserRevision}`  ");
        builder.AppendLine($"IDL input SHA-256: `{report.IdlInputSha256}`  ");
        builder.AppendLine($"Generated compile policy: {report.GeneratedBindingCompilePolicy}");
        builder.AppendLine();
        builder.AppendLine($"Definitions: {report.Summary.Definitions}; members: {report.Summary.Members}; members with manual evidence: {report.Summary.MembersWithManualEvidence}; generated outputs compiled: {report.Summary.GeneratedOutputsCompiled}.");
        builder.AppendLine();
        builder.AppendLine("## Low-lifetime-risk candidate");
        builder.AppendLine();
        builder.AppendLine($"`{report.LowLifetimeRiskCandidate.Definition}` — {report.LowLifetimeRiskCandidate.Rationale}");
        builder.AppendLine();
        builder.AppendLine("## Member inventory");
        builder.AppendLine();
        builder.AppendLine("| Definition | Member | IDL kind | Inheritance | Manual evidence | Generated active | Tests | Selected WPT | Lifetime | Migration risk |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | --- | ---: | ---: | --- | --- |");
        foreach (var definition in report.Definitions)
        {
            if (definition.Members.Count == 0)
            {
                builder.AppendLine($"| {EscapeMarkdown(definition.Name)} | — | {EscapeMarkdown(definition.IdlKind)} | {EscapeMarkdown(definition.Inheritance)} | 0 | {definition.GeneratedCompileIncluded} | 0 | 0 | {definition.LifetimeComplexity} | {EscapeMarkdown(definition.MigrationRisk)} |");
                continue;
            }

            foreach (var member in definition.Members)
            {
                builder.AppendLine($"| {EscapeMarkdown(definition.Name)} | {EscapeMarkdown(member.Name)} | {EscapeMarkdown(member.IdlKind)} | {EscapeMarkdown(definition.Inheritance)} | {member.ManualImplementationLocations.Count} | {member.ActiveCompileInclusion} | {member.TestCoverage.Count} | {member.SelectedWptCoverage.Count} | {member.LifetimeComplexity} | {EscapeMarkdown(member.MigrationRisk)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Audit limitations");
        builder.AppendLine();
        foreach (var warning in report.AuditWarnings)
        {
            builder.AppendLine($"- {warning}");
        }

        return builder.ToString();
    }

    private static string EscapeMarkdown(string? value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static WebIdlInventoryOptions ParseOptions(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var idlExplicit = false;
        var outputExplicit = false;
        var options = new WebIdlInventoryOptions
        {
            RepositoryRoot = root,
            IdlDirectory = Path.Combine(root, "FenBrowser.Core", "WebIDL", "Idl"),
            OutputDirectory = Path.Combine(root, "Results", "webidl", "manual-binding-inventory"),
            SelectedWptRoot = @"C:\Users\udayk\Videos\wpt",
            SelectedWptPaths = new List<string>()
        };

        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            if (TryReadOption(arg, "--root", args, ref index, out var repositoryRoot))
            {
                options.RepositoryRoot = Path.GetFullPath(repositoryRoot);
                continue;
            }

            if (TryReadOption(arg, "--idl", args, ref index, out var idlDirectory))
            {
                options.IdlDirectory = Path.GetFullPath(idlDirectory);
                idlExplicit = true;
                continue;
            }

            if (TryReadOption(arg, "--output-dir", args, ref index, out var outputDirectory))
            {
                options.OutputDirectory = Path.GetFullPath(outputDirectory);
                outputExplicit = true;
                continue;
            }

            if (TryReadOption(arg, "--wpt-root", args, ref index, out var wptRoot))
            {
                options.SelectedWptRoot = Path.GetFullPath(wptRoot);
                continue;
            }

            if (TryReadOption(arg, "--selected-wpt", args, ref index, out var selectedWpt))
            {
                options.SelectedWptPaths = selectedWpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        if (!idlExplicit)
        {
            options.IdlDirectory = Path.Combine(options.RepositoryRoot, "FenBrowser.Core", "WebIDL", "Idl");
        }

        if (!outputExplicit)
        {
            options.OutputDirectory = Path.Combine(options.RepositoryRoot, "Results", "webidl", "manual-binding-inventory");
        }

        return options;
    }

    private static bool TryReadOption(string arg, string name, string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (!string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) || index + 1 >= args.Length)
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    private sealed class ParsedDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Inheritance { get; set; } = string.Empty;
        public List<string> SourceFiles { get; set; } = new();
        public List<ParsedMember> Members { get; } = new();
    }

    private sealed class ParsedMember
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string TypeText { get; set; } = string.Empty;
    }

    private sealed class SourceFile
    {
        public string Path { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string[] Lines { get; set; } = Array.Empty<string>();
    }

    private sealed class SourceEvidenceCandidate
    {
        public string Path { get; set; } = string.Empty;
        public int Line { get; set; }
        public string Evidence { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
    }
}

internal sealed class WebIdlInventoryOptions
{
    public string RepositoryRoot { get; set; } = string.Empty;
    public string IdlDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string? SelectedWptRoot { get; set; }
    public List<string> SelectedWptPaths { get; set; } = new();
}

internal sealed class WebIdlInventoryReport
{
    public string SchemaVersion { get; set; } = string.Empty;
    public string FenBrowserRevision { get; set; } = string.Empty;
    public string IdlInputSha256 { get; set; } = string.Empty;
    public string IdlRoot { get; set; } = string.Empty;
    public string GeneratedBindingCompilePolicy { get; set; } = string.Empty;
    public string RuntimeBindingPolicy { get; set; } = string.Empty;
    public WebIdlInventorySummary Summary { get; set; } = new();
    public List<WebIdlDefinitionInventory> Definitions { get; set; } = new();
    public WebIdlLowLifetimeCandidate LowLifetimeRiskCandidate { get; set; } = new();
    public List<string> AuditWarnings { get; set; } = new();
}

internal sealed class WebIdlInventorySummary
{
    public int Definitions { get; set; }
    public int Members { get; set; }
    public int MembersWithManualEvidence { get; set; }
    public int MembersWithActiveTests { get; set; }
    public int MembersWithSelectedWptCoverage { get; set; }
    public int GeneratedOutputsPresent { get; set; }
    public int GeneratedOutputsCompiled { get; set; }
}

internal sealed class WebIdlDefinitionInventory
{
    public string Name { get; set; } = string.Empty;
    public string IdlKind { get; set; } = string.Empty;
    public string Inheritance { get; set; } = string.Empty;
    public List<string> IdlSources { get; set; } = new();
    public string GeneratedOutputLocation { get; set; } = string.Empty;
    public bool GeneratedOutputExists { get; set; }
    public bool GeneratedCompileIncluded { get; set; }
    public string LifetimeComplexity { get; set; } = string.Empty;
    public string MigrationRisk { get; set; } = string.Empty;
    public List<WebIdlMemberInventory> Members { get; set; } = new();
}

internal sealed class WebIdlMemberInventory
{
    public string Name { get; set; } = string.Empty;
    public string IdlKind { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public List<WebIdlSourceEvidence> ManualImplementationLocations { get; set; } = new();
    public string GeneratedOutputLocation { get; set; } = string.Empty;
    public bool ActiveCompileInclusion { get; set; }
    public string ConversionImplementation { get; set; } = string.Empty;
    public string BrandCheckImplementation { get; set; } = string.Empty;
    public string DescriptorImplementation { get; set; } = string.Empty;
    public string ExceptionBehavior { get; set; } = string.Empty;
    public List<string> TestCoverage { get; set; } = new();
    public List<string> SelectedWptCoverage { get; set; } = new();
    public string RealSiteUsage { get; set; } = string.Empty;
    public string LifetimeComplexity { get; set; } = string.Empty;
    public string MigrationRisk { get; set; } = string.Empty;
}

internal sealed class WebIdlSourceEvidence
{
    public string Path { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public bool ActiveCompileInclusion { get; set; }
}

internal sealed class WebIdlLowLifetimeCandidate
{
    public string Definition { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public List<string> Preconditions { get; set; } = new();
}
