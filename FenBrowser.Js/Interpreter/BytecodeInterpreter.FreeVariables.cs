using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Reading and writing a variable a function does not declare through a
// FreeSlotSite: the record of how many scopes out the name resolved and what
// kind of binding it found there. The register-window loop keys its sites by
// slot; the dispatch loop and compiled code key theirs by instruction, because
// a block scope can give one name a nearer binding at one instruction than at
// another. Either way a site is only ever a hint - every use re-verifies the
// binding it points at, and a miss walks the chain as the specification does.
public sealed partial class BytecodeInterpreter
{
    /// <summary>
    /// Reads through <paramref name="site"/>, starting <see cref="FreeSlotSite.Hops"/>
    /// links out from <paramref name="start"/>. False when the site no longer
    /// describes the binding, or the binding is still in its dead zone.
    /// </summary>
    internal bool TryReadThroughSite(
        FreeSlotSite site, EnvironmentRecord? start, BytecodeFunction function, int icOffset, string name, out JsValue value)
    {
        var env = start;
        for (var hop = site.Hops; hop > 0 && env is not null; hop--)
        {
            env = env.OuterEnv;
        }

        if (site.SlotOwner is { } owner)
        {
            if (env is DeclarativeEnvironmentRecord declarative &&
                declarative.TryReadOwnSlot(owner, site.TargetSlot, out value))
            {
                return true;
            }
        }
        else if (env is GlobalEnvironmentRecord global &&
                 global.LexicalVersion == site.LexicalVersion &&
                 global.GlobalObjectHandle is { } globalHandle &&
                 TryGetLoadIC(function, icOffset, JsValue.FromObject(globalHandle), name, out value))
        {
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// The write half of <see cref="TryReadThroughSite"/>. A const, a binding in
    /// its dead zone, and a global property that is not a plain writable data
    /// property all refuse, and the caller's walk reports them.
    /// </summary>
    internal bool TryWriteThroughSite(
        FreeSlotSite site, EnvironmentRecord? start, BytecodeFunction function, int icOffset, string name, JsValue value)
    {
        var env = start;
        for (var hop = site.Hops; hop > 0 && env is not null; hop--)
        {
            env = env.OuterEnv;
        }

        if (site.SlotOwner is { } owner)
        {
            return env is DeclarativeEnvironmentRecord declarative &&
                   declarative.TryWriteOwnSlot(owner, site.TargetSlot, value);
        }

        // A hit proves an own writable data property, where SetMutableBinding
        // on the object record is exactly [[Set]].
        return env is GlobalEnvironmentRecord global &&
               global.LexicalVersion == site.LexicalVersion &&
               global.GlobalObjectHandle is { } globalHandle &&
               TryStoreIC(function, icOffset, globalHandle, JsValue.FromObject(globalHandle), name, value);
    }

    /// <summary>
    /// A site for a read that found <paramref name="name"/> in <paramref name="env"/>,
    /// <paramref name="hops"/> links out, or null when the binding is not one of
    /// the two cacheable shapes: a record with slot numbering, or a property of
    /// the global object (a top-level let or const lives on the global record's
    /// lexical half, which the property cache cannot see).
    /// </summary>
    internal FreeSlotSite? CreateReadSite(BytecodeFunction function, int icOffset, int hops, EnvironmentRecord env, string name)
    {
        if (env is GlobalEnvironmentRecord global)
        {
            if (global.HasLexicalDeclaration(name) || global.GlobalObjectHandle is not { } handle)
            {
                return null;
            }

            PopulateLoadIC(function, icOffset, JsValue.FromObject(handle), name);
            return FreeSlotSite.OnGlobalObject(hops, global.LexicalVersion);
        }

        return SlotSiteFor(hops, env, name);
    }

    /// <summary>The write counterpart of <see cref="CreateReadSite"/>.</summary>
    internal FreeSlotSite? CreateWriteSite(BytecodeFunction function, int icOffset, int hops, EnvironmentRecord env, string name)
    {
        if (env is GlobalEnvironmentRecord global)
        {
            if (global.HasLexicalDeclaration(name) || global.GlobalObjectHandle is not { } handle)
            {
                return null;
            }

            PopulateStoreIC(function, icOffset, JsValue.FromObject(handle), name);
            return FreeSlotSite.OnGlobalObject(hops, global.LexicalVersion);
        }

        return SlotSiteFor(hops, env, name);
    }

    private static FreeSlotSite? SlotSiteFor(int hops, EnvironmentRecord env, string name) =>
        env is DeclarativeEnvironmentRecord declarative &&
        declarative.SlotOwner is { } owner &&
        declarative.TryGetSlotIndex(name, out var targetSlot)
            ? FreeSlotSite.AtSlot(hops, owner, targetSlot)
            : null;
}
