using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FenBrowser.Core.WebIDL
{
    // ── WebIDL Parser ────────────────────────────────────────────────────────
    // Spec: https://webidl.spec.whatwg.org/
    // Parses a subset of WebIDL sufficient to drive binding generation:
    //   - interface / partial interface / mixin / includes
    //   - attribute (readonly, static, inherit, stringifier)
    //   - operation (regular, static, stringifier, getter, setter, deleter)
    //   - constructor
    //   - const
    //   - typedef
    //   - enum
    //   - dictionary
    //   - iterable<K,V> / setlike<T> / maplike<K,V>
    //   - Extended attributes: [CEReactions], [Exposed], [SameObject],
    //                          [NewObject], [LegacyNullToEmptyString],
    //                          [LegacyUnenumerableNamedProperties], etc.
    // ─────────────────────────────────────────────────────────────────────────

    #region AST nodes

    public enum IdlMemberKind
    {
        Attribute, Operation, Const, Constructor, Iterable, Setlike, Maplike,
        Stringifier, StaticAttribute, StaticOperation, Inherit
    }

    public sealed class IdlType
    {
        public string Name { get; set; }           // e.g. "DOMString", "long", "unsigned long"
        public bool Nullable { get; set; }          // T?
        public bool IsSequence { get; set; }
        public bool IsRecord { get; set; }
        public bool IsObservableArray { get; set; }
        public bool IsPromise { get; set; }
        public bool IsFrozenArray { get; set; }
        public bool IsAny { get; set; }
        public bool IsVoid { get; set; }            // alias for undefined return
        public bool IsUndefined { get; set; }
        public List<IdlType> TypeArguments { get; set; } = new();
        public List<IdlType> UnionTypes { get; set; }   // non-null = union type

        public bool IsUnion => UnionTypes != null && UnionTypes.Count > 0;
        public override string ToString()
        {
            if (IsUnion) return $"({string.Join(" or ", UnionTypes)})";
            var n = Name ?? "any";
            if (IsSequence) return $"sequence<{(TypeArguments.Count > 0 ? TypeArguments[0] : "any")}>";
            if (IsRecord) return $"record<{(TypeArguments.Count > 1 ? TypeArguments[0] + ", " + TypeArguments[1] : "any")}>";
            if (IsPromise) return $"Promise<{(TypeArguments.Count > 0 ? TypeArguments[0].ToString() : "any")}>";
            return Nullable ? n + "?" : n;
        }
    }

    public sealed class IdlArgument
    {
        public string Name { get; set; }
        public IdlType Type { get; set; }
        public bool Optional { get; set; }
        public bool Variadic { get; set; }
        public string DefaultValue { get; set; }
        public List<IdlExtendedAttribute> ExtAttrs { get; set; } = new();
    }

    public sealed class IdlExtendedAttribute
    {
        public string Name { get; set; }
        public string ArgumentString { get; set; }  // raw string after '='
        public List<IdlArgument> Arguments { get; set; }
    }

    public sealed class IdlMember
    {
        public IdlMemberKind Kind { get; set; }
        public string Name { get; set; }
        public IdlType Type { get; set; }
        public bool Readonly { get; set; }
        public bool Static { get; set; }
        public bool Inherit { get; set; }
        public bool Stringifier { get; set; }
        public bool Getter { get; set; }
        public bool Setter { get; set; }
        public bool Deleter { get; set; }
        public List<IdlArgument> Arguments { get; set; } = new();
        public List<IdlType> IterableTypes { get; set; } = new();
        public string ConstValue { get; set; }
        public List<IdlExtendedAttribute> ExtAttrs { get; set; } = new();
    }

    public abstract class IdlDefinition
    {
        public string Name { get; set; }
        public List<IdlExtendedAttribute> ExtAttrs { get; set; } = new();
    }

    public sealed class IdlInterface : IdlDefinition
    {
        public string Inherits { get; set; }
        public bool IsPartial { get; set; }
        public bool IsMixin { get; set; }
        public List<IdlMember> Members { get; set; } = new();
        public string Namespace { get; set; }
    }

    public sealed class IdlDictionary : IdlDefinition
    {
        public string Inherits { get; set; }
        public bool IsPartial { get; set; }
        public List<IdlDictionaryMember> Members { get; set; } = new();
    }

    public sealed class IdlDictionaryMember
    {
        public string Name { get; set; }
        public IdlType Type { get; set; }
        public bool Required { get; set; }
        public string DefaultValue { get; set; }
        public List<IdlExtendedAttribute> ExtAttrs { get; set; } = new();
    }

    public sealed class IdlEnum : IdlDefinition
    {
        public List<string> Values { get; set; } = new();
    }

    public sealed class IdlTypedef : IdlDefinition
    {
        public IdlType Type { get; set; }
    }

    public sealed class IdlIncludes : IdlDefinition
    {
        public string Target { get; set; }    // interface that includes
        public string Mixin { get; set; }     // mixin being included
    }

    public sealed class IdlNamespace : IdlDefinition
    {
        public bool IsPartial { get; set; }
        public List<IdlMember> Members { get; set; } = new();
    }

    public sealed class IdlCallback : IdlDefinition
    {
        public IdlType ReturnType { get; set; }
        public List<IdlArgument> Arguments { get; set; } = new();
        public bool IsFunction { get; set; }
        public List<IdlMember> Members { get; set; } = new();
    }

    public sealed class IdlParseResult
    {
        public List<IdlDefinition> Definitions { get; } = new();
        public List<string> Errors { get; } = new();
        public bool Success => Errors.Count == 0;
    }

    #endregion

    /// <summary>
    /// Recursive-descent WebIDL parser.
    /// Implements a production-grade subset of the WebIDL grammar.
    /// </summary>
    public sealed class WebIdlParser
    {
        private List<Token> _tokens;
        private int _pos;
        private List<string> _errors;

        private sealed class Token
        {
            public enum Kind { Ident, Number, String, Symbol, Eof }
            public Kind Type { get; set; }
            public string Value { get; set; }
            public int Line { get; set; }
        }

        public IdlParseResult Parse(string idl)
        {
            var result = new IdlParseResult();
            _errors = result.Errors;

            _tokens = Tokenize(idl);
            _pos = 0;

            while (!IsEof())
            {
                try
                {
                    var def = ParseDefinition();
                    if (def != null) result.Definitions.Add(def);
                }
                catch (Exception ex)
                {
                    _errors.Add($"Parse error at token '{Current().Value}': {ex.Message}");
                    SkipToNextDefinition();
                }
            }

            return result;
        }

        // ── Tokenizer ────────────────────────────────────────────────────────

        private static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            int i = 0, line = 1;
            while (i < input.Length)
            {
                char c = input[i];

                // Whitespace
                if (c == '\n') { line++; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }

                // Single-line comment
                if (c == '/' && i + 1 < input.Length && input[i + 1] == '/')
                {
                    while (i < input.Length && input[i] != '\n') i++;
                    continue;
                }

                // Multi-line comment
                if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < input.Length && !(input[i] == '*' && input[i + 1] == '/')) i++;
                    i += 2;
                    continue;
                }

                // String literal
                if (c == '"')
                {
                    int start = i + 1;
                    i++;
                    while (i < input.Length && input[i] != '"') i++;
                    tokens.Add(new Token { Type = Token.Kind.String, Value = input.Substring(start, i - start), Line = line });
                    i++; // skip closing "
                    continue;
                }

                // Identifier or keyword
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '-'))
                        i++;
                    tokens.Add(new Token { Type = Token.Kind.Ident, Value = input.Substring(start, i - start), Line = line });
                    continue;
                }

                // Number
                if (char.IsDigit(c) || (c == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
                {
                    int start = i;
                    if (c == '-') i++;
                    while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.' || input[i] == 'e' || input[i] == 'E' || input[i] == '+' || input[i] == '-' || input[i] == 'x' || input[i] == 'X'))
                        i++;
                    tokens.Add(new Token { Type = Token.Kind.Number, Value = input.Substring(start, i - start), Line = line });
                    continue;
                }

                // Symbols
                tokens.Add(new Token { Type = Token.Kind.Symbol, Value = c.ToString(), Line = line });
                i++;
            }

            tokens.Add(new Token { Type = Token.Kind.Eof, Value = "<EOF>", Line = line });
            return tokens;
        }

        // ── Grammar productions ───────────────────────────────────────────────

        private IdlDefinition ParseDefinition()
        {
            var extAttrs = ParseExtendedAttributeList();

            // partial interface | partial dictionary | partial namespace
            if (Peek("partial"))
            {
                Consume("partial");
                if (Peek("interface"))
                {
                    Consume("interface");
                    if (Peek("mixin"))
                    {
                        Consume("mixin");
                        return ParseInterface(extAttrs, isPartial: true, isMixin: true);
                    }
                    return ParseInterface(extAttrs, isPartial: true);
                }
                if (Peek("dictionary"))
                {
                    Consume("dictionary");
                    return ParseDictionary(extAttrs, isPartial: true);
                }
                if (Peek("namespace"))
                {
                    Consume("namespace");
                    return ParseNamespace(extAttrs, isPartial: true);
                }
            }

            if (Peek("interface"))
            {
                Consume("interface");
                if (Peek("mixin"))
                {
                    Consume("mixin");
                    return ParseInterface(extAttrs, isMixin: true);
                }
                return ParseInterface(extAttrs);
            }

            if (Peek("dictionary"))
            {
                Consume("dictionary");
                return ParseDictionary(extAttrs);
            }

            if (Peek("enum"))
            {
                Consume("enum");
                return ParseEnum(extAttrs);
            }

            if (Peek("typedef"))
            {
                Consume("typedef");
                return ParseTypedef(extAttrs);
            }

            if (Peek("namespace"))
            {
                Consume("namespace");
                return ParseNamespace(extAttrs);
            }

            if (Peek("callback"))
            {
                Consume("callback");
                if (Peek("interface"))
                {
                    Consume("interface");
                    return ParseCallbackInterface(extAttrs);
                }
                return ParseCallbackFunction(extAttrs);
            }

            // Includes statement: "TargetInterface includes MixinInterface ;"
            var name = ConsumeIdent();
            if (Peek("includes"))
            {
                Consume("includes");
                var mixin = ConsumeIdent();
                Consume(";");
                return new IdlIncludes
                {
                    Name = name,
                    Target = name,
                    Mixin = mixin,
                    ExtAttrs = extAttrs,
                };
            }

            _errors.Add($"Unexpected token '{Current().Value}' at top level.");
            SkipToNextDefinition();
            return null;
        }

        private IdlInterface ParseInterface(
            List<IdlExtendedAttribute> extAttrs,
            bool isPartial = false,
            bool isMixin = false)
        {
            var iface = new IdlInterface
            {
                Name = ConsumeIdent(),
                IsPartial = isPartial,
                IsMixin = isMixin,
                ExtAttrs = extAttrs,
            };

            if (Peek(":"))
            {
                Consume(":");
                iface.Inherits = ConsumeIdent();
            }

            Consume("{");
            while (!Peek("}") && !IsEof())
            {
                try
                {
                    var member = ParseInterfaceMember();
                    if (member != null) iface.Members.Add(member);
                }
                catch (Exception ex)
                {
                    _errors.Add($"Error in interface '{iface.Name}': {ex.Message}");
                    SkipToSemicolon();
                }
            }
            Consume("}");
            Consume(";");
            return iface;
        }

        private IdlMember ParseInterfaceMember()
        {
            var extAttrs = ParseExtendedAttributeList();
            var member = new IdlMember { ExtAttrs = extAttrs };

            bool isStatic = false, isStringifier = false, isInherit = false;
            bool isReadonly = false, isGetter = false, isSetter = false, isDeleter = false;

            // Qualifiers
            while (true)
            {
                if (Peek("static")) { Consume("static"); isStatic = true; }
                else if (Peek("stringifier")) { Consume("stringifier"); isStringifier = true; }
                else if (Peek("inherit")) { Consume("inherit"); isInherit = true; }
                else if (Peek("readonly")) { Consume("readonly"); isReadonly = true; }
                else if (Peek("getter")) { Consume("getter"); isGetter = true; }
                else if (Peek("setter")) { Consume("setter"); isSetter = true; }
                else if (Peek("deleter")) { Consume("deleter"); isDeleter = true; }
                else break;
            }

            // const
            if (Peek("const"))
            {
                Consume("const");
                var constType = ParseType();
                var constName = ConsumeIdent();
                Consume("=");
                var constVal = ConsumeConstValue();
                Consume(";");
                return new IdlMember
                {
                    Kind = IdlMemberKind.Const,
                    Name = constName,
                    Type = constType,
                    ConstValue = constVal,
                    ExtAttrs = extAttrs,
                };
            }

            // constructor
            if (Peek("constructor"))
            {
                Consume("constructor");
                Consume("(");
                var args = ParseArgumentList();
                Consume(")");
                Consume(";");
                return new IdlMember
                {
                    Kind = IdlMemberKind.Constructor,
                    Arguments = args,
                    ExtAttrs = extAttrs,
                };
            }

            // iterable / setlike / maplike
            if (Peek("iterable"))
            {
                Consume("iterable");
                Consume("<");
                var types = new List<IdlType> { ParseType() };
                if (Peek(",")) { Consume(","); types.Add(ParseType()); }
                Consume(">");
                Consume(";");
                return new IdlMember { Kind = IdlMemberKind.Iterable, IterableTypes = types, ExtAttrs = extAttrs };
            }
            if (Peek("setlike"))
            {
                Consume("setlike");
                Consume("<"); var t = ParseType(); Consume(">"); Consume(";");
                return new IdlMember { Kind = IdlMemberKind.Setlike, Readonly = isReadonly, IterableTypes = new List<IdlType> { t }, ExtAttrs = extAttrs };
            }
            if (Peek("maplike"))
            {
                Consume("maplike");
                Consume("<"); var k = ParseType(); Consume(","); var v = ParseType(); Consume(">"); Consume(";");
                return new IdlMember { Kind = IdlMemberKind.Maplike, Readonly = isReadonly, IterableTypes = new List<IdlType> { k, v }, ExtAttrs = extAttrs };
            }

            // attribute
            if (Peek("attribute"))
            {
                Consume("attribute");
                var attrType = ParseType();
                var attrName = ConsumeIdent();
                Consume(";");
                return new IdlMember
                {
                    Kind = isStatic ? IdlMemberKind.StaticAttribute : IdlMemberKind.Attribute,
                    Name = attrName,
                    Type = attrType,
                    Readonly = isReadonly,
                    Static = isStatic,
                    Inherit = isInherit,
                    Stringifier = isStringifier,
                    ExtAttrs = extAttrs,
                };
            }

            // operation (return-type identifier ( args ) ;)
            if (!IsEof() && !Peek("}"))
            {
                var retType = ParseType();
                string opName = null;
                if (!Peek("("))
                {
                    opName = TryConsumeIdent();
                }
                Consume("(");
                var opArgs = ParseArgumentList();
                Consume(")");
                Consume(";");
                return new IdlMember
                {
                    Kind = isStatic ? IdlMemberKind.StaticOperation : IdlMemberKind.Operation,
                    Name = opName,
                    Type = retType,
                    Static = isStatic,
                    Stringifier = isStringifier,
                    Getter = isGetter,
                    Setter = isSetter,
                    Deleter = isDeleter,
                    Arguments = opArgs,
                    ExtAttrs = extAttrs,
                };
            }

            return null;
        }

        private IdlDictionary ParseDictionary(List<IdlExtendedAttribute> extAttrs, bool isPartial = false)
        {
            var dict = new IdlDictionary
            {
                Name = ConsumeIdent(),
                IsPartial = isPartial,
                ExtAttrs = extAttrs,
            };
            if (Peek(":")) { Consume(":"); dict.Inherits = ConsumeIdent(); }
            Consume("{");
            while (!Peek("}") && !IsEof())
            {
                var mExt = ParseExtendedAttributeList();
                bool required = Peek("required");
                if (required) Consume("required");
                var mType = ParseType();
                var mName = ConsumeIdent();
                string mDefault = null;
                if (Peek("=")) { Consume("="); mDefault = ConsumeConstValue(); }
                Consume(";");
                dict.Members.Add(new IdlDictionaryMember
                {
                    Name = mName,
                    Type = mType,
                    Required = required,
                    DefaultValue = mDefault,
                    ExtAttrs = mExt,
                });
            }
            Consume("}");
            Consume(";");
            return dict;
        }

        private IdlEnum ParseEnum(List<IdlExtendedAttribute> extAttrs)
        {
            var e = new IdlEnum { Name = ConsumeIdent(), ExtAttrs = extAttrs };
            Consume("{");
            while (!Peek("}") && !IsEof())
            {
                e.Values.Add(ConsumeString());
                if (Peek(",")) Consume(",");
            }
            Consume("}");
            Consume(";");
            return e;
        }

        private IdlTypedef ParseTypedef(List<IdlExtendedAttribute> extAttrs)
        {
            var type = ParseType();
            var name = ConsumeIdent();
            Consume(";");
            return new IdlTypedef { Name = name, Type = type, ExtAttrs = extAttrs };
        }

        private IdlCallback ParseCallbackInterface(List<IdlExtendedAttribute> extAttrs)
        {
            var cb = new IdlCallback
            {
                Name = ConsumeIdent(),
                IsFunction = false,
                ExtAttrs = extAttrs,
            };

            Consume("{");
            while (!Peek("}") && !IsEof())
            {
                var member = ParseInterfaceMember();
                if (member != null)
                {
                    cb.Members.Add(member);
                }
            }
            Consume("}");
            Consume(";");
            return cb;
        }

        private IdlCallback ParseCallbackFunction(List<IdlExtendedAttribute> extAttrs)
        {
            var cb = new IdlCallback
            {
                Name = ConsumeIdent(),
                IsFunction = true,
                ExtAttrs = extAttrs,
            };

            Consume("=");
            cb.ReturnType = ParseType();
            Consume("(");
            cb.Arguments = ParseArgumentList();
            Consume(")");
            Consume(";");
            return cb;
        }

        private IdlNamespace ParseNamespace(List<IdlExtendedAttribute> extAttrs, bool isPartial = false)
        {
            var ns = new IdlNamespace { Name = ConsumeIdent(), IsPartial = isPartial, ExtAttrs = extAttrs };
            Consume("{");
            while (!Peek("}") && !IsEof())
            {
                try
                {
                    var m = ParseInterfaceMember();
                    if (m != null) ns.Members.Add(m);
                }
                catch
                {
                    SkipToSemicolon();
                }
            }
            Consume("}");
            Consume(";");
            return ns;
        }

        // ── Type parsing ─────────────────────────────────────────────────────

        private IdlType ParseType()
        {
            // Union type
            if (Peek("(")) return ParseUnionType();

            bool nullable = false;
            var type = new IdlType();

            // Prefix modifiers
            string name = Current().Value;

            // Check for sequence<>, Promise<>, FrozenArray<>, ObservableArray<>, record<>
            if (Peek("sequence") || Peek("FrozenArray") || Peek("ObservableArray") ||
                Peek("Promise") || Peek("record"))
            {
                var keyword = Current().Value;
                Advance();
                Consume("<");
                type.TypeArguments.Add(ParseType());
                if (Peek(",") && keyword == "record") { Consume(","); type.TypeArguments.Add(ParseType()); }
                Consume(">");
                switch (keyword)
                {
                    case "sequence":         type.IsSequence = true; break;
                    case "FrozenArray":      type.IsFrozenArray = true; break;
                    case "ObservableArray":  type.IsObservableArray = true; break;
                    case "Promise":          type.IsPromise = true; break;
                    case "record":           type.IsRecord = true; break;
                }
                type.Name = keyword;
            }
            else
            {
                // Possibly multi-word type: "unsigned long long", "unrestricted double", etc.
                var sb = new StringBuilder();
                if (Peek("unsigned") || Peek("unrestricted"))
                {
                    sb.Append(Current().Value); Advance(); sb.Append(' ');
                }
                if (Peek("long")) { sb.Append("long"); Advance(); if (Peek("long")) { sb.Append(" long"); Advance(); } }
                else { sb.Append(Current().Value); Advance(); }
                type.Name = sb.ToString().Trim();
            }

            if (Peek("?")) { Consume("?"); nullable = true; }
            type.Nullable = nullable;

            if (type.Name == "void" || type.Name == "undefined") type.IsUndefined = true;
            if (type.Name == "any") type.IsAny = true;

            return type;
        }

        private IdlType ParseUnionType()
        {
            Consume("(");
            var types = new List<IdlType> { ParseType() };
            while (Peek("or")) { Consume("or"); types.Add(ParseType()); }
            Consume(")");
            bool nullable = false;
            if (Peek("?")) { Consume("?"); nullable = true; }
            return new IdlType { UnionTypes = types, Nullable = nullable, Name = "union" };
        }

        // ── Argument list ────────────────────────────────────────────────────

        private List<IdlArgument> ParseArgumentList()
        {
            var args = new List<IdlArgument>();
            while (!Peek(")") && !IsEof())
            {
                var argExt = ParseExtendedAttributeList();
                bool optional = Peek("optional");
                if (optional) Consume("optional");
                var argType = ParseType();
                bool variadic = false;
                if (Peek("...")) { Consume("..."); variadic = true; }
                var argName = ConsumeIdent();
                string argDefault = null;
                if (optional && Peek("=")) { Consume("="); argDefault = ConsumeConstValue(); }
                args.Add(new IdlArgument
                {
                    Name = argName,
                    Type = argType,
                    Optional = optional,
                    Variadic = variadic,
                    DefaultValue = argDefault,
                    ExtAttrs = argExt,
                });
                if (Peek(",")) Consume(",");
            }
            return args;
        }

        // ── Extended attribute list ───────────────────────────────────────────

        private List<IdlExtendedAttribute> ParseExtendedAttributeList()
        {
            var attrs = new List<IdlExtendedAttribute>();
            if (!Peek("[")) return attrs;
            Consume("[");
            while (!Peek("]") && !IsEof())
            {
                attrs.Add(ParseExtendedAttribute());
                if (Peek(",")) Consume(",");
            }
            Consume("]");
            return attrs;
        }

        private IdlExtendedAttribute ParseExtendedAttribute()
        {
            var name = ConsumeIdent();
            var attr = new IdlExtendedAttribute { Name = name };

            if (Peek("="))
            {
                Consume("=");
                if (Peek("("))
                {
                    // List form: [ExtAttr=(a, b, c)]
                    Consume("(");
                    var sb = new StringBuilder();
                    while (!Peek(")") && !IsEof()) { sb.Append(Current().Value); Advance(); }
                    Consume(")");
                    attr.ArgumentString = sb.ToString();
                }
                else
                {
                    attr.ArgumentString = Current().Value;
                    Advance();
                }
            }
            else if (Peek("("))
            {
                Consume("(");
                attr.Arguments = ParseArgumentList();
                Consume(")");
            }

            return attr;
        }

        // ── Const value parsing ──────────────────────────────────────────────

        private string ConsumeConstValue()
        {
            // null, true, false, number, Infinity, -Infinity, NaN, string
            if (Peek("-") || Current().Type == Token.Kind.Number ||
                Peek("true") || Peek("false") || Peek("null") ||
                Peek("Infinity") || Peek("NaN") || Peek("\"") ||
                Current().Type == Token.Kind.String)
            {
                var val = Current().Value;
                Advance();
                if (val == "-" && Current().Type == Token.Kind.Number)
                {
                    val = "-" + Current().Value;
                    Advance();
                }
                return val;
            }
            if (Peek("{")) { SkipBalanced("{", "}"); return "{}"; }
            if (Peek("[")) { SkipBalanced("[", "]"); return "[]"; }
            return Current().Value;
        }

        // ── Token helpers ────────────────────────────────────────────────────

        private bool IsEof() => _pos >= _tokens.Count || _tokens[_pos].Type == Token.Kind.Eof;
        private Token Current() => _pos < _tokens.Count ? _tokens[_pos] : _tokens[_tokens.Count - 1];

        private bool Peek(string value) =>
            !IsEof() && Current().Value == value;

        private void Advance() { if (!IsEof()) _pos++; }

        private void Consume(string value)
        {
            if (Current().Value != value)
                throw new Exception($"Expected '{value}' but got '{Current().Value}'");
            Advance();
        }

        private string ConsumeIdent()
        {
            if (Current().Type != Token.Kind.Ident && Current().Type != Token.Kind.Number)
            {
                // Some IDL names are keywords; accept them as identifiers
                var kw = Current().Value;
                if (!IsEof() && kw != "{" && kw != "}" && kw != ";" && kw != "(" && kw != ")")
                {
                    Advance();
                    return kw;
                }
                throw new Exception($"Expected identifier but got '{Current().Value}'");
            }
            var v = Current().Value;
            Advance();
            return v;
        }

        private string TryConsumeIdent()
        {
            if (IsEof() || Current().Type == Token.Kind.Symbol) return null;
            var v = Current().Value;
            Advance();
            return v;
        }

        private string ConsumeString()
        {
            if (Current().Type != Token.Kind.String)
                throw new Exception($"Expected string literal but got '{Current().Value}'");
            var v = Current().Value;
            Advance();
            return v;
        }

        private void SkipToSemicolon()
        {
            while (!IsEof() && !Peek(";")) Advance();
            if (!IsEof()) Advance(); // consume ;
        }

        private void SkipToNextDefinition()
        {
            // Skip forward to a position that could start a new definition
            int depth = 0;
            while (!IsEof())
            {
                if (Peek("{")) depth++;
                else if (Peek("}")) { if (depth > 0) depth--; else { Advance(); if (Peek(";")) Advance(); return; } }
                else if (depth == 0 && Peek(";")) { Advance(); return; }
                Advance();
            }
        }

        private void SkipBalanced(string open, string close)
        {
            Consume(open);
            int depth = 1;
            while (!IsEof() && depth > 0)
            {
                if (Peek(open)) depth++;
                else if (Peek(close)) depth--;
                Advance();
            }
        }
    }
}
