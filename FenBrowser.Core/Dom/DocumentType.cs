namespace FenBrowser.Core.Dom
{
    public class DocumentType : Node
    {
        public override NodeType NodeType => NodeType.DocumentType;
        public override string NodeName => Name ?? "html";

        public string Name { get; set; }
        public string PublicId { get; set; }
        public string SystemId { get; set; }

        public DocumentType(string name = "html", string publicId = null, string systemId = null)
        {
            Name = name;
            PublicId = publicId;
            SystemId = systemId;
        }

        public override string ToString()
        {
            return $"<!DOCTYPE {Name}>";
        }

        public override Node CloneNode(bool deep)
        {
            return new DocumentType(Name, PublicId, SystemId);
        }
    }
}
