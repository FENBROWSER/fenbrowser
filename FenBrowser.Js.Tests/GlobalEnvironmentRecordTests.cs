using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GlobalEnvironmentRecordTests
{
    [Fact]
    public void GlobalThisIsExposedAsThisBinding()
    {
        var globalObject = new FakeGlobalObject();
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.FromInt32(99));

        Assert.True(env.HasThisBinding);
        Assert.False(env.HasSuperBinding);
        Assert.Null(env.WithBaseObject);
        Assert.Equal(99, env.GlobalThisValue.AsInt32());
    }

    [Fact]
    public void DeclarativeBindingShadowsGlobalObjectBinding()
    {
        // The declarative half is consulted first: a top-level `let x` must shadow a
        // same-named property of the global object (ECMA-262 9.1.1.4.6 step 2).
        var globalObject = new FakeGlobalObject();
        globalObject.Define("x", JsValue.FromInt32(1));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        env.CreateMutableBinding("x", deletable: false);
        env.InitializeBinding("x", JsValue.FromInt32(42));

        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("x", strict: true, out var value));
        Assert.Equal(42, value.AsInt32());
    }

    [Fact]
    public void MissingBindingFallsThroughToGlobalObject()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("g", JsValue.FromInt32(7));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.True(env.HasBinding("g"));
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("g", strict: true, out var value));
        Assert.Equal(7, value.AsInt32());
    }

    [Fact]
    public void StrictReadOfTrulyMissingNameReturnsNotFound()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);

        Assert.False(env.HasBinding("missing"));
        Assert.Equal(BindingOpResult.NotFound, env.GetBindingValue("missing", strict: true, out _));
    }

    [Fact]
    public void DuplicateLexicalDeclarationIsAlreadyDeclared()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);
        env.CreateMutableBinding("x", deletable: false);

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateMutableBinding("x", deletable: false));
        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateImmutableBinding("x", strict: true));
    }

    [Fact]
    public void HasLexicalDeclarationAndHasVarDeclarationTrackSeparately()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);
        env.CreateMutableBinding("lexical", deletable: false);
        env.TryRecordVarName("legacy");

        Assert.True(env.HasLexicalDeclaration("lexical"));
        Assert.False(env.HasLexicalDeclaration("legacy"));

        Assert.True(env.HasVarDeclaration("legacy"));
        Assert.False(env.HasVarDeclaration("lexical"));
    }

    [Fact]
    public void TryRecordVarNameIsIdempotent()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);

        Assert.True(env.TryRecordVarName("v"));
        Assert.False(env.TryRecordVarName("v"));
        Assert.Single(env.VarNamesSnapshotForTest, "v");
    }

    [Fact]
    public void DeleteBindingRoutesToTheOwningHalfAndClearsVarName()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("g", JsValue.FromInt32(1));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);
        env.TryRecordVarName("g");

        Assert.Equal(BindingOpResult.Ok, env.DeleteBinding("g"));
        Assert.Empty(env.VarNamesSnapshotForTest);
        Assert.False(env.HasBinding("g"));
    }

    [Fact]
    public void DeleteBindingOnMissingNameReturnsNotFound()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);

        Assert.Equal(BindingOpResult.NotFound, env.DeleteBinding("ghost"));
    }

    [Fact]
    public void HasRestrictedGlobalPropertyIdentifiesNonConfigurableOwnProperties()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("normal", JsValue.FromInt32(1));
        globalObject.DefineRestricted("undefined", JsValue.Undefined);
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.False(env.HasRestrictedGlobalProperty("normal"));
        Assert.False(env.HasRestrictedGlobalProperty("absent"));
        Assert.True(env.HasRestrictedGlobalProperty("undefined"));
    }

    [Fact]
    public void CanDeclareGlobalVarRequiresExistingPropertyOrExtensibleObject()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("existing", JsValue.FromInt32(1));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.True(env.CanDeclareGlobalVar("existing"));
        Assert.True(env.CanDeclareGlobalVar("newOne"));

        globalObject.IsExtensible = false;
        Assert.True(env.CanDeclareGlobalVar("existing"));
        Assert.False(env.CanDeclareGlobalVar("newOne"));
    }

    [Fact]
    public void CreateGlobalVarBindingAddsVarNameAndInstallsProperty()
    {
        var globalObject = new FakeGlobalObject();
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.Equal(BindingOpResult.Ok, env.CreateGlobalVarBinding("v", deletable: false));
        Assert.True(env.HasVarDeclaration("v"));
        Assert.True(globalObject.HasOwnProperty("v"));
    }

    [Fact]
    public void CreateGlobalVarBindingReusesExistingGlobalProperty()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("v", JsValue.FromInt32(7));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.Equal(BindingOpResult.Ok, env.CreateGlobalVarBinding("v", deletable: false));
        Assert.True(env.HasVarDeclaration("v"));

        // Value stays at 7 - we don't blow away an existing binding when var-hoisting.
        Assert.True(globalObject.TryGet("v", out var value));
        Assert.Equal(7, value.AsInt32());
    }

    [Fact]
    public void CreateGlobalVarBindingRejectsLexicalCollision()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);
        env.CreateMutableBinding("x", deletable: false);

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateGlobalVarBinding("x", deletable: false));
    }

    [Fact]
    public void CreateGlobalVarBindingRejectsWhenObjectFrozen()
    {
        var globalObject = new FakeGlobalObject { IsExtensible = false };
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.Equal(BindingOpResult.ConstAssignment, env.CreateGlobalVarBinding("v", deletable: false));
    }

    [Fact]
    public void SetMutableBindingRoutesToTheCorrectHalf()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("g", JsValue.FromInt32(1));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);
        env.CreateMutableBinding("local", deletable: false);
        env.InitializeBinding("local", JsValue.FromInt32(2));

        Assert.Equal(BindingOpResult.Ok, env.SetMutableBinding("local", JsValue.FromInt32(20), strict: true));
        Assert.Equal(BindingOpResult.Ok, env.SetMutableBinding("g", JsValue.FromInt32(99), strict: true));

        env.GetBindingValue("local", strict: true, out var localValue);
        Assert.Equal(20, localValue.AsInt32());
        Assert.True(globalObject.TryGet("g", out var globalValue));
        Assert.Equal(99, globalValue.AsInt32());
    }

    [Fact]
    public void CanDeclareGlobalFunctionAllowsMissingOnExtensible()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);

        Assert.True(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CanDeclareGlobalFunctionRejectsMissingOnFrozen()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject { IsExtensible = false }, JsValue.Undefined);

        Assert.False(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CanDeclareGlobalFunctionAllowsConfigurableExistingProperty()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.Define("fn", JsValue.FromInt32(1));
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.True(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CanDeclareGlobalFunctionAllowsNonConfigurableWritableEnumerable()
    {
        // Spec 9.1.1.4.16 step 4: non-configurable but writable+enumerable data is OK
        // because the redefinition is observationally identical apart from the value.
        var globalObject = new FakeGlobalObject();
        globalObject.DefineDataExplicit("fn", JsValue.FromInt32(1), configurable: false, writable: true, enumerable: true);
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.True(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CanDeclareGlobalFunctionRejectsNonConfigurableAccessorProperty()
    {
        // Step 3 fails (not configurable), step 4 fails (not a data descriptor), so
        // step 5 returns false.
        var globalObject = new FakeGlobalObject();
        globalObject.DefineAccessor("fn", configurable: false);
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.False(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CanDeclareGlobalFunctionRejectsNonConfigurableReadOnly()
    {
        var globalObject = new FakeGlobalObject();
        globalObject.DefineDataExplicit("fn", JsValue.FromInt32(1), configurable: false, writable: false, enumerable: true);
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.False(env.CanDeclareGlobalFunction("fn"));
    }

    [Fact]
    public void CreateGlobalFunctionBindingInstallsConfigurableDataProperty()
    {
        var globalObject = new FakeGlobalObject();
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.Equal(BindingOpResult.Ok, env.CreateGlobalFunctionBinding("fn", JsValue.FromInt32(7), deletable: true));
        Assert.True(globalObject.TryGet("fn", out var value));
        Assert.Equal(7, value.AsInt32());
        Assert.True(env.HasVarDeclaration("fn"));
    }

    [Fact]
    public void CreateGlobalFunctionBindingPreservesNonConfigurableExisting()
    {
        // Spec: when existing own property is NOT configurable but is writable+enumerable
        // (CanDeclareGlobalFunction returned true via step 4), we leave the attributes
        // intact and only update the value through [[Set]].
        var globalObject = new FakeGlobalObject();
        globalObject.DefineDataExplicit("fn", JsValue.FromInt32(0), configurable: false, writable: true, enumerable: true);
        var env = new GlobalEnvironmentRecord(globalObject, JsValue.Undefined);

        Assert.Equal(BindingOpResult.Ok, env.CreateGlobalFunctionBinding("fn", JsValue.FromInt32(42), deletable: true));
        Assert.True(globalObject.TryGet("fn", out var value));
        Assert.Equal(42, value.AsInt32());
        Assert.False(globalObject.IsOwnPropertyConfigurable("fn"));
    }

    [Fact]
    public void CreateGlobalFunctionBindingRejectsConflictWithLexicalDeclaration()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject(), JsValue.Undefined);
        env.CreateMutableBinding("fn", deletable: false);

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateGlobalFunctionBinding("fn", JsValue.FromInt32(1), deletable: true));
    }

    [Fact]
    public void CreateGlobalFunctionBindingRejectsWhenFrozenAndAbsent()
    {
        var env = new GlobalEnvironmentRecord(new FakeGlobalObject { IsExtensible = false }, JsValue.Undefined);

        Assert.Equal(BindingOpResult.ConstAssignment, env.CreateGlobalFunctionBinding("fn", JsValue.FromInt32(1), deletable: true));
    }

    private sealed class FakeGlobalObject : IGlobalObject
    {
        private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);

        public ObjectHandle? AsObjectHandle => null;

        public bool IsExtensible { get; set; } = true;

        public void Define(string name, JsValue value)
            => _store[name] = new Entry(value, Configurable: true, Writable: true, Enumerable: true, IsAccessor: false);

        public void DefineRestricted(string name, JsValue value)
            => _store[name] = new Entry(value, Configurable: false, Writable: true, Enumerable: true, IsAccessor: false);

        public void DefineDataExplicit(string name, JsValue value, bool configurable, bool writable, bool enumerable)
            => _store[name] = new Entry(value, configurable, writable, enumerable, IsAccessor: false);

        public void DefineAccessor(string name, bool configurable)
            => _store[name] = new Entry(JsValue.Undefined, configurable, Writable: false, Enumerable: true, IsAccessor: true);

        public bool HasProperty(string name) => _store.ContainsKey(name);

        public bool HasOwnProperty(string name) => _store.ContainsKey(name);

        public bool IsOwnPropertyConfigurable(string name)
            => !_store.TryGetValue(name, out var entry) || entry.Configurable;

        public bool IsOwnDataPropertyWritableEnumerable(string name)
            => _store.TryGetValue(name, out var entry) && !entry.IsAccessor && entry.Writable && entry.Enumerable;

        public bool TryGet(string name, out JsValue value)
        {
            if (_store.TryGetValue(name, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = JsValue.Undefined;
            return false;
        }

        public bool TrySet(string name, JsValue value)
        {
            if (_store.TryGetValue(name, out var entry))
            {
                _store[name] = entry with { Value = value };
            }
            else
            {
                _store[name] = new Entry(value, Configurable: true, Writable: true, Enumerable: true, IsAccessor: false);
            }

            return true;
        }

        public bool DefineMutableData(string name, JsValue value, bool deletable)
        {
            _store[name] = new Entry(value, Configurable: deletable, Writable: true, Enumerable: true, IsAccessor: false);
            return true;
        }

        public bool DeleteProperty(string name)
        {
            if (_store.TryGetValue(name, out var entry) && !entry.Configurable)
            {
                return false;
            }

            return _store.Remove(name);
        }

        private readonly record struct Entry(
            JsValue Value,
            bool Configurable,
            bool Writable,
            bool Enumerable,
            bool IsAccessor);
    }
}
