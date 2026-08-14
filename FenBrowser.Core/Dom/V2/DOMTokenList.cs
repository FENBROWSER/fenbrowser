// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: DOMTokenList interface.
    /// https://dom.spec.whatwg.org/#interface-domtokenlist
    ///
    /// Represents a set of space-separated tokens (like class names).
    /// </summary>
    public sealed class DOMTokenList : IEnumerable<string>
    {
        private readonly Element _element;
        private readonly string _attributeName;

        private string[] _tokens;
        private string _cachedValue;

        internal DOMTokenList(Element element, string attributeName)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _attributeName = attributeName ?? throw new ArgumentNullException(nameof(attributeName));
        }

        public int Length
        {
            get
            {
                EnsureTokens();
                return _tokens.Length;
            }
        }

        [System.Runtime.CompilerServices.IndexerName("ItemAt")]
        public string this[int index]
        {
            get
            {
                EnsureTokens();
                if (index < 0 || index >= _tokens.Length)
                    return null;
                return _tokens[index];
            }
        }

        public string Item(int index) => this[index];

        public string Value
        {
            get => _element.GetAttribute(_attributeName) ?? "";
            set => _element.SetAttribute(_attributeName, value ?? "");
        }

        public bool Contains(string token)
        {
            ValidateToken(token);
            EnsureTokens();
            for (var i = 0; i < _tokens.Length; i++)
            {
                if (string.Equals(_tokens[i], token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void Add(params string[] tokens)
        {
            if (tokens == null)
                return;

            foreach (var token in tokens)
                ValidateToken(token);

            EnsureTokens();

            var newTokens = new List<string>(_tokens);
            foreach (var token in tokens)
            {
                if (!newTokens.Contains(token))
                    newTokens.Add(token);
            }

            RunUpdateSteps(newTokens);
        }

        public void Remove(params string[] tokens)
        {
            if (tokens == null)
                return;

            foreach (var token in tokens)
                ValidateToken(token);

            EnsureTokens();

            var newTokens = new List<string>(_tokens.Length);
            foreach (var t in _tokens)
            {
                bool remove = false;
                foreach (var token in tokens)
                {
                    if (t == token)
                    {
                        remove = true;
                        break;
                    }
                }
                if (!remove)
                    newTokens.Add(t);
            }

            RunUpdateSteps(newTokens);
        }

        public bool Toggle(string token, bool? force = null)
        {
            ValidateToken(token);

            bool present = ContainsValidated(token);

            if (force.HasValue)
            {
                if (force.Value)
                {
                    if (!present)
                        Add(token);
                    return true;
                }

                if (present)
                    Remove(token);
                return false;
            }

            if (present)
            {
                Remove(token);
                return false;
            }

            Add(token);
            return true;
        }

        public bool Replace(string oldToken, string newToken)
        {
            ValidateToken(oldToken);
            ValidateToken(newToken);

            EnsureTokens();

            int index = Array.IndexOf(_tokens, oldToken);
            if (index < 0)
                return false;

            if (Array.IndexOf(_tokens, newToken) >= 0)
            {
                Remove(oldToken);
            }
            else
            {
                var newTokens = new List<string>(_tokens);
                newTokens[index] = newToken;
                RunUpdateSteps(newTokens);
            }

            return true;
        }

        public bool Supports(string token)
        {
            if (!string.Equals(_attributeName, "sandbox", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_element.LocalName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomException("TypeError", "This DOMTokenList does not define supported tokens.");
            }

            if (token == null)
                token = string.Empty;

            return token.ToLowerInvariant() switch
            {
                "allow-downloads" => true,
                "allow-forms" => true,
                "allow-modals" => true,
                "allow-orientation-lock" => true,
                "allow-pointer-lock" => true,
                "allow-popups" => true,
                "allow-popups-to-escape-sandbox" => true,
                "allow-presentation" => true,
                "allow-same-origin" => true,
                "allow-scripts" => true,
                "allow-top-navigation" => true,
                "allow-top-navigation-by-user-activation" => true,
                "allow-top-navigation-to-custom-protocols" => true,
                _ => false,
            };
        }

        public IEnumerator<string> GetEnumerator()
        {
            EnsureTokens();
            foreach (var token in _tokens)
                yield return token;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private bool ContainsValidated(string token)
        {
            EnsureTokens();
            for (var i = 0; i < _tokens.Length; i++)
            {
                if (string.Equals(_tokens[i], token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw new DomException("SyntaxError", "Token cannot be empty");

            for (var i = 0; i < token.Length; i++)
            {
                if (IsAsciiWhitespace(token[i]))
                {
                    throw new DomException("InvalidCharacterError",
                        "Token cannot contain whitespace");
                }
            }
        }

        private static bool IsAsciiWhitespace(char value) =>
            value is ' ' or '\t' or '\r' or '\n' or '\f';

        private void EnsureTokens()
        {
            var currentValue = _element.GetAttribute(_attributeName) ?? "";

            if (_tokens != null && _cachedValue == currentValue)
                return;

            var parsedTokens = currentValue.Split(
                new[] { ' ', '\t', '\r', '\n', '\f' },
                StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var orderedTokens = new List<string>(parsedTokens.Length);
            foreach (var token in parsedTokens)
            {
                if (seen.Add(token))
                    orderedTokens.Add(token);
            }

            _tokens = orderedTokens.ToArray();
            _cachedValue = currentValue;
        }

        private void RunUpdateSteps(List<string> tokens)
        {
            if (tokens.Count == 0 && !_element.HasAttribute(_attributeName))
            {
                _tokens = Array.Empty<string>();
                _cachedValue = "";
                return;
            }

            var value = string.Join(" ", tokens);
            _element.SetAttribute(_attributeName, value);
            _tokens = tokens.ToArray();
            _cachedValue = value;
        }

        public override string ToString() => Value;
    }
}
