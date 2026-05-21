namespace FenBrowser.Js.Environments;

// ECMA-262 9.1 Environment Records: the abstract operations return either a successful
// value or one of these spec-recognized error conditions. Translating each into a JS
// ReferenceError / TypeError / SyntaxError is the interpreter's responsibility, not the
// environment record's, so this enum stays free of any throw machinery.
public enum BindingOpResult : byte
{
    Ok,
    NotFound,
    TdzAccess,
    ConstAssignment,
    AlreadyDeclared,
    NotInitializable,
}
