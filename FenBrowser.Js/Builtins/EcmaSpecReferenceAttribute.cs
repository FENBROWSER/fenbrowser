namespace FenBrowser.Js.Builtins;

// Documents the ECMA-262 (or related) specification section a builtin module, type, or
// method implements. The attribute is informational - the value flows into per-module
// audits, test262 mapping, and source navigation - but does not change runtime
// behavior.
//
// The section string should follow the spec's own numbering ("21.3.1" for Math, "25.6"
// for Promise) and ideally name the abstract operation when the attribute is applied
// to a method ("OrdinaryFunctionCall", "ResolveBinding"). The optional Url field
// carries a direct link into the editor's draft when one exists; consumers should
// treat it as a hint, not a guarantee of stability.
[AttributeUsage(
    AttributeTargets.Class
    | AttributeTargets.Struct
    | AttributeTargets.Interface
    | AttributeTargets.Method
    | AttributeTargets.Property
    | AttributeTargets.Field,
    AllowMultiple = true,
    Inherited = false)]
public sealed class EcmaSpecReferenceAttribute : Attribute
{
    public EcmaSpecReferenceAttribute(string section)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        Section = section;
    }

    public string Section { get; }

    public string? AbstractOperation { get; init; }

    public string? Url { get; init; }
}
