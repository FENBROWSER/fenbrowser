using System.Collections.Generic;

namespace FenBrowser.Core.Parsing
{
    public sealed class HtmlTokenizerError
    {
        public HtmlTokenizerError(string message, int offset, int line, int column)
        {
            Message = message;
            Offset = offset;
            Line = line;
            Column = column;
        }

        public string Message { get; }
        public int Offset { get; }
        public int Line { get; }
        public int Column { get; }
    }

    public enum HtmlParsingOutcomeClass
    {
        Success,
        Degraded,
        Failed
    }

    public enum HtmlParsingReasonCode
    {
        None,
        InputSizeLimitExceeded,
        TokenEmissionLimitExceeded,
        AttributeLimitExceeded,
        OpenElementsDepthLimitExceeded,
        MalformedInput,
        Exception
    }

    public sealed class HtmlParsingOutcome
    {
        public HtmlParsingOutcomeClass OutcomeClass { get; set; } = HtmlParsingOutcomeClass.Success;
        public HtmlParsingReasonCode ReasonCode { get; set; } = HtmlParsingReasonCode.None;
        public string Detail { get; set; }
        public bool IsRetryable { get; set; }
        public IReadOnlyList<HtmlTokenizerError> TokenizerErrors { get; set; } = System.Array.Empty<HtmlTokenizerError>();
        public int TokenizerErrorCount { get; set; }
        public bool TokenizerErrorsTruncated { get; set; }
    }
}
