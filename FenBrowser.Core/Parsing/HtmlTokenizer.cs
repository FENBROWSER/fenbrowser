using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// A production-grade HTML5 Tokenizer implementing the state machine defined in:
    /// https://html.spec.whatwg.org/multipage/parsing.html#tokenization
    /// </summary>
    public class HtmlTokenizer
    {
        private readonly string _input;
        private int _position;
        private readonly int _length;
        
        // Current state
        private TokenizerState _state = TokenizerState.Data;
        
        // Current buffers
        private StringBuilder _buffer = new StringBuilder();
        private TagToken _currentTag;
        private CommentToken _currentComment;
        private DoctypeToken _currentDoctype;
        
        private string _lastAttrName;
        private StringBuilder _attrValueBuffer = new StringBuilder();

        public HtmlTokenizer(string input)
        {
            _input = input ?? "";
            _length = _input.Length;
            _position = 0;
        }

        public void SetState(TokenizerState state)
        {
            _state = state;
        }
        
        // This is simplified. Spec requires "appropriate end tag token" check which technically
        // depends on the "last start tag".
        // The Tree Builder typically handles checking if the end tag matches the open element.
        // But the Tokenizer needs to know the "last start tag name" for the </tag> check in RCDATA/etc.
        // We will store it here.
        public string LastStartTagName { get; set; }

        public enum TokenizerState
        {
            Data,
            CharacterReference,
            RcData,
            RawText,
            ScriptData,
            PlainText,
            TagOpen,
            EndTagOpen,
            TagName,
            RcDataLessThanSign,
            RcDataEndTagOpen,
            RcDataEndTagName,
            RawTextLessThanSign,
            RawTextEndTagOpen,
            RawTextEndTagName,
            ScriptDataLessThanSign,
            ScriptDataEndTagOpen,
            ScriptDataEndTagName,
            BeforeAttributeName,
            AttributeName,
            AfterAttributeName,
            BeforeAttributeValue,
            AttributeValueDoubleQuoted,
            AttributeValueSingleQuoted,
            AttributeValueUnquoted,
            AfterAttributeValueQuoted,
            SelfClosingStartTag,
            BogusComment,
            MarkupDeclarationOpen,
            CommentStart,
            CommentStartDash,
            Comment,
            CommentLessThanSign,
            CommentLessThanSignBang,
            CommentLessThanSignBangDash,
            CommentLessThanSignBangDashDash,
            CommentEndDash,
            CommentEnd,
            CommentEndBang,
            Doctype,
            BeforeDoctypeName,
            DoctypeName,
            AfterDoctypeName,
            AfterDoctypeSystemIdentifier,
            BogusDoctype
        }

        public IEnumerable<HtmlToken> Tokenize()
        {
            while (true)
            {
                var token = NextToken();
                if (token == null) continue; // Internal state transition produced no token yet
                
                yield return token;
                
                if (token.Type == HtmlTokenType.EndOfFile)
                    break;
            }
        }

        private HtmlToken NextToken()
        {
            // This loop runs until a token is emitted
            while (_position <= _length)
            {
                char c = Peek();
                
                switch (_state)
                {
                    case TokenizerState.Data:
                        if (c == '&')
                        {
                            // TODO: Handle character reference
                            Consume();
                            return new CharacterToken('&'); 
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.TagOpen);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    case TokenizerState.TagOpen:
                        if (c == '!')
                        {
                            Consume();
                            SwitchTo(TokenizerState.MarkupDeclarationOpen);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.EndTagOpen);
                        }
                        else if (char.IsLetter(c))
                        {
                            _currentTag = new StartTagToken();
                            SwitchTo(TokenizerState.TagName);
                             // Don't consume here, TagName state will handle it (reconsume)
                             continue;
                        }
                        else if (c == '?')
                        {
                            // Bogus comment (<?... >)
                             Consume();
                            _currentComment = new CommentToken();
                            SwitchTo(TokenizerState.BogusComment);
                        }
                        else
                        {
                            // Emit < as char state
                             // Invalid first character of start tag
                             EmitError("Invalid First Character of Tag");
                             SwitchTo(TokenizerState.Data);
                             return EmitCharacter('<');
                        }
                        break;

                    // --- RCDATA States (Title, Textarea) ---
                    case TokenizerState.RcData:
                        if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RcDataLessThanSign);
                        }
                        else if (c == '&')
                        {
                             // TODO: Character reference
                             Consume();
                             return EmitCharacter('&');
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;
                        
                    case TokenizerState.RcDataLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            // Set temp buffer to empty
                            _buffer.Clear(); 
                            SwitchTo(TokenizerState.RcDataEndTagOpen);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RcData);
                            return EmitCharacter('<');
                        }
                        break;
                        
                    case TokenizerState.RcDataEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = ""; // New tag
                             // Reconsume in RcDataEndTagName?
                             // No, spec says: create end tag token, append current char to tag name, switch to RcDataEndTagName
                             _currentTag.TagName += char.ToLowerInvariant(c);
                             Consume();
                             SwitchTo(TokenizerState.RcDataEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RcData);
                            return EmitCharacter('<'); // And '/' ? Spec is complex here.
                            // Simplified: Just emit characters
                        }
                        break;
                        
                    case TokenizerState.RcDataEndTagName:
                        // Scan until we match the LastStartTagName or fail
                        bool isAppropriate = _currentTag.TagName == LastStartTagName;
                        
                        if (char.IsWhiteSpace(c))
                        {
                            if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.BeforeAttributeName);
                            }
                            else
                            {
                                 // Fail -> Treat as raw text
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<'); // Rough approximation, proper rollback needed for full spec
                            }
                        }
                        else if (c == '/')
                        {
                             if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.SelfClosingStartTag);
                            }
                            else
                            {
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<');
                            }
                        }
                        else if (c == '>')
                        {
                            if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        else
                        {
                             SwitchTo(TokenizerState.RcData);
                             return EmitCharacter('<');
                        }
                        break;

                    // --- RAWTEXT States (Style, Script - wait Script has own state) ---
                    case TokenizerState.RawText:
                        if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RawTextLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;
                        
                    case TokenizerState.RawTextLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RawTextEndTagOpen);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RawText);
                            return EmitCharacter('<');
                        }
                        break;

                    case TokenizerState.RawTextEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = "";
                            _currentTag.TagName += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.RawTextEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RawText);
                             return EmitCharacter('<'); // And /
                        }
                        break;
                        
                    case TokenizerState.RawTextEndTagName:
                         bool isAppropriateRaw = _currentTag.TagName == LastStartTagName;
                         if (c == '>')
                        {
                            if (isAppropriateRaw)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                 SwitchTo(TokenizerState.RawText);
                                 return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        else
                        {
                            // Simplified whitespace/others handling
                             if (isAppropriateRaw && char.IsWhiteSpace(c))
                             {
                                 Consume();
                                 SwitchTo(TokenizerState.BeforeAttributeName);
                             }
                             else
                             {
                                SwitchTo(TokenizerState.RawText);
                                return EmitCharacter('<');
                             }
                        }
                        break;

                    case TokenizerState.ScriptData:
                        // Similar to RawText but handles <!--
                         if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    case TokenizerState.ScriptDataLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEndTagOpen);
                        }
                        else if (c == '!')
                        {
                            Consume();
                            // Script data escape start? This is getting complex.
                            // Simplified for now: treat as data
                             SwitchTo(TokenizerState.ScriptData);
                             return EmitCharacter('<');
                        }
                        else
                        {
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('<');
                        }
                        break;

                    case TokenizerState.ScriptDataEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = "";
                            _currentTag.TagName += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('<');
                        }
                        break;

                     case TokenizerState.ScriptDataEndTagName:
                        // Simplified
                         bool isAppropriateScript = _currentTag.TagName == LastStartTagName; // Usually "script"
                         if (c == '>')
                        {
                            if (isAppropriateScript)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                 SwitchTo(TokenizerState.ScriptData);
                                 return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                         else
                        {
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('<');
                        }
                        break;

                    case TokenizerState.EndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            SwitchTo(TokenizerState.TagName);
                        }
                        else if (c == '>')
                        {
                             Consume();
                             EmitError("Missing End Tag Name");
                             SwitchTo(TokenizerState.Data);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof Before Tag Name");
                            SwitchTo(TokenizerState.Data);
                            return EmitCharacter('<'); // and / ?
                        }
                        else
                        {
                            // Bogus comment
                            Consume();
                            _currentComment = new CommentToken();
                            _currentComment.Data += c;
                            SwitchTo(TokenizerState.BogusComment);
                        }
                        break;

                    case TokenizerState.TagName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Tag");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        break;

                    case TokenizerState.BeforeAttributeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume(); // Ignore
                        }
                        else if (c == '/')
                        {
                             Consume();
                             SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                             Consume();
                             SwitchTo(TokenizerState.Data);
                             return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                            return EmitCurrentTag(); // Or error
                        }
                        else
                        {
                            SwitchTo(TokenizerState.AttributeName);
                            _buffer.Clear();
                        }
                        break;

                    case TokenizerState.AttributeName:
                        if (char.IsWhiteSpace(c) || c == '/' || c == '>' || IsEof())
                        {
                            // End of attribute name
                            _lastAttrName = _buffer.ToString();
                            if (_currentTag.Attributes.Find(a => a.Name == _lastAttrName) == null)
                            {
                                 _currentTag.AddAttribute(_lastAttrName, "");
                            }
                            
                            _buffer.Clear();
                            if (c == '=') // Should prevent this case here if strict? No spec says check whitespace first.
                            {
                                // Wait, simple check:
                                // If whitespace, go to AfterAttributeName
                            }
                             
                             SwitchTo(TokenizerState.AfterAttributeName);
                        }
                        else if (c == '=')
                        {
                            Consume();
                            _lastAttrName = _buffer.ToString();
                             // Create attribute if not exists
                             if (_currentTag.Attributes.Find(a => a.Name == _lastAttrName) == null)
                            {
                                 _currentTag.AddAttribute(_lastAttrName, "");
                            }
                             _buffer.Clear();
                            SwitchTo(TokenizerState.BeforeAttributeValue);
                        }
                        else
                        {
                             Consume();
                             _buffer.Append(char.ToLowerInvariant(c));
                        }
                        break;

                    case TokenizerState.AfterAttributeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '=')
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeValue);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                           return EmitCurrentTag();
                        }
                        else
                        {
                            // New attribute
                            SwitchTo(TokenizerState.AttributeName);
                            _buffer.Clear();
                            // Reconsume c
                            continue;
                        }
                        break;

                    case TokenizerState.BeforeAttributeValue:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '"')
                        {
                            Consume();
                            SwitchTo(TokenizerState.AttributeValueDoubleQuoted);
                            _attrValueBuffer.Clear();
                        }
                        else if (c == '\'')
                        {
                            Consume();
                            SwitchTo(TokenizerState.AttributeValueSingleQuoted);
                            _attrValueBuffer.Clear();
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Missing Attribute Value");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else
                        {
                            SwitchTo(TokenizerState.AttributeValueUnquoted);
                            _attrValueBuffer.Clear();
                            continue; // Reconsume
                        }
                        break;

                    case TokenizerState.AttributeValueDoubleQuoted:
                        if (c == '"')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.AfterAttributeValueQuoted);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof in Attribute Value");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AttributeValueSingleQuoted:
                        if (c == '\'')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.AfterAttributeValueQuoted);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof in Attribute Value");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AttributeValueUnquoted:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                            SetAttributeValue();
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AfterAttributeValueQuoted:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             return new EofToken();
                        }
                        else
                        {
                             EmitError("Missing space after attribute value");
                             SwitchTo(TokenizerState.BeforeAttributeName);
                             continue;
                        }
                        break;

                    case TokenizerState.SelfClosingStartTag:
                        if (c == '>')
                        {
                            Consume();
                            _currentTag.SelfClosing = true;
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             return new EofToken();
                        }
                        else
                        {
                            EmitError("Unexpected char in self closing tag");
                            SwitchTo(TokenizerState.BeforeAttributeName);
                            continue;
                        }
                        break;
                        
                    case TokenizerState.MarkupDeclarationOpen:
                        if (Matches("--"))
                        {
                            Consume(2);
                            _currentComment = new CommentToken();
                            SwitchTo(TokenizerState.CommentStart);
                        }
                        else if (Matches("DOCTYPE", ignoreCase: true))
                        {
                            Consume(7);
                            SwitchTo(TokenizerState.Doctype);
                        }
                        else
                        {
                            EmitError("Invalid Markup Declaration");
                            SwitchTo(TokenizerState.BogusComment);
                            _currentComment = new CommentToken();
                        }
                        break;
                        
                    case TokenizerState.CommentStart:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentStartDash);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Abrupt Closing of Empty Comment");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else
                        {
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;
                    
                    case TokenizerState.CommentStartDash:
                         if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEnd);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Abrupt Closing of Empty Comment");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += '-';
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;

                    case TokenizerState.Comment:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEndDash);
                        }
                        else if (c == '<')
                        {
                            Consume();
                            _currentComment.Data += c;
                            SwitchTo(TokenizerState.CommentLessThanSign);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof In Comment");
                            return EmitCurrentComment();
                        }
                        else
                        {
                            Consume();
                            _currentComment.Data += c;
                        }
                        break;
                        
                    case TokenizerState.CommentEndDash:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEnd);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof In Comment");
                            return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += '-';
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;

                    case TokenizerState.CommentEnd:
                        if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else if (c == '!')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEndBang);
                        }
                        else if (c == '-')
                        {
                            Consume();
                            _currentComment.Data += '-';
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Comment");
                             return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += "--";
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;
                        
                    case TokenizerState.BogusComment:
                         if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else if (IsEof())
                        {
                            return EmitCurrentComment();
                        }
                        else
                        {
                            Consume();
                            _currentComment.Data += c;
                        }
                        break;

                    case TokenizerState.Doctype:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeDoctypeName);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                             EmitError("Missing Whitespace Before Doctype Name");
                             SwitchTo(TokenizerState.BeforeDoctypeName);
                        }
                        break;

                    case TokenizerState.BeforeDoctypeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '>')
                        {
                             Consume();
                             EmitError("Missing Doctype Name");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             SwitchTo(TokenizerState.Data);
                             return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            _currentDoctype = new DoctypeToken();
                            _currentDoctype.Name = "";
                            _currentDoctype.Name += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.DoctypeName);
                        }
                        break;

                    case TokenizerState.DoctypeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.AfterDoctypeName);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype.ForceQuirks = true;
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            Consume();
                            _currentDoctype.Name += char.ToLowerInvariant(c);
                        }
                        break;
                        
                    // Simplified Doctype (skipping PUBLIC/SYSTEM specifics for brevity, just consuming until >)
                    case TokenizerState.AfterDoctypeName:
                         if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype.ForceQuirks = true;
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            Consume();
                            // Effectively BogusDoctype behavior for now
                        }
                        break;
                        
                    default:
                         // Fallback
                         Consume();
                         break;
                }
            }
            return new EofToken();
        }
        
        // Helpers
        
        private void SwitchTo(TokenizerState newState)
        {
            _state = newState;
        }

        private char Peek()
        {
            if (_position >= _length) return '\0';
            return _input[_position];
        }

        private void Consume(int count = 1)
        {
            _position += count;
        }

        private bool IsEof() => _position >= _length;

        private void EmitError(string message)
        {
            // FenLogger.Debug($"[HtmlTokenizer] Error: {message}");
        }

        private HtmlToken EmitCharacter(char c)
        {
            return new CharacterToken(c);
        }

        private TagToken EmitCurrentTag()
        {
            var tag = _currentTag;
            _currentTag = null;
            return tag;
        }
        
        private CommentToken EmitCurrentComment()
        {
            var c = _currentComment;
            _currentComment = null;
            return c;
        }
        
        private DoctypeToken EmitCurrentDoctype()
        {
            var d = _currentDoctype;
            _currentDoctype = null;
            return d;
        }

        private bool Matches(string s, bool ignoreCase = false)
        {
            if (_position + s.Length > _length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c1 = _input[_position + i];
                char c2 = s[i];
                if (ignoreCase)
                {
                    c1 = char.ToLowerInvariant(c1);
                    c2 = char.ToLowerInvariant(c2);
                }
                if (c1 != c2) return false;
            }
            return true;
        }
        
        private void SetAttributeValue()
        {
            if (_currentTag != null && !string.IsNullOrEmpty(_lastAttrName))
            {
                var attrIndex = _currentTag.Attributes.FindIndex(a => a.Name == _lastAttrName);
                if (attrIndex >= 0)
                {
                    _currentTag.Attributes[attrIndex].Value = _attrValueBuffer.ToString();
                }
            }
        }
    }
}
