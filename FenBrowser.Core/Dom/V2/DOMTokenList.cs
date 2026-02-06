// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

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
            if (tokens == null || tokens.Length == 0)
                return;

            // Validate tokens
            foreach (var token in tokens)
            {
                ValidateToken(token);
            }

            EnsureTokens();

            // Build new token list
            var newTokens = new List<string>(_tokens);
            foreach (var token in tokens)
            {
                if (!newTokens.Contains(token))
                    newTokens.Add(token);
            }

            // Update attribute
            UpdateAttribute(newTokens);
        }

        /// <summary>
        /// Removes the given tokens.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-remove
        /// </summary>
        public void Remove(params string[] tokens)
        {
            if (tokens == null || tokens.Length == 0)
                return;

            // Validate tokens
            foreach (var token in tokens)
            {
                ValidateToken(token);
            }

            EnsureTokens();

            // Build new token list without the specified tokens
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

            // Update attribute
            UpdateAttribute(newTokens);
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
                else
                {
                    if (present)
                        Remove(token);
                    return false;
                }
            }
            else
            {
                if (present)
                {
                    Remove(token);
                    return false;
                }
                else
                {
                    Add(token);
                    return true;
                }
            }
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

            // Check if new token already exists
            if (Array.IndexOf(_tokens, newToken) >= 0)
            {
                // Just remove old token
                Remove(oldToken);
            }
            else
            {
                // Replace in place
                var newTokens = new List<string>(_tokens);
                newTokens[index] = newToken;
                UpdateAttribute(newTokens);
            }

            return true;
        }

        /// <summary>
        /// Returns true if the token is a valid supported token.
        /// https://dom.spec.whatwg.org/#dom-domtokenlist-supports
        /// </summary>
        public bool Supports(string token)
        {
            // For classList, all tokens are supported
            return true;
        }

        // --- IEnumerable Implementation ---

        public IEnumerator<string> GetEnumerator()
        {
            EnsureTokens();
            foreach (var token in _tokens)
                yield return token;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // --- Private Helpers ---

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

            // Check if cache is valid
            if (_tokens != null && _cachedValue == currentValue)
                return;

            // Parse tokens
            _tokens = currentValue.Split(
                new[] { ' ', '\t', '\r', '\n', '\f' },
                StringSplitOptions.RemoveEmptyEntries);
            _cachedValue = currentValue;
        }

        private void UpdateAttribute(List<string> tokens)
        {
            var value = string.Join(" ", tokens);
            _element.SetAttribute(_attributeName, value);

            // Update cache
            _tokens = tokens.ToArray();
            _cachedValue = value;
        }

        public override string ToString() => Value;
    }
}
