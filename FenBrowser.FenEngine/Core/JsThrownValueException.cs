using System;

namespace FenBrowser.FenEngine.Core
{
    public sealed class JsThrownValueException : Exception
    {
        public FenValue ThrownValue { get; }

        public JsThrownValueException(FenValue thrownValue)
            : base("JavaScript thrown value")
        {
            ThrownValue = thrownValue;
        }
    }
}
