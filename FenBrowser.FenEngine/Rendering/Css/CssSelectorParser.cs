using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering
{
    public static partial class CssLoader
    {
        public enum Combinator { Descendant, Child, AdjacentSibling, GeneralSibling }

        public class SelectorChain
        {
            public List<SelectorSegment> Segments = new List<SelectorSegment>(); // left-to-right parsed
            public bool Important;    // !important
            public int Specificity;   // computed from segments
        }

        public class SelectorSegment
        {
            public string Tag;                    // e.g. "div"
            public string Id;                     // e.g. "main"
            public List<string> Classes = new List<string>();          // e.g. ["foo","bar"]
            public List<string> PseudoClasses;    // e.g. [":first-child"]
            public string PseudoElement;          // e.g. "before", "after"
            public List<Tuple<string, string, string>> Attributes; // e.g. [("type", "=", "text")]
            public Combinator? Next;              // relation to the NEXT segment (left-to-right)
            public List<SelectorSegment> NotSelectors; // :not() selectors (elements must NOT match these)
        }

        private static List<SelectorChain> ParseSelectors(string selectorText)
        {
            // Split on commas at top level (not inside anything else ? here we assume plain selectors).
            var parts = selectorText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SelectorChain>();
            foreach (var p in parts)
            {
                var chain = ParseSelectorChain(p.Trim());
                if (chain != null) list.Add(chain);
            }
            return list;
        }

        private static SelectorChain ParseSelectorChain(string s)
        {
            // Supports sequences separated by space (descendant) or '>' (child)
            // Each segment supports: tag (optional), #id, .class(.class2...)
            var chain = new SelectorChain();
            var tokens = TokenizeSelector(s);
            if (tokens.Count == 0) return null;

            var seg = new SelectorSegment { Classes = new List<string>() };
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == " ")
                {
                    seg.Next = Combinator.Descendant;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == ">")
                {
                    seg.Next = Combinator.Child;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == "+")
                {
                    seg.Next = Combinator.AdjacentSibling;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == "~")
                {
                    seg.Next = Combinator.GeneralSibling;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith(":"))
                {
                    string lower = t.ToLowerInvariant();
                    if (lower.StartsWith("::"))
                    {
                        string pe = lower.Substring(2);
                        if (pe == "before" || pe == "after")
                        {
                            seg.PseudoElement = pe;
                        }
                        else
                        {
                            if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                            seg.PseudoClasses.Add(t.Substring(2));
                        }
                    }
                    else
                    {
                        string val = lower.Substring(1);
                        if (val == "before" || val == "after")
                        {
                            seg.PseudoElement = val;
                        }
                        else if (val.StartsWith("not("))
                        {
                            // Handle :not() selector
                            var notArg = ExtractPseudoArg(val);
                            if (!string.IsNullOrEmpty(notArg))
                            {
                                // Parse the inner selector
                                var notSeg = ParseSimpleSelector(notArg);
                                if (notSeg != null)
                                {
                                    if (seg.NotSelectors == null) seg.NotSelectors = new List<SelectorSegment>();
                                    seg.NotSelectors.Add(notSeg);
                                }
                            }
                        }
                        else
                        {
                            if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                            seg.PseudoClasses.Add(t.Substring(1));
                        }
                    }
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing: [attr], [attr=val], [attr~=val], [attr|=val], [attr^=val], [attr$=val], [attr*=val]
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();

                    // Check for operator (in order of length to match longest first)
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    
                    foreach (var op in operators)
                    {
                        int idx = content.IndexOf(op);
                        if (idx >= 0)
                        {
                            foundOp = op;
                            opIndex = idx;
                            break;
                        }
                    }

                    if (foundOp == null || opIndex < 0)
                    {
                        // Just [attr] - presence check
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        // Handle CSS escape sequences: backslash followed by a character means the character itself
                        // e.g., "second\ two" becomes "second two"
                        val = System.Text.RegularExpressions.Regex.Replace(val, @"\\(.)", "$1");
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else
                {
                    seg.Tag = t;
                }
            }
            chain.Segments.Add(seg);

            // compute specificity: ids*100 + classes*10 + tags*1
            int ids = 0, cl = 0, tg = 0;
            foreach (var s2 in chain.Segments)
            {
                if (!string.IsNullOrEmpty(s2.Id)) ids++;
                if (!string.IsNullOrEmpty(s2.Tag)) tg++;
                if (s2.Classes != null) cl += s2.Classes.Count;
            }
            chain.Specificity = ids * 100 + cl * 10 + tg;

            return chain;
        }

        private static List<string> TokenizeSelector(string s)
        {
            // turns "div#main .x > span.y [attr=val]" into ["div","#main"," ",".x",">","span",".y"," ","[attr=val]"]
            var r = new List<string>();
            var sb = new StringBuilder();
            Action flush = () => { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Clear(); } };

            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    flush();
                    // coalesce spaces
                    if (r.Count == 0 || r[r.Count - 1] != " ") r.Add(" ");
                }
                else if (c == '>' || c == '+' || c == '~')
                {
                    // Check if ~ is part of ~= attribute selector (peek ahead)
                    if (c == '~' && i + 1 < s.Length && s[i + 1] == '=')
                    {
                        // This is part of an attribute selector inside [], should be handled there
                        sb.Append(c);
                    }
                    else
                    {
                        flush(); r.Add(c.ToString());
                    }
                }
                else if (c == '[')
                {
                    // Read entire attribute selector until closing ]
                    flush();
                    sb.Append(c);
                    i++;
                    int bracketDepth = 1;
                    while (i < s.Length && bracketDepth > 0)
                    {
                        char ac = s[i];
                        sb.Append(ac);
                        if (ac == '[') bracketDepth++;
                        else if (ac == ']') bracketDepth--;
                        i++;
                    }
                    i--; // Back up since outer loop will increment
                    flush();
                }
                else if (c == '.' || c == '#')
                {
                    flush(); sb.Append(c);
                }
                else if (c == ':')
                {
                    flush(); 
                    sb.Append(c);
                    // Handle double colon ::
                    if (i + 1 < s.Length && s[i + 1] == ':')
                    {
                        i++;
                        sb.Append(s[i]);
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            flush();
            // trim leading/trailing spaces tokens
            if (r.Count > 0 && r[0] == " ") r.RemoveAt(0);
            if (r.Count > 0 && r[r.Count - 1] == " ") r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>
        /// Parse a simple selector (for :not() argument) - supports tag, .class, #id, [attr]
        /// </summary>
        private static SelectorSegment ParseSimpleSelector(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            
            var seg = new SelectorSegment { Classes = new List<string>() };
            var tokens = TokenizeSelector(s);
            
            foreach (var t in tokens)
            {
                if (t == " " || t == ">" || t == "+" || t == "~") continue; // Skip combinators
                
                if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();
                    
                    // Check for operator 
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    foreach (var op in operators)
                    {
                         int idx = content.IndexOf(op);
                         if (idx >= 0) { foundOp=op; opIndex=idx; break; }
                    }

                    if (foundOp == null || opIndex < 0)
                    {
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        val = Regex.Replace(val, @"\\(.)", "$1");
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else if (t.StartsWith(":"))
                {
                     string val = t.Substring(1).ToLowerInvariant();
                     if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                     seg.PseudoClasses.Add(val);
                }
                else
                {
                    seg.Tag = t;
                }
            }
            return seg;
        }

        private static SelectorSegment ParseSegment(List<string> tokens)
        {
            var seg = new SelectorSegment();
            foreach (var t in tokens)
            {
                if (t == " " || t == ">" || t == "+" || t == "~") continue; // Skip combinators
                if (t == "*") { seg.Tag = "*"; continue; }
                if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing (improved for escapes)
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    foreach (var op in operators)
                    {
                        int idx = content.IndexOf(op);
                        if (idx >= 0)
                        {
                            foundOp = op;
                            opIndex = idx;
                            break;
                        }
                    }
                    if (foundOp == null || opIndex < 0)
                    {
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        // Unescape CSS escapes
                        val = Regex.Replace(val, @"\\(.)", "$1");
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else if (t.StartsWith(":"))
                {
                    // Pseudo-class or pseudo-element
                    string val = t.Substring(1).ToLowerInvariant();
                    if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                    seg.PseudoClasses.Add(val);
                }
                else
                {
                    seg.Tag = t;
                }
            }
            return seg;
        }

        /// <summary>
        /// Extract the argument from a pseudo-class like ":nth-child(2n+1)" -> "2n+1"
        /// </summary>
        private static string ExtractPseudoArg(string pseudoClass)
        {
            if (string.IsNullOrEmpty(pseudoClass)) return "";

            int start = pseudoClass.IndexOf('(');
            int end = pseudoClass.LastIndexOf(')');

            if (start >= 0 && end > start)
                return pseudoClass.Substring(start + 1, end - start - 1).Trim();

            return "";
        }
    }
}
