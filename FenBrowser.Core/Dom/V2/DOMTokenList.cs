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

        // Cached token list (invalidated on attribute change)
        private string[] _tokens;
        private string _cachedValue;

        /// <summary>
        /// Creates a DOMTokenList for the given element and attribute.
        /// </summary>
        internal DOMTokenList(Element element, string attributeName)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _attributeName = attributeName ?? throw new ArgumentNullException(nameof(attributeName));
        }

        /// <summary>
        /// Returns the number of tokens.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-length
        /// </summary>
        public int Length
        {
            get
            {
                EnsureTokens();
                return _tokens.Length;
            }
        }

        /// <summary>
        /// Returns the token at the specified index.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-item
        /// </summary>
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

        /// <summary>
        /// Returns the token at the specified index.
        /// </summary>
        public string Item(int index) => this[index];

        /// <summary>
        /// Gets or sets the underlying attribute value.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-value
        /// </summary>
        public string Value
        {
            get => _element.GetAttribute(_attributeName) ?? "";
            set => _element.SetAttribute(_attributeName, value ?? "");
        }

        /// <summary>
        /// Returns true if the token is present.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-contains
        /// </summary>
        public bool Contains(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            EnsureTokens();
            foreach (var t in _tokens)
            {
                if (t == token)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds the given tokens.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-add
        /// </summary>
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

        /// <summary>
        /// Removes the given tokens.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-remove
        /// </summary>
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

        /// <summary>
        /// Toggles a token. Returns true if the token is now present.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-toggle
        /// </summary>
        public bool Toggle(string token, bool? force = null)
        {
            ValidateToken(token);

            bool present = Contains(token);

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

        /// <summary>
        /// Replaces a token with another.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-replace
        /// </summary>
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

        /// <summary>
        /// Returns true if the token is one of the supported tokens defined for
        /// this element/attribute pair.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-supports
        /// </summary>
        public bool Supports(string token)
        {
            // classList has no specification-defined supported-token set, so the
            // DOM validation steps require TypeError rather than treating every
            // syntactically valid class name as "supported".
            if (!string.Equals(_attributeName, "sandbox", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_element.LocalName, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                throw new DomException("TypeError", "This DOMTokenList does not define supported tokens.");
            }

            if (token == null)
                token = string.Empty;

            // HTML iframe sandbox keywords are ASCII case-insensitive.
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

        private void ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw new DomException("SyntaxError", "Token cannot be empty");

            if (token.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '\f' }) >= 0)
                throw new DomException("InvalidCharacterError",
                    "Token cannot contain whitespace");
        }

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
            // DOMTokenList update steps explicitly preserve an absent attribute
            // when the token set is empty. This matters for calls such as
            // element.classList.remove("x") on an element with no class attribute.
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
