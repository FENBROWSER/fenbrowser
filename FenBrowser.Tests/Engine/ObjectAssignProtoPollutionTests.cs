using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec / Security: ECMA-262 §7.3.25 CopyDataProperties (used by
    // Object.assign) calls CreateDataPropertyOrThrow, which is
    // [[DefineOwnProperty]] semantics. It must never fire the inherited
    // __proto__ accessor on Object.prototype - if it does, the attacker-
    // controlled key "__proto__" gets to mutate the target's prototype
    // chain. This is the classic Node.js-style prototype pollution attack.
    [Collection("Engine Tests")]
    public class ObjectAssignProtoPollutionTests
    {
        [Fact]
        public void ObjectAssign_DoesNotMutatePrototypeChainOfTarget()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                var target = {};
                var hostile = JSON.parse('{""__proto__"":{""isAdmin"":true}}');
                Object.assign(target, hostile);
                var inheritedIsAdmin = ({}).isAdmin;
                var ownProto = Object.prototype.isAdmin;
            ");

            // Object.prototype must not have gained an isAdmin property.
            // If pollution happened, every plain object inherits it.
            Assert.True(rt.GetGlobal("inheritedIsAdmin").IsUndefined,
                "Object.assign must not let __proto__ pollute Object.prototype");
            Assert.True(rt.GetGlobal("ownProto").IsUndefined,
                "Object.prototype must remain free of attacker keys");
        }

        [Fact]
        public void ObjectAssign_DoesNotChangeTargetActualPrototype()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                var target = Object.assign({}, JSON.parse('{""__proto__"":{""sneaky"":true}}'));
                // If pollution succeeded, target's [[Prototype]] would now be
                // the {sneaky:true} object instead of Object.prototype.
                var lookup = target.sneaky;
                var protoIsObjectPrototype =
                    Object.getPrototypeOf(target) === Object.prototype;
            ");

            Assert.True(rt.GetGlobal("lookup").IsUndefined,
                "target.sneaky must not be reachable - pollution was blocked");
            Assert.True(rt.GetGlobal("protoIsObjectPrototype").ToBoolean(),
                "target's [[Prototype]] must remain Object.prototype after Object.assign");
        }

        [Fact]
        public void ObjectAssign_OtherKeysStillCopyNormally()
        {
            var rt = new FenRuntime();
            rt.ExecuteSimple(@"
                var target = {};
                Object.assign(target, {a: 1, b: 'two'});
                var a = target.a;
                var b = target.b;
            ");

            Assert.Equal(1d, rt.GetGlobal("a").ToNumber());
            Assert.Equal("two", rt.GetGlobal("b").ToString());
        }
    }
}
