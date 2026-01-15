using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// DOM Living Standard: DOMTokenList
    /// A set of unique space-separated tokens (e.g., className).
    /// </summary>
    public class DOMTokenList : IEnumerable<string>
    {
        private readonly List<string> _tokens = new();
        private readonly Element _element;
        private readonly string _attributeName;
        
        /// <summary>
        /// Create an empty DOMTokenList.
        /// </summary>
        public DOMTokenList() { }
        
        /// <summary>
        /// Create a DOMTokenList bound to an element's attribute.
        /// </summary>
        public DOMTokenList(Element element, string attributeName)
        {
            _element = element;
            _attributeName = attributeName;
            
            // Initialize from existing attribute
            if (element.Attributes.TryGetValue(attributeName, out var value))
            {
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_tokens.Contains(token))
                        _tokens.Add(token);
                }
            }
        }
        
        public int Length => _tokens.Count;
        
        public string this[int index] => index >= 0 && index < _tokens.Count ? _tokens[index] : null;
        
        /// <summary>
        /// Returns true if the token is present.
        /// </summary>
        public bool Contains(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            return _tokens.Contains(token);
        }
        
        /// <summary>
        /// Adds the given token(s) to the list.
        /// </summary>
        public void Add(params string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                ValidateToken(token);
                if (!_tokens.Contains(token))
                    _tokens.Add(token);
            }
            Sync();
        }
        
        /// <summary>
        /// Removes the given token(s) from the list.
        /// </summary>
        public void Remove(params string[] tokens)
        {
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                _tokens.Remove(token);
            }
            Sync();
        }
        
        /// <summary>
        /// If token exists, removes it and returns false.
        /// If token doesn't exist, adds it and returns true.
        /// If force is given, adds if true, removes if false.
        /// </summary>
        public bool Toggle(string token, bool? force = null)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            ValidateToken(token);
            
            if (force.HasValue)
            {
                if (force.Value)
                {
                    Add(token);
                    return true;
                }
                else
                {
                    Remove(token);
                    return false;
                }
            }
            
            if (_tokens.Contains(token))
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
        
        /// <summary>
        /// Replaces oldToken with newToken.
        /// Returns true if oldToken was found and replaced.
        /// </summary>
        public bool Replace(string oldToken, string newToken)
        {
            ValidateToken(oldToken);
            ValidateToken(newToken);
            
            int index = _tokens.IndexOf(oldToken);
            if (index < 0) return false;
            
            _tokens[index] = newToken;
            Sync();
            return true;
        }
        
        /// <summary>
        /// Returns true if the token is a valid token for an attribute that supports this list.
        /// </summary>
        public bool Supports(string token)
        {
            // By default, all tokens are supported
            return !string.IsNullOrWhiteSpace(token);
        }
        
        /// <summary>
        /// The serialized value of the list.
        /// </summary>
        public string Value
        {
            get => string.Join(" ", _tokens);
            set
            {
                _tokens.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!_tokens.Contains(token))
                            _tokens.Add(token);
                    }
                }
                Sync();
            }
        }
        
        public override string ToString() => Value;
        
        public IEnumerator<string> GetEnumerator() => _tokens.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        
        /// <summary>
        /// For backward compatibility: convert to HashSet.
        /// </summary>
        public HashSet<string> ToHashSet() => new HashSet<string>(_tokens);
        
        private void ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new DOMException("SyntaxError", "Token cannot be empty");
            if (token.Contains(' '))
                throw new DOMException("InvalidCharacterError", "Token cannot contain spaces");
        }
        
        private void Sync()
        {
            if (_element != null && _attributeName != null)
            {
                if (_tokens.Count > 0)
                    _element.Attributes[_attributeName] = Value;
                else
                    _element.Attributes.Remove(_attributeName);
                
                // Mark style dirty (classes affect styling)
                _element.MarkDirty(InvalidationKind.Style);
            }
        }
    }
    
    /// <summary>
    /// Simple DOM Exception for spec compliance.
    /// </summary>
    public class DOMException : Exception
    {
        public string Name { get; }
        
        public DOMException(string name, string message) : base(message)
        {
            Name = name;
        }
    }
}
