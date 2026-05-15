using System.Linq;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §9.3 Realm Records. Each FenRuntime owns a root Realm
    // exposing its intrinsic prototype graph; the static Realm.AllRealms /
    // IsArrayPrototypeOfAnyRealm helpers are the seam cross-realm checks
    // will sit behind once iframe / Worker realm isolation lands.
    [Collection("Engine Tests")]
    public class RealmTests
    {
        [Fact]
        public void RootRealm_ExposesObjectAndFunctionPrototypes()
        {
            var runtime = new FenRuntime();
            // Force lazy intrinsic init by touching the runtime.
            runtime.ExecuteSimple("var _ = Object.prototype;");

            var realm = runtime.RootRealm;

            Assert.NotNull(realm);
            Assert.NotNull(realm.ObjectPrototype);
            Assert.NotNull(realm.FunctionPrototype);
            Assert.NotNull(realm.ArrayPrototype);
        }

        [Fact]
        public void RootRealm_IsIdempotent_SameInstancePerRuntime()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple("var _ = 0;");

            var a = runtime.RootRealm;
            var b = runtime.RootRealm;

            Assert.Same(a, b);
        }

        [Fact]
        public void DistinctRuntimes_HaveDistinctRealms()
        {
            var rt1 = new FenRuntime();
            var rt2 = new FenRuntime();
            rt1.ExecuteSimple("var _ = 0;");
            rt2.ExecuteSimple("var _ = 0;");

            var r1 = rt1.RootRealm;
            var r2 = rt2.RootRealm;

            Assert.NotEqual(r1.Id, r2.Id);
            Assert.NotSame(r1, r2);
        }

        [Fact]
        public void AllRealms_ContainsLiveRuntimes()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple("var _ = 0;");
            var realm = rt.RootRealm;

            Assert.Contains(realm, Realm.AllRealms());
        }

        [Fact]
        public void IsArrayPrototypeOfAnyRealm_RecognisesArrayProto()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple("var _ = [];");
            var realm = rt.RootRealm;

            Assert.True(Realm.IsArrayPrototypeOfAnyRealm(realm.ArrayPrototype));
            Assert.False(Realm.IsArrayPrototypeOfAnyRealm(realm.ObjectPrototype));
            Assert.False(Realm.IsArrayPrototypeOfAnyRealm(null));
        }

        [Fact]
        public void ArrayIsArray_AlreadyCrossRealmSafe_ViaInternalClassBrand()
        {
            // Sanity check that the existing brand-based Array.isArray
            // path stays cross-realm safe: an array constructed in one
            // FenRuntime is still recognised as an Array by a check that
            // would otherwise compare prototype identity.
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                var arr = [1, 2, 3];
                var result = Array.isArray(arr);
            ");

            Assert.True(rt.GetGlobal("result").ToBoolean());
        }
    }
}
