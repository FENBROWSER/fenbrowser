using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using System.Collections.Generic;

namespace FenBrowser.Tests.Engine
{
    public class AncestorFilterTests
    {
        [Fact]
        public void AncestorFilter_Propagates_On_Append()
        {
            var grandParent = new Element("div");
            grandParent.SetAttribute("class", "grand");

            var parent = new Element("div");
            parent.SetAttribute("class", "parent");

            var child = new Element("span");
            child.SetAttribute("class", "child");

            // Build Tree
            grandParent.AppendChild(parent);
            parent.AppendChild(child);

            // Access property to ensure it's calculated (though it should be eager)
            Assert.True(grandParent.AncestorFilter == 0); // No ancestors
            Assert.True(parent.AncestorFilter != 0);
            Assert.True(child.AncestorFilter != 0);

            // Child filter should theoretically contain bits from grandparent
            // We can't easily check exact bits without exposing hash func, 
            // but we can check if it includes parent's filter.
            Assert.Equal(parent.AncestorFilter | parent.ComputeFeatureHash(), child.AncestorFilter);
            Assert.Equal(grandParent.AncestorFilter | grandParent.ComputeFeatureHash(), parent.AncestorFilter);
        }

        [Fact]
        public void AncestorFilter_Updates_On_StructureChange()
        {
            var root1 = new Element("div");
            root1.SetAttribute("id", "root1");

            var root2 = new Element("div");
            root2.SetAttribute("id", "root2");

            var child = new Element("span");

            // Attach to Root 1
            root1.AppendChild(child);
            long filter1 = child.AncestorFilter;
            Assert.NotEqual(0, filter1);

            // Move to Root 2
            root2.AppendChild(child); // Implicitly removes from root1
            long filter2 = child.AncestorFilter;
            Assert.NotEqual(0, filter2);
            
            // Filters should differ because ancestors differ
            Assert.NotEqual(filter1, filter2);
        }

        [Fact]
        public void AncestorFilter_Matches_Logic()
        {
            var root = new Element("div");
            root.SetAttribute("class", "foo");
            
            var child = new Element("span");
            root.AppendChild(child);

            // Should match .foo span
            Assert.True(SelectorMatcher.Matches(child, ".foo span"));

            // Should NOT match .bar span
            // This verifies that the filter doesn't break basic matching
            Assert.False(SelectorMatcher.Matches(child, ".bar span"));
        }
    }
}
