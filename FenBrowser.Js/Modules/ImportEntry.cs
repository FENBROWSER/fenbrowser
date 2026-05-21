namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.3 ImportEntry Record.
//
// Fields:
//   ModuleRequest : module specifier (the literal string in the import statement).
//   ImportName    : the name imported from that module. The two sentinel values
//                   `*namespace*` and `default` carry their spec meaning:
//                     * `*namespace*` -> `import * as ns from "mod"` - the binding is
//                       the module's namespace object.
//                     * `default`     -> `import x from "mod"`        - the binding is
//                       the module's default export.
//                   Any other string is a named import.
//   LocalName     : the name the imported value is bound to inside the importer's
//                   module environment.
public readonly record struct ImportEntry(
    string ModuleRequest,
    string ImportName,
    string LocalName)
{
    public const string NamespaceImport = "*namespace*";
    public const string DefaultImport = "default";

    public bool IsNamespaceImport => string.Equals(ImportName, NamespaceImport, StringComparison.Ordinal);
    public bool IsDefaultImport => string.Equals(ImportName, DefaultImport, StringComparison.Ordinal);
}
