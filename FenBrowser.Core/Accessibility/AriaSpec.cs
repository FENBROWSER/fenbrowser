// W3C ARIA 1.2 specification data
// FenBrowser.Core.Accessibility

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// ARIA role enumeration (concrete + abstract). Enum names are designed so that
    /// ToString().ToLowerInvariant() returns the ARIA role string used in the spec.
    /// </summary>
    public enum AriaRole
    {
        None = 0,
        // Widget roles
        Alert,
        Alertdialog,
        Button,
        Checkbox,
        Combobox,
        Dialog,
        Feed,
        Figure,
        Form,
        Grid,
        Gridcell,
        Group,
        Heading,
        Img,
        Link,
        List,
        Listbox,
        Listitem,
        Log,
        Main,
        Marquee,
        Math,
        Menu,
        Menubar,
        Menuitem,
        Menuitemcheckbox,
        Menuitemradio,
        Navigation,
        Note,
        Option,
        Presentation,
        Progressbar,
        Radio,
        Radiogroup,
        Region,
        Row,
        Rowgroup,
        Rowheader,
        Scrollbar,
        Search,
        Searchbox,
        Separator,
        Slider,
        Spinbutton,
        Status,
        Switch,
        Tab,
        Table,
        Tablist,
        Tabpanel,
        Term,
        Textbox,
        Timer,
        Toolbar,
        Tooltip,
        Tree,
        Treegrid,
        Treeitem,
        // Landmark roles
        Banner,
        Complementary,
        Contentinfo,
        // Table / cell roles
        Cell,
        Columnheader,
        // Content roles
        Article,
        Code,
        Definition,
        Deletion,
        Directory,
        Document,
        Emphasis,
        Generic,
        Insertion,
        Paragraph,
        Strong,
        Subscript,
        Superscript,
        Term2,
        Time,
        Application,
        Caption,
    }

    public enum AriaPropertyType
    {
        Boolean,
        Tristate,
        Token,
        TokenList,
        IdRef,
        IdRefList,
        Integer,
        Number,
        String,
    }

    public sealed class AriaRoleInfo
    {
        public bool IsAbstract { get; }
        public bool IsLandmark { get; }
        /// <summary>True if accessible name may be computed from element text content.</summary>
        public bool NameFromContents { get; }
        /// <summary>True if accessible name MUST NOT be provided by author (e.g. none/presentation).</summary>
        public bool NameProhibited { get; }

        public AriaRoleInfo(
            bool isAbstract = false,
            bool isLandmark = false,
            bool nameFromContents = false,
            bool nameProhibited = false)
        {
            IsAbstract = isAbstract;
            IsLandmark = isLandmark;
            NameFromContents = nameFromContents;
            NameProhibited = nameProhibited;
        }
    }

    public sealed class AriaPropertyInfo
    {
        public AriaPropertyType Type { get; }
        public string[] AllowedTokens { get; }
        public bool IsGlobal { get; }

        public AriaPropertyInfo(AriaPropertyType type, bool isGlobal = false, string[] allowedTokens = null)
        {
            Type = type;
            IsGlobal = isGlobal;
            AllowedTokens = allowedTokens ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Static registry of ARIA roles and properties per ARIA 1.2.
    /// </summary>
    public static class AriaSpec
    {
        // Lookup from lowercase ARIA role name → enum value
        private static readonly Dictionary<string, AriaRole> RoleByName =
            new Dictionary<string, AriaRole>(StringComparer.OrdinalIgnoreCase);

        public static readonly Dictionary<AriaRole, AriaRoleInfo> Roles =
            new Dictionary<AriaRole, AriaRoleInfo>();

        public static readonly Dictionary<string, AriaPropertyInfo> Properties =
            new Dictionary<string, AriaPropertyInfo>(StringComparer.OrdinalIgnoreCase);

        static AriaSpec()
        {
            // ---- Roles ----
            Roles[AriaRole.None]             = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Presentation]     = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Generic]          = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Alert]            = new AriaRoleInfo();
            Roles[AriaRole.Alertdialog]      = new AriaRoleInfo();
            Roles[AriaRole.Application]      = new AriaRoleInfo();
            Roles[AriaRole.Article]          = new AriaRoleInfo();
            Roles[AriaRole.Banner]           = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Button]           = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Caption]          = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Cell]             = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Checkbox]         = new AriaRoleInfo();
            Roles[AriaRole.Code]             = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Columnheader]     = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Combobox]         = new AriaRoleInfo();
            Roles[AriaRole.Complementary]    = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Contentinfo]      = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Definition]       = new AriaRoleInfo();
            Roles[AriaRole.Deletion]         = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Dialog]           = new AriaRoleInfo();
            Roles[AriaRole.Directory]        = new AriaRoleInfo();
            Roles[AriaRole.Document]         = new AriaRoleInfo();
            Roles[AriaRole.Emphasis]         = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Feed]             = new AriaRoleInfo();
            Roles[AriaRole.Figure]           = new AriaRoleInfo();
            Roles[AriaRole.Form]             = new AriaRoleInfo();
            Roles[AriaRole.Grid]             = new AriaRoleInfo();
            Roles[AriaRole.Gridcell]         = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Group]            = new AriaRoleInfo();
            Roles[AriaRole.Heading]          = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Img]              = new AriaRoleInfo();
            Roles[AriaRole.Insertion]        = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Link]             = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.List]             = new AriaRoleInfo();
            Roles[AriaRole.Listbox]          = new AriaRoleInfo();
            Roles[AriaRole.Listitem]         = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Log]              = new AriaRoleInfo();
            Roles[AriaRole.Main]             = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Marquee]          = new AriaRoleInfo();
            Roles[AriaRole.Math]             = new AriaRoleInfo();
            Roles[AriaRole.Menu]             = new AriaRoleInfo();
            Roles[AriaRole.Menubar]          = new AriaRoleInfo();
            Roles[AriaRole.Menuitem]         = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Menuitemcheckbox] = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Menuitemradio]    = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Navigation]       = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Note]             = new AriaRoleInfo();
            Roles[AriaRole.Option]           = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Paragraph]        = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Progressbar]      = new AriaRoleInfo();
            Roles[AriaRole.Radio]            = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Radiogroup]       = new AriaRoleInfo();
            Roles[AriaRole.Region]           = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Row]              = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Rowgroup]         = new AriaRoleInfo();
            Roles[AriaRole.Rowheader]        = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Scrollbar]        = new AriaRoleInfo();
            Roles[AriaRole.Search]           = new AriaRoleInfo(isLandmark: true);
            Roles[AriaRole.Searchbox]        = new AriaRoleInfo();
            Roles[AriaRole.Separator]        = new AriaRoleInfo();
            Roles[AriaRole.Slider]           = new AriaRoleInfo();
            Roles[AriaRole.Spinbutton]       = new AriaRoleInfo();
            Roles[AriaRole.Status]           = new AriaRoleInfo();
            Roles[AriaRole.Strong]           = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Subscript]        = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Superscript]      = new AriaRoleInfo(nameProhibited: true);
            Roles[AriaRole.Switch]           = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Tab]              = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Table]            = new AriaRoleInfo();
            Roles[AriaRole.Tablist]          = new AriaRoleInfo();
            Roles[AriaRole.Tabpanel]         = new AriaRoleInfo();
            Roles[AriaRole.Term]             = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Textbox]          = new AriaRoleInfo();
            Roles[AriaRole.Time]             = new AriaRoleInfo();
            Roles[AriaRole.Timer]            = new AriaRoleInfo();
            Roles[AriaRole.Toolbar]          = new AriaRoleInfo();
            Roles[AriaRole.Tooltip]          = new AriaRoleInfo(nameFromContents: true);
            Roles[AriaRole.Tree]             = new AriaRoleInfo();
            Roles[AriaRole.Treegrid]         = new AriaRoleInfo();
            Roles[AriaRole.Treeitem]         = new AriaRoleInfo(nameFromContents: true);

            // Build name lookup from enum values
            foreach (var kvp in Roles)
            {
                var name = kvp.Key.ToString().ToLowerInvariant();
                RoleByName[name] = kvp.Key;
            }

            // ---- Properties ----
            // Global properties (valid on all roles)
            Properties["aria-atomic"]          = new AriaPropertyInfo(AriaPropertyType.Boolean,   isGlobal: true);
            Properties["aria-busy"]            = new AriaPropertyInfo(AriaPropertyType.Boolean,   isGlobal: true);
            Properties["aria-controls"]        = new AriaPropertyInfo(AriaPropertyType.IdRefList, isGlobal: true);
            Properties["aria-current"]         = new AriaPropertyInfo(AriaPropertyType.Token,     isGlobal: true,
                                                   allowedTokens: new[] { "page", "step", "location", "date", "time", "true", "false" });
            Properties["aria-describedby"]     = new AriaPropertyInfo(AriaPropertyType.IdRefList, isGlobal: true);
            Properties["aria-details"]         = new AriaPropertyInfo(AriaPropertyType.IdRef,     isGlobal: true);
            Properties["aria-disabled"]        = new AriaPropertyInfo(AriaPropertyType.Boolean,   isGlobal: true);
            Properties["aria-dropeffect"]      = new AriaPropertyInfo(AriaPropertyType.TokenList, isGlobal: true,
                                                   allowedTokens: new[] { "copy", "execute", "link", "move", "none", "popup" });
            Properties["aria-errormessage"]    = new AriaPropertyInfo(AriaPropertyType.IdRef,     isGlobal: true);
            Properties["aria-flowto"]          = new AriaPropertyInfo(AriaPropertyType.IdRefList, isGlobal: true);
            Properties["aria-grabbed"]         = new AriaPropertyInfo(AriaPropertyType.Tristate,  isGlobal: true);
            Properties["aria-haspopup"]        = new AriaPropertyInfo(AriaPropertyType.Token,     isGlobal: true,
                                                   allowedTokens: new[] { "false", "true", "menu", "listbox", "tree", "grid", "dialog" });
            Properties["aria-hidden"]          = new AriaPropertyInfo(AriaPropertyType.Boolean,   isGlobal: true);
            Properties["aria-invalid"]         = new AriaPropertyInfo(AriaPropertyType.Token,     isGlobal: true,
                                                   allowedTokens: new[] { "grammar", "false", "spelling", "true" });
            Properties["aria-keyshortcuts"]    = new AriaPropertyInfo(AriaPropertyType.String,    isGlobal: true);
            Properties["aria-label"]           = new AriaPropertyInfo(AriaPropertyType.String,    isGlobal: true);
            Properties["aria-labelledby"]      = new AriaPropertyInfo(AriaPropertyType.IdRefList, isGlobal: true);
            Properties["aria-live"]            = new AriaPropertyInfo(AriaPropertyType.Token,     isGlobal: true,
                                                   allowedTokens: new[] { "assertive", "off", "polite" });
            Properties["aria-owns"]            = new AriaPropertyInfo(AriaPropertyType.IdRefList, isGlobal: true);
            Properties["aria-relevant"]        = new AriaPropertyInfo(AriaPropertyType.TokenList, isGlobal: true,
                                                   allowedTokens: new[] { "additions", "all", "removals", "text" });
            Properties["aria-roledescription"] = new AriaPropertyInfo(AriaPropertyType.String,    isGlobal: true);

            // Widget-specific properties
            Properties["aria-activedescendant"] = new AriaPropertyInfo(AriaPropertyType.IdRef);
            Properties["aria-autocomplete"]    = new AriaPropertyInfo(AriaPropertyType.Token,
                                                   allowedTokens: new[] { "inline", "list", "both", "none" });
            Properties["aria-checked"]         = new AriaPropertyInfo(AriaPropertyType.Tristate,
                                                   allowedTokens: new[] { "false", "mixed", "true", "undefined" });
            Properties["aria-colcount"]        = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-colindex"]        = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-colspan"]         = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-expanded"]        = new AriaPropertyInfo(AriaPropertyType.Boolean,
                                                   allowedTokens: new[] { "false", "true", "undefined" });
            Properties["aria-level"]           = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-modal"]           = new AriaPropertyInfo(AriaPropertyType.Boolean);
            Properties["aria-multiline"]       = new AriaPropertyInfo(AriaPropertyType.Boolean);
            Properties["aria-multiselectable"] = new AriaPropertyInfo(AriaPropertyType.Boolean);
            Properties["aria-orientation"]     = new AriaPropertyInfo(AriaPropertyType.Token,
                                                   allowedTokens: new[] { "horizontal", "undefined", "vertical" });
            Properties["aria-placeholder"]     = new AriaPropertyInfo(AriaPropertyType.String);
            Properties["aria-posinset"]        = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-pressed"]         = new AriaPropertyInfo(AriaPropertyType.Tristate,
                                                   allowedTokens: new[] { "false", "mixed", "true", "undefined" });
            Properties["aria-readonly"]        = new AriaPropertyInfo(AriaPropertyType.Boolean);
            Properties["aria-required"]        = new AriaPropertyInfo(AriaPropertyType.Boolean);
            Properties["aria-rowcount"]        = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-rowindex"]        = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-rowspan"]         = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-selected"]        = new AriaPropertyInfo(AriaPropertyType.Boolean,
                                                   allowedTokens: new[] { "false", "true", "undefined" });
            Properties["aria-setsize"]         = new AriaPropertyInfo(AriaPropertyType.Integer);
            Properties["aria-sort"]            = new AriaPropertyInfo(AriaPropertyType.Token,
                                                   allowedTokens: new[] { "ascending", "descending", "none", "other" });
            Properties["aria-valuemax"]        = new AriaPropertyInfo(AriaPropertyType.Number);
            Properties["aria-valuemin"]        = new AriaPropertyInfo(AriaPropertyType.Number);
            Properties["aria-valuenow"]        = new AriaPropertyInfo(AriaPropertyType.Number);
            Properties["aria-valuetext"]       = new AriaPropertyInfo(AriaPropertyType.String);
        }

        /// <summary>
        /// Returns true if the role attribute value contains at least one recognized ARIA role token.
        /// </summary>
        public static bool IsValidRole(string roleAttrValue)
        {
            if (string.IsNullOrWhiteSpace(roleAttrValue)) return false;
            foreach (var token in roleAttrValue.Split(' '))
            {
                var t = token.Trim();
                if (!string.IsNullOrEmpty(t) && RoleByName.ContainsKey(t))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Parses the first recognized ARIA role from a role attribute value.
        /// Returns AriaRole.None when no recognized token is found.
        /// </summary>
        public static AriaRole ParseRole(string roleAttrValue)
        {
            if (string.IsNullOrWhiteSpace(roleAttrValue)) return AriaRole.None;
            foreach (var token in roleAttrValue.Split(' '))
            {
                var t = token.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(t) && RoleByName.TryGetValue(t, out var role))
                    return role;
            }
            return AriaRole.None;
        }

        /// <summary>
        /// Returns false when the value is clearly invalid for a constrained (token/boolean/integer/number)
        /// ARIA property. Returns true for unknown properties and unconstrained types.
        /// Per ARIA spec §6.2.4: invalid values are treated as the property's default—do NOT reject them.
        /// </summary>
        public static bool IsValidPropertyValue(string propName, string value)
        {
            if (!Properties.TryGetValue(propName, out var info)) return true;
            if (string.IsNullOrEmpty(value)) return true;

            switch (info.Type)
            {
                case AriaPropertyType.Boolean:
                    return value == "true" || value == "false";

                case AriaPropertyType.Tristate:
                    return value == "true" || value == "false" || value == "mixed" || value == "undefined";

                case AriaPropertyType.Token:
                    if (info.AllowedTokens.Length == 0) return true;
                    return Array.IndexOf(info.AllowedTokens, value.ToLowerInvariant()) >= 0;

                case AriaPropertyType.TokenList:
                    if (info.AllowedTokens.Length == 0) return true;
                    foreach (var token in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Array.IndexOf(info.AllowedTokens, token.ToLowerInvariant()) < 0)
                            return false;
                    }
                    return true;

                case AriaPropertyType.Integer:
                    return int.TryParse(value.Trim(), out _);

                case AriaPropertyType.Number:
                    return double.TryParse(value.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _);

                case AriaPropertyType.IdRef:
                case AriaPropertyType.IdRefList:
                case AriaPropertyType.String:
                default:
                    return true;
            }
        }
    }
}
