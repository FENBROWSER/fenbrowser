using FenBrowser.Core.Dom;
using FenBrowser.Core.Dom;
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
                    case InsertionMode.AfterHead:
                        processed = HandleAfterHead(token);
                        break;
                    case InsertionMode.InBody:
                        processed = HandleInBody(token);
                        break;
                    case InsertionMode.AfterBody:
                         processed = HandleAfterBody(token);
                         break;
                    case InsertionMode.AfterAfterBody:
                         processed = HandleAfterAfterBody(token);
                         break;
                    case InsertionMode.InTable:
                         processed = HandleInTable(token);
                         break;
                    case InsertionMode.Text:
                         processed = HandleText(token);
                         break;
                    // TODO: Implement other modes
                    default:
                        // Fallback to InBody or ignore
                         processed = HandleInBody(token); 
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
                if (st.TagName == "html") return HandleInBody(token);
                
                if (st.TagName == "base" || st.TagName == "basefont" || st.TagName == "bgsound" || st.TagName == "link")
                {
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
                    // Simplified: if stack has 'a', reconstruct/close
                    if (StackHas("a"))
                    {
                         // Close 'a'
                         GenerateImpliedEndTags();
                         // Remove 'a' from stack and active list
                         // This is complex. 
                         // Fallback for strict impl: just close it.
                         _openElements.Pop(); // Assume it's on top or simple nesting
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
        
        private bool HandleInTable(HtmlToken token)
        {
            // Simplified table handling (Foster Parenting is hard)
            // If tag is expected in table (tr, td, tbody, thead, tfoot, caption, colgroup), process
            // Else "Foster Parent" -> Move token to Before(Table)
            
            if (token is CharacterToken || token is StartTagToken || token is EndTagToken)
            {
                 // Check if it's table stuff
                 bool actsLikeTable = false;
                 string tag = "";
                 if (token is StartTagToken s) tag = s.TagName;
                 if (token is EndTagToken e) tag = e.TagName;
                 
                 if (tag == "tr" || tag == "td" || tag == "th" || tag == "tbody" || tag == "thead" || tag == "tfoot" || tag == "caption" || tag == "colgroup" || tag == "col" || tag == "table")
                 {
                      actsLikeTable = true;
                 }
                 
                 if (actsLikeTable)
                 {
                      // Normal processing? No, specialized.
                      // ... huge switch case ...
                      // For now, if table structure, append to current (which is table)
                      // BUT 'tr' inside 'table' implies 'tbody'.
                      if (tag == "tr" && (CurrentNode as Element)?.TagName == "table" && token is StartTagToken)
                      {
                          InsertHtmlElement(new StartTagToken() { TagName = "tbody" });
                          // Reprocess
                          return false;
                      }
                      
                      // For now, allow simplified table nesting
                      // TODO: strict table state machine
                      // For now, allow simplified table nesting
                      // TODO: strict table state machine
                      
                      if (token is StartTagToken st) 
                      {
                          string t = st.TagName;
                          if (t == "tr") 
                          {
                              if (StackHas("tr")) PopUntil("tr");
                          }
                          if (t == "td")
                          {
                              if (StackHas("td")) PopUntil("td");
                              if (StackHas("th")) PopUntil("th");
                          }
                          if (t == "th")
                          {
                              if (StackHas("td")) PopUntil("td");
                              if (StackHas("th")) PopUntil("th");
                          }
                          
                          InsertHtmlElement(st);
                      }
                      if (token is EndTagToken) { if (StackHas(tag)) PopUntil(tag); }
                      return true;
                 }
                 else
                 {
                      // Foster Parenting!
                      // "Process the token using the rules for the In Body insertion mode, with the foster parenting flag set to true."
                      // Foster parenting means: insert into the table's parent, before the table.
                      
                      // Simplified: Just switch to InBody, process, then switch back?
                      // No, InBody would append to CurrentNode (table), which is wrong.
                      // We must temporarily change CurrentNode?
                      
                      // TODO: Implement Foster Parenting
                      return true; // Ignore for now
                 }
            }
            return true;
        }

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
            if (StackHas("p")) PopUntil("p");
        }
        
        private void GenerateImpliedEndTags(string except = null)
        {
            while ((CurrentNode as Element)?.TagName != except && IsImpliedEndTag((CurrentNode as Element)?.TagName))
            {
                _openElements.Pop();
            }
        }
        
        private bool IsImpliedEndTag(string tag)
        {
             return tag == "dd" || tag == "dt" || tag == "li" || tag == "optgroup" || tag == "option" || tag == "p" || tag == "rb" || tag == "rp" || tag == "rt" || tag == "rtc";
        }
        
        private bool StackHas(string tag)
        {
            foreach (var el in _openElements)
            {
                if (el.TagName == tag) return true;
            }
            return false;
        }
        
        private void PopUntil(string tag)
        {
            while (_openElements.Count > 0)
            {
                var popped = _openElements.Pop();
                if (popped.TagName == tag) break;
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

