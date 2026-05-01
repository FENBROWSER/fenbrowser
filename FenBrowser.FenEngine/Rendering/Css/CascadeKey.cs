// SpecRef: CSS Cascade Level 5 layer ordering and importance sorting
// CapabilityId: CSS-CASCADE-LAYERS-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// Represents the full, spec-compliant ranking key for a CSS declaration in the cascade.
    /// Sorts ascending: lower value means lower priority, higher value wins.
    /// </summary>
    public readonly struct CascadeKey : IComparable<CascadeKey>
    {
        public readonly CssOrigin Origin;
        public readonly bool Important;
        public readonly ushort SpecificityA;
        public readonly ushort SpecificityB;
        public readonly ushort SpecificityC;
        public readonly int LayerOrder; // 0 for unlayered, >0 for explicit layers
        public readonly int ScopeProximity; // 0 for none, smaller means closer ancestor
        public readonly int StylesheetSourceOrder;
        public readonly int RuleOrder;
        public readonly int DeclarationOrder;

        public CascadeKey(
            CssOrigin origin,
            bool important,
            ushort specificityA,
            ushort specificityB,
            ushort specificityC,
            int layerOrder,
            int scopeProximity,
            int stylesheetSourceOrder,
            int ruleOrder,
            int declarationOrder)
        {
            Origin = origin;
            Important = important;
            SpecificityA = specificityA;
            SpecificityB = specificityB;
            SpecificityC = specificityC;
            LayerOrder = layerOrder;
            ScopeProximity = scopeProximity;
            StylesheetSourceOrder = stylesheetSourceOrder;
            RuleOrder = ruleOrder;
            DeclarationOrder = declarationOrder;
        }

        public int CompareTo(CascadeKey other)
        {
            // 1. Origin & Importance Transition (Level 5 Cascade)
            int thisWeight = GetOriginImportanceWeight(Origin, Important, LayerOrder);
            int otherWeight = GetOriginImportanceWeight(other.Origin, other.Important, other.LayerOrder);
            int weightDiff = thisWeight.CompareTo(otherWeight);
            if (weightDiff != 0) return weightDiff;

            // 2. Scope Proximity
            if (ScopeProximity != other.ScopeProximity)
            {
                // Unscoped (0) is lowest priority.
                // Among scoped, SMALLER distance means closer, which is HIGHER priority.
                // Since this CompareTo returns ascending (higher value = later = wins),
                // if both are scoped (>0), we want the smaller proximity to return a positive diff.
                if (ScopeProximity == 0) return -1;
                if (other.ScopeProximity == 0) return 1;
                return other.ScopeProximity.CompareTo(ScopeProximity);
            }

            // 3. Specificity
            if (SpecificityA != other.SpecificityA) return SpecificityA.CompareTo(other.SpecificityA);
            if (SpecificityB != other.SpecificityB) return SpecificityB.CompareTo(other.SpecificityB);
            if (SpecificityC != other.SpecificityC) return SpecificityC.CompareTo(other.SpecificityC);

            // 4. Source Order
            int sourceDiff = StylesheetSourceOrder.CompareTo(other.StylesheetSourceOrder);
            if (sourceDiff != 0) return sourceDiff;

            // 5. Rule Order
            int ruleDiff = RuleOrder.CompareTo(other.RuleOrder);
            if (ruleDiff != 0) return ruleDiff;

            // 6. Declaration Order
            return DeclarationOrder.CompareTo(other.DeclarationOrder);
        }

        private static int GetOriginImportanceWeight(CssOrigin origin, bool important, int layerOrder)
        {
            if (!important)
            {
                if (origin == CssOrigin.UserAgent) return 0;     // UA normal
                if (origin == CssOrigin.User) return 100;        // User normal
                if (origin == CssOrigin.Author)
                {
                    if (layerOrder == 0) return 300;             // Unlayered author
                    return 200 + Math.Min(layerOrder, 99);       // Layered author (higher layer = higher priority)
                }
            }
            else
            {
                if (origin == CssOrigin.Author)
                {
                    if (layerOrder == 0) return 400;             // Unlayered author important
                    return 510 - Math.Min(layerOrder, 100);      // Layered author important (lower layer = higher priority)
                }
                if (origin == CssOrigin.User) return 600;        // User important
                if (origin == CssOrigin.UserAgent) return 700;   // UA important
            }
            
            return 0;
        }

        public override string ToString()
        {
            return $"O={Origin},!={Important},Spec=({SpecificityA},{SpecificityB},{SpecificityC}),Sheet={StylesheetSourceOrder},Rule={RuleOrder},Decl={DeclarationOrder}";
        }
    }
}
