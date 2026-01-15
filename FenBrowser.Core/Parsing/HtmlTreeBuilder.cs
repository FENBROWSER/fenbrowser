using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// A production-grade HTML5 Tree Builder.
    /// Implements insertion modes and tree construction rules.
    /// https://html.spec.whatwg.org/multipage/parsing.html#tree-construction
    /// </summary>
    public class HtmlTreeBuilder
    {
        private readonly HtmlTokenizer _tokenizer;
        private readonly Document _document;
        
        // Stack of Open Elements
        private readonly Stack<Element> _openElements = new Stack<Element>();
        
        // List of Active Formatting Elements (for Adoption Agency Algorithm)
        private readonly List<Element> _activeFormattingElements = new List<Element>();
        
        // Current insertion mode
        private InsertionMode _insertionMode = InsertionMode.Initial;
        private InsertionMode _originalInsertionMode; // For "In Text" etc
        
        // Pointers
        private Element _headElement;
        private Element _formElement;
        
        private bool _framesetOk = true;

        public HtmlTreeBuilder(string html)
        {
            _tokenizer = new HtmlTokenizer(html);
            _document = new Document();
            // stack is initially empty? No, usually Document is root? 
            // Spec says stack of open elements is initially empty.
            // But usually we append to Document.
            // Actually, "Process Initial" handles this.
        }

        public Document Build()
        {
            foreach (var token in _tokenizer.Tokenize())
            {
                ProcessToken(token);
            }
            return _document;
        }

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

        // Template Insertion Mode Stack
        private readonly Stack<InsertionMode> _templateInsertionModes = new Stack<InsertionMode>();

        private void ProcessToken(HtmlToken token)
        {
            // Simplified dispatch based on mode
            bool processed = false;
            
            // Loop for re-processing tokens (mode switching without consuming)
            while (!processed)
            {
                switch (_insertionMode)
                {
                    case InsertionMode.Initial:
                        processed = HandleInitial(token);
                        break;
                    case InsertionMode.BeforeHtml:
                        processed = HandleBeforeHtml(token);
                        break;
                    case InsertionMode.BeforeHead:
                        processed = HandleBeforeHead(token);
                        break;
                    case InsertionMode.InHead:
                        processed = HandleInHead(token);
                        break;
                    case InsertionMode.InHeadNoscript:
                         processed = HandleInHeadNoscript(token);
                         break;
                    case InsertionMode.AfterHead:
                        processed = HandleAfterHead(token);
                        break;
                    case InsertionMode.InBody:
                        processed = HandleInBody(token);
                        break;
                    case InsertionMode.Text:
                        processed = HandleText(token);
                        break;
                    case InsertionMode.InTable:
                        processed = HandleInTable(token);
                        break;
                    case InsertionMode.InTableBody:
                        processed = HandleInTableBody(token);
                        break;
                    case InsertionMode.InRow:
                        processed = HandleInRow(token);
                        break;
                    case InsertionMode.InCell:
                        processed = HandleInCell(token);
                        break;
                    case InsertionMode.InCaption:
                        processed = HandleInCaption(token);
                        break;
                    case InsertionMode.InColumnGroup:
                        processed = HandleInColumnGroup(token);
                        break;
                    case InsertionMode.InTemplate:
                        processed = HandleInTemplate(token);
                        break;
                    case InsertionMode.AfterBody:
                        processed = HandleAfterBody(token);
                        break;
                    case InsertionMode.AfterAfterBody:
                        processed = HandleAfterAfterBody(token);
                        break;
                    default:
                        if (DebugConfig.LogHtmlParse)
                            FenBrowser.Core.FenLogger.Warn($"[HTML] Unhandled Mode: {_insertionMode} for token {token.Type}", LogCategory.HtmlParsing);
                        processed = HandleInBody(token); // Fallback
                        break;
                }
            }
        }
        
        // --- Insertion Mode Handlers ---
        
        private bool HandleInitial(HtmlToken token)
        {
            if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
                return true; // Ignore whitespace
            
            if (token is CommentToken comment)
            {
                _document.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is DoctypeToken dt)
            {
                // Emit DOCTYPE
                var doctypeNode = new DocumentType(dt.Name, dt.PublicIdentifier, dt.SystemIdentifier);
                _document.AppendChild(doctypeNode);
                
                // TODO: Quirks mode detection logic
                SwitchTo(InsertionMode.BeforeHtml);
                return true;
            }
            
            // Anything else?
            // "If the document is not an iframe srcdoc document..." -> Parse error, set quarks mode.
            // Switch to BeforeHtml and Reprocess
            SwitchTo(InsertionMode.BeforeHtml);
            return false; // Reprocess
        }

        private bool HandleInTemplate(HtmlToken token)
        {
            if (token is CharacterToken || token is CommentToken || token is DoctypeToken)
            {
                return HandleInBody(token);
            }
            
            if (token is StartTagToken st)
            {
                if (st.TagName == "base" || st.TagName == "basefont" || st.TagName == "bgsound" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "noframes" || st.TagName == "script" || st.TagName == "style" || st.TagName == "template" || st.TagName == "title")
                {
                    return HandleInHead(token);
                }
                
                // Pop template mode and push new one based on tag?
                // Simplified: Just process in InBody for now, but handle 'template' end tag.
                // The spec for InTemplate is complex (dispatch to current template insertion mode).
                // We'll mimic this by checking the stack.
                
                if (_templateInsertionModes.Count > 0)
                {
                    var currentTemplateMode = _templateInsertionModes.Peek();
                    // Dispatch to that mode?
                    // We can't easily recurse ProcessToken without changing _insertionMode.
                    // But changing _insertionMode changes it for everyone.
                    // So we effectively temporarily switch mode?
                    
                    var oldMode = _insertionMode;
                    _insertionMode = currentTemplateMode;
                    // Process
                    // BUT avoiding infinite recursion if it comes back here.
                    // For now, let's treat InTemplate as InBody for content.
                    return HandleInBody(token);
                }
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "template")
                {
                    if (!InScope("template", new[] { "html" })) 
                    {
                        return true; // Error
                    }
                    GenerateImpliedEndTags();
                    if ((CurrentNode as Element)?.TagName != "template") { /* Parse error */ }
                    PopUntil("template");
                    ClearActiveFormattingElementsMarker();
                    if (_templateInsertionModes.Count > 0) _templateInsertionModes.Pop();
                    ResetInsertionMode();
                    return true;
                }
            }
            
            if (token is EofToken)
            {
                if (!InScope("template", new[] { "html" }))
                {
                    // Stop parsing
                     return true; 
                }
                // Error
                PopUntil("template");
                ClearActiveFormattingElementsMarker();
                if (_templateInsertionModes.Count > 0) _templateInsertionModes.Pop();
                ResetInsertionMode();
                return false; // Reprocess
            }
            
            return HandleInBody(token);
        }

        private bool HandleInHeadNoscript(HtmlToken token)
        {
             if (token is EndTagToken et && et.TagName == "noscript")
             {
                 _openElements.Pop();
                 SwitchTo(InsertionMode.InHead);
                 return true;
             }
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 return HandleInHead(token);
             }
             if (token is StartTagToken st && (st.TagName == "basefont" || st.TagName == "bgsound" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "noframes" || st.TagName == "style"))
             {
                 return HandleInHead(token); 
             }
             // Anything else -> Error, pop noscript, reprocess
             _openElements.Pop();
             SwitchTo(InsertionMode.InHead);
             return false;
        }
        
        private bool HandleBeforeHtml(HtmlToken token)
        {
             if (token is DoctypeToken)
            {
                // Ignore (Parse error)
                return true;
            }
            
            if (token is CommentToken comment)
            {
                _document.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
                return true; // Ignore
                
            if (token is StartTagToken st && st.TagName == "html")
            {
                var html = CreateElement(st);
                _document.AppendChild(html);
                _openElements.Push(html);
                SwitchTo(InsertionMode.BeforeHead);
                return true;
            }
            
            // Anything else? Create <html> and reprocess
            var artificialHtml = new Element("html");
            _document.AppendChild(artificialHtml);
            _openElements.Push(artificialHtml);
            SwitchTo(InsertionMode.BeforeHead);
            return false; // Reprocess
        }
        
        private bool HandleBeforeHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
                return true; // Ignore
                
            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is DoctypeToken) return true; // Ignore
            
            if (token is StartTagToken st && st.TagName == "html")
            {
                return HandleInBody(token); // Process "in body" rules for html tag? Spec says: "Process the token using rules for In Body"
            }
            
            if (token is StartTagToken headTag && headTag.TagName == "head")
            {
                var head = InsertHtmlElement(headTag);
                _headElement = head;
                SwitchTo(InsertionMode.InHead);
                return true;
            }
            
            // Anything else? Create <head> and reprocess
             var artificialHead = InsertHtmlElement(new StartTagToken() { TagName = "head" });
            _headElement = artificialHead;
            SwitchTo(InsertionMode.InHead);
            return false;
        }
        
        private bool HandleInHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 InsertCharacter(ct); // Valid in head if whitespace
                 return true;
             }
             
             if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                if (string.Equals(st.TagName, "html", StringComparison.OrdinalIgnoreCase)) return HandleInBody(token);
                
                // FIX: Use case-insensitive comparison for tag names
                var tagLower = st.TagName?.ToLowerInvariant() ?? "";
                if (tagLower == "base" || tagLower == "basefont" || tagLower == "bgsound" || tagLower == "link")
                {
                    // DEBUG: Log LINK token attributes
                    if (tagLower == "link")
                    {
                        /* [PERF-REMOVED] */
                        if (st.Attributes != null)
                        {
                            // Debug logging removed for performance
                        }
                    }
                    InsertHtmlElement(st);
                    _openElements.Pop(); // Immediately pop (void elements)
                    return true;
                }
                
                if (st.TagName == "meta")
                {
                   InsertHtmlElement(st);
                   _openElements.Pop();
                   // TODO: Handle charset extraction
                   return true;
                }
                
                if (st.TagName == "title")
                {
                    InsertGenericRCDATAElement(st);
                    return true;
                }
                
                // NOSCRIPT, NOFRAMES, STYLE -> "Generic Raw Text Element"
                if (st.TagName == "style" || st.TagName == "noframes") // NoFrames is rawtext?
                {
                     InsertGenericRawTextElement(st);
                     return true;
                }
                 if (st.TagName == "noscript")
                {
                    // If scripting enabled -> Generic raw text. else -> normal implementation.
                    // Assuming enabled:
                    InsertGenericRawTextElement(st);
                    return true;
                }
                
                if (st.TagName == "script")
                {
                    // Complex script handling
                     var script = InsertHtmlElement(st);
                     _tokenizer.SetState(HtmlTokenizer.TokenizerState.ScriptData); // Wait, tokenizer state is internal? 
                     // We need to access Tokenizer to change state.
                     _originalInsertionMode = _insertionMode;
                     // Switch to Text mode logic in TreeBuilder? 
                     // Spec says: switch tokenizer to script data state.
                     SwitchTo(InsertionMode.Text); 
                     
                     // We store the last start tag name for the tokenizer to use
                     _tokenizer.LastStartTagName = "script";
                     return true;
                }
                
                if (st.TagName == "head") return true; // Ignore
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "head")
                {
                    _openElements.Pop(); // Pop head
                    SwitchTo(InsertionMode.AfterHead);
                    return true;
                }
                if (et.TagName == "body" || et.TagName == "html" || et.TagName == "br")
                {
                     // Act as if head closed
                     _openElements.Pop();
                     SwitchTo(InsertionMode.AfterHead);
                     return false; // Reprocess
                }
                // Ignore other end tags
                return true; 
            }
            
            // Anything else? Pop head and reprocess
             _openElements.Pop();
             SwitchTo(InsertionMode.AfterHead);
             return false;
        }
        
        private bool HandleAfterHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 InsertCharacter(ct);
                 return true;
             }
             
             if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                if (st.TagName == "html") return HandleInBody(token);
                if (st.TagName == "body")
                {
                    InsertHtmlElement(st);
                    _framesetOk = false;
                    SwitchTo(InsertionMode.InBody);
                    return true;
                }
                
                if (st.TagName == "frameset")
                {
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InFrameset);
                    return true;
                }
                
                // "base", "link", etc... -> error, push back to head
                if (st.TagName == "base" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "script" || st.TagName == "style" || st.TagName == "title")
                {
                     // Append TO HEAD
                     // This requires we keep reference to head (we do: _headElement)
                     var node = CreateElement(st);
                     _headElement.AppendChild(node);
                     // If it has content (script/style/title), we are in trouble because we aren't switching modes correctly to parse their content.
                     // But wait, "Process token ... in InHead mode".
                     // So specific logic needed. 
                     // Simplified: Just ignore for now or implement properly later. 
                     return true;
                }
                 
                 if (st.TagName == "head") return true; // Ignore
            }
            
            // Anything else? Create <body> and reprocess
             InsertHtmlElement(new StartTagToken() { TagName = "body" });
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        private bool HandleInBody(HtmlToken token)
        {
             if (token is CharacterToken ct)
             {
                 if (ct.Data == "\0") return true; // Ignore null
                 // Reconstruct active formatting elements?
                 // Insert character
                 InsertCharacter(ct);
                 return true;
             }
             
              if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                if (st.TagName == "html")
                {
                    // Parse error. Add attributes to html element if missing.
                    return true;
                }
                
                if (st.TagName == "base" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "script" || st.TagName == "style" || st.TagName == "title")
                {
                    return HandleInHead(token);
                }
                
                if (st.TagName == "body")
                {
                    // Parse error.
                    return true;
                }
                
                if (st.TagName == "div" || st.TagName == "p" || st.TagName == "ul" || st.TagName == "ol" || st.TagName == "dl" || st.TagName == "blockquote" || st.TagName == "article" || st.TagName == "section" || st.TagName == "nav" || st.TagName == "header" || st.TagName == "footer" || st.TagName == "main")
                {
                    if ((CurrentNode as Element)?.TagName == "p" && st.TagName != "p") // Simplified p-closing
                    {
                         ClosePElement();
                    }
                    if (st.TagName == "p")
                    {
                         if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                    }
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "li")
                {
                    if ((CurrentNode as Element)?.TagName == "li") PopUntil("li");
                    if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "dd" || st.TagName == "dt")
                {
                     if ((CurrentNode as Element)?.TagName == "dd") PopUntil("dd");
                     if ((CurrentNode as Element)?.TagName == "dt") PopUntil("dt");
                     if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                     InsertHtmlElement(st);
                     return true;
                }

                if (st.TagName == "h1" || st.TagName == "h2" || st.TagName == "h3" || st.TagName == "h4" || st.TagName == "h5" || st.TagName == "h6")
                {
                    if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "a")
                {
                    // Adoption Agency Algorithm - Active Formatting Elements
                    // Strict non-nesting: if stack has 'a', pop until it's closed
                    if (StackHas("a"))
                    {
                        FenLogger.Debug("[Parser] Closing nested <a>", LogCategory.HtmlParsing);
                        PopUntil("a");
                    }
                    InsertHtmlElement(st);
                    // Push to active formatting elements
                    _activeFormattingElements.Add((Element)CurrentNode);
                    return true;
                }
                
                if (st.TagName == "b" || st.TagName == "strong" || st.TagName == "em" || st.TagName == "i" || st.TagName == "u" || st.TagName == "s" || st.TagName == "small" || st.TagName == "code")
                {
                     // Reconstruct active formatting
                     InsertHtmlElement(st);
                     _activeFormattingElements.Add((Element)CurrentNode);
                     return true;
                }
                
                if (st.TagName == "textarea")
                {
                    InsertGenericRCDATAElement(st);
                    return true;
                }
                
                if (st.TagName == "xmp" || st.TagName == "iframe" || st.TagName == "noembed" || st.TagName == "noscript")
                {
                    InsertGenericRawTextElement(st);
                    return true;
                }

                if (st.TagName == "img" || st.TagName == "br" || st.TagName == "embed" || st.TagName == "hr" || st.TagName == "input" || st.TagName == "source" || st.TagName == "area" ||
                    // FIX: Treat SVG common shapes as void to prevent incorrect nesting
                    st.TagName == "path" || st.TagName == "rect" || st.TagName == "circle" || st.TagName == "line" || st.TagName == "polyline" || st.TagName == "polygon" || st.TagName == "ellipse" || st.TagName == "stop" || st.TagName == "use" || st.TagName == "image")
                {
                     // Void elements
                     if (st.TagName == "hr" && (CurrentNode as Element)?.TagName == "p") ClosePElement();
                     
                     var el = InsertHtmlElement(st);
                     _openElements.Pop(); // Immediately close
                     return true;
                }
                
                // Form
                if (st.TagName == "form")
                {
                    if (_formElement != null) return true; // Ignore nested forms
                    if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                    _formElement = InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "table")
                {
                    if ((CurrentNode as Element)?.TagName == "p") ClosePElement();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InTable);
                    return true;
                }
                
                if (st.TagName == "p" || st.TagName == "div" || st.TagName == "ul" || st.TagName == "ol" || st.TagName == "li" || 
                    st.TagName == "h1" || st.TagName == "h2" || st.TagName == "h3" || st.TagName == "h4" || st.TagName == "h5" || st.TagName == "h6" ||
                    st.TagName == "section" || st.TagName == "article" || st.TagName == "aside" || st.TagName == "header" || st.TagName == "footer" || st.TagName == "nav")
                {
                    if (StackHas("p")) ClosePElement();
                    InsertHtmlElement(st);
                    return true;
                }
                
                // Ordinary element
                InsertHtmlElement(st);
                return true;
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "body")
                {
                    SwitchTo(InsertionMode.AfterBody);
                    return true;
                }
                if (et.TagName == "html")
                {
                     SwitchTo(InsertionMode.AfterBody);
                     return false; // Reprocess
                }
                
                if (et.TagName == "p")
                {
                    if (!StackHas("p"))
                    {
                        // Parse error: </p> without <p>. Create <p> and close it. (Implies <p></p>)
                        if (DebugConfig.LogHtmlParse)
                             FenLogger.Log("[HTML] Recovered </p> without open <p> (Inserted empty paragraph)", LogCategory.HtmlParsing);
                        InsertHtmlElement(new StartTagToken() { TagName = "p" });
                    }
                    // Close p
                    PopUntil("p");
                    return true;
                }
                
                if (et.TagName == "div" || et.TagName == "ul" || et.TagName == "ol" || et.TagName == "li" || et.TagName == "h1" || et.TagName == "h2")
                {
                     if (StackHas(et.TagName)) PopUntil(et.TagName);
                     return true;
                }
                
                if (et.TagName == "form")
                {
                     // Close form
                     // Spec is complex taking _formElement into account
                     if (_formElement != null) _formElement = null; // Simply nullify
                     if (StackHas("form")) PopUntil("form");
                     return true;
                }
                
                // Formatting elements (Adoption Agency)
                if (et.TagName == "a" || et.TagName == "b" || et.TagName == "i" || et.TagName == "strong" || et.TagName == "em")
                {
                    // Simplified Adoption Agency: Just close if on stack
                    if (StackHas(et.TagName)) PopUntil(et.TagName);
                    // Also remove from active formatting list
                    _activeFormattingElements.RemoveAll(e => e.TagName == et.TagName);
                    return true;
                }
                
                // Scripts?
                if (et.TagName == "script")
                {
                    // Should be handled in Text mode
                    return true;
                }
            }
            
            if (token is EofToken)
            {
                // Stop
                return true;
            }

            return true;
        }
        
        private bool HandleAfterBody(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 return HandleInBody(token); // Spec says process as if in body? No, spec says process in body for whitespace
             }
             if (token is CommentToken)
             {
                 // Append to html element
                 _openElements.First().AppendChild(new Comment(((CommentToken)token).Data)); // _openElements bottom is html
                 return true;
             }
             
             if (token is EndTagToken et && et.TagName == "html")
             {
                 SwitchTo(InsertionMode.AfterAfterBody);
                 return true;
             }
             
             if (token is EofToken) return true;
             
             // Parse error -> switch to InBody and reprocess
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        private bool HandleAfterAfterBody(HtmlToken token)
        {
             if (token is CommentToken)
             {
                 _document.AppendChild(new Comment(((CommentToken)token).Data));
                 return true;
             }
             if (token is EofToken) return true;
             
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        // --- Table Insertion Modes ---

        private bool HandleInTable(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                if (IsTableWhitespace(ct))
                {
                    // In Table Text (pending whitespace)
                    InsertCharacter(ct);
                    return true;
                }
                // Anything else -> Foster Parent
                // Fallthrough to Foster Parenting below
            }

            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken) return true; // Ignore

            if (token is StartTagToken st)
            {
                if (st.TagName == "caption")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st); // Marker
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InCaption);
                    return true;
                }
                if (st.TagName == "colgroup")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InColumnGroup);
                    return true;
                }
                if (st.TagName == "col")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(new StartTagToken { TagName = "colgroup" });
                    SwitchTo(InsertionMode.InColumnGroup);
                    return false; // Reprocess col
                }
                if (st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InTableBody);
                    return true;
                }
                if (st.TagName == "td" || st.TagName == "th" || st.TagName == "tr")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(new StartTagToken { TagName = "tbody" });
                    SwitchTo(InsertionMode.InTableBody);
                    return false; // Reprocess
                }
                
                if (st.TagName == "table")
                {
                    // Parse error -> check scope closure
                    if (!InTableScope("table"))
                    {
                         // ignore
                         return true;
                    }
                    PopUntil("table");
                    // Reprocess "table" in ResetInsertionMode (Back to InBody probably?)
                    // Simplified: treat as end of table, then reprocess
                    // But spec says: "Act as if an end tag token with tag name 'table' had been seen, then... process the token in InBody"
                    // So we close current, then reprocess `st`
                    return HandleInTable(new EndTagToken { TagName = "table" }) ? HandleInBody(token) : false;
                }

                if (st.TagName == "style" || st.TagName == "script" || st.TagName == "template")
                {
                    return HandleInHead(token);
                }
                
                if (st.TagName == "input")
                {
                    // Special case: if hidden, append to table. Else foster parent.
                    bool hidden = false;
                    var type = st.Attributes.FirstOrDefault(a => a.Name.Equals("type", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        InsertHtmlElement(st);
                        _openElements.Pop();
                        return true;
                    }
                }
                
                if (st.TagName == "form")
                {
                    // Parse error
                    if (_formElement != null) return true; // Ignore
                    _formElement = InsertHtmlElement(st);
                    _openElements.Pop(); // Immediately pop
                    return true;
                }
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "table")
                {
                    if (!InTableScope("table"))
                    {
                        // Error
                        return true;
                    }
                    PopUntil("table");
                    ResetInsertionMode();
                    return true;
                }
                
                if (et.TagName == "body" || et.TagName == "caption" || et.TagName == "col" || et.TagName == "colgroup" || et.TagName == "html" || et.TagName == "tbody" || et.TagName == "td" || et.TagName == "tfoot" || et.TagName == "th" || et.TagName == "thead" || et.TagName == "tr")
                {
                    // Parse error -> ignore
                    return true;
                }
            }
            
            if (token is EofToken)
            {
                return HandleInBody(token); // Propagate up
            }

            // --- Foster Parenting ---
            // "Enable foster parenting, process the token using the rules for the In Body insertion mode"
            return FosterParent(token);
        }

        private bool HandleInTableBody(HtmlToken token)
        {
             if (token is StartTagToken st)
             {
                 if (st.TagName == "tr")
                 {
                     ClearStackBackToTableBodyContext();
                     InsertHtmlElement(st);
                     SwitchTo(InsertionMode.InRow);
                     return true;
                 }
                 if (st.TagName == "th" || st.TagName == "td")
                 {
                     ClearStackBackToTableBodyContext();
                     InsertHtmlElement(new StartTagToken { TagName = "tr" });
                     SwitchTo(InsertionMode.InRow);
                     return false; // Reprocess
                 }
                 if (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "table")
                 {
                     // Close body
                     if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
                     {
                         // Error
                         return true; 
                     }
                     ClearStackBackToTableBodyContext();
                     _openElements.Pop(); // Pop body
                     SwitchTo(InsertionMode.InTable);
                     return false; // Reprocess
                 }
             }
             
             if (token is EndTagToken et)
             {
                 if (et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead")
                 {
                     if (!InTableScope(et.TagName)) return true; // Error
                     ClearStackBackToTableBodyContext();
                     _openElements.Pop();
                     SwitchTo(InsertionMode.InTable);
                     return true;
                 }
                 if (et.TagName == "table")
                 {
                      if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
                     {
                         // Error
                         return true; 
                     }
                     ClearStackBackToTableBodyContext();
                     _openElements.Pop();
                     SwitchTo(InsertionMode.InTable);
                     return false; // Reprocess
                 }
             }
             
             return HandleInTable(token); // Anything else -> processed in InTable (which might foster parent)
        }

        private bool HandleInRow(HtmlToken token)
        {
             if (token is StartTagToken st)
             {
                 if (st.TagName == "th" || st.TagName == "td")
                 {
                     ClearStackBackToTableRowContext();
                     InsertHtmlElement(st);
                     SwitchTo(InsertionMode.InCell);
                     _activeFormattingElements.Add(null); // Marker
                     return true;
                 }
                 if (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "tr" || st.TagName == "table")
                 {
                     if (!InTableScope("tr")) return true; // Error
                     ClearStackBackToTableRowContext();
                     _openElements.Pop(); // Pop tr
                     SwitchTo(InsertionMode.InTableBody);
                     return false; // Reprocess
                 }
             }
             
             if (token is EndTagToken et)
             {
                 if (et.TagName == "tr")
                 {
                     if (!InTableScope("tr")) return true; // Ignore
                     ClearStackBackToTableRowContext();
                     _openElements.Pop(); // Pop tr
                     SwitchTo(InsertionMode.InTableBody);
                     return true;
                 }
                 if (et.TagName == "table")
                 {
                      if (!InTableScope("tr")) return true;
                      ClearStackBackToTableRowContext();
                      _openElements.Pop(); // Pop tr
                      SwitchTo(InsertionMode.InTableBody);
                      return false; // Reprocess
                 }
                 if (et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead")
                 {
                      if (!InTableScope(et.TagName)) return true; // Error
                      if (!InTableScope("tr")) return true; // Error
                      ClearStackBackToTableRowContext();
                      _openElements.Pop(); // Pop tr
                      SwitchTo(InsertionMode.InTableBody);
                      return false; // Reprocess
                 }
             }

             return HandleInTable(token);
        }
        
        private bool HandleInCell(HtmlToken token)
        {
            if (token is StartTagToken st)
            {
                 if (st.TagName == "td" || st.TagName == "th" || st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "tr")
                 {
                     if (!InTableScope("td") && !InTableScope("th")) 
                     {
                         // Parse error: open cell not found (shouldn't happen in InCell mode unless stack corrupted or manipulated)
                         return true; 
                     }
                     CloseCell();
                     return false; // Reprocess
                 }
            }

            if (token is EndTagToken et)
            {
                if (et.TagName == "td" || et.TagName == "th")
                {
                    if (!InTableScope(et.TagName)) return true; // Ignore
                    GenerateImpliedEndTags();
                    if ((CurrentNode as Element)?.TagName != et.TagName)
                    {
                        // Parse error
                    }
                    PopUntil(et.TagName);
                    ClearActiveFormattingElementsMarker();
                    SwitchTo(InsertionMode.InRow);
                    return true;
                }
                if (et.TagName == "body" || et.TagName == "caption" || et.TagName == "col" || et.TagName == "colgroup" || et.TagName == "html")
                {
                    // Parse error -> ignore
                    return true;
                }
                if (et.TagName == "table" || et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead" || et.TagName == "tr")
                {
                    if (!InTableScope(et.TagName)) return true; // Error
                    CloseCell();
                    return false; // Reprocess
                }
            }
            
            return HandleInBody(token);
        }
        
        private void CloseCell()
        {
            GenerateImpliedEndTags();
            if (CurrentTag != "td" && CurrentTag != "th")
            {
                 // Error
            }
            while (CurrentTag != "td" && CurrentTag != "th" && _openElements.Count > 0)
            {
                _openElements.Pop();
            }
             if (_openElements.Count > 0) _openElements.Pop();
             ClearActiveFormattingElementsMarker();
             SwitchTo(InsertionMode.InRow);
        }
        
        // --- Foster Parenting Logic ---
        private bool FosterParent(HtmlToken token)
        {
            // Find the table element in the stack
            Element table = null;
            // Iterate reverse?
            foreach (var el in _openElements)
            {
                if (string.Equals(el.TagName, "table", StringComparison.OrdinalIgnoreCase)) 
                {
                    table = el;
                    break; 
                }
            }
            if (table == null) return HandleInBody(token); // Should not happen in InTable mode

            Node parent = table.Parent;
            Node nextSibling = table; // We insert before table
            
            if (parent == null)
            {
                // Table popped off stack? fallback to previous element in stack.
                // Spec says: use element before table in stack.
                parent = _openElements.SkipWhile(e => e != table).Skip(1).FirstOrDefault(); 
                if (parent == null) parent = _document; // Fallback
                nextSibling = null; // Append
            }
            
            // Temporary divert inserts to parent
            var originalNode = CurrentNode;
            
            // How to implement redirect? Code uses `InsertHtmlElement` which uses `CurrentNode`.
            // We can't easily change `CurrentNode` (it's peek of stack).
            // We have to manual insert.
            
            if (token is CharacterToken ct)
            {
                // Attempt to coalesce with previous text node
                Node prev = null;
                if (nextSibling != null)
                {
                    var idx = parent.Children.IndexOf(nextSibling);
                    if (idx > 0) prev = parent.Children[idx - 1];
                }
                else
                {
                    prev = parent.Children.LastOrDefault();
                }

                if (prev is Text txt)
                {
                    txt.Data += ct.Data;
                    return true;
                }

                var text = new Text(ct.Data);
                if (nextSibling != null && parent != null)
                    parent.InsertBefore(text, nextSibling);
                else
                    parent?.AppendChild(text);
                return true;
            }
            
            if (token is StartTagToken st)
            {
                // Create element but don't push to stack?
                // Wait, if it's a start tag, we might enter a new mode or push to stack.
                // Spec says: "Process token using In Body... with foster parenting flag"
                // This means when InBody inserts an element, it should foster parent it.
                // This arch is hard to retrofit.
                
                // SIMPLIFIED FOSTER PARENTING:
                // Only handle text and basic void elements. 
                // Complex elements inside improper table context are hard.
                if (DebugConfig.LogHtmlParse)
                     FenBrowser.Core.FenLogger.Warn($"[HTML] Simple Foster Parent for {st.TagName}", LogCategory.HtmlParsing);
                     
                var el = new Element(st.TagName);
                foreach(var a in st.Attributes) el.SetAttribute(a.Name, a.Value);
                
                 if (nextSibling != null && parent != null)
                    parent.InsertBefore(el, nextSibling);
                else
                    parent?.AppendChild(el);
                    
                // If not void, we should push it to stack?
                // But then it's in stack but its parent is ouside table.
                if (!HtmlParser.IsVoid(st.TagName))
                {
                    _openElements.Push(el);
                }
                return true;
            }
            
            return true;
        }

        private bool HandleInCaption(HtmlToken token)
        {
            if (token is EndTagToken et && et.TagName == "caption")
            {
                if (!InTableScope("caption")) return true; // Error
                GenerateImpliedEndTags();
                if ((CurrentNode as Element)?.TagName != "caption") { /* Parse error */ }
                PopUntil("caption");
                ClearActiveFormattingElementsMarker();
                SwitchTo(InsertionMode.InTable);
                return true;
            }
            if (token is StartTagToken st && (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "td" || st.TagName == "tfoot" || st.TagName == "th" || st.TagName == "thead" || st.TagName == "tr"))
            {
                 if (!InTableScope("caption")) return true; // Error
                 GenerateImpliedEndTags();
                 PopUntil("caption");
                 ClearActiveFormattingElementsMarker();
                 SwitchTo(InsertionMode.InTable);
                 return false; // Reprocess
            }
            if (token is EndTagToken et2 && et2.TagName == "table")
            {
                 if (!InTableScope("caption")) return true; // Error
                 GenerateImpliedEndTags();
                 PopUntil("caption");
                 ClearActiveFormattingElementsMarker();
                 SwitchTo(InsertionMode.InTable);
                 return false; // Reprocess
            }
            return HandleInBody(token);
        }

        private bool HandleInColumnGroup(HtmlToken token)
        {
             if (token is CharacterToken ct && IsTableWhitespace(ct))
             {
                 InsertCharacter(ct);
                 return true;
             }
             if (token is CommentToken c)
             {
                 CurrentNode.AppendChild(new Comment(c.Data));
                 return true;
             }
             if (token is DoctypeToken) return true;
             if (token is StartTagToken st)
             {
                 if (st.TagName == "html") return HandleInBody(token);
                 if (st.TagName == "col")
                 {
                     InsertHtmlElement(st);
                     _openElements.Pop(); // Col is void
                     // Attributes acknowledgment
                     return true;
                 }
                 if (st.TagName == "template") return HandleInHead(token);
             }
             if (token is EndTagToken et && et.TagName == "colgroup")
             {
                 if ((CurrentNode as Element)?.TagName != "colgroup") { /* Parse error */ }
                 _openElements.Pop();
                 SwitchTo(InsertionMode.InTable);
                 return true;
             }
             if (token is EofToken) return HandleInBody(token);
             
             // Anything else: pop colgroup, reprocess
             if ((CurrentNode as Element)?.TagName != "colgroup") { /* Parse error */ }
             _openElements.Pop();
             SwitchTo(InsertionMode.InTable);
             return false;
        }

        private bool IsTableWhitespace(CharacterToken ct)
        {
            // ASCII whitespace
            return string.IsNullOrWhiteSpace(ct.Data);
        }

        private bool InScope(string tagName, string[] scopeLimits)
        {
            foreach (var node in _openElements) // Iterates top to bottom? C# stack enumerates top-down (LIFO)
            {
                if (node is Element el)
                {
                    if (string.Equals(el.TagName, tagName, StringComparison.OrdinalIgnoreCase)) return true;
                    if (scopeLimits.Any(s => string.Equals(s, el.TagName, StringComparison.OrdinalIgnoreCase))) return false;
                }
            }
            return false;
        }
        
        private bool InTableScope(string tagName)
        {
            return InScope(tagName, new[] { "html", "table", "template" }); // Table scope limits
        }
        
        private void ClearStackBackToTableContext()
        {
            while (CurrentTag != "table" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                _openElements.Pop();
            }
        }
        
        private void ClearStackBackToTableBodyContext()
        {
            while (CurrentTag != "tbody" && CurrentTag != "tfoot" && CurrentTag != "thead" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                _openElements.Pop();
            }
        }
        
        private void ClearStackBackToTableRowContext()
        {
            while (CurrentTag != "tr" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                _openElements.Pop();
            }
        }
        
        private void ClearActiveFormattingElementsMarker()
        {
            while (_activeFormattingElements.Count > 0)
            {
                var entry = _activeFormattingElements[_activeFormattingElements.Count - 1];
                _activeFormattingElements.RemoveAt(_activeFormattingElements.Count - 1);
                if (entry == null) break;
            }
        }
        
        private string CurrentTag => (CurrentNode as Element)?.TagName?.ToLowerInvariant();

        private void IgnoreToken(HtmlToken token) { } // No-op

        private bool HandleText(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
                return true;
            }
            
            if (token is EndTagToken et)
            {
                // If closing the element that put us in Text mode (CurrentNode), pop and switch back.
                // Note: The Tokenizer ensures we only get this EndTag if it matches the start tag (for RCDATA/RAWTEXT).
                // So we can blindly accept it.
                _openElements.Pop();
                SwitchTo(_originalInsertionMode);
                return true;
            }
            
            // Should not happen in RCDATA/RAWTEXT unless tokenizer has issues or script data?
            // If somehow we get here, ignore or treat as char?
            return false; 
        }

        // --- Helpers ---

        private void ResetInsertionMode()
        {
            // Simplified Reset logic based on stack
             foreach (var node in _openElements) // Top to bottom?
            {
                var el = node as Element;
                if (el == null) continue;
                var tagName = el.TagName;
                
                if (tagName.Equals("select", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InSelect); return; }
                if (tagName.Equals("td", StringComparison.OrdinalIgnoreCase) || tagName.Equals("th", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InCell); return; }
                if (tagName.Equals("tr", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InRow); return; }
                if (tagName.Equals("tbody", StringComparison.OrdinalIgnoreCase) || tagName.Equals("thead", StringComparison.OrdinalIgnoreCase) || tagName.Equals("tfoot", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InTableBody); return; }
                if (tagName.Equals("caption", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InCaption); return; }
                if (tagName.Equals("colgroup", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InColumnGroup); return; }
                if (tagName.Equals("table", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InTable); return; }
                if (tagName.Equals("template", StringComparison.OrdinalIgnoreCase)) { 
                     // TODO: Current template insertion mode
                     SwitchTo(InsertionMode.InBody); // Simplified
                     return;
                }
                if (tagName.Equals("head", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InBody); return; } 
                if (tagName.Equals("body", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InBody); return; }
                if (tagName.Equals("frameset", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InFrameset); return; }
                if (tagName.Equals("html", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InBody); return; }
            }
             SwitchTo(InsertionMode.InBody);
        }
        
        private Node CurrentNode => _openElements.Count > 0 ? _openElements.Peek() : _document;
        
        private void SwitchTo(InsertionMode mode)
        {
            _insertionMode = mode;
        }
        
        private Element CreateElement(StartTagToken token)
        {
            var el = new Element(token.TagName);
            
            // Foreign Content Adjustments
            // Apply if:
            // 1. We are creating an svg or math element itself, OR
            // 2. We are already inside an svg or math element
            bool isForeignContent = token.TagName == "svg" || token.TagName == "math" ||
                                    (CurrentNode as Element)?.TagName == "svg" || (CurrentNode as Element)?.TagName == "math" || 
                                    IsSvgOrMathDescendant(CurrentNode);
            if (isForeignContent)
            {
                 AdjustForeignAttributes(token);
                 // We don't change tag name for now as Skia backend might expect lowercase or handle it.
                 // But attributes like viewBox are case sensitive in Svg.Skia.
            }

            foreach (var attr in token.Attributes)
            {
                el.SetAttribute(attr.Name, attr.Value);
                if (token.TagName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    /* [PERF-REMOVED] */
                }
            }
            return el;
        }
        
        private bool IsSvgOrMathDescendant(Node node)
        {
             // Simplified check up the stack
             foreach (var el in _openElements)
             {
                 if (el.TagName == "svg" || el.TagName == "math") return true;
             }
             return false;
        }

        private void AdjustForeignAttributes(StartTagToken token)
        {
             for (int i = 0; i < token.Attributes.Count; i++)
             {
                 var attr = token.Attributes[i];
                 var lower = attr.Name;
                 if (_foreignAttributeMap.TryGetValue(lower, out var fixedName))
                 {
                      token.Attributes[i] = new HtmlAttribute(fixedName, attr.Value);
                 }
             }
        }

        private static readonly Dictionary<string, string> _foreignAttributeMap = new Dictionary<string, string>
        {
            { "viewbox", "viewBox" },
            { "preserveaspectratio", "preserveAspectRatio" },
            { "gradientunits", "gradientUnits" },
            { "gradienttransform", "gradientTransform" },
            { "patternunits", "patternUnits" },
            { "patterntransform", "patternTransform" },
            { "maskunits", "maskUnits" },
            { "maskcontentunits", "maskContentUnits" },
            { "markerunits", "markerUnits" },
            { "markerwidth", "markerWidth" },
            { "markerheight", "markerHeight" },
            { "refx", "refX" },
            { "refy", "refY" },
            { "stop-color", "stop-color" }, // Keep as is
            { "stop-opacity", "stop-opacity" },
            { "lineargradient", "linearGradient" },
            { "radialgradient", "radialGradient" },
            { "clippath", "clipPath" },
            { "textlength", "textLength" },
            { "startoffset", "startOffset" },
            { "stddeviation", "stdDeviation" },
            { "basefrequency", "baseFrequency" },
            { "numoctaves", "numOctaves" },
            { "stitchtiles", "stitchTiles" },
            { "surfacescale", "surfaceScale" },
            { "specularconstant", "specularConstant" },
            { "specularexponent", "specularExponent" },
            { "targetx", "targetX" },
            { "targety", "targetY" },
            { "kernelmatrix", "kernelMatrix" },
            { "diffuseconstant", "diffuseConstant" },
            { "primitiveunits", "primitiveUnits" },
            { "filterunits", "filterUnits" },
            { "definitionurl", "definitionURL" },
            { "attributename", "attributeName" },
            { "attributetype", "attributeType" },
            { "calcmode", "calcMode" },
            { "keytimes", "keyTimes" },
            { "keysplines", "keySplines" }
            // Add more as needed
        };
        
        private Element InsertHtmlElement(StartTagToken token)
        {
            var el = CreateElement(token);
            CurrentNode.AppendChild(el);
            FenLogger.Debug($"[Parser] Pushing {el.TagName}_{el.GetHashCode()} to stack (Depth: {_openElements.Count})", LogCategory.HtmlParsing);
            _openElements.Push(el);
            return el;
        }
        
        private void InsertCharacter(CharacterToken token)
        {
            // Optimize: if current node's last child is text, append
            var last = CurrentNode.Children.LastOrDefault();
            if (last != null &&UnsafeIsText(last))
            {
                last.NodeValue += token.Data;
            }
            else
            {
                CurrentNode.AppendChild(new Text(token.Data));
            }
        }

        private bool UnsafeIsText(Node e) => e.NodeType == NodeType.Text; // Avoid property implementation details

        private void ClosePElement()
        {
            if (StackHas("p"))
            {
                if (DebugConfig.LogHtmlParse)
                    FenLogger.Log("[HTML] Auto-closed <p> (implied end tag)", LogCategory.HtmlParsing);
                PopUntil("p");
            }
        }
        
        private void GenerateImpliedEndTags(string except = null)
        {
            while ((CurrentNode as Element)?.TagName != null)
            {
                 string currentTag = (CurrentNode as Element).TagName;
                 if (except != null && string.Equals(currentTag, except, StringComparison.OrdinalIgnoreCase)) break;
                 
                 if (IsImpliedEndTag(currentTag))
                 {
                    if (DebugConfig.LogHtmlParse)
                        FenLogger.Log($"[HTML] Implied end tag for <{currentTag}>", LogCategory.HtmlParsing);
                    _openElements.Pop();
                 }
                 else
                 {
                     break;
                 }
            }
        }
        
        private bool IsImpliedEndTag(string tag)
        {
             return string.Equals(tag, "dd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "dt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "li", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "optgroup", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "option", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "p", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rtc", StringComparison.OrdinalIgnoreCase);
        }
        
        private bool StackHas(string tagName)
        {
            return _openElements.Any(e => string.Equals(e.TagName, tagName, StringComparison.OrdinalIgnoreCase));
        }
        
        private void PopUntil(string tagName)
        {
            var targetFound = _openElements.Any(e => string.Equals(e.TagName, tagName, StringComparison.OrdinalIgnoreCase));
            FenLogger.Debug($"[Parser] PopUntil({tagName}). Target in stack: {targetFound}. Current top: {(_openElements.Count > 0 ? _openElements.Peek().TagName : "NULL")}", LogCategory.HtmlParsing);
            
            if (targetFound)
            {
                while (_openElements.Count > 1)
                {
                    var popped = _openElements.Pop();
                    FenLogger.Debug($"[Parser] Popped {popped.TagName}_{popped.GetHashCode()}", LogCategory.HtmlParsing);
                    if (string.Equals(popped.TagName, tagName, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }
        
        // RCDATA / RAWTEXT helpers
        private void InsertGenericRCDATAElement(StartTagToken token)
        {
            InsertHtmlElement(token);
            _tokenizer.SetState(HtmlTokenizer.TokenizerState.RcData);
            _tokenizer.LastStartTagName = token.TagName;
            _originalInsertionMode = _insertionMode;
            SwitchTo(InsertionMode.Text);
        }
        
        private void InsertGenericRawTextElement(StartTagToken token)
        {
            InsertHtmlElement(token);
            _tokenizer.SetState(HtmlTokenizer.TokenizerState.RawText);
            _tokenizer.LastStartTagName = token.TagName;
            _originalInsertionMode = _insertionMode;
            SwitchTo(InsertionMode.Text);
        }
    }
}

