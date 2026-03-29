using System;

namespace FenBrowser.FenEngine.Core
{
    public sealed class JsThrownValueException : Exception
    {
        public const string ThrownValueDataKey = "ThrownValue";

        public FenValue ThrownValue { get; }

        public JsThrownValueException(FenValue thrownValue)
            : base("JavaScript thrown value")
        {
            ThrownValue = thrownValue;
        }

        public static Exception CreateBoundaryException(string message, FenValue thrownValue, Exception innerException = null)
        {
            var boundaryException = innerException == null
                ? new Exception(message)
                : new Exception(message, innerException);
            boundaryException.Data[ThrownValueDataKey] = thrownValue;
            return boundaryException;
        }

        public static bool TryExtract(Exception exception, out FenValue thrownValue)
        {
            thrownValue = FenValue.Undefined;
            if (exception == null)
            {
                return false;
            }

            if (exception is JsThrownValueException jsThrownValueException)
            {
                thrownValue = jsThrownValueException.ThrownValue;
                return true;
            }

            var thrownValueProperty = exception.GetType().GetProperty(nameof(ThrownValue));
            if (thrownValueProperty?.PropertyType == typeof(FenValue) &&
                thrownValueProperty.GetValue(exception) is FenValue propertyValue)
            {
                thrownValue = propertyValue;
                return true;
            }

            if (exception.Data?[ThrownValueDataKey] is FenValue dataValue)
            {
                thrownValue = dataValue;
                return true;
            }

            if (!ReferenceEquals(exception.InnerException, exception) &&
                TryExtract(exception.InnerException, out var nestedThrownValue))
            {
                thrownValue = nestedThrownValue;
                return true;
            }

            return false;
        }
    }
}
