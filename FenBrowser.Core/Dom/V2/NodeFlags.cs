// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Node type enumeration.
    /// https://dom.spec.whatwg.org/#interface-node
    /// </summary>
    public enum NodeType : ushort
    {
        Element = 1,
        Attribute = 2,  // Legacy, Attr is not a Node in modern DOM
        Text = 3,
        CDataSection = 4,
        EntityReference = 5,  // Legacy
        Entity = 6,  // Legacy
        ProcessingInstruction = 7,
        Comment = 8,
        Document = 9,
        DocumentType = 10,
        DocumentFragment = 11,
        Notation = 12  // Legacy
    }

    /// <summary>
    /// DOM Living Standard: Document position flags for compareDocumentPosition.
    /// https://dom.spec.whatwg.org/#dom-node-comparedocumentposition
    /// </summary>
    [Flags]
    public enum DocumentPosition : ushort
    {
        Disconnected = 0x01,
        Preceding = 0x02,
        Following = 0x04,
        Contains = 0x08,
        ContainedBy = 0x10,
        ImplementationSpecific = 0x20
    }

    /// <summary>
    /// Internal node flags bitfield for fast type/state checks.
    /// Chromium pattern: avoids virtual dispatch overhead.
    /// </summary>
    [Flags]
    internal enum NodeFlags : uint
    {
        None = 0,

        // --- Node Type Bits (mutually exclusive, bits 0-7) ---
        IsElement = 1 << 0,
        IsText = 1 << 1,
        IsComment = 1 << 2,
        IsDocument = 1 << 3,
        IsDocumentType = 1 << 4,
        IsDocumentFragment = 1 << 5,
        IsProcessingInstruction = 1 << 6,
        IsCDataSection = 1 << 7,

        // --- Capability Bits (bits 8-15) ---
        /// <summary>Node can have children (Document, Element, DocumentFragment)</summary>
        IsContainer = 1 << 8,
        /// <summary>Node is connected to a document tree</summary>
        IsConnected = 1 << 9,
        /// <summary>Node is in a shadow tree</summary>
        InShadowTree = 1 << 10,
        /// <summary>Element has a shadow root attached</summary>
        HasShadowRoot = 1 << 11,
        /// <summary>Node needs slot assignment (for shadow DOM)</summary>
        NeedsSlotAssignment = 1 << 12,

        // --- Dirty Flags (bits 16-23) ---
        /// <summary>This node's style needs recalculation</summary>
        StyleDirty = 1 << 16,
        /// <summary>A descendant's style needs recalculation</summary>
        ChildStyleDirty = 1 << 17,
        /// <summary>This node's layout needs recalculation</summary>
        LayoutDirty = 1 << 18,
        /// <summary>A descendant's layout needs recalculation</summary>
        ChildLayoutDirty = 1 << 19,
        /// <summary>This node needs repainting</summary>
        PaintDirty = 1 << 20,
        /// <summary>A descendant needs repainting</summary>
        ChildPaintDirty = 1 << 21,

        // --- Optimization Hints (bits 24-31) ---
        /// <summary>Element has ID attribute (for fast getElementById)</summary>
        HasId = 1 << 24,
        /// <summary>Element has class attribute</summary>
        HasClass = 1 << 25,
        /// <summary>Element has style attribute</summary>
        HasStyleAttribute = 1 << 26,
        /// <summary>Element has event listeners</summary>
        HasEventListeners = 1 << 27,
    }

    /// <summary>
    /// Invalidation kind flags for MarkDirty.
    /// </summary>
    [Flags]
    public enum InvalidationKind
    {
        None = 0,
        Style = 1 << 0,
        Layout = 1 << 1,
        Paint = 1 << 2,
        All = Style | Layout | Paint
    }

    /// <summary>
    /// Quirks mode enumeration per HTML spec.
    /// https://html.spec.whatwg.org/multipage/parsing.html#the-initial-insertion-mode
    /// </summary>
    public enum QuirksMode
    {
        NoQuirks,
        Quirks,
        LimitedQuirks
    }

    /// <summary>
    /// Shadow root mode per DOM spec.
    /// https://dom.spec.whatwg.org/#shadowroot-mode
    /// </summary>
    public enum ShadowRootMode
    {
        Open,
        Closed
    }

    /// <summary>
    /// Slot assignment mode for shadow DOM.
    /// </summary>
    public enum SlotAssignmentMode
    {
        Named,
        Manual
    }
}
