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

    private sealed class FakeGlobalObject : IBindingObject
    {
        private readonly Dictionary<string, JsValue> _store = new(StringComparer.Ordinal);

        public ObjectHandle? AsObjectHandle => null;

        public void Define(string name, JsValue value) => _store[name] = value;

        public bool HasProperty(string name) => _store.ContainsKey(name);

        public bool TryGet(string name, out JsValue value) => _store.TryGetValue(name, out value!);

        public bool TrySet(string name, JsValue value)
        {
            _store[name] = value;
            return true;
        }

        public bool DefineMutableData(string name, JsValue value, bool deletable)
        {
            _ = deletable;
            _store[name] = value;
            return true;
        }

        public bool DeleteProperty(string name) => _store.Remove(name);
    }
}
