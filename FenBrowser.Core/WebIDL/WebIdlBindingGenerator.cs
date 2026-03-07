using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FenBrowser.Core.WebIDL
{
    // â”€â”€ WebIDL Binding Generator â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Generates C# binding glue from parsed WebIDL definitions.
    // The generated code wires the JS engine's FenValue world to the C# DOM API.
    //
    // Generated artifacts per interface:
    //   - Prototype object (FenObject with inherited chain)
    //   - Constructor function
    //   - Attribute getter/setter trampolines
    //   - Operation bindings with overload resolution
    //   - Brand check (internal [[Brand]] slot)
    //   - "Same object" caching for appropriate getters
    //   - Cross-realm identity preservation stubs
    //
    // The generator emits C# source; the host project compiles it.
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed class BindingGeneratorOptions
    {
        public string Namespace { get; set; } = "FenBrowser.FenEngine.Bindings";
        public string EngineNamespace { get; set; } = "FenBrowser.FenEngine.Core";
        public string DomNamespace { get; set; } = "FenBrowser.Core.Dom.V2";
        public bool EmitBrandChecks { get; set; } = true;
        public bool EmitSameObjectCaching { get; set; } = true;
        public bool EmitExposedChecks { get; set; } = true;
        public bool EmitCEReactions { get; set; } = true;
    }

    public sealed class GeneratedFile
    {
        public string FileName { get; set; }
        public string SourceCode { get; set; }
    }

    /// <summary>
    /// Generates C# source files from a <see cref="IdlParseResult"/>.
    /// </summary>
    public sealed class WebIdlBindingGenerator
    {
        private readonly BindingGeneratorOptions _opts;
        private readonly Dictionary<string, string> _typedefs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _knownInterfaces = new(StringComparer.Ordinal);
        private readonly HashSet<string> _knownDictionaries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _knownEnums = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _concreteBindableInterfaces = new(StringComparer.Ordinal)
        {
            "Attr",
            "CharacterData",
            "Comment",
            "Document",
            "DocumentFragment",
            "DocumentType",
            "Element",
            "Event",
            "EventTarget",
            "HTMLCollection",
            "NamedNodeMap",
            "Node",
            "NodeList",
            "ShadowRoot",
            "Text"
        };

        public WebIdlBindingGenerator(BindingGeneratorOptions options = null)
        {
            _opts = options ?? new BindingGeneratorOptions();
        }

        public List<GeneratedFile> Generate(IdlParseResult parsed)
        {
            if (parsed == null) throw new ArgumentNullException(nameof(parsed));

            var files = new List<GeneratedFile>();

            // Flatten partial interfaces / includes
            var merged = MergeDefinitions(parsed.Definitions);
            BuildTypeRegistry(parsed.Definitions, merged);

            foreach (var def in merged)
            {
                switch (def)
                {
                    case IdlInterface iface when !iface.IsMixin:
                        files.Add(GenerateInterfaceBinding(iface));
                        break;
                    case IdlDictionary dict:
                        files.Add(GenerateDictionaryBinding(dict));
                        break;
                    case IdlEnum enm:
                        files.Add(GenerateEnumBinding(enm));
                        break;
                    case IdlNamespace ns:
                        files.Add(GenerateNamespaceBinding(ns));
                        break;
                    case IdlCallback cb:
                        files.Add(GenerateCallbackBinding(cb));
                        break;
                }
            }

            return files;
        }

        private void BuildTypeRegistry(List<IdlDefinition> originalDefinitions, List<IdlDefinition> mergedDefinitions)
        {
            _typedefs.Clear();
            _knownInterfaces.Clear();
            _knownDictionaries.Clear();
            _knownEnums.Clear();

            foreach (var def in originalDefinitions ?? Enumerable.Empty<IdlDefinition>())
            {
                switch (def)
                {
                    case IdlTypedef typedef when !string.IsNullOrWhiteSpace(typedef.Name):
                        _typedefs[typedef.Name] = typedef.Type?.ToString() ?? typedef.Type?.Name ?? "any";
                        break;
                    case IdlEnum enm when !string.IsNullOrWhiteSpace(enm.Name):
                        _knownEnums.Add(enm.Name);
                        break;
                }
            }

            foreach (var def in mergedDefinitions ?? Enumerable.Empty<IdlDefinition>())
            {
                switch (def)
                {
                    case IdlInterface iface when !iface.IsMixin && !string.IsNullOrWhiteSpace(iface.Name):
                        _knownInterfaces.Add(iface.Name);
                        break;
                    case IdlDictionary dict when !string.IsNullOrWhiteSpace(dict.Name):
                        _knownDictionaries.Add(dict.Name);
                        break;
                    case IdlEnum enm when !string.IsNullOrWhiteSpace(enm.Name):
                        _knownEnums.Add(enm.Name);
                        break;
                }
            }
        }

        // â”€â”€ Definition merging â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private List<IdlDefinition> MergeDefinitions(List<IdlDefinition> defs)
        {
            var interfaces = new Dictionary<string, IdlInterface>(StringComparer.Ordinal);
            var dictionaries = new Dictionary<string, IdlDictionary>(StringComparer.Ordinal);
            var namespaces = new Dictionary<string, IdlNamespace>(StringComparer.Ordinal);
            var mixins = new Dictionary<string, IdlInterface>(StringComparer.Ordinal);
            var pendingInterfacePartials = new Dictionary<string, List<IdlInterface>>(StringComparer.Ordinal);
            var pendingDictionaryPartials = new Dictionary<string, List<IdlDictionary>>(StringComparer.Ordinal);
            var pendingNamespacePartials = new Dictionary<string, List<IdlNamespace>>(StringComparer.Ordinal);
            var includes = new List<IdlIncludes>();
            var result = new List<IdlDefinition>();

            // First pass: collect
            foreach (var def in defs)
            {
                switch (def)
                {
                    case IdlInterface iface:
                        if (iface.IsMixin)
                        {
                            if (mixins.TryGetValue(iface.Name, out var existingMixin))
                            {
                                MergeInterfaceInto(existingMixin, iface, preferExistingBaseMetadata: true);
                            }
                            else
                            {
                                mixins[iface.Name] = CloneInterface(iface);
                            }
                        }
                        else if (iface.IsPartial)
                        {
                            if (interfaces.TryGetValue(iface.Name, out var existing))
                            {
                                MergeInterfaceInto(existing, iface, preferExistingBaseMetadata: true);
                            }
                            else
                            {
                                AddPending(pendingInterfacePartials, iface.Name, CloneInterface(iface));
                            }
                        }
                        else
                        {
                            var aggregate = CloneInterface(iface);
                            interfaces[iface.Name] = aggregate;
                            if (pendingInterfacePartials.TryGetValue(iface.Name, out var partials))
                            {
                                foreach (var partial in partials)
                                {
                                    MergeInterfaceInto(aggregate, partial, preferExistingBaseMetadata: true);
                                }
                                pendingInterfacePartials.Remove(iface.Name);
                            }
                        }
                        break;
                    case IdlDictionary dict:
                        if (dict.IsPartial)
                        {
                            if (dictionaries.TryGetValue(dict.Name, out var existingDictionary))
                            {
                                MergeDictionaryInto(existingDictionary, dict);
                            }
                            else
                            {
                                AddPending(pendingDictionaryPartials, dict.Name, CloneDictionary(dict));
                            }
                        }
                        else
                        {
                            var aggregate = CloneDictionary(dict);
                            dictionaries[dict.Name] = aggregate;
                            if (pendingDictionaryPartials.TryGetValue(dict.Name, out var partials))
                            {
                                foreach (var partial in partials)
                                {
                                    MergeDictionaryInto(aggregate, partial);
                                }
                                pendingDictionaryPartials.Remove(dict.Name);
                            }
                        }
                        break;
                    case IdlNamespace ns:
                        if (ns.IsPartial)
                        {
                            if (namespaces.TryGetValue(ns.Name, out var existingNamespace))
                            {
                                MergeNamespaceInto(existingNamespace, ns);
                            }
                            else
                            {
                                AddPending(pendingNamespacePartials, ns.Name, CloneNamespace(ns));
                            }
                        }
                        else
                        {
                            var aggregate = CloneNamespace(ns);
                            namespaces[ns.Name] = aggregate;
                            if (pendingNamespacePartials.TryGetValue(ns.Name, out var partials))
                            {
                                foreach (var partial in partials)
                                {
                                    MergeNamespaceInto(aggregate, partial);
                                }
                                pendingNamespacePartials.Remove(ns.Name);
                            }
                        }
                        break;
                    case IdlIncludes inc:
                        includes.Add(inc);
                        break;
                    default:
                        result.Add(def);
                        break;
                }
            }

            // Apply includes (mixin members â†’ target interface)
            foreach (var inc in includes)
            {
                if (interfaces.TryGetValue(inc.Target, out var target) &&
                    mixins.TryGetValue(inc.Mixin, out var mixin))
                {
                    MergeInterfaceInto(target, mixin, preferExistingBaseMetadata: true, includeOnlyMembers: true);
                }
            }

            result.AddRange(interfaces.Values);
            result.AddRange(dictionaries.Values);
            result.AddRange(namespaces.Values);
            result.RemoveAll(d => d == null);
            return result;
        }

        private static void AddPending<T>(Dictionary<string, List<T>> pending, string key, T item)
        {
            if (!pending.TryGetValue(key, out var list))
            {
                list = new List<T>();
                pending[key] = list;
            }

            list.Add(item);
        }

        private static IdlInterface CloneInterface(IdlInterface source)
        {
            return new IdlInterface
            {
                Name = source.Name,
                Inherits = source.Inherits,
                IsPartial = source.IsPartial,
                IsMixin = source.IsMixin,
                Namespace = source.Namespace,
                Members = new List<IdlMember>(source.Members ?? Enumerable.Empty<IdlMember>()),
                ExtAttrs = new List<IdlExtendedAttribute>(source.ExtAttrs ?? Enumerable.Empty<IdlExtendedAttribute>())
            };
        }

        private static IdlDictionary CloneDictionary(IdlDictionary source)
        {
            return new IdlDictionary
            {
                Name = source.Name,
                Inherits = source.Inherits,
                IsPartial = source.IsPartial,
                Members = new List<IdlDictionaryMember>(source.Members ?? Enumerable.Empty<IdlDictionaryMember>()),
                ExtAttrs = new List<IdlExtendedAttribute>(source.ExtAttrs ?? Enumerable.Empty<IdlExtendedAttribute>())
            };
        }

        private static IdlNamespace CloneNamespace(IdlNamespace source)
        {
            return new IdlNamespace
            {
                Name = source.Name,
                IsPartial = source.IsPartial,
                Members = new List<IdlMember>(source.Members ?? Enumerable.Empty<IdlMember>()),
                ExtAttrs = new List<IdlExtendedAttribute>(source.ExtAttrs ?? Enumerable.Empty<IdlExtendedAttribute>())
            };
        }

        private static void MergeInterfaceInto(IdlInterface target, IdlInterface source, bool preferExistingBaseMetadata, bool includeOnlyMembers = false)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (!includeOnlyMembers)
            {
                if (string.IsNullOrWhiteSpace(target.Inherits) || !preferExistingBaseMetadata)
                {
                    target.Inherits = string.IsNullOrWhiteSpace(target.Inherits) ? source.Inherits : target.Inherits;
                }

                if (string.IsNullOrWhiteSpace(target.Namespace) || !preferExistingBaseMetadata)
                {
                    target.Namespace = string.IsNullOrWhiteSpace(target.Namespace) ? source.Namespace : target.Namespace;
                }

                MergeExtAttrs(target.ExtAttrs, source.ExtAttrs);
            }

            if (source.Members != null && source.Members.Count > 0)
            {
                target.Members.AddRange(source.Members);
            }
        }

        private static void MergeDictionaryInto(IdlDictionary target, IdlDictionary source)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(target.Inherits))
            {
                target.Inherits = source.Inherits;
            }

            MergeExtAttrs(target.ExtAttrs, source.ExtAttrs);
            if (source.Members != null && source.Members.Count > 0)
            {
                target.Members.AddRange(source.Members);
            }
        }

        private static void MergeNamespaceInto(IdlNamespace target, IdlNamespace source)
        {
            if (target == null || source == null)
            {
                return;
            }

            MergeExtAttrs(target.ExtAttrs, source.ExtAttrs);
            if (source.Members != null && source.Members.Count > 0)
            {
                target.Members.AddRange(source.Members);
            }
        }

        private static void MergeExtAttrs(List<IdlExtendedAttribute> target, List<IdlExtendedAttribute> source)
        {
            if (target == null || source == null || source.Count == 0)
            {
                return;
            }

            foreach (var attr in source)
            {
                if (attr == null)
                {
                    continue;
                }

                var exists = target.Any(existing =>
                    existing != null &&
                    string.Equals(existing.Name, attr.Name, StringComparison.Ordinal) &&
                    string.Equals(existing.ArgumentString, attr.ArgumentString, StringComparison.Ordinal));

                if (!exists)
                {
                    target.Add(attr);
                }
            }
        }

        // â”€â”€ Interface binding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private GeneratedFile GenerateInterfaceBinding(IdlInterface iface)
        {
            var sb = new StringBuilder();
            var className = iface.Name + "Binding";

            EmitFileHeader(sb, iface.Name);

            sb.AppendLine($"namespace {_opts.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>Auto-generated WebIDL binding for <c>{iface.Name}</c>.</summary>");
            sb.AppendLine($"    public static class {className}");
            sb.AppendLine("    {");

            // Brand slot name
            sb.AppendLine($"        private const string BrandSlot = \"[[{iface.Name}Brand]]\";");
            sb.AppendLine();

            // Prototype factory
            EmitPrototype(sb, iface);

            // Constructor
            EmitConstructor(sb, iface);

            // Attribute bindings
            foreach (var attr in iface.Members.Where(m => m.Kind == IdlMemberKind.Attribute || m.Kind == IdlMemberKind.StaticAttribute))
                EmitAttribute(sb, iface.Name, attr);

            // Operation bindings
            var ops = iface.Members
                .Where(m => m.Kind == IdlMemberKind.Operation || m.Kind == IdlMemberKind.StaticOperation)
                .GroupBy(m => m.Name ?? "__anonymous__")
                .ToList();

            foreach (var opGroup in ops)
                EmitOperation(sb, iface.Name, opGroup.Key, opGroup.ToList());

            // Brand check helper
            if (_opts.EmitBrandChecks)
                EmitBrandCheck(sb, iface.Name);

            // Wrapper helper (JS value â†’ C# object)
            EmitWrapUnwrap(sb, iface.Name);

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return new GeneratedFile
            {
                FileName = $"{className}.g.cs",
                SourceCode = sb.ToString(),
            };
        }

        private void EmitFileHeader(StringBuilder sb, string interfaceName)
        {
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine($"// WebIDL binding for {interfaceName}.");
            sb.AppendLine("// DO NOT EDIT. Regenerate by re-running the binding generator.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine($"using {_opts.EngineNamespace};");
            sb.AppendLine($"using {_opts.DomNamespace};");
            sb.AppendLine("using FenBrowser.FenEngine.Errors;");
            sb.AppendLine("using FenBrowser.Core.WebIDL;");
            sb.AppendLine();
        }

        private void EmitPrototype(StringBuilder sb, IdlInterface iface)
        {
            sb.AppendLine($"        /// <summary>Creates the prototype object for <c>{iface.Name}</c>.</summary>");
            sb.AppendLine($"        public static FenObject CreatePrototype(FenObject objectPrototype, FenObject parentPrototype = null)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var proto = new FenObject();");
            if (iface.Inherits != null)
                sb.AppendLine($"            proto.SetPrototype(parentPrototype ?? objectPrototype);");
            else
                sb.AppendLine("            proto.SetPrototype(objectPrototype);");
            sb.AppendLine($"            proto.InternalClass = \"{iface.Name}Prototype\";");
            sb.AppendLine();

            // Exposed check
            var exposed = GetExtAttr(iface.ExtAttrs, "Exposed");
            if (exposed != null && _opts.EmitExposedChecks)
                sb.AppendLine($"            // [Exposed={exposed.ArgumentString}]");

            foreach (var attr in iface.Members.Where(m => m.Kind == IdlMemberKind.Attribute))
            {
                var sameObj = GetExtAttr(attr.ExtAttrs, "SameObject") != null && _opts.EmitSameObjectCaching;
                sb.AppendLine($"            // Attribute: {attr.Type} {attr.Name}{(attr.Readonly ? " (readonly)" : "")}");
                var getterName = $"_getter_{attr.Name}";
                sb.AppendLine($"            var {getterName} = new FenFunction(\"get {attr.Name}\", (args, thisVal) =>");
                sb.AppendLine("            {");
                EmitBrandCheckCall(sb, iface.Name, 16, "getter");
                sb.AppendLine($"                dynamic native = Unwrap(thisVal);");
                sb.AppendLine($"                if (native == null) throw new FenTypeError(\"Illegal invocation\");");
                sb.AppendLine($"                return ToJsValue(native.{PascalCase(attr.Name)});");
                sb.AppendLine("            });");

                if (!attr.Readonly)
                {
                    var setterName = $"_setter_{attr.Name}";
                    sb.AppendLine($"            var {setterName} = new FenFunction(\"set {attr.Name}\", (args, thisVal) =>");
                    sb.AppendLine("            {");
                    EmitBrandCheckCall(sb, iface.Name, 16, "setter");
                    if (_opts.EmitCEReactions && GetExtAttr(attr.ExtAttrs, "CEReactions") != null)
                        sb.AppendLine("                // [CEReactions] custom element reaction queue processing");
                    sb.AppendLine($"                dynamic native = Unwrap(thisVal);");
                    sb.AppendLine($"                if (native == null) throw new FenTypeError(\"Illegal invocation\");");
                    sb.AppendLine($"                var val = args.Length > 0 ? args[0] : FenValue.Undefined;");
                    sb.AppendLine($"                native.{PascalCase(attr.Name)} = FromJsValue_{SafeMethodName(attr.Type?.Name ?? "any")}(val);");
                    sb.AppendLine("                return FenValue.Undefined;");
                    sb.AppendLine("            });");
                    sb.AppendLine($"            proto.DefineOwnProperty(\"{attr.Name}\", new PropertyDescriptor {{ Getter = {getterName}, Setter = {setterName}, Enumerable = false, Configurable = true }});");
                }
                else
                {
                    sb.AppendLine($"            proto.DefineOwnProperty(\"{attr.Name}\", new PropertyDescriptor {{ Getter = {getterName}, Setter = null, Enumerable = false, Configurable = true }});");
                }
                sb.AppendLine();
            }

            sb.AppendLine("            return proto;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private void EmitConstructor(StringBuilder sb, IdlInterface iface)
        {
            var ctorMembers = iface.Members.Where(m => m.Kind == IdlMemberKind.Constructor).ToList();

            sb.AppendLine($"        /// <summary>Creates the constructor function for <c>{iface.Name}</c>.</summary>");
            sb.AppendLine($"        public static FenFunction CreateConstructor(FenObject proto)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var ctor = new FenFunction(\"{iface.Name}\", (args, thisVal) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                var obj = new FenObject();");
            sb.AppendLine($"                obj.SetPrototype(proto);");
            sb.AppendLine($"                obj.Set(BrandSlot, FenValue.FromBoolean(true));");
            sb.AppendLine($"                obj.InternalClass = \"{iface.Name}\";");

            if (ctorMembers.Count > 0)
            {
                sb.AppendLine($"                // Constructor overload resolution ({ctorMembers.Count} overload(s))");
                sb.AppendLine($"                Initialize?.Invoke(obj, args);");
            }

            sb.AppendLine($"                return FenValue.FromObject(obj);");
            sb.AppendLine("            });");
            sb.AppendLine("            ctor.Prototype = proto;");
            sb.AppendLine($"            ctor.Set(\"prototype\", FenValue.FromObject(proto));");
            sb.AppendLine("            return ctor;");
            sb.AppendLine("        }");
            sb.AppendLine();

            if (ctorMembers.Count > 0)
            {
                sb.AppendLine($"        // Called by the generated constructor to initialise a new {iface.Name}.");
                sb.AppendLine($"        internal static System.Action<FenObject, FenValue[]> Initialize;");
                sb.AppendLine();
            }
        }

        private void EmitAttribute(StringBuilder sb, string ifaceName, IdlMember attr)
        {
            // Static attributes hang off the constructor, not the prototype.
            var ctype = MapType(attr.Type);
            sb.AppendLine($"        // Static attribute: {attr.Type} {attr.Name}");
            sb.AppendLine($"        internal static System.Func<{ctype}> GetStaticValue_{attr.Name};");
            if (!attr.Readonly)
                sb.AppendLine($"        internal static System.Action<{ctype}> SetStaticValue_{attr.Name};");
            sb.AppendLine($"        public static FenValue Get_{attr.Name}() =>");
            sb.AppendLine($"            GetStaticValue_{attr.Name} != null ? ToJsValue(GetStaticValue_{attr.Name}()) : FenValue.Undefined;");
            if (!attr.Readonly)
            {
                sb.AppendLine($"        public static void Set_{attr.Name}(FenValue val) {{");
                sb.AppendLine($"            SetStaticValue_{attr.Name}?.Invoke(FromJsValue_{SafeMethodName(attr.Type?.Name ?? "any")}(val));");
                sb.AppendLine($"        }}");
            }
            sb.AppendLine();
        }

        private void EmitOperation(StringBuilder sb, string ifaceName, string opName, List<IdlMember> overloads)
        {
            sb.AppendLine($"        // Operation: {opName} ({overloads.Count} overload(s))");
            sb.AppendLine($"        public static FenFunction Create_{opName}Op()");
            sb.AppendLine("        {");
            sb.AppendLine($"            return new FenFunction(\"{opName}\", (args, thisVal) =>");
            sb.AppendLine("            {");
            EmitBrandCheckCall(sb, ifaceName, 16, "operation");

            // Simple overload resolution by argument count
            for (int i = 0; i < overloads.Count; i++)
            {
                var op = overloads[i];
                int minArgs = op.Arguments.Count(a => !a.Optional && !a.Variadic);
                int maxArgs = op.Arguments.Any(a => a.Variadic) ? int.MaxValue : op.Arguments.Count;

                if (overloads.Count > 1)
                    sb.AppendLine($"                // Overload {i + 1}: {op.Type} {opName}({string.Join(", ", op.Arguments.Select(FormatArg))})");

                sb.AppendLine($"                if (args.Length >= {minArgs})");
                sb.AppendLine("                {");
                sb.AppendLine($"                    dynamic native = Unwrap(thisVal);");
                sb.AppendLine($"                    if (native == null) throw new FenTypeError(\"Illegal invocation\");");

                // Convert each argument
                for (int j = 0; j < op.Arguments.Count; j++)
                {
                    var arg = op.Arguments[j];
                    var jsAccessor = j < op.Arguments.Count && !arg.Variadic
                        ? $"args.Length > {j} ? args[{j}] : FenValue.Undefined"
                        : $"args.Skip({j}).ToArray()";
                    sb.AppendLine($"                    var arg_{arg.Name} = FromJsValue_{SafeMethodName(arg.Type?.Name ?? "any")}({jsAccessor});");
                }

                var argList = string.Join(", ", op.Arguments.Select(a => $"arg_{a.Name}"));
                if (op.Type.IsUndefined || op.Type.Name == "void" || op.Type.Name == "undefined")
                {
                    sb.AppendLine($"                    native.{PascalCase(opName)}({argList});");
                    sb.AppendLine("                    return FenValue.Undefined;");
                }
                else
                {
                    sb.AppendLine($"                    return ToJsValue(native.{PascalCase(opName)}({argList}));");
                }

                sb.AppendLine("                }");
            }

            sb.AppendLine($"                throw new FenTypeError(\"Wrong number of arguments for {ifaceName}.{opName}\");");
            sb.AppendLine("            });");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private void EmitBrandCheck(StringBuilder sb, string ifaceName)
        {
            sb.AppendLine($"        private static bool HasBrand(FenValue v)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!v.IsObject) return false;");
            sb.AppendLine("            var obj = v.AsObject();");
            sb.AppendLine("            if (obj == null) return false;");
            sb.AppendLine("            var brand = obj.Get(BrandSlot);");
            sb.AppendLine("            return brand.IsBoolean && brand.AsBoolean();");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private void EmitBrandCheckCall(StringBuilder sb, string ifaceName, int indent, string context)
        {
            if (!_opts.EmitBrandChecks) return;
            var spaces = new string(' ', indent);
            sb.AppendLine($"{spaces}if (!HasBrand(thisVal)) throw new FenTypeError($\"Failed to execute '{context}' on '{ifaceName}': Illegal invocation.\");");
        }

        private void EmitWrapUnwrap(StringBuilder sb, string ifaceName)
        {
            sb.AppendLine($"        // Wrap/unwrap helpers â€” override WrapperRegistry to wire real C# objects.");
            sb.AppendLine($"        internal static System.Action<object, FenObject> WrapImpl;");
            sb.AppendLine($"        internal static System.Func<FenObject, object> UnwrapImpl;");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"        private static object Unwrap(FenValue v)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!v.IsObject) return null;");
            sb.AppendLine("            var obj = v.AsObject() as FenObject;");
            sb.AppendLine("            if (obj == null) return null;");
            sb.AppendLine("            if (UnwrapImpl != null) return UnwrapImpl(obj);");
            sb.AppendLine("            var slot = obj.Get(\"[[NativeRef]]\");");
            sb.AppendLine("            // NativeRef slot stores target as IObject with Target property (dynamic dispatch)");
            sb.AppendLine("            if (slot.IsObject) { dynamic wrapper = slot.AsObject(); try { return wrapper.Target; } catch { } }");
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static FenValue ToJsValue(object v) => v switch");
            sb.AppendLine("        {");
            sb.AppendLine("            null => FenValue.Null,");
            sb.AppendLine("            bool b => FenValue.FromBoolean(b),");
            sb.AppendLine("            int i => FenValue.FromNumber(i),");
            sb.AppendLine("            long l => FenValue.FromNumber(l),");
            sb.AppendLine("            double d => FenValue.FromNumber(d),");
            sb.AppendLine("            float f => FenValue.FromNumber(f),");
            sb.AppendLine("            string s => FenValue.FromString(s),");
            foreach (var enumName in _knownEnums.OrderBy(n => n, StringComparer.Ordinal))
            {
                var safeName = SafeMethodName(enumName);
                sb.AppendLine($"            {enumName} e_{safeName} => FenValue.FromString({enumName}Converter.ToString(e_{safeName})),");
            }
            sb.AppendLine("            _ => FenValue.Undefined,");
            sb.AppendLine("        };");
            sb.AppendLine();

            // Type converters
            foreach (var typeName in new[] { "DOMString", "USVString", "ByteString", "boolean", "long", "unsigned long", "double", "short", "unsigned short", "long long", "unsigned long long", "float", "unrestricted float", "unrestricted double", "DOMHighResTimeStamp", "any", "object" })
            {
                var safeType = CSharpTypeName(typeName);
                sb.AppendLine($"        private static {safeType} FromJsValue_{SafeMethodName(typeName)}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(typeName, "v")};");
            }

            foreach (var typeName in _typedefs.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var safeType = CSharpTypeName(typeName);
                sb.AppendLine($"        private static {safeType} FromJsValue_{SafeMethodName(typeName)}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(typeName, "v")};");
            }

            foreach (var enumName in _knownEnums.OrderBy(n => n, StringComparer.Ordinal))
            {
                var safeType = CSharpTypeName(enumName);
                sb.AppendLine($"        private static {safeType} FromJsValue_{SafeMethodName(enumName)}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(enumName, "v")};");
            }

            foreach (var dictName in _knownDictionaries.OrderBy(n => n, StringComparer.Ordinal))
            {
                var safeType = CSharpTypeName(dictName);
                sb.AppendLine($"        private static {safeType} FromJsValue_{SafeMethodName(dictName)}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(dictName, "v")};");
            }

            foreach (var interfaceName in _knownInterfaces.OrderBy(n => n, StringComparer.Ordinal))
            {
                var safeType = CSharpTypeName(interfaceName);
                sb.AppendLine($"        private static {safeType} FromJsValue_{SafeMethodName(interfaceName)}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(interfaceName, "v")};");
            }
        }

        // â”€â”€ Dictionary binding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private GeneratedFile GenerateDictionaryBinding(IdlDictionary dict)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, dict.Name);

            sb.AppendLine($"namespace {_opts.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>Auto-generated binding for WebIDL dictionary <c>{dict.Name}</c>.</summary>");
            sb.AppendLine($"    public sealed class {dict.Name}Init");
            sb.AppendLine("    {");

            foreach (var m in dict.Members)
            {
                var ctype = CSharpTypeName(m.Type.Name);
                string defVal = m.DefaultValue != null ? CSharpDefaultValue(m.DefaultValue, m.Type.Name) : (m.Required ? null : "default");
                string propLine = defVal != null
                    ? $"        public {ctype} {PascalCase(m.Name)} {{ get; set; }} = {defVal};"
                    : $"        public {ctype} {PascalCase(m.Name)} {{ get; set; }}";
                sb.AppendLine(propLine);
            }

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Converts a JS object to <see cref=\"{dict.Name}Init\"/>.</summary>");
            sb.AppendLine("        public static " + dict.Name + "Init FromJsObject(FenObject obj)");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (obj == null) return new {dict.Name}Init();");
            sb.AppendLine($"            return new {dict.Name}Init");
            sb.AppendLine("            {");
            foreach (var m in dict.Members)
            {
                sb.AppendLine($"                {PascalCase(m.Name)} = ConvertMember_{m.Name}(obj?.Get(\"{m.Name}\") ?? FenValue.Undefined),");
            }
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();
            foreach (var m in dict.Members)
            {
                var ctype = CSharpTypeName(m.Type.Name);
                sb.AppendLine($"        private static {ctype} ConvertMember_{m.Name}(FenValue v) =>");
                sb.AppendLine($"            {ConversionExpr(m.Type.Name, "v")};");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return new GeneratedFile { FileName = $"{dict.Name}Binding.g.cs", SourceCode = sb.ToString() };
        }

        // â”€â”€ Enum binding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private GeneratedFile GenerateEnumBinding(IdlEnum enm)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, enm.Name);

            sb.AppendLine($"namespace {_opts.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public enum {enm.Name}");
            sb.AppendLine("    {");
            foreach (var val in enm.Values)
                sb.AppendLine($"        {PascalCase(val.Replace("-", "_"))},");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public static class {enm.Name}Converter");
            sb.AppendLine("    {");
            sb.AppendLine($"        public static {enm.Name} FromString(string s) => s switch");
            sb.AppendLine("        {");
            foreach (var val in enm.Values)
                sb.AppendLine($"            \"{val}\" => {enm.Name}.{PascalCase(val.Replace("-", "_"))},");
            sb.AppendLine($"            _ => throw new System.ArgumentException(\"Invalid {enm.Name} value: \" + s),");
            sb.AppendLine("        };");
            sb.AppendLine($"        public static string ToString({enm.Name} v) => v switch");
            sb.AppendLine("        {");
            foreach (var val in enm.Values)
                sb.AppendLine($"            {enm.Name}.{PascalCase(val.Replace("-", "_"))} => \"{val}\",");
            sb.AppendLine("            _ => throw new System.ArgumentException(),");
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return new GeneratedFile { FileName = $"{enm.Name}Binding.g.cs", SourceCode = sb.ToString() };
        }

        private GeneratedFile GenerateCallbackBinding(IdlCallback cb)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, cb.Name);

            sb.AppendLine($"namespace {_opts.Namespace}");
            sb.AppendLine("{");

            if (cb.IsFunction)
            {
                var arguments = cb.Arguments?.Count > 0
                    ? string.Join(", ", cb.Arguments.Select((arg, index) =>
                        $"{MapType(arg.Type)} {EscapeIdentifier(string.IsNullOrWhiteSpace(arg.Name) ? $"arg{index}" : arg.Name)}"))
                    : string.Empty;
                sb.AppendLine($"    public delegate {MapType(cb.ReturnType)} {cb.Name}({arguments});");
            }
            else
            {
                sb.AppendLine($"    public interface I{cb.Name}");
                sb.AppendLine("    {");
                foreach (var member in cb.Members ?? Enumerable.Empty<IdlMember>())
                {
                    if (member.Kind == IdlMemberKind.Operation)
                    {
                        var arguments = member.Arguments?.Count > 0
                            ? string.Join(", ", member.Arguments.Select((arg, index) =>
                                $"{MapType(arg.Type)} {EscapeIdentifier(string.IsNullOrWhiteSpace(arg.Name) ? $"arg{index}" : arg.Name)}"))
                            : string.Empty;
                        sb.AppendLine($"        {MapType(member.Type)} {PascalCase(member.Name)}({arguments});");
                    }
                }
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");
            return new GeneratedFile { FileName = $"{cb.Name}CallbackBinding.g.cs", SourceCode = sb.ToString() };
        }

        // â”€â”€ Namespace binding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private GeneratedFile GenerateNamespaceBinding(IdlNamespace ns)
        {
            var sb = new StringBuilder();
            EmitFileHeader(sb, ns.Name);

            sb.AppendLine($"namespace {_opts.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static class {ns.Name}Namespace");
            sb.AppendLine("    {");
            sb.AppendLine($"        public static FenObject CreateNamespaceObject()");
            sb.AppendLine("        {");
            sb.AppendLine($"            var obj = new FenObject();");
            sb.AppendLine($"            obj.InternalClass = \"{ns.Name}\";");
            foreach (var op in ns.Members.Where(m => m.Kind == IdlMemberKind.Operation || m.Kind == IdlMemberKind.StaticOperation))
            {
                sb.AppendLine($"            obj.Set(\"{op.Name}\", FenValue.FromFunction(Create_{op.Name}Op()));");
            }
            foreach (var attr in ns.Members.Where(m => m.Kind == IdlMemberKind.Attribute || m.Kind == IdlMemberKind.StaticAttribute))
            {
                sb.AppendLine($"            obj.Set(\"{attr.Name}\", Get_{attr.Name}());");
            }
            sb.AppendLine("            return obj;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return new GeneratedFile { FileName = $"{ns.Name}NamespaceBinding.g.cs", SourceCode = sb.ToString() };
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static IdlExtendedAttribute GetExtAttr(List<IdlExtendedAttribute> attrs, string name) =>
            attrs?.Find(a => a.Name == name);

        private static string PascalCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length == 1) return s.ToUpperInvariant();
            // camelCase â†’ PascalCase
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string EscapeIdentifier(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "value";

            return s switch
            {
                "event" => "@event",
                "params" => "@params",
                "object" => "@object",
                "string" => "@string",
                "base" => "@base",
                "namespace" => "@namespace",
                _ => s
            };
        }

        private static readonly HashSet<string> _primitiveTypes = new(StringComparer.Ordinal)
        {
            "DOMString", "USVString", "CSSOMString", "ByteString",
            "boolean", "long", "short", "unsigned long", "unsigned short",
            "long long", "unsigned long long",
            "double", "unrestricted double", "DOMHighResTimeStamp",
            "float", "unrestricted float",
            "any", "object",
        };

        /// <summary>Returns a valid C# method-name suffix for the IDL type, preserving known named types.</summary>
        private string SafeMethodName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || typeName == "union") return "any";

            if (!_primitiveTypes.Contains(typeName) &&
                !_typedefs.ContainsKey(typeName) &&
                !_knownInterfaces.Contains(typeName) &&
                !_knownDictionaries.Contains(typeName) &&
                !_knownEnums.Contains(typeName))
            {
                return "any";
            }

            return typeName.Replace(" ", "_").Replace("<", "_").Replace(">", "_").Replace(",", "_").Replace("?", "Nullable");
        }

        private string ResolveNamedType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return "any";
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = typeName;
            while (_typedefs.TryGetValue(current, out var next) && !string.IsNullOrWhiteSpace(next) && seen.Add(current))
            {
                current = next;
            }

            return current;
        }

        private bool CanBindConcreteInterface(string typeName)
        {
            return !string.IsNullOrWhiteSpace(typeName) && _concreteBindableInterfaces.Contains(typeName);
        }

        private string CSharpTypeName(string idlType)
        {
            if (idlType == null) return "object";
            idlType = ResolveNamedType(idlType);
            // Handle parameterised forms like "sequence<T>", "record<K,V>", "Promise<T>", "FrozenArray<T>"
            if (idlType.StartsWith("sequence<") || idlType.StartsWith("FrozenArray<") ||
                idlType.StartsWith("ObservableArray<"))
                return "object[]";
            if (idlType.StartsWith("record<")) return "System.Collections.Generic.Dictionary<string, object>";
            if (idlType.StartsWith("Promise<")) return "System.Threading.Tasks.Task<object>";
            if (_knownEnums.Contains(idlType)) return idlType;
            if (_knownDictionaries.Contains(idlType)) return $"{idlType}Init";
            if (_knownInterfaces.Contains(idlType)) return CanBindConcreteInterface(idlType) ? idlType : "object";
            return idlType switch
            {
                "DOMString" or "USVString" or "CSSOMString" or "ByteString" => "string",
                "boolean" => "bool",
                "long" or "short" => "int",
                "unsigned long" or "unsigned short" => "uint",
                "long long" => "long",
                "unsigned long long" => "ulong",
                "double" or "unrestricted double" or "DOMHighResTimeStamp" => "double",
                "float" or "unrestricted float" => "float",
                "byte" => "byte",
                "octet" => "byte",
                "void" or "undefined" => "void",
                "any" or "object" => "object",
                "ArrayBuffer" or "BufferSource" => "byte[]",
                // Browser-specific opaque types â€” use object for binding stubs
                "WindowProxy" or "DOMImplementation" or "Location" or "History" or
                "Navigation" or "CustomElementRegistry" or "BarProp" or "Screen" or
                "Performance" or "Navigator" or "NodeFilter" or "DOMStringMap" or
                "HTMLAllCollection" or "ElementInternals" or "AbortSignal" => "object",
                _ => "object", // Safe fallback for all unknown types
            };
        }

        private string MapType(IdlType t)
        {
            if (t == null) return "object";
            if (t.IsUnion) return "object";
            if (t.IsSequence || t.IsFrozenArray || t.IsObservableArray)
            {
                var itemType = t.TypeArguments != null && t.TypeArguments.Count > 0 ? MapType(t.TypeArguments[0]) : "object";
                return $"{itemType}[]";
            }

            if (t.IsRecord) return "System.Collections.Generic.Dictionary<string, object>";
            if (t.IsPromise)
            {
                var itemType = t.TypeArguments != null && t.TypeArguments.Count > 0 ? MapType(t.TypeArguments[0]) : "object";
                return $"System.Threading.Tasks.Task<{itemType}>";
            }

            return CSharpTypeName(t.Name ?? "any");
        }

        private string ConversionExpr(string idlType, string varName)
        {
            idlType = ResolveNamedType(idlType);

            if (_knownEnums.Contains(idlType))
            {
                return $"{idlType}Converter.FromString({varName}.AsString(null) ?? throw new FenTypeError(\"Expected string\"))";
            }

            if (_knownDictionaries.Contains(idlType))
            {
                return $"{idlType}Init.FromJsObject({varName}.IsObject ? {varName}.AsObject() as FenObject : null)";
            }

            if (_knownInterfaces.Contains(idlType))
            {
                return CanBindConcreteInterface(idlType)
                    ? $"{varName}.IsObject ? ({idlType})Unwrap({varName}) : default"
                    : $"{varName}.IsObject ? (object)Unwrap({varName}) : null";
            }

            return idlType switch
            {
                "DOMString" or "USVString" or "CSSOMString" or "ByteString" =>
                    $"{varName}.IsUndefined || {varName}.IsNull ? null : {varName}.AsString(null)",
                "boolean" => $"{varName}.AsBoolean()",
                "long" or "short" => $"(int){varName}.ToNumber()",
                "unsigned long" or "unsigned short" => $"(uint){varName}.ToNumber()",
                "long long" => $"(long){varName}.ToNumber()",
                "unsigned long long" => $"(ulong){varName}.ToNumber()",
                "double" or "unrestricted double" or "DOMHighResTimeStamp" => $"{varName}.ToNumber()",
                "float" or "unrestricted float" => $"(float){varName}.ToNumber()",
                "any" => varName,
                "object" => $"{varName}.IsObject ? (object){varName}.AsObject() : null",
                _ => $"default",
            };
        }

        private string CSharpDefaultValue(string val, string typeName)
        {
            if (val == null) return "default";
            var resolvedType = ResolveNamedType(typeName);
            return val switch
            {
                "null" => "null",
                "true" => "true",
                "false" => "false",
                "\"\"" or "" => "\"\"",
                "{}" or "[]" => "default",
                _ when resolvedType is "boolean" => val == "true" ? "true" : "false",
                // Bare string literals for primitive string types need quoting
                _ when resolvedType is "DOMString" or "USVString" or "CSSOMString" or "ByteString" && !val.StartsWith("\"") => $"\"{val}\"",
                // Already-quoted string ("foo")
                _ when val.StartsWith("\"") => val.Trim('"') is var inner ? $"\"{inner}\"" : val,
                // Non-primitive types (enums, interfaces) â€” default(T) for safety
                _ when !_primitiveTypes.Contains(resolvedType) => "default",
                _ => val,
            };
        }

        private static string FormatArg(IdlArgument a) =>
            $"{(a.Optional ? "optional " : "")}{a.Type} {a.Name}{(a.Variadic ? "..." : "")}{(a.DefaultValue != null ? " = " + a.DefaultValue : "")}";
    }

    /// <summary>Wraps a native C# object reference inside a FenObject internal slot.</summary>
    public sealed class NativeRefWrapper
    {
        public object Target { get; }
        public NativeRefWrapper(object target) => Target = target;
    }
}

