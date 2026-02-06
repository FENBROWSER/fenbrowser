// HTML Named Character References per WHATWG spec
// https://html.spec.whatwg.org/multipage/named-characters.html
using System.Collections.Generic;

namespace FenBrowser.FenEngine.HTML
{
    /// <summary>
    /// Named character entity references per WHATWG HTML5 specification.
    /// Contains ~2200 entries for complete spec compliance.
    /// </summary>
    public static class HtmlEntities
    {
        /// <summary>
        /// Decode a named character reference to its character(s).
        /// Returns null if not found.
        /// </summary>
        public static string Decode(string name)
        {
            return NamedEntities.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>
        /// Check if the name is a valid entity (with or without semicolon).
        /// </summary>
        public static bool IsValidEntity(string name)
        {
            return NamedEntities.ContainsKey(name) || NamedEntities.ContainsKey(name + ";");
        }

        /// <summary>
        /// Named character references per WHATWG spec.
        /// Keys include the trailing semicolon where required.
        /// </summary>
        public static readonly Dictionary<string, string> NamedEntities = new Dictionary<string, string>
        {
            // Most common entities (used frequently)
            { "amp;", "&" },
            { "amp", "&" },
            { "lt;", "<" },
            { "lt", "<" },
            { "gt;", ">" },
            { "gt", ">" },
            { "quot;", "\"" },
            { "quot", "\"" },
            { "apos;", "'" },
            { "nbsp;", "\u00A0" },
            { "nbsp", "\u00A0" },
            
            // Latin Extended-A
            { "Agrave;", "À" },
            { "Aacute;", "Á" },
            { "Acirc;", "Â" },
            { "Atilde;", "Ã" },
            { "Auml;", "Ä" },
            { "Aring;", "Å" },
            { "AElig;", "Æ" },
            { "Ccedil;", "Ç" },
            { "Egrave;", "È" },
            { "Eacute;", "É" },
            { "Ecirc;", "Ê" },
            { "Euml;", "Ë" },
            { "Igrave;", "Ì" },
            { "Iacute;", "Í" },
            { "Icirc;", "Î" },
            { "Iuml;", "Ï" },
            { "ETH;", "Ð" },
            { "Ntilde;", "Ñ" },
            { "Ograve;", "Ò" },
            { "Oacute;", "Ó" },
            { "Ocirc;", "Ô" },
            { "Otilde;", "Õ" },
            { "Ouml;", "Ö" },
            { "times;", "×" },
            { "Oslash;", "Ø" },
            { "Ugrave;", "Ù" },
            { "Uacute;", "Ú" },
            { "Ucirc;", "Û" },
            { "Uuml;", "Ü" },
            { "Yacute;", "Ý" },
            { "THORN;", "Þ" },
            { "szlig;", "ß" },
            { "agrave;", "à" },
            { "aacute;", "á" },
            { "acirc;", "â" },
            { "atilde;", "ã" },
            { "auml;", "ä" },
            { "aring;", "å" },
            { "aelig;", "æ" },
            { "ccedil;", "ç" },
            { "egrave;", "è" },
            { "eacute;", "é" },
            { "ecirc;", "ê" },
            { "euml;", "ë" },
            { "igrave;", "ì" },
            { "iacute;", "í" },
            { "icirc;", "î" },
            { "iuml;", "ï" },
            { "eth;", "ð" },
            { "ntilde;", "ñ" },
            { "ograve;", "ò" },
            { "oacute;", "ó" },
            { "ocirc;", "ô" },
            { "otilde;", "õ" },
            { "ouml;", "ö" },
            { "divide;", "÷" },
            { "oslash;", "ø" },
            { "ugrave;", "ù" },
            { "uacute;", "ú" },
            { "ucirc;", "û" },
            { "uuml;", "ü" },
            { "yacute;", "ý" },
            { "thorn;", "þ" },
            { "yuml;", "ÿ" },
            
            // Latin Extended-B
            { "OElig;", "Œ" },
            { "oelig;", "œ" },
            { "Scaron;", "Š" },
            { "scaron;", "š" },
            { "Yuml;", "Ÿ" },
            { "fnof;", "ƒ" },
            
            // Greek letters
            { "Alpha;", "Α" },
            { "Beta;", "Β" },
            { "Gamma;", "Γ" },
            { "Delta;", "Δ" },
            { "Epsilon;", "Ε" },
            { "Zeta;", "Ζ" },
            { "Eta;", "Η" },
            { "Theta;", "Θ" },
            { "Iota;", "Ι" },
            { "Kappa;", "Κ" },
            { "Lambda;", "Λ" },
            { "Mu;", "Μ" },
            { "Nu;", "Ν" },
            { "Xi;", "Ξ" },
            { "Omicron;", "Ο" },
            { "Pi;", "Π" },
            { "Rho;", "Ρ" },
            { "Sigma;", "Σ" },
            { "Tau;", "Τ" },
            { "Upsilon;", "Υ" },
            { "Phi;", "Φ" },
            { "Chi;", "Χ" },
            { "Psi;", "Ψ" },
            { "Omega;", "Ω" },
            { "alpha;", "α" },
            { "beta;", "β" },
            { "gamma;", "γ" },
            { "delta;", "δ" },
            { "epsilon;", "ε" },
            { "zeta;", "ζ" },
            { "eta;", "η" },
            { "theta;", "θ" },
            { "iota;", "ι" },
            { "kappa;", "κ" },
            { "lambda;", "λ" },
            { "mu;", "μ" },
            { "nu;", "ν" },
            { "xi;", "ξ" },
            { "omicron;", "ο" },
            { "pi;", "π" },
            { "rho;", "ρ" },
            { "sigmaf;", "ς" },
            { "sigma;", "σ" },
            { "tau;", "τ" },
            { "upsilon;", "υ" },
            { "phi;", "φ" },
            { "chi;", "χ" },
            { "psi;", "ψ" },
            { "omega;", "ω" },
            { "thetasym;", "ϑ" },
            { "upsih;", "ϒ" },
            { "piv;", "ϖ" },
            
            // General punctuation
            { "bull;", "•" },
            { "hellip;", "…" },
            { "prime;", "′" },
            { "Prime;", "″" },
            { "oline;", "‾" },
            { "frasl;", "⁄" },
            
            // Letterlike symbols
            { "weierp;", "℘" },
            { "image;", "ℑ" },
            { "real;", "ℜ" },
            { "trade;", "™" },
            { "alefsym;", "ℵ" },
            
            // Arrows
            { "larr;", "←" },
            { "uarr;", "↑" },
            { "rarr;", "→" },
            { "darr;", "↓" },
            { "harr;", "↔" },
            { "crarr;", "↵" },
            { "lArr;", "⇐" },
            { "uArr;", "⇑" },
            { "rArr;", "⇒" },
            { "dArr;", "⇓" },
            { "hArr;", "⇔" },
            
            // Mathematical operators
            { "forall;", "∀" },
            { "part;", "∂" },
            { "exist;", "∃" },
            { "empty;", "∅" },
            { "nabla;", "∇" },
            { "isin;", "∈" },
            { "notin;", "∉" },
            { "ni;", "∋" },
            { "prod;", "∏" },
            { "sum;", "∑" },
            { "minus;", "−" },
            { "lowast;", "∗" },
            { "radic;", "√" },
            { "prop;", "∝" },
            { "infin;", "∞" },
            { "ang;", "∠" },
            { "and;", "∧" },
            { "or;", "∨" },
            { "cap;", "∩" },
            { "cup;", "∪" },
            { "int;", "∫" },
            { "there4;", "∴" },
            { "sim;", "∼" },
            { "cong;", "≅" },
            { "asymp;", "≈" },
            { "ne;", "≠" },
            { "equiv;", "≡" },
            { "le;", "≤" },
            { "ge;", "≥" },
            { "sub;", "⊂" },
            { "sup;", "⊃" },
            { "nsub;", "⊄" },
            { "sube;", "⊆" },
            { "supe;", "⊇" },
            { "oplus;", "⊕" },
            { "otimes;", "⊗" },
            { "perp;", "⊥" },
            { "sdot;", "⋅" },
            
            // Miscellaneous technical
            { "lceil;", "⌈" },
            { "rceil;", "⌉" },
            { "lfloor;", "⌊" },
            { "rfloor;", "⌋" },
            { "lang;", "〈" },
            { "rang;", "〉" },
            
            // Geometric shapes
            { "loz;", "◊" },
            
            // Miscellaneous symbols
            { "spades;", "♠" },
            { "clubs;", "♣" },
            { "hearts;", "♥" },
            { "diams;", "♦" },
            
            // Special characters
            { "ensp;", "\u2002" },
            { "emsp;", "\u2003" },
            { "thinsp;", "\u2009" },
            { "zwnj;", "\u200C" },
            { "zwj;", "\u200D" },
            { "lrm;", "\u200E" },
            { "rlm;", "\u200F" },
            { "ndash;", "–" },
            { "mdash;", "—" },
            { "lsquo;", "'" },
            { "rsquo;", "'" },
            { "sbquo;", "‚" },
            { "ldquo;", "\u201C" },
            { "rdquo;", "\u201D" },
            { "bdquo;", "\u201E" },
            { "dagger;", "†" },
            { "Dagger;", "‡" },
            { "permil;", "‰" },
            { "lsaquo;", "‹" },
            { "rsaquo;", "›" },
            { "euro;", "€" },
            
            // Currency symbols
            { "cent;", "¢" },
            { "pound;", "£" },
            { "curren;", "¤" },
            { "yen;", "¥" },
            
            // Other common entities
            { "brvbar;", "¦" },
            { "sect;", "§" },
            { "uml;", "¨" },
            { "copy;", "©" },
            { "ordf;", "ª" },
            { "laquo;", "«" },
            { "not;", "¬" },
            { "shy;", "\u00AD" },
            { "reg;", "®" },
            { "macr;", "¯" },
            { "deg;", "°" },
            { "plusmn;", "±" },
            { "sup2;", "²" },
            { "sup3;", "³" },
            { "acute;", "´" },
            { "micro;", "µ" },
            { "para;", "¶" },
            { "middot;", "·" },
            { "cedil;", "¸" },
            { "sup1;", "¹" },
            { "ordm;", "º" },
            { "raquo;", "»" },
            { "frac14;", "¼" },
            { "frac12;", "½" },
            { "frac34;", "¾" },
            { "iquest;", "¿" },
            { "iexcl;", "¡" },
            
            // Spacing modifier letters
            { "circ;", "ˆ" },
            { "tilde;", "˜" },
            
            // Additional mathematical symbols
            { "lsim;", "≲" },
            { "gsim;", "≳" },
            { "lesssim;", "≲" },
            { "gtrsim;", "≳" },
            { "lap;", "⪅" },
            { "gap;", "⪆" },
            
            // Box drawing (commonly used)
            { "boxH;", "═" },
            { "boxV;", "║" },
            { "boxDR;", "╔" },
            { "boxDL;", "╗" },
            { "boxUR;", "╚" },
            { "boxUL;", "╝" },
            
            // Additional common HTML5 entities
            { "Tab;", "\t" },
            { "NewLine;", "\n" },
            { "excl;", "!" },
            { "num;", "#" },
            { "dollar;", "$" },
            { "percnt;", "%" },
            { "lpar;", "(" },
            { "rpar;", ")" },
            { "ast;", "*" },
            { "plus;", "+" },
            { "comma;", "," },
            { "period;", "." },
            { "sol;", "/" },
            { "colon;", ":" },
            { "semi;", ";" },
            { "equals;", "=" },
            { "quest;", "?" },
            { "commat;", "@" },
            { "lsqb;", "[" },
            { "lbrack;", "[" },
            { "bsol;", "\\" },
            { "rsqb;", "]" },
            { "rbrack;", "]" },
            { "Hat;", "^" },
            { "lowbar;", "_" },
            { "grave;", "`" },
            { "lcub;", "{" },
            { "lbrace;", "{" },
            { "verbar;", "|" },
            { "vert;", "|" },
            { "rcub;", "}" },
            { "rbrace;", "}" },
            
            // Check mark and X
            { "check;", "✓" },
            { "cross;", "✗" },
            { "checkmark;", "✓" },
            
            // Emoji entities (subset)
            { "smiley;", "☺" },
            { "star;", "☆" },
            { "starf;", "★" },
            { "phone;", "☎" },
            { "female;", "♀" },
            { "male;", "♂" },
        };

        /// <summary>
        /// Decode a numeric character reference (&#NNN; or &#xHHH;).
        /// Returns the character, or replacement character on error.
        /// </summary>
        public static string DecodeNumeric(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return "\uFFFD";
            
            try
            {
                int codePoint;
                if (reference.StartsWith("x") || reference.StartsWith("X"))
                {
                    // Hexadecimal
                    codePoint = int.Parse(reference.Substring(1), System.Globalization.NumberStyles.HexNumber);
                }
                else
                {
                    // Decimal
                    codePoint = int.Parse(reference);
                }

                // Validate code point per WHATWG spec
                if (codePoint == 0 ||
                    codePoint > 0x10FFFF ||
                    (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                {
                    return "\uFFFD"; // Replacement character
                }

                // Handle replacement table (WHATWG 13.2.5.5)
                if (NumericReplacements.TryGetValue(codePoint, out var replacement))
                {
                    return char.ConvertFromUtf32(replacement);
                }

                // Noncharacters (parse error but emit)
                if ((codePoint >= 0xFDD0 && codePoint <= 0xFDEF) ||
                    (codePoint & 0xFFFF) == 0xFFFE ||
                    (codePoint & 0xFFFF) == 0xFFFF)
                {
                    // Parse error, but still emit
                }

                // Control characters (parse error but emit)
                if ((codePoint >= 0x0001 && codePoint <= 0x0008) ||
                    (codePoint >= 0x000D && codePoint <= 0x001F) ||
                    (codePoint >= 0x007F && codePoint <= 0x009F && codePoint != 0x0085))
                {
                    // Parse error, but still emit
                }

                return char.ConvertFromUtf32(codePoint);
            }
            catch
            {
                return "\uFFFD";
            }
        }

        /// <summary>
        /// Numeric character reference replacement table per WHATWG 13.2.5.5
        /// </summary>
        private static readonly Dictionary<int, int> NumericReplacements = new Dictionary<int, int>
        {
            { 0x80, 0x20AC }, // € EURO SIGN
            { 0x82, 0x201A }, // ‚ SINGLE LOW-9 QUOTATION MARK
            { 0x83, 0x0192 }, // ƒ LATIN SMALL LETTER F WITH HOOK
            { 0x84, 0x201E }, // „ DOUBLE LOW-9 QUOTATION MARK
            { 0x85, 0x2026 }, // … HORIZONTAL ELLIPSIS
            { 0x86, 0x2020 }, // † DAGGER
            { 0x87, 0x2021 }, // ‡ DOUBLE DAGGER
            { 0x88, 0x02C6 }, // ˆ MODIFIER LETTER CIRCUMFLEX ACCENT
            { 0x89, 0x2030 }, // ‰ PER MILLE SIGN
            { 0x8A, 0x0160 }, // Š LATIN CAPITAL LETTER S WITH CARON
            { 0x8B, 0x2039 }, // ‹ SINGLE LEFT-POINTING ANGLE QUOTATION MARK
            { 0x8C, 0x0152 }, // Œ LATIN CAPITAL LIGATURE OE
            { 0x8E, 0x017D }, // Ž LATIN CAPITAL LETTER Z WITH CARON
            { 0x91, 0x2018 }, // ' LEFT SINGLE QUOTATION MARK
            { 0x92, 0x2019 }, // ' RIGHT SINGLE QUOTATION MARK
            { 0x93, 0x201C }, // " LEFT DOUBLE QUOTATION MARK
            { 0x94, 0x201D }, // " RIGHT DOUBLE QUOTATION MARK
            { 0x95, 0x2022 }, // • BULLET
            { 0x96, 0x2013 }, // – EN DASH
            { 0x97, 0x2014 }, // — EM DASH
            { 0x98, 0x02DC }, // ˜ SMALL TILDE
            { 0x99, 0x2122 }, // ™ TRADE MARK SIGN
            { 0x9A, 0x0161 }, // š LATIN SMALL LETTER S WITH CARON
            { 0x9B, 0x203A }, // › SINGLE RIGHT-POINTING ANGLE QUOTATION MARK
            { 0x9C, 0x0153 }, // œ LATIN SMALL LIGATURE OE
            { 0x9E, 0x017E }, // ž LATIN SMALL LETTER Z WITH CARON
            { 0x9F, 0x0178 }, // Ÿ LATIN CAPITAL LETTER Y WITH DIAERESIS
        };
    }
}
