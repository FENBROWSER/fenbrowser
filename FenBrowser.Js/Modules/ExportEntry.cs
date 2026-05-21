namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.3 ExportEntry Record.
//
// Fields:
//   ExportName    : the name the exported binding is published under. Null for the
//                   `export * from "mod"` case, where every name re-exports.
//   ModuleRequest : module specifier for re-exports (`export ... from "mod"`); null
//                   for local exports.
//   ImportName    : the name being re-exported from the requested module; null for
//                   local exports. `all` and `all-but-default` are sentinel values
//                   for the two flavors of star re-export.
//   LocalName     : the local binding being exported. Null when the entry is a pure
//                   re-export with no local declaration.
//
// The combinations encode every export form per 16.2.3.7 step 7:
//   `export var x`                  -> {x, null, null, x}
//   `export default fn`             -> {default, null, null, *default*}
//   `export {x} from "mod"`         -> {x, "mod", x, null}
//   `export * from "mod"`           -> {null, "mod", all, null}
//   `export * as ns from "mod"`     -> {ns, "mod", all-but-default, null}
public readonly record struct ExportEntry(
    string? ExportName,
    string? ModuleRequest,
    string? ImportName,
    string? LocalName)
{
    public const string AllExports = "all";
    public const string AllButDefaultExports = "all-but-default";
    public const string DefaultLocalName = "*default*";

    public bool IsLocalExport => ModuleRequest is null;
    public bool IsReexport => ModuleRequest is not null;

    public bool IsStarReexport
        => IsReexport && ExportName is null && string.Equals(ImportName, AllExports, StringComparison.Ordinal);

    public bool IsNamespaceReexport
        => IsReexport && ExportName is not null
            && string.Equals(ImportName, AllButDefaultExports, StringComparison.Ordinal);
}
