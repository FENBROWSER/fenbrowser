using System.Collections.Generic;
using System.Text;

namespace FenBrowser.FenEngine.HTML
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

    public abstract class HtmlToken
    {
        public HtmlTokenType Type { get; set; }
        
        public T As<T>() where T : HtmlToken
        {
            return (T)this;
        }
    }

    public class DoctypeToken : HtmlToken
    {
        public string Name { get; set; }
        public string PublicIdentifier { get; set; }
        public string SystemIdentifier { get; set; }
        public bool ForceQuirks { get; set; }

        public DoctypeToken() { Type = HtmlTokenType.Doctype; }
    }

    public class TagToken : HtmlToken
    {
        public string TagName { get; set; }
        public bool SelfClosing { get; set; }
        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>();

        // Helpers for construction
        internal string CurrentAttributeName;
        internal StringBuilder CurrentAttributeValue = new StringBuilder();

        public void StartAttribute(string name)
        {
            FlushAttribute();
            CurrentAttributeName = name;
            CurrentAttributeValue.Clear();
        }

        public void AppendAttributeValue(string value)
        {
            CurrentAttributeValue.Append(value);
        }
        public void AppendAttributeValue(char value)
        {
            CurrentAttributeValue.Append(value);
        }

        public void FlushAttribute()
        {
            if (!string.IsNullOrEmpty(CurrentAttributeName))
            {
                Attributes[CurrentAttributeName] = CurrentAttributeValue.ToString();
                CurrentAttributeName = null;
                CurrentAttributeValue.Clear();
            }
        }
    }

    public class StartTagToken : TagToken
    {
        public StartTagToken() { Type = HtmlTokenType.StartTag; }
    }

    public class EndTagToken : TagToken
    {
        public EndTagToken() { Type = HtmlTokenType.EndTag; }
    }

    public class CommentToken : HtmlToken
    {
        public StringBuilder Data { get; } = new StringBuilder();
        public CommentToken() { Type = HtmlTokenType.Comment; }
    }

    public class CharacterToken : HtmlToken
    {
        public StringBuilder Data { get; } = new StringBuilder();
        public CharacterToken() { Type = HtmlTokenType.Character; }
        public CharacterToken(char c) { Type = HtmlTokenType.Character; Data.Append(c); }
        public CharacterToken(string s) { Type = HtmlTokenType.Character; Data.Append(s); }
    }

    public class EofToken : HtmlToken
    {
        public EofToken() { Type = HtmlTokenType.EndOfFile; }
    }
}
