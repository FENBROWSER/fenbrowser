namespace FenBrowser.Core.Dom
{
    public class MutationObserverInit
    {
        public bool ChildList { get; set; }
        public bool Attributes { get; set; }
        public bool CharacterData { get; set; }
        public bool Subtree { get; set; }
        public bool AttributeOldValue { get; set; }
        public bool CharacterDataOldValue { get; set; }
        public string[] AttributeFilter { get; set; }
    }
}
