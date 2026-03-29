using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security
{
    /// <summary>
    /// Structured result for security and policy decisions.
    /// </summary>
    public sealed class SecurityDecision
    {
        private SecurityDecision(
            string policy,
            bool isAllowed,
            string code,
            string message,
            IReadOnlyDictionary<string, object> data)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            IsAllowed = isAllowed;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Data = Clone(data);
        }

        public string Policy { get; }

        public bool IsAllowed { get; }

        public string Code { get; }

        public string Message { get; }

        public Dictionary<string, object> Data { get; }

        public static SecurityDecision Allow(
            string policy,
            string code,
            string message,
            IReadOnlyDictionary<string, object> data = null)
        {
            return new SecurityDecision(policy, true, code, message, data);
        }

        public static SecurityDecision Deny(
            string policy,
            string code,
            string message,
            IReadOnlyDictionary<string, object> data = null)
        {
            return new SecurityDecision(policy, false, code, message, data);
        }

        public void Log(LogCategory category = LogCategory.Security, LogLevel? allowLevel = null)
        {
            var entry = new LogEntry
            {
                Category = category,
                Level = IsAllowed ? (allowLevel ?? LogLevel.Info) : LogLevel.Warn,
                Message = $"[{Policy}] {Message}"
            };
            entry.WithData("policy", Policy);
            entry.WithData("decision", IsAllowed ? "allow" : "deny");
            entry.WithData("code", Code);

            if (Data != null)
            {
                foreach (var pair in Data)
                {
                    entry.WithData(pair.Key, pair.Value);
                }
            }

            LogManager.Log(entry);
        }

        private static Dictionary<string, object> Clone(IReadOnlyDictionary<string, object> data)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var clone = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in data)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }
    }
}
