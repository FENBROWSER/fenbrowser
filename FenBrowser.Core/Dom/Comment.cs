namespace FenBrowser.Core.Dom
{
    public class Comment : Node
    {
        public override NodeType NodeType => NodeType.Comment;
        public override string NodeName => "#comment";

        public string Data { get; set; }
        public override string NodeValue { get => Data; set => Data = value; }

        public Comment(string data)
        {
            Data = data;
        }

        public override Node CloneNode(bool deep)
        {
            return new Comment(Data);
        }
    }
}
