// SpecRef: ECMA-262 §9.3 Realms
// CapabilityId: JS-REALMS-01
// Determinism: strict

using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// ECMA-262 §9.3 Realm Record. A realm is the bundle of intrinsics
    /// (Object.prototype, Function.prototype, Array.prototype, etc.), the
    /// global object, and the global environment that a script executes
    /// against. Cross-realm operations (iframe with another window's
    /// document, Worker postMessage, etc.) require knowing which realm
    /// an object belongs to so brand checks and intrinsic comparisons
    /// stay correct.
    ///
    /// Today FenEngine runs every script in one shared realm. This type
    /// is the seam that future iframe/Worker isolation will hang off:
    /// a new realm has a fresh intrinsic graph, and cross-realm checks
    /// (Array.isArray, instanceof through %Symbol.hasInstance%, branded
    /// type guards) consult the registry instead of comparing against
    /// a single set of prototype singletons.
    /// </summary>
    public sealed class Realm
    {
        private static readonly object s_registryLock = new();
        private static readonly List<WeakReference<Realm>> s_registry = new();
        private static int s_nextId;

        public int Id { get; }
        public IObject ObjectPrototype { get; internal set; }
        public IObject FunctionPrototype { get; internal set; }
        public IObject ArrayPrototype { get; internal set; }
        public IObject ErrorPrototype { get; internal set; }
        public IObject PromisePrototype { get; internal set; }
        public FenObject GlobalObject { get; internal set; }
        public FenEnvironment GlobalEnv { get; internal set; }

        internal Realm()
        {
            Id = System.Threading.Interlocked.Increment(ref s_nextId);
            lock (s_registryLock)
            {
                s_registry.Add(new WeakReference<Realm>(this));
            }
        }

        /// <summary>
        /// Walks every live realm. Used by cross-realm brand checks such as
        /// Array.isArray, which today get away with an InternalClass check
        /// but in a multi-realm world must accept arrays from any realm.
        /// </summary>
        public static IEnumerable<Realm> AllRealms()
        {
            lock (s_registryLock)
            {
                for (int i = s_registry.Count - 1; i >= 0; i--)
                {
                    if (s_registry[i].TryGetTarget(out var realm))
                    {
                        yield return realm;
                    }
                    else
                    {
                        s_registry.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// True when <paramref name="proto"/> is the Array.prototype of any
        /// live realm. Lets a cross-realm Array check skip a brittle
        /// reference comparison against a single runtime's intrinsic.
        /// </summary>
        public static bool IsArrayPrototypeOfAnyRealm(IObject proto)
        {
            if (proto == null) return false;
            foreach (var realm in AllRealms())
            {
                if (ReferenceEquals(realm.ArrayPrototype, proto)) return true;
            }
            return false;
        }
    }
}
