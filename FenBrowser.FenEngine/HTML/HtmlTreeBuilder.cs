using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;

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
            InHeadNoscript,
            AfterHead,
            InBody,
            Text,
            InTable,
            InTableText,
            InCaption,
            InColumnGroup,
            InTableBody,
            InRow,
            InCell,
            InSelect,
            InSelectInTable,
            InTemplate,
            AfterBody,
            InFrameset,
            AfterFrameset,
            AfterAfterBody,
            AfterAfterFrameset
        }

        private readonly HtmlTokenizer _tokenizer;
        private Document _doc;
        private InsertionMode _mode;
        private InsertionMode _originalMode; // For Text mode and InTableText mode
        
        // "Stack of Open Elements"
        private readonly List<Element> _openElements = new List<Element>();
        
        // "List of Active Formatting Elements"
        private readonly List<Element> _formattingElements = new List<Element>();
        
        // "Stack of Template Insertion Modes" (WHATWG 13.2.4.1)
        private readonly Stack<InsertionMode> _templateModes = new Stack<InsertionMode>();
        
        // Pending table character tokens (InTableText mode)
        private readonly List<CharacterToken> _pendingTableChars = new List<CharacterToken>();
        
        private Element _headElement; // Reference to <head>
        private Element _formElement; // Reference to current <form>
        private bool _framesetOk = true; // Frameset-ok flag

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
            _headElement = null;
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
            // Foreign content check per WHATWG 13.2.6.5
            if (ShouldProcessAsForeignContent())
            {
                ProcessForeignContent(token);
                return;
            }
            
            switch (_mode)
            {
                case InsertionMode.Initial:
                    if (token is DoctypeToken)
                    {
                        var doctype = token.As<DoctypeToken>();
                        _doc.Mode = DetermineQuirksMode(doctype);
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
                        _headElement = InsertHtmlElement(startHead);
                        _mode = InsertionMode.InHead;
                    }
                    else
                    {
                        var implicitHead = new StartTagToken { TagName = "HEAD" };
                        _headElement = InsertHtmlElement(implicitHead);
                        _mode = InsertionMode.InHead;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.InHead:
                    if (HandleInHead(token)) return;
                    break;
                    
                case InsertionMode.InHeadNoscript:
                    HandleInHeadNoscript(token);
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
                         _framesetOk = true;
                    }
                    else if (token is StartTagToken startFrameset && startFrameset.TagName == "FRAMESET")
                    {
                        InsertHtmlElement(startFrameset);
                        _mode = InsertionMode.InFrameset;
                    }
                    else
                    {
                        var implicitBody = new StartTagToken { TagName = "BODY" };
                        InsertHtmlElement(implicitBody);
                        _mode = InsertionMode.InBody;
                        _framesetOk = true;
                        ProcessToken(token);
                    }
                    break;

                case InsertionMode.InBody:
                    HandleInBody(token);
                    break;
                    
                case InsertionMode.Text:
                    HandleTextMode(token);
                    break;
                    
                case InsertionMode.InTable:
                    HandleInTable(token);
                    break;
                    
                case InsertionMode.InTableText:
                    HandleInTableText(token);
                    break;
                    
                case InsertionMode.InCaption:
                    HandleInCaption(token);
                    break;
                    
                case InsertionMode.InColumnGroup:
                    HandleInColumnGroup(token);
                    break;
                    
                case InsertionMode.InTableBody:
                    HandleInTableBody(token);
                    break;
                    
                case InsertionMode.InRow:
                    HandleInRow(token);
                    break;
                    
                case InsertionMode.InCell:
                    HandleInCell(token);
                    break;
                    
                case InsertionMode.InSelect:
                    HandleInSelect(token);
                    break;
                    
                case InsertionMode.InSelectInTable:
                    HandleInSelectInTable(token);
                    break;
                    
                case InsertionMode.InTemplate:
                    HandleInTemplate(token);
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
                    
                case InsertionMode.InFrameset:
                    HandleInFrameset(token);
                    break;
                    
                case InsertionMode.AfterFrameset:
                    HandleAfterFrameset(token);
                    break;
                    
                case InsertionMode.AfterAfterBody:
                    if (IsWhitespace(token)) { /* Ignore */ }
                    else if (token is EofToken) { /* Stop */ }
                    else
                    {
                        _mode = InsertionMode.InBody;
                        ProcessToken(token);
                    }
                    break;
                    
                case InsertionMode.AfterAfterFrameset:
                    if (IsWhitespace(token)) { /* Ignore */ }
                    else if (token is EofToken) { /* Stop */ }
                    break;
            }
        }

        private static QuirksMode DetermineQuirksMode(DoctypeToken dt)
        {
            if (dt == null) return QuirksMode.Quirks;
            if (dt.ForceQuirks) return QuirksMode.Quirks;

            var name = (dt.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(name, "html", StringComparison.Ordinal))
                return QuirksMode.Quirks;

            var publicId = (dt.PublicIdentifier ?? string.Empty).Trim().ToLowerInvariant();
            var systemId = (dt.SystemIdentifier ?? string.Empty).Trim().ToLowerInvariant();

            // WHATWG quirks triggers (condensed but compatible set).
            if (publicId.StartsWith("-//w3o//dtd w3 html 3.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 4.0 transitional//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 4.0 frameset//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3.2", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer", StringComparison.Ordinal) ||
                publicId.StartsWith("-//netscape comm. corp.//dtd", StringComparison.Ordinal) ||
                publicId.StartsWith("-//webtechs//dtd mozilla html", StringComparison.Ordinal))
            {
                return QuirksMode.Quirks;
            }

            if ((publicId.StartsWith("-//w3c//dtd xhtml 1.0 transitional//", StringComparison.Ordinal) ||
                 publicId.StartsWith("-//w3c//dtd xhtml 1.0 frameset//", StringComparison.Ordinal)) ||
                ((publicId.StartsWith("-//w3c//dtd html 4.01 transitional//", StringComparison.Ordinal) ||
                  publicId.StartsWith("-//w3c//dtd html 4.01 frameset//", StringComparison.Ordinal)) &&
                 string.IsNullOrEmpty(systemId)))
            {
                return QuirksMode.LimitedQuirks;
            }

            return QuirksMode.NoQuirks;
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
                    _openElements.Remove(_headElement); // Pop head
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
                // WHATWG 13.2.6.4.7: SVG and MATH elements enter foreign content
                else if (start.TagName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    ReconstructActiveFormattingElements();
                    var attrs = GetAttributesDictionary(start);
                    var element = ForeignContent.CreateForeignElement("svg", ForeignContent.SvgNamespace, attrs);
                    InsertElement(element);
                    if (start.SelfClosing) _openElements.RemoveAt(_openElements.Count - 1);
                }
                else if (start.TagName.Equals("math", StringComparison.OrdinalIgnoreCase))
                {
                    ReconstructActiveFormattingElements();
                    var attrs = GetAttributesDictionary(start);
                    var element = ForeignContent.CreateForeignElement("math", ForeignContent.MathMLNamespace, attrs);
                    InsertElement(element);
                    if (start.SelfClosing) _openElements.RemoveAt(_openElements.Count - 1);
                }
                // WHATWG 13.2.6.4.10: TR handling - insert implied TBODY if needed
                else if (start.TagName.Equals("TR", StringComparison.OrdinalIgnoreCase))
                {
                    // If current node is TABLE, insert implied TBODY
                    if (CurrentNode?.TagName == "TABLE")
                    {
                        var tbody = new Element("TBODY", _doc);
                        CurrentNode.AppendChild(tbody);
                        _openElements.Add(tbody);
                    }
                    // Now insert the TR
                    InsertHtmlElement(start);
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
            _tokenizer.SetState("RAWTEXT");
            _tokenizer.SetLastStartTagName(token.TagName);
            _originalMode = _mode;
            _mode = InsertionMode.Text;
        }

        /// <summary>
        /// Full Adoption Agency Algorithm per WHATWG 13.2.6.4.7
        /// This is one of the most complex parts of the HTML5 parsing spec.
        /// Handles misnested formatting elements like: <b><i></b></i>
        /// </summary>
        private void RunAdoptionAgency(string tagName)
        {
            // Step 1: Let outer loop counter be 0
            int outerLoopCounter = 0;
            
            // Step 2: Outer loop - repeat up to 8 times
            while (outerLoopCounter < 8)
            {
                outerLoopCounter++;
                
                // Step 3: Find the formatting element - last entry with tag name
                int formattingElementIndex = -1;
                Element formattingElement = null;
                for (int i = _formattingElements.Count - 1; i >= 0; i--)
                {
                    var entry = _formattingElements[i];
                    if (entry == null) break; // Hit a marker
                    if (entry.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        formattingElementIndex = i;
                        formattingElement = entry;
                        break;
                    }
                }
                
                // Step 4: If no formatting element, process as "any other end tag"
                if (formattingElement == null)
                {
                    ProcessAnyOtherEndTag(tagName);
                    return;
                }
                
                // Step 5: If formatting element not in stack of open elements
                int stackIndex = _openElements.IndexOf(formattingElement);
                if (stackIndex < 0)
                {
                    // Parse error - remove from formatting list
                    _formattingElements.RemoveAt(formattingElementIndex);
                    return;
                }
                
                // Step 6: Check if formatting element is in scope
                if (!HasElementInScope(tagName))
                {
                    // Parse error - ignore token
                    return;
                }
                
                // Step 7: If formatting element is not the current node, parse error
                // (we continue anyway)
                
                // Step 8: Find the furthest block
                Element furthestBlock = null;
                int furthestBlockIndex = -1;
                for (int i = stackIndex + 1; i < _openElements.Count; i++)
                {
                    if (IsSpecial(_openElements[i].TagName))
                    {
                        furthestBlock = _openElements[i];
                        furthestBlockIndex = i;
                        break;
                    }
                }
                
                // Step 9: If no furthest block, pop elements and remove formatting element
                if (furthestBlock == null)
                {
                    while (_openElements.Count > stackIndex)
                    {
                        _openElements.RemoveAt(_openElements.Count - 1);
                    }
                    _formattingElements.Remove(formattingElement);
                    return;
                }
                
                // Step 10: Common ancestor is element immediately above formatting element
                Element commonAncestor = stackIndex > 0 ? _openElements[stackIndex - 1] : null;
                
                // Step 11: Bookmark - position in formatting list
                int bookmark = formattingElementIndex;
                
                // Step 12: Let node and last node be furthest block
                Element node = furthestBlock;
                Element lastNode = furthestBlock;
                int nodeIndex = furthestBlockIndex;
                
                // Step 13: Inner loop counter
                int innerLoopCounter = 0;
                
                // Step 14: Inner loop
                while (true)
                {
                    innerLoopCounter++;
                    
                    // Step 14.1: Move node to previous entry in stack
                    nodeIndex--;
                    if (nodeIndex < 0) break;
                    node = _openElements[nodeIndex];
                    
                    // Step 14.2: If node is formatting element, break
                    if (node == formattingElement) break;
                    
                    // Step 14.3: If inner loop counter > 3 and node is in formatting list, remove it
                    int nodeFormattingIndex = _formattingElements.IndexOf(node);
                    if (innerLoopCounter > 3 && nodeFormattingIndex >= 0)
                    {
                        _formattingElements.RemoveAt(nodeFormattingIndex);
                        if (nodeFormattingIndex < bookmark) bookmark--;
                        continue;
                    }
                    
                    // Step 14.4: If node is not in formatting list, remove from stack and continue
                    if (nodeFormattingIndex < 0)
                    {
                        _openElements.RemoveAt(nodeIndex);
                        continue;
                    }
                    
                    // Step 14.5: Create new element for token, replace node
                    var newElement = new Element(node.TagName, _doc);
                    foreach (var attr in node.Attributes)
                    {
                        newElement.SetAttributeUnsafe(attr.Name, attr.Value);
                    }
                    
                    // Replace in formatting list
                    _formattingElements[nodeFormattingIndex] = newElement;
                    
                    // Replace in open elements stack
                    _openElements[nodeIndex] = newElement;
                    
                    // Update node reference
                    node = newElement;
                    
                    // Step 14.6: If last node is furthest block, move bookmark
                    if (lastNode == furthestBlock)
                    {
                        bookmark = nodeFormattingIndex + 1;
                    }
                    
                    // Step 14.7: Append last node to node
                    node.AppendChild(lastNode);
                    
                    // Step 14.8: Set last node to node
                    lastNode = node;
                }
                
                // Step 15: Insert last node at appropriate place
                InsertAtAppropriatePlace(lastNode, commonAncestor);
                
                // Step 16: Create new element for formatting element
                var newFormattingElement = new Element(formattingElement.TagName, _doc);
                foreach (var attr in formattingElement.Attributes)
                {
                    newFormattingElement.SetAttributeUnsafe(attr.Name, attr.Value);
                }
                
                // Step 17: Move all children of furthest block to new element
                var children = furthestBlock.Children.ToList();
                foreach (var child in children)
                {
                    newFormattingElement.AppendChild(child);
                }
                
                // Step 18: Append new element to furthest block
                furthestBlock.AppendChild(newFormattingElement);
                
                // Step 19: Remove formatting element from formatting list, insert new element at bookmark
                _formattingElements.Remove(formattingElement);
                if (bookmark > _formattingElements.Count) bookmark = _formattingElements.Count;
                _formattingElements.Insert(bookmark, newFormattingElement);
                
                // Step 20: Remove formatting element from stack, insert after furthest block
                _openElements.Remove(formattingElement);
                int fbStackIndex = _openElements.IndexOf(furthestBlock);
                if (fbStackIndex >= 0)
                {
                    _openElements.Insert(fbStackIndex + 1, newFormattingElement);
                }
                else
                {
                    _openElements.Add(newFormattingElement);
                }
            }
        }
        
        /// <summary>
        /// Process end tag using "any other end tag" rules.
        /// </summary>
        private void ProcessAnyOtherEndTag(string tagName)
        {
            for (int i = _openElements.Count - 1; i >= 0; i--)
            {
                var node = _openElements[i];
                if (node.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                {
                    GenerateImpliedEndTags(tagName);
                    while (_openElements.Count > i)
                    {
                        _openElements.RemoveAt(_openElements.Count - 1);
                    }
                    return;
                }
                if (IsSpecial(node.TagName))
                {
                    // Parse error - ignore
                    return;
                }
            }
        }
        
        /// <summary>
        /// Insert node at the appropriate place (for AAA).
        /// </summary>
        private void InsertAtAppropriatePlace(Node node, Element overrideTarget)
        {
            Element target = overrideTarget ?? CurrentNode;
            if (target != null)
            {
                target.AppendChild(node);
            }
            else
            {
                _doc.AppendChild(node);
            }
        }
        
        /// <summary>
        /// Reconstruct the active formatting elements per WHATWG 13.2.4.3
        /// </summary>
        private void ReconstructActiveFormattingElements()
        {
            // Step 1: If list is empty, return
            if (_formattingElements.Count == 0) return;
            
            // Step 2: If last entry is a marker or in stack, return
            var last = _formattingElements[_formattingElements.Count - 1];
            if (last == null || _openElements.Contains(last)) return;
            
            // Step 3: Let entry be last entry
            int entryIndex = _formattingElements.Count - 1;
            
            // Step 4: Rewind - go back until we find one in stack or hit marker/start
            Rewind:
            if (entryIndex == 0) goto Create;
            entryIndex--;
            var entry = _formattingElements[entryIndex];
            if (entry != null && !_openElements.Contains(entry)) goto Rewind;
            
            // Step 5: Advance
            Advance:
            entryIndex++;
            
            // Step 6: Create element for entry
            Create:
            entry = _formattingElements[entryIndex];
            if (entry == null)
            {
                // Skip markers
                if (entryIndex < _formattingElements.Count - 1) goto Advance;
                return;
            }
            
            var newElement = new Element(entry.TagName, _doc);
            foreach (var attr in entry.Attributes)
            {
                newElement.SetAttributeUnsafe(attr.Name, attr.Value);
            }
            
            // Step 7: Append new element to current node, push to stack
            CurrentNode?.AppendChild(newElement);
            _openElements.Add(newElement);
            
            // Step 8: Replace entry in formatting list
            _formattingElements[entryIndex] = newElement;
            
            // Step 9: If not last entry, advance
            if (entryIndex < _formattingElements.Count - 1) goto Advance;
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
            var el = new Element(token.TagName, _doc);
            foreach (var kv in token.Attributes)
            {
                el.SetAttributeUnsafe(kv.Key, kv.Value);
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
                var t = new Text(token.Data.ToString(), _doc);
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
            return tag == "B" || tag == "I" || tag == "U" || tag == "EM" || tag == "STRONG" || tag == "SMALL" || tag == "CODE" || tag == "FONT" || tag == "A" || tag == "BIG" || tag == "NOBR" || tag == "S" || tag == "STRIKE" || tag == "TT";
        }
        
        private bool IsSpecial(string tag) { return tag == "ADDRESS" || tag == "APPLET" || tag == "AREA" || tag == "ARTICLE" || tag == "ASIDE" || tag == "BASE" || tag == "BASEFONT" || tag == "BGSOUND" || tag == "BLOCKQUOTE" || tag == "BODY" || tag == "BR" || tag == "BUTTON" || tag == "CAPTION" || tag == "CENTER" || tag == "COL" || tag == "COLGROUP" || tag == "DD" || tag == "DETAILS" || tag == "DIR" || tag == "DIV" || tag == "DL" || tag == "DT" || tag == "EMBED" || tag == "FIELDSET" || tag == "FIGCAPTION" || tag == "FIGURE" || tag == "FOOTER" || tag == "FORM" || tag == "FRAME" || tag == "FRAMESET" || tag == "H1" || tag == "H2" || tag == "H3" || tag == "H4" || tag == "H5" || tag == "H6" || tag == "HEAD" || tag == "HEADER" || tag == "HGROUP" || tag == "HR" || tag == "HTML" || tag == "IFRAME" || tag == "IMG" || tag == "INPUT" || tag == "ISINDEX" || tag == "LI" || tag == "LINK" || tag == "LISTING" || tag == "MAIN" || tag == "MARQUEE" || tag == "MENU" || tag == "META" || tag == "NAV" || tag == "NOEMBED" || tag == "NOFRAMES" || tag == "NOSCRIPT" || tag == "OBJECT" || tag == "OL" || tag == "P" || tag == "PARAM" || tag == "PLAINTEXT" || tag == "PRE" || tag == "SCRIPT" || tag == "SECTION" || tag == "SELECT" || tag == "SOURCE" || tag == "STYLE" || tag == "SUMMARY" || tag == "TABLE" || tag == "TBODY" || tag == "TD" || tag == "TEMPLATE" || tag == "TEXTAREA" || tag == "TFOOT" || tag == "TH" || tag == "THEAD" || tag == "TITLE" || tag == "TR" || tag == "TRACK" || tag == "UL" || tag == "WBR" || tag == "XMP"; }
        
        private bool IsTableScope(string tag)
        {
            return tag == "HTML" || tag == "TABLE" || tag == "TEMPLATE";
        }
        
        private bool HasElementInTableScope(string tagName)
        {
            for (int i = _openElements.Count - 1; i >= 0; i--)
            {
                var tag = _openElements[i].TagName;
                if (tag == tagName) return true;
                if (IsTableScope(tag)) return false;
            }
            return false;
        }
        
        private bool HasElementInScope(string tagName)
        {
            for (int i = _openElements.Count - 1; i >= 0; i--)
            {
                var tag = _openElements[i].TagName;
                if (tag == tagName) return true;
                if (IsSpecial(tag) && tag != tagName) return false;
            }
            return false;
        }
        
        // --- InHeadNoscript Mode ---
        private void HandleInHeadNoscript(HtmlToken token)
        {
            if (IsWhitespace(token))
            {
                InsertCharacter(token.As<CharacterToken>());
            }
            else if (token is EndTagToken endNoscript && endNoscript.TagName == "NOSCRIPT")
            {
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InHead;
            }
            else
            {
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InHead;
                ProcessToken(token);
            }
        }
        
        // --- Text Mode (for script/style content) ---
        private void HandleTextMode(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
            }
            else if (token is EndTagToken end)
            {
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = _originalMode;
            }
            else if (token is EofToken)
            {
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = _originalMode;
                ProcessToken(token);
            }
        }
        
        // --- InTable Mode ---
        private void HandleInTable(HtmlToken token)
        {
            if (token is CharacterToken ct && IsTableElement(CurrentNode?.TagName))
            {
                _pendingTableChars.Clear();
                _originalMode = _mode;
                _mode = InsertionMode.InTableText;
                ProcessToken(token);
                return;
            }
            
            if (token is StartTagToken start)
            {
                switch (start.TagName)
                {
                    case "CAPTION":
                        ClearStackBackToTableContext();
                        InsertHtmlElement(start);
                        _mode = InsertionMode.InCaption;
                        return;
                    case "COLGROUP":
                        ClearStackBackToTableContext();
                        InsertHtmlElement(start);
                        _mode = InsertionMode.InColumnGroup;
                        return;
                    case "COL":
                        ClearStackBackToTableContext();
                        InsertHtmlElement(new StartTagToken { TagName = "COLGROUP" });
                        _mode = InsertionMode.InColumnGroup;
                        ProcessToken(token);
                        return;
                    case "TBODY":
                    case "TFOOT":
                    case "THEAD":
                        ClearStackBackToTableContext();
                        InsertHtmlElement(start);
                        _mode = InsertionMode.InTableBody;
                        return;
                    case "TD":
                    case "TH":
                    case "TR":
                        ClearStackBackToTableContext();
                        InsertHtmlElement(new StartTagToken { TagName = "TBODY" });
                        _mode = InsertionMode.InTableBody;
                        ProcessToken(token);
                        return;
                    case "TABLE":
                        if (HasElementInTableScope("TABLE"))
                        {
                            PopStackUntil("TABLE");
                            ResetInsertionModeAppropriately();
                            ProcessToken(token);
                        }
                        return;
                    case "STYLE":
                    case "SCRIPT":
                    case "TEMPLATE":
                        HandleInHead(token);
                        return;
                    case "FORM":
                        if (_formElement != null || HasElementInScope("TEMPLATE")) return;
                        _formElement = InsertHtmlElement(start);
                        _openElements.Remove(_formElement);
                        return;
                }
            }
            else if (token is EndTagToken end)
            {
                switch (end.TagName)
                {
                    case "TABLE":
                        if (!HasElementInTableScope("TABLE")) return;
                        PopStackUntil("TABLE");
                        ResetInsertionModeAppropriately();
                        return;
                    case "TEMPLATE":
                        HandleInHead(token);
                        return;
                }
            }
            
            // Foster parenting fallback
            HandleInBody(token);
        }
        
        private bool IsTableElement(string tag)
        {
            return tag == "TABLE" || tag == "TBODY" || tag == "TFOOT" || tag == "THEAD" || tag == "TR";
        }
        
        private void ClearStackBackToTableContext()
        {
            while (_openElements.Count > 0)
            {
                var tag = CurrentNode.TagName;
                if (tag == "TABLE" || tag == "TEMPLATE" || tag == "HTML") break;
                _openElements.RemoveAt(_openElements.Count - 1);
            }
        }
        
        // --- InTableText Mode ---
        private void HandleInTableText(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                _pendingTableChars.Add(ct);
            }
            else
            {
                bool hasNonWhitespace = _pendingTableChars.Any(c => !string.IsNullOrWhiteSpace(c.Data.ToString()));
                if (hasNonWhitespace)
                {
                    // Foster parent the characters
                    foreach (var c in _pendingTableChars)
                        HandleInBody(c);
                }
                else
                {
                    foreach (var c in _pendingTableChars)
                        InsertCharacter(c);
                }
                _pendingTableChars.Clear();
                _mode = _originalMode;
                ProcessToken(token);
            }
        }
        
        // --- InCaption Mode ---
        private void HandleInCaption(HtmlToken token)
        {
            if (token is EndTagToken end && end.TagName == "CAPTION")
            {
                if (!HasElementInTableScope("CAPTION")) return;
                GenerateImpliedEndTags();
                PopStackUntil("CAPTION");
                ClearFormattingToMarker();
                _mode = InsertionMode.InTable;
            }
            else if ((token is StartTagToken st && (st.TagName == "CAPTION" || st.TagName == "COL" || st.TagName == "COLGROUP" || 
                     st.TagName == "TBODY" || st.TagName == "TD" || st.TagName == "TFOOT" || st.TagName == "TH" || 
                     st.TagName == "THEAD" || st.TagName == "TR")) ||
                     (token is EndTagToken et && et.TagName == "TABLE"))
            {
                if (!HasElementInTableScope("CAPTION")) return;
                GenerateImpliedEndTags();
                PopStackUntil("CAPTION");
                ClearFormattingToMarker();
                _mode = InsertionMode.InTable;
                ProcessToken(token);
            }
            else
            {
                HandleInBody(token);
            }
        }
        
        private void ClearFormattingToMarker()
        {
            // Remove elements from formatting list until marker or empty
            while (_formattingElements.Count > 0)
            {
                var last = _formattingElements[_formattingElements.Count - 1];
                _formattingElements.RemoveAt(_formattingElements.Count - 1);
                if (last == null) break; // Marker
            }
        }
        
        // --- InColumnGroup Mode ---
        private void HandleInColumnGroup(HtmlToken token)
        {
            if (IsWhitespace(token))
            {
                InsertCharacter(token.As<CharacterToken>());
            }
            else if (token is StartTagToken start && start.TagName == "COL")
            {
                InsertHtmlElement(start);
                _openElements.RemoveAt(_openElements.Count - 1);
            }
            else if (token is EndTagToken end && end.TagName == "COLGROUP")
            {
                if (CurrentNode?.TagName != "COLGROUP") return;
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTable;
            }
            else if (token is EndTagToken endCol && endCol.TagName == "COL")
            {
                return; // Ignore
            }
            else
            {
                if (CurrentNode?.TagName != "COLGROUP") return;
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTable;
                ProcessToken(token);
            }
        }
        
        // --- InTableBody Mode ---
        private void HandleInTableBody(HtmlToken token)
        {
            if (token is StartTagToken start && start.TagName == "TR")
            {
                ClearStackBackToTableBodyContext();
                InsertHtmlElement(start);
                _mode = InsertionMode.InRow;
            }
            else if (token is StartTagToken startCell && (startCell.TagName == "TD" || startCell.TagName == "TH"))
            {
                ClearStackBackToTableBodyContext();
                InsertHtmlElement(new StartTagToken { TagName = "TR" });
                _mode = InsertionMode.InRow;
                ProcessToken(token);
            }
            else if (token is EndTagToken end && (end.TagName == "TBODY" || end.TagName == "TFOOT" || end.TagName == "THEAD"))
            {
                if (!HasElementInTableScope(end.TagName)) return;
                ClearStackBackToTableBodyContext();
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTable;
            }
            else if ((token is StartTagToken stTable && (stTable.TagName == "CAPTION" || stTable.TagName == "COL" || 
                     stTable.TagName == "COLGROUP" || stTable.TagName == "TBODY" || stTable.TagName == "TFOOT" || stTable.TagName == "THEAD")) ||
                     (token is EndTagToken etTable && etTable.TagName == "TABLE"))
            {
                if (!HasElementInTableScope("TBODY") && !HasElementInTableScope("THEAD") && !HasElementInTableScope("TFOOT")) return;
                ClearStackBackToTableBodyContext();
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTable;
                ProcessToken(token);
            }
            else
            {
                HandleInTable(token);
            }
        }
        
        private void ClearStackBackToTableBodyContext()
        {
            while (_openElements.Count > 0)
            {
                var tag = CurrentNode.TagName;
                if (tag == "TBODY" || tag == "TFOOT" || tag == "THEAD" || tag == "TEMPLATE" || tag == "HTML") break;
                _openElements.RemoveAt(_openElements.Count - 1);
            }
        }
        
        // --- InRow Mode ---
        private void HandleInRow(HtmlToken token)
        {
            if (token is StartTagToken start && (start.TagName == "TD" || start.TagName == "TH"))
            {
                ClearStackBackToTableRowContext();
                InsertHtmlElement(start);
                _mode = InsertionMode.InCell;
                // Insert marker in formatting elements
                _formattingElements.Add(null);
            }
            else if (token is EndTagToken end && end.TagName == "TR")
            {
                if (!HasElementInTableScope("TR")) return;
                ClearStackBackToTableRowContext();
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTableBody;
            }
            else if ((token is StartTagToken stRow && (stRow.TagName == "CAPTION" || stRow.TagName == "COL" || 
                     stRow.TagName == "COLGROUP" || stRow.TagName == "TBODY" || stRow.TagName == "TFOOT" || 
                     stRow.TagName == "THEAD" || stRow.TagName == "TR")) ||
                     (token is EndTagToken etRow && etRow.TagName == "TABLE"))
            {
                if (!HasElementInTableScope("TR")) return;
                ClearStackBackToTableRowContext();
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTableBody;
                ProcessToken(token);
            }
            else if (token is EndTagToken endBody && (endBody.TagName == "TBODY" || endBody.TagName == "TFOOT" || endBody.TagName == "THEAD"))
            {
                if (!HasElementInTableScope(endBody.TagName)) return;
                if (!HasElementInTableScope("TR")) return;
                ClearStackBackToTableRowContext();
                _openElements.RemoveAt(_openElements.Count - 1);
                _mode = InsertionMode.InTableBody;
                ProcessToken(token);
            }
            else
            {
                HandleInTable(token);
            }
        }
        
        private void ClearStackBackToTableRowContext()
        {
            while (_openElements.Count > 0)
            {
                var tag = CurrentNode.TagName;
                if (tag == "TR" || tag == "TEMPLATE" || tag == "HTML") break;
                _openElements.RemoveAt(_openElements.Count - 1);
            }
        }
        
        // --- InCell Mode ---
        private void HandleInCell(HtmlToken token)
        {
            if (token is EndTagToken end && (end.TagName == "TD" || end.TagName == "TH"))
            {
                if (!HasElementInTableScope(end.TagName)) return;
                GenerateImpliedEndTags();
                PopStackUntil(end.TagName);
                ClearFormattingToMarker();
                _mode = InsertionMode.InRow;
            }
            else if ((token is StartTagToken st && (st.TagName == "CAPTION" || st.TagName == "COL" || 
                     st.TagName == "COLGROUP" || st.TagName == "TBODY" || st.TagName == "TD" || 
                     st.TagName == "TFOOT" || st.TagName == "TH" || st.TagName == "THEAD" || st.TagName == "TR")))
            {
                if (!HasElementInTableScope("TD") && !HasElementInTableScope("TH")) return;
                CloseCell();
                ProcessToken(token);
            }
            else if (token is EndTagToken endTable && (endTable.TagName == "TBODY" || endTable.TagName == "TFOOT" || 
                     endTable.TagName == "THEAD" || endTable.TagName == "TR" || endTable.TagName == "TABLE"))
            {
                if (!HasElementInTableScope(endTable.TagName)) return;
                CloseCell();
                ProcessToken(token);
            }
            else
            {
                HandleInBody(token);
            }
        }
        
        private void CloseCell()
        {
            GenerateImpliedEndTags();
            if (CurrentNode.TagName == "TD" || CurrentNode.TagName == "TH")
            {
                PopStackUntil(CurrentNode.TagName);
            }
            ClearFormattingToMarker();
            _mode = InsertionMode.InRow;
        }
        
        // --- InSelect Mode ---
        private void HandleInSelect(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
            }
            else if (token is StartTagToken start)
            {
                switch (start.TagName)
                {
                    case "OPTION":
                        if (CurrentNode?.TagName == "OPTION")
                            _openElements.RemoveAt(_openElements.Count - 1);
                        InsertHtmlElement(start);
                        break;
                    case "OPTGROUP":
                        if (CurrentNode?.TagName == "OPTION")
                            _openElements.RemoveAt(_openElements.Count - 1);
                        if (CurrentNode?.TagName == "OPTGROUP")
                            _openElements.RemoveAt(_openElements.Count - 1);
                        InsertHtmlElement(start);
                        break;
                    case "SELECT":
                        PopStackUntil("SELECT");
                        ResetInsertionModeAppropriately();
                        break;
                    case "INPUT":
                    case "TEXTAREA":
                    case "KEYGEN":
                        if (!HasElementInScope("SELECT")) return;
                        PopStackUntil("SELECT");
                        ResetInsertionModeAppropriately();
                        ProcessToken(token);
                        break;
                    case "SCRIPT":
                    case "TEMPLATE":
                        HandleInHead(token);
                        break;
                }
            }
            else if (token is EndTagToken end)
            {
                switch (end.TagName)
                {
                    case "OPTGROUP":
                        if (CurrentNode?.TagName == "OPTION" && _openElements.Count > 1 &&
                            _openElements[_openElements.Count - 2].TagName == "OPTGROUP")
                        {
                            _openElements.RemoveAt(_openElements.Count - 1);
                        }
                        if (CurrentNode?.TagName == "OPTGROUP")
                            _openElements.RemoveAt(_openElements.Count - 1);
                        break;
                    case "OPTION":
                        if (CurrentNode?.TagName == "OPTION")
                            _openElements.RemoveAt(_openElements.Count - 1);
                        break;
                    case "SELECT":
                        if (!HasElementInScope("SELECT")) return;
                        PopStackUntil("SELECT");
                        ResetInsertionModeAppropriately();
                        break;
                    case "TEMPLATE":
                        HandleInHead(token);
                        break;
                }
            }
        }
        
        // --- InSelectInTable Mode ---
        private void HandleInSelectInTable(HtmlToken token)
        {
            if (token is StartTagToken start && (start.TagName == "CAPTION" || start.TagName == "TABLE" || 
                start.TagName == "TBODY" || start.TagName == "TFOOT" || start.TagName == "THEAD" || 
                start.TagName == "TR" || start.TagName == "TD" || start.TagName == "TH"))
            {
                PopStackUntil("SELECT");
                ResetInsertionModeAppropriately();
                ProcessToken(token);
            }
            else if (token is EndTagToken end && (end.TagName == "CAPTION" || end.TagName == "TABLE" || 
                end.TagName == "TBODY" || end.TagName == "TFOOT" || end.TagName == "THEAD" || 
                end.TagName == "TR" || end.TagName == "TD" || end.TagName == "TH"))
            {
                if (!HasElementInTableScope(end.TagName)) return;
                PopStackUntil("SELECT");
                ResetInsertionModeAppropriately();
                ProcessToken(token);
            }
            else
            {
                HandleInSelect(token);
            }
        }
        
        // --- InTemplate Mode ---
        private void HandleInTemplate(HtmlToken token)
        {
            if (token is CharacterToken || token is CommentToken || token is DoctypeToken)
            {
                HandleInBody(token);
            }
            else if (token is StartTagToken start)
            {
                switch (start.TagName)
                {
                    case "BASE":
                    case "BASEFONT":
                    case "BGSOUND":
                    case "LINK":
                    case "META":
                    case "NOFRAMES":
                    case "SCRIPT":
                    case "STYLE":
                    case "TEMPLATE":
                    case "TITLE":
                        HandleInHead(token);
                        break;
                    case "CAPTION":
                    case "COLGROUP":
                    case "TBODY":
                    case "TFOOT":
                    case "THEAD":
                        _templateModes.Pop();
                        _templateModes.Push(InsertionMode.InTable);
                        _mode = InsertionMode.InTable;
                        ProcessToken(token);
                        break;
                    case "COL":
                        _templateModes.Pop();
                        _templateModes.Push(InsertionMode.InColumnGroup);
                        _mode = InsertionMode.InColumnGroup;
                        ProcessToken(token);
                        break;
                    case "TR":
                        _templateModes.Pop();
                        _templateModes.Push(InsertionMode.InTableBody);
                        _mode = InsertionMode.InTableBody;
                        ProcessToken(token);
                        break;
                    case "TD":
                    case "TH":
                        _templateModes.Pop();
                        _templateModes.Push(InsertionMode.InRow);
                        _mode = InsertionMode.InRow;
                        ProcessToken(token);
                        break;
                    default:
                        _templateModes.Pop();
                        _templateModes.Push(InsertionMode.InBody);
                        _mode = InsertionMode.InBody;
                        ProcessToken(token);
                        break;
                }
            }
            else if (token is EndTagToken end && end.TagName == "TEMPLATE")
            {
                HandleInHead(token);
            }
            else if (token is EofToken)
            {
                if (!HasElementInScope("TEMPLATE")) return;
                PopStackUntil("TEMPLATE");
                ClearFormattingToMarker();
                _templateModes.Pop();
                ResetInsertionModeAppropriately();
                ProcessToken(token);
            }
        }
        
        // --- InFrameset Mode ---
        private void HandleInFrameset(HtmlToken token)
        {
            if (IsWhitespace(token))
            {
                InsertCharacter(token.As<CharacterToken>());
            }
            else if (token is StartTagToken start)
            {
                switch (start.TagName)
                {
                    case "FRAMESET":
                        InsertHtmlElement(start);
                        break;
                    case "FRAME":
                        InsertHtmlElement(start);
                        _openElements.RemoveAt(_openElements.Count - 1);
                        break;
                    case "NOFRAMES":
                        HandleInHead(token);
                        break;
                }
            }
            else if (token is EndTagToken end && end.TagName == "FRAMESET")
            {
                if (CurrentNode?.TagName == "HTML") return;
                _openElements.RemoveAt(_openElements.Count - 1);
                if (CurrentNode?.TagName != "FRAMESET")
                    _mode = InsertionMode.AfterFrameset;
            }
        }
        
        // --- AfterFrameset Mode ---
        private void HandleAfterFrameset(HtmlToken token)
        {
            if (IsWhitespace(token))
            {
                InsertCharacter(token.As<CharacterToken>());
            }
            else if (token is StartTagToken start && start.TagName == "NOFRAMES")
            {
                HandleInHead(token);
            }
            else if (token is EndTagToken end && end.TagName == "HTML")
            {
                _mode = InsertionMode.AfterAfterFrameset;
            }
        }
        
        // --- Reset Insertion Mode Appropriately (WHATWG 13.2.4.1) ---
        private void ResetInsertionModeAppropriately()
        {
            for (int i = _openElements.Count - 1; i >= 0; i--)
            {
                var node = _openElements[i];
                bool last = i == 0;
                var tag = node.TagName;
                
                if (tag == "SELECT")
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var ancestor = _openElements[j].TagName;
                        if (ancestor == "TEMPLATE") break;
                        if (ancestor == "TABLE")
                        {
                            _mode = InsertionMode.InSelectInTable;
                            return;
                        }
                    }
                    _mode = InsertionMode.InSelect;
                    return;
                }
                if (tag == "TD" || tag == "TH") { _mode = InsertionMode.InCell; return; }
                if (tag == "TR") { _mode = InsertionMode.InRow; return; }
                if (tag == "TBODY" || tag == "THEAD" || tag == "TFOOT") { _mode = InsertionMode.InTableBody; return; }
                if (tag == "CAPTION") { _mode = InsertionMode.InCaption; return; }
                if (tag == "COLGROUP") { _mode = InsertionMode.InColumnGroup; return; }
                if (tag == "TABLE") { _mode = InsertionMode.InTable; return; }
                if (tag == "TEMPLATE") { _mode = _templateModes.Count > 0 ? _templateModes.Peek() : InsertionMode.InBody; return; }
                if (tag == "BODY") { _mode = InsertionMode.InBody; return; }
                if (tag == "FRAMESET") { _mode = InsertionMode.InFrameset; return; }
                if (tag == "HTML") { _mode = _headElement == null ? InsertionMode.BeforeHead : InsertionMode.AfterHead; return; }
                if (last) { _mode = InsertionMode.InBody; return; }
            }
        }
        
        // --- Foreign Content Handling (WHATWG 13.2.6.5) ---
        
        private bool ShouldProcessAsForeignContent()
        {
            if (_openElements.Count == 0) return false;
            var node = CurrentNode;
            if (node == null) return false;
            return ForeignContent.IsInForeignContent(node) && !ForeignContent.IsHtmlIntegrationPoint(node);
        }
        
        private void ProcessForeignContent(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
                return;
            }
            
            if (token is StartTagToken start)
            {
                // Check for breakout elements - switch to HTML parsing
                if (ForeignContent.IsBreakoutElement(start.TagName))
                {
                    // Pop foreign elements and reprocess as HTML
                    while (_openElements.Count > 0 && ForeignContent.IsInForeignContent(CurrentNode))
                    {
                        if (ForeignContent.IsHtmlIntegrationPoint(CurrentNode)) break;
                        _openElements.RemoveAt(_openElements.Count - 1);
                    }
                    ProcessToken(token);
                    return;
                }
                
                // Handle SVG element
                if (start.TagName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = GetAttributesDictionary(start);
                    var element = ForeignContent.CreateForeignElement(start.TagName, ForeignContent.SvgNamespace, attrs);
                    InsertElement(element);
                    if (start.SelfClosing) _openElements.RemoveAt(_openElements.Count - 1);
                    return;
                }
                
                // Handle MATH element
                if (start.TagName.Equals("math", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = GetAttributesDictionary(start);
                    var element = ForeignContent.CreateForeignElement(start.TagName, ForeignContent.MathMLNamespace, attrs);
                    InsertElement(element);
                    if (start.SelfClosing) _openElements.RemoveAt(_openElements.Count - 1);
                    return;
                }
                
                // Insert element in current foreign namespace
                var currentNs = CurrentNode?.NamespaceUri ?? ForeignContent.HtmlNamespace;
                var foreignAttrs = GetAttributesDictionary(start);
                var foreignElement = ForeignContent.CreateForeignElement(start.TagName, currentNs, foreignAttrs);
                InsertElement(foreignElement);
                if (start.SelfClosing) _openElements.RemoveAt(_openElements.Count - 1);
                return;
            }
            
            if (token is EndTagToken end)
            {
                // Pop matching element
                for (int i = _openElements.Count - 1; i >= 0; i--)
                {
                    var el = _openElements[i];
                    if (el.TagName.Equals(end.TagName, StringComparison.OrdinalIgnoreCase))
                    {
                        while (_openElements.Count > i)
                            _openElements.RemoveAt(_openElements.Count - 1);
                        return;
                    }
                    // If we hit HTML namespace, stop searching
                    if (el.NamespaceUri == ForeignContent.HtmlNamespace)
                    {
                        ProcessToken(token);
                        return;
                    }
                }
            }
        }
        
        private void InsertElement(Element element)
        {
            if (CurrentNode != null)
                CurrentNode.AppendChild(element);
            else
                _doc.AppendChild(element);
            _openElements.Add(element);
        }
        
        private Dictionary<string, string> GetAttributesDictionary(StartTagToken token)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (token.Attributes != null)
            {
                foreach (var kv in token.Attributes)
                    dict[kv.Key] = kv.Value;
            }
            return dict;
        }
    }
}
