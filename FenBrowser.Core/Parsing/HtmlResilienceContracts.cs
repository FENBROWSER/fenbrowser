namespace FenBrowser.Core.Parsing
{
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
    }
}
