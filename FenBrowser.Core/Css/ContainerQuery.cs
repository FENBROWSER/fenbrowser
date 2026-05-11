using System.Collections.Generic;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// Represents a CSS container query rule
    /// </summary>
    public class ContainerQuery
    {
        public string Selector { get; set; }
        public List<QueryCondition> Conditions { get; set; } = new();
        public Dictionary<string, string> Rules { get; set; } = new();
    }
    
    /// <summary>
    /// Represents a condition in a container query (e.g., min-width: 400px)
    /// </summary>
    public class QueryCondition
    {
        public string Feature { get; set; }
        public string Value { get; set; }
        public bool IsNot { get; set; }
    }
}
