using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom;

namespace FenBrowser.FenEngine.HTML
{
    public class HtmlTreeBuilder
    {
        private enum InsertionMode
        {
            Initial,
            BeforeHtml,
            BeforeHead,
            InHead,
            AfterHead,
            InBody,
            AfterBody,
            AfterAfterBody
        }

        private readonly HtmlTokenizer _tokenizer;
        private Document _doc;
        private InsertionMode _mode;
        
        // "Stack of Open Elements"
        private readonly List<Element> _openElements = new List<Element>();
        
        // "List of Active Formatting Elements"
        private readonly List<Element> _formattingElements = new List<Element>();
        
        private Element _headParams; // Reference to <head>

        public HtmlTreeBuilder(HtmlTokenizer tokenizer)
        {
            _tokenizer = tokenizer;
            _mode = InsertionMode.Initial;
        }

        public Document Build()
        {
            _doc = new Document();
            _openElements.Clear();
            _formattingElements.Clear();
            _headParams = null;
            _mode = InsertionMode.Initial;
            
            var tokens = _tokenizer.Tokenize();
            foreach (var token in tokens)
            {
                ProcessToken(token);
            }
            
            return _doc;
        }

        private Element CurrentNode => _openElements.Count > 0 ? _openElements[_openElements.Count - 1] : null;

        private void ProcessToken(HtmlToken token)
        {
            switch (_mode)
            {
                case InsertionMode.Initial:
                    if (token is DoctypeToken)
                    {
                        // TODO: Set quirks mode based on public/system id
                        _mode = InsertionMode.BeforeHtml;
                    }
                    else if (IsWhitespace(token)) { /* Ignore */ }
                    else
                    {
                        _mode = InsertionMode.BeforeHtml;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.BeforeHtml:
                    if (token is StartTagToken startHtml && startHtml.TagName == "HTML")
                    {
                        InsertHtmlElement(startHtml);
                        _mode = InsertionMode.BeforeHead;
                    }
                    else if (IsWhitespace(token)) { /* Ignore */ }
                    else
                    {
                        // Implicit HTML
                        var implicitHtml = new StartTagToken { TagName = "HTML" };
                        InsertHtmlElement(implicitHtml);
                        _mode = InsertionMode.BeforeHead;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.BeforeHead:
                    if (IsWhitespace(token)) { /* Ignore */ }
                    else if (token is StartTagToken startHead && startHead.TagName == "HEAD")
                    {
                        _headParams = InsertHtmlElement(startHead);
                        _mode = InsertionMode.InHead;
                    }
                    else
                    {
                        var implicitHead = new StartTagToken { TagName = "HEAD" };
                        _headParams = InsertHtmlElement(implicitHead);
                        _mode = InsertionMode.InHead;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.InHead:
                    if (HandleInHead(token)) return;
                    break;

                case InsertionMode.AfterHead:
                    if (IsWhitespace(token)) 
                    {
                        InsertCharacter(token.As<CharacterToken>());
                    }
                    else if (token is StartTagToken startBody && startBody.TagName == "BODY")
                    {
                        InsertHtmlElement(startBody);
                         _mode = InsertionMode.InBody;
                    }
                    else
                    {
                        var implicitBody = new StartTagToken { TagName = "BODY" };
                        InsertHtmlElement(implicitBody);
                        _mode = InsertionMode.InBody;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.InBody:
                    HandleInBody(token);
                    break;
                    
                case InsertionMode.AfterBody:
                    if (IsWhitespace(token)) { /* Process main insertion? */ }
                    else if (token is EndTagToken endHtml && endHtml.TagName == "HTML")
                    {
                        _mode = InsertionMode.AfterAfterBody;
                    }
                    else
                    {
                        _mode = InsertionMode.InBody;
                        ProcessToken(token);
                    }
                    break;
            }
        }
        
        private bool HandleInHead(HtmlToken token)
        {
            if (IsWhitespace(token))
            {
                InsertCharacter(token.As<CharacterToken>());
                return true;
            }
            
            if (token is StartTagToken start)
            {
                if (start.TagName == "HEAD") return true; // Ignore
                if (start.TagName == "TITLE") { ParseGenericRawText(start); return true; }
                if (start.TagName == "STYLE" || start.TagName == "SCRIPT") { ParseGenericRawText(start); return true; }
                if (start.TagName == "META" || start.TagName == "LINK" || start.TagName == "BASE")
                {
                    InsertHtmlElement(start);
                    _openElements.RemoveAt(_openElements.Count - 1); // Pop immediately (void)
                    return true;
                }
            }
            else if (token is EndTagToken end)
            {
                if (end.TagName == "HEAD")
                {
                    _openElements.Remove(_headParams); // Pop head
                    _mode = InsertionMode.AfterHead;
                    return true;
                }
            }

            // Anything else -> Pop Head, reprocess
            if (CurrentNode.TagName == "HEAD") _openElements.RemoveAt(_openElements.Count - 1);
            _mode = InsertionMode.AfterHead;
            ProcessToken(token);
            return true;
        }

        private void HandleInBody(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                ReconstructActiveFormattingElements();
                InsertCharacter(ct);
            }
            else if (token is StartTagToken start)
            {
                if (IsFormattingTag(start.TagName))
                {
                    ReconstructActiveFormattingElements();
                    var el = InsertHtmlElement(start);
                    AddFormattingElement(el);
                }
                else if (IsVoid(start.TagName))
                {
                    ReconstructActiveFormattingElements();
                    InsertHtmlElement(start);
                    _openElements.RemoveAt(_openElements.Count - 1);
                }
                else
                {
                    ReconstructActiveFormattingElements();
                    InsertHtmlElement(start);
                }
            }
            else if (token is EndTagToken end)
            {
                if (IsFormattingTag(end.TagName))
                {
                    // Adoption Agency Algorithm (Simplified)
                     RunAdoptionAgency(end.TagName);
                }
                else if (end.TagName == "BODY")
                {
                    _mode = InsertionMode.AfterBody;
                }
                else if (end.TagName == "HTML")
                {
                     // Reprocess
                     _mode = InsertionMode.AfterBody;
                     ProcessToken(token);
                }
                else
                {
                    // Standard closing
                    GenerateImpliedEndTags(end.TagName);
                    PopStackUntil(end.TagName);
                }
            }
        }
        
        private void ParseGenericRawText(StartTagToken token)
        {
            InsertHtmlElement(token);
            // In a real parser, we switch tokenizer state to RCDATA or RAWTEXT
            // But here we rely on the specific tag end token to close it
        }

        private void RunAdoptionAgency(string tagName)
        {
             // 1. Find formatting element
             int fmtIndex = -1;
             for(int i=_formattingElements.Count-1; i>=0; i--) {
                 if (_formattingElements[i].TagName == tagName) { fmtIndex = i; break; }
             }
             if (fmtIndex == -1) {
                 // Not in formatting list? Just pop if open.
                 PopStackUntil(tagName);
                 return;
             }
             
             // For MVP: Simple pop. Full AAA is complex (bookmarks, reparenting).
             // Implementing FULL AAA is risky without extensive tests.
             // Fallback: Close up to formatted element, reopen others?
             
             // Simplest Approximation:
             // Close open elements until we hit tagName.
             // If we hit non-matches, we technically "mis-nested". 
             // Browsers would reopen them. 
             // We will implement closing, then REOPENING the mismatched ones.
             
             var popped = new List<Element>();
             while(_openElements.Count > 0)
             {
                 var current = _openElements.Last();
                 _openElements.RemoveAt(_openElements.Count - 1);
                 
                 if (current.TagName == tagName)
                 {
                     // Found it. Stop popping.
                     // Remove from formatting list
                     _formattingElements.Remove(current);
                     break;
                 }
                 else
                 {
                     popped.Add(current);
                 }
             }
             
             // Reopen disjoint
             foreach(var p in ((IEnumerable<Element>)popped).Reverse())
             {
                 if (!IsVoid(p.TagName) && !IsSpecial(p.TagName))
                 {
                     var restart = new Element(p.TagName);
                     // Copy attributes?
                     CurrentNode.AppendChild(restart);
                     _openElements.Add(restart);
                 }
             }
        }
        
        private void ReconstructActiveFormattingElements()
        {
            if (_formattingElements.Count == 0) return;
            
            // Check if last one is open
            var last = _formattingElements.Last();
            if (_openElements.Contains(last)) return;
            
            // Reopen if missing from stack but present in formatting list?
        }
        
        private void AddFormattingElement(Element el)
        {
            _formattingElements.Add(el);
        }

        private void GenerateImpliedEndTags(string exception = null)
        {
            while (_openElements.Count > 0)
            {
                var tag = CurrentNode.TagName;
                if (tag != exception && (tag == "DD" || tag == "DT" || tag == "LI" || tag == "P")) // etc
                {
                    _openElements.RemoveAt(_openElements.Count - 1);
                }
                else return;
            }
        }

        private Element InsertHtmlElement(StartTagToken token)
        {
            var el = new Element(token.TagName);
            foreach (var kv in token.Attributes)
            {
                el.SetAttribute(kv.Key, kv.Value);
            }
            if (CurrentNode != null) CurrentNode.AppendChild(el);
            else _doc.AppendChild(el);
            
            _openElements.Add(el);
            return el;
        }

        private void InsertCharacter(CharacterToken token)
        {
            if (CurrentNode == null) return;
            if (CurrentNode.LastChild is Text textNode)
            {
                textNode.Data += token.Data.ToString();
            }
            else
            {
                var t = new Text(token.Data.ToString());
                CurrentNode.AppendChild(t);
            }
        }

        private void PopStackUntil(string tagName)
        {
             for (int i = _openElements.Count - 1; i >= 0; i--)
            {
                var el = _openElements[i];
                _openElements.RemoveAt(i);
                if (el.TagName == tagName) break;
            }
        }

        private bool IsWhitespace(HtmlToken token)
        {
             return token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data.ToString());
        }

        private bool IsVoid(string tag)
        {
             return tag == "AREA" || tag == "BASE" || tag == "BR" || tag == "COL" || tag == "EMBED" || tag == "HR" || tag == "IMG" || tag == "INPUT" || tag == "LINK" || tag == "META" || tag == "PARAM" || tag == "SOURCE" || tag == "TRACK" || tag == "WBR";
        }
        
        private bool IsFormattingTag(string tag)
        {
            return tag == "B" || tag == "I" || tag == "U" || tag == "EM" || tag == "STRONG" || tag == "SMALL" || tag == "CODE" || tag == "FONT";
        }
        
        private bool IsSpecial(string tag) { return tag == "ADDRESS" || tag == "APPLET" || tag == "AREA" || tag == "ARTICLE" || tag == "ASIDE" || tag == "BASE" || tag == "BASEFONT" || tag == "BGSOUND" || tag == "BLOCKQUOTE" || tag == "BODY" || tag == "BR" || tag == "BUTTON" || tag == "CAPTION" || tag == "CENTER" || tag == "COL" || tag == "COLGROUP" || tag == "DD" || tag == "DETAILS" || tag == "DIR" || tag == "DIV" || tag == "DL" || tag == "DT" || tag == "EMBED" || tag == "FIELDSET" || tag == "FIGCAPTION" || tag == "FIGURE" || tag == "FOOTER" || tag == "FORM" || tag == "FRAME" || tag == "FRAMESET" || tag == "H1" || tag == "H2" || tag == "H3" || tag == "H4" || tag == "H5" || tag == "H6" || tag == "HEAD" || tag == "HEADER" || tag == "HGROUP" || tag == "HR" || tag == "HTML" || tag == "IFRAME" || tag == "IMG" || tag == "INPUT" || tag == "ISINDEX" || tag == "LI" || tag == "LINK" || tag == "LISTING" || tag == "MAIN" || tag == "MARQUEE" || tag == "MENU" || tag == "META" || tag == "NAV" || tag == "NOEMBED" || tag == "NOFRAMES" || tag == "NOSCRIPT" || tag == "OBJECT" || tag == "OL" || tag == "P" || tag == "PARAM" || tag == "PLAINTEXT" || tag == "PRE" || tag == "SCRIPT" || tag == "SECTION" || tag == "SELECT" || tag == "SOURCE" || tag == "STYLE" || tag == "SUMMARY" || tag == "TABLE" || tag == "TBODY" || tag == "TD" || tag == "TEMPLATE" || tag == "TEXTAREA" || tag == "TFOOT" || tag == "TH" || tag == "THEAD" || tag == "TITLE" || tag == "TR" || tag == "TRACK" || tag == "UL" || tag == "WBR" || tag == "XMP"; }
    }
}
