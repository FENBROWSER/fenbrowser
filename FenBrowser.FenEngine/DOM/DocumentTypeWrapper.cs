using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    public sealed class DocumentTypeWrapper : NodeWrapper
    {
        private readonly DocumentType _documentType;

        public DocumentTypeWrapper(DocumentType documentType, IExecutionContext context)
            : base(documentType, context)
        {
            _documentType = documentType;
        }

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key)
            {
                case "name":
                    return FenValue.FromString(_documentType.Name);
                case "publicId":
                    return FenValue.FromString(_documentType.PublicId);
                case "systemId":
                    return FenValue.FromString(_documentType.SystemId);
            }

            return base.Get(key, context);
        }
    }
}
