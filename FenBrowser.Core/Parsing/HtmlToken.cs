using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    public enum HtmlTokenType
    {
        Doctype,
        StartTag,
        EndTag,
        Comment,
        Character,
        EndOfFile
    }

    public class HtmlAttribute
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public HtmlAttribute(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }

    public abstract class HtmlToken
    {
        public HtmlTokenType Type { get; }

        protected HtmlToken(HtmlTokenType type)
        {
            Type = type;
        }
    }

    public class DoctypeToken : HtmlToken
    {
        public string Name { get; set; }
        public string PublicIdentifier { get; set; }
        public string SystemIdentifier { get; set; }
        public bool ForceQuirks { get; set; }

        public DoctypeToken() : base(HtmlTokenType.Doctype) { }
    }

    public abstract class TagToken : HtmlToken
    {
        public string TagName { get; set; }
        public bool SelfClosing { get; set; }
        public List<HtmlAttribute> Attributes { get; } = new List<HtmlAttribute>();

        protected TagToken(HtmlTokenType type) : base(type) { }

        public void AddAttribute(string name, string value)
        {
            // Duplicate attribute check could go here, or in the tokenizer
            Attributes.Add(new HtmlAttribute(name, value));
        }
    }

    public class StartTagToken : TagToken
    {
        public StartTagToken() : base(HtmlTokenType.StartTag) { }
    }

    public class EndTagToken : TagToken
    {
        public EndTagToken() : base(HtmlTokenType.EndTag) { }
    }

    public class CommentToken : HtmlToken
    {
        public string Data { get; set; } = "";
        public CommentToken() : base(HtmlTokenType.Comment) { }
    }

    public class CharacterToken : HtmlToken
    {
        public string Data { get; set; }

        public CharacterToken(string data) : base(HtmlTokenType.Character)
        {
            Data = data;
        }
        
        public CharacterToken(char c) : base(HtmlTokenType.Character)
        {
            Data = c.ToString();
        }
    }

    public class EofToken : HtmlToken
    {
        public EofToken() : base(HtmlTokenType.EndOfFile) { }
    }
}
