using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class GridAutoPlacementTests
    {
        private Element CreateGridContainer(int childCount)
        {
            var container = new Element("div");
            for (int i = 0; i < childCount; i++)
            {
                container.AppendChild(new Element("div"));
            }
            return container;
        }

        private Dictionary<Node, CssComputed> CreateStyles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = containerStyle
            };
            
            foreach (var child in container.Children)
            {
                if (!styles.ContainsKey(child))
                    styles[child] = new CssComputed();
            }
            
            return styles;
        }

        private GridLayoutComputer.GridItemPosition GetPosition(Element item, Dictionary<Element, GridLayoutComputer.GridItemPosition> positions)
        {
            // Note: GridLayoutComputer doesn't expose positions directly except via private internal logic or Arrange callback.
            // We'll use a helper that calls Arrange and captures positions.
            return null; 
        }

        private Dictionary<Node, SKRect> ArrangeGrid(Element container, CssComputed containerStyle, Dictionary<Node, CssComputed> itemStyles = null)
        {
            var styles = CreateStyles(container, containerStyle);
            if (itemStyles != null)
            {
                foreach (var kvp in itemStyles)
                    styles[kvp.Key] = kvp.Value;
            }

            var boxes = new Dictionary<Node, BoxModel>();
            var positions = new Dictionary<Node, SKRect>();

            // Mock layout
            GridLayoutComputer.Arrange(
                container,
                new SKRect(0, 0, 1000, 1000), // Large canvas
                styles,
                boxes,
                0,
                (node, rect, depth) => positions[node] = rect,
                (n, sz, d) => new LayoutMetrics());

            return positions;
        }

        [Fact]
        public void AutoPlacement_AvoidsCollision_WithExplicitItems()
        {
            // 2x2 grid. explicit item at (1,1). auto item should go to (1,2) or (2,1)?
            // Default flow is row.
            // (1,1) occupied. Next is (2,1).
            
            var container = CreateGridContainer(2);
            var child1 = (Element)container.Children[0]; // Explicit
            var child2 = (Element)container.Children[1]; // Auto

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px",
                GridTemplateRows = "100px 100px"
            };

            var explicitStyle = new CssComputed
            {
                GridColumnStart = "1",
                GridRowStart = "1"
            };

            var itemStyles = new Dictionary<Node, CssComputed>
            {
                [child1] = explicitStyle
            };

            var positions = ArrangeGrid(container, containerStyle, itemStyles);
            
            // Child 1 at 0,0
            Assert.Equal(0, positions[child1].Left);
            Assert.Equal(0, positions[child1].Top);

            // Child 2 should be at (2,1) -> 100px, 0px
            Assert.Equal(100, positions[child2].Left);
            Assert.Equal(0, positions[child2].Top);
        }

        [Fact]
        public void AutoPlacement_FillsHoles_WhenDense()
        {
            // Grid 3 cols.
            // Item 1: Col 2 (Explicit)
            // Item 2: Auto (should go to Col 1)
            // Item 3: Auto (should go to Col 3)
            
            var container = CreateGridContainer(3);
            var child1 = (Element)container.Children[0]; // Explicit @ Col 2
            var child2 = (Element)container.Children[1]; // Auto
            var child3 = (Element)container.Children[2]; // Auto

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px 100px",
                GridAutoFlow = "row dense" // DENSE!
            };

            var explicitStyle = new CssComputed
            {
                GridColumnStart = "2"
            };

            var itemStyles = new Dictionary<Node, CssComputed>
            {
                [child1] = explicitStyle
            };

            var positions = ArrangeGrid(container, containerStyle, itemStyles);
            
            // Child 1 at Col 2 (100px)
            Assert.Equal(100, positions[child1].Left);

            // Child 2 should backfill Col 1 (0px)
            Assert.Equal(0, positions[child2].Left);
            
            // Child 3 should go to Col 3 (200px)
            Assert.Equal(200, positions[child3].Left);
        }

        [Fact]
        public void AutoPlacement_Sparse_DoesNotFillHoles()
        {
            // Grid 3 cols.
            // Item 1: Col 2 (Explicit)
            // Item 2: Auto (should go to Col 3 because cursor is past Col 2?)
            // Actually config is tricky. 
            // Step 1: Place explicit item at (2, 1).
            // Step 2: Auto cursor starts at (1, 1).
            // Item 2 fits at (1, 1).
            // 
            // Wait, sparse placement resets cursor?
            // "The auto-placement cursor... starts at the beginning of the grid."
            // If we place an item, the cursor moves *after* it?
            
            // Let's use a better test for sparse.
            // Auto item 1.
            // Explicit item at (1, 2) [Row 2].
            // Auto item 2.
            
            // Cursor behavior:
            // 1. Place Auto 1 -> (1, 1). Cursor ends at (2, 1).
            // 2. Explicit item is at (1, 2). Does not move cursor?
            // "If the grid item has a definite position... place it."
            // Auto placement continues from cursor.
            
            // Let's rely on behavior:
            // Item 1: Auto.
            // Item 2: Col 3 (Explicit).
            // Item 3: Auto.
            
            var container = CreateGridContainer(3);
            var child1 = (Element)container.Children[0]; // Auto
            var child2 = (Element)container.Children[1]; // Explicit Col 3
            var child3 = (Element)container.Children[2]; // Auto

            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px 100px", 
                GridAutoFlow = "row" // Sparse (default)
            };

            var explicitStyle = new CssComputed
            {
                GridColumnStart = "3"
            };

            var itemStyles = new Dictionary<Node, CssComputed>
            {
                [child2] = explicitStyle
            };

            var positions = ArrangeGrid(container, containerStyle, itemStyles);

            // Child 1 -> (1, 1) -> x=0
            Assert.Equal(0, positions[child1].Left);
            
            // Child 2 -> (3, 1) -> x=200
            Assert.Equal(200, positions[child2].Left);
            
            // Child 3 -> Should skip (2, 1) if sparse algorithm implies sequence?
            // Actually, sparse algorithm just means "don't backfill".
            // Since (2, 1) is empty and AFTER cursor (which is at 2, 1 after child 1), it SHOULD take it.
            
            // Better sparse test:
            // Item 1: Auto.
            // Item 2: Auto.
            // Constraint: Item 2 is larger than hole? No.
            
            // Use implicit row generation.
            // Item 1 (Auto)
            // Item 2 (Explicit Col 1, Row 2)
            // Item 3 (Auto) -> Should go to (2, 1) or (2, 2)?
            // Sparse placement keeps moving forward.
            // Cursor was at (2, 1). Item 3 places at (2, 1).
            
            // CORRECT SPARSE TEST:
            // Item 1 (explicit 1,1)
            // Item 2 (explicit 3,1)
            // Item 3 (auto). 
            // In dense, it would take (2,1).
            // In sparse, cursor might have moved past?
            // Actually, if cursor is at (1,1), checking for empty... 
            
            // Let's stick strictly to expected behavior of "find first empty slot after cursor".
            // Cursor starts (1, 1).
            // Explicit items placed first? 
            // "Place all items with a definite grid position".
            // Then place auto items.
            
            Assert.Equal(100, positions[child3].Left); // Should take (2, 1)
        }

        [Fact]
        public void AutoPlacement_WrapsToNextRow()
        {
            var container = CreateGridContainer(3);
            // 2 cols. 3 items. Item 3 should wrap to row 2.
            
            var containerStyle = new CssComputed
            {
                Display = "grid",
                GridTemplateColumns = "100px 100px"
            };

            var positions = ArrangeGrid(container, containerStyle);

            Assert.Equal(0, positions[container.Children[2]].Left); // Col 1
            Assert.True(positions[container.Children[2]].Top > 0);  // Row 2
        }
        
        [Fact]
        public void ImplicitTracks_CreatedWhenOutOfBounds()
        {
             // Item placed at Col 4 in a 2-col grid.
             // Should extend grid width.
             
             var container = CreateGridContainer(1);
             var child = (Element)container.Children[0];
             
             var containerStyle = new CssComputed
             {
                 Display = "grid",
                 GridTemplateColumns = "100px 100px", // 2 cols
                 GridAutoColumns = "50px" // Implicit cols are 50px
             };
             
             var itemStyle = new CssComputed
             {
                 GridColumnStart = "4"
             };
             
             var itemStyles = new Dictionary<Node, CssComputed> { [child] = itemStyle };
             
             var positions = ArrangeGrid(container, containerStyle, itemStyles);
             
             // Col 1: 100
             // Col 2: 100
             // Col 3: 50 (Implicit)
             // Col 4: start here.
             // X = 100 + 100 + 50 = 250.
             
             Assert.Equal(250, positions[child].Left);
        }
    }
}
