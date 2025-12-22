using System.Net;

namespace FenBrowser.Core.Dom
{
    public class Text : Node
    {
        public override NodeType NodeType => NodeType.Text;
        public override string NodeName => "#text";

        private string _data;
        public string Data 
        { 
            get => _data; 
            set 
            {
                _data = value;
                OnMutation?.Invoke(this, "characterData", null, null, null, null);
            }
        }

        public override string NodeValue 
        { 
            get => Data; 
            set => Data = value; 
        }

        public Text(string data)
        {
            _data = data;
        }

        public override string ToString()
        {
            return "#text: " + (_data ?? "");
        }
    }
}
