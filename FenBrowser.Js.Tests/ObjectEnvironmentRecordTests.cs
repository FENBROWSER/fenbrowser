using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectEnvironmentRecordTests
{
    [Fact]
    public void HasBindingDelegatesToBindingObject()
    {
        var backing = new FakeBindingObject();
        backing.DefineMutableData("x", JsValue.FromInt32(1), deletable: true);
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.True(env.HasBinding("x"));
        Assert.False(env.HasBinding("y"));
    }

    [Fact]
    public void CreateMutableBindingInstallsPropertyOnBindingObject()
    {
        var backing = new FakeBindingObject();
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.Ok, env.CreateMutableBinding("x", deletable: true));
        Assert.True(backing.HasProperty("x"));
    }

    [Fact]
    public void CreateImmutableBindingIsRejectedForObjectEnvRecords()
    {
        var env = new ObjectEnvironmentRecord(new FakeBindingObject(), isWithEnvironment: false, outerEnv: null);

        // ECMA-262 9.1.1.2.3: CreateImmutableBinding is intentionally never called on
        // an object env record. The interpreter should never request it; if it does
        // we surface NotInitializable rather than silently corrupting the object.
        Assert.Equal(BindingOpResult.NotInitializable, env.CreateImmutableBinding("k", strict: true));
    }

    [Fact]
    public void InitializeBindingSetsTheValue()
    {
        var backing = new FakeBindingObject();
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);
        env.CreateMutableBinding("x", deletable: true);

        Assert.Equal(BindingOpResult.Ok, env.InitializeBinding("x", JsValue.FromInt32(42)));

        Assert.True(env.GetBindingValue("x", strict: true, out var value) == BindingOpResult.Ok);
        Assert.Equal(42, value.AsInt32());
    }

    [Fact]
    public void GetBindingValueStrictMissReturnsNotFound()
    {
        var env = new ObjectEnvironmentRecord(new FakeBindingObject(), isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.GetBindingValue("ghost", strict: true, out _));
    }

    [Fact]
    public void GetBindingValueNonStrictMissReturnsUndefined()
    {
        var env = new ObjectEnvironmentRecord(new FakeBindingObject(), isWithEnvironment: false, outerEnv: null);

        // Non-strict missing read on an object env record returns undefined per
        // 9.1.1.2.6 step 3 - never throws ReferenceError.
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("ghost", strict: false, out var value));
        Assert.Equal(JsValueTag.Undefined, value.Tag);
    }

    [Fact]
    public void SetMutableBindingOnMissingStrictReturnsNotFound()
    {
        var env = new ObjectEnvironmentRecord(new FakeBindingObject(), isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.SetMutableBinding("ghost", JsValue.FromInt32(1), strict: true));
    }

    [Fact]
    public void SetMutableBindingNonStrictOnReadOnlyFallsThrough()
    {
        var backing = new FakeBindingObject();
        backing.DefineMutableData("frozen", JsValue.FromInt32(1), deletable: false);
        backing.MakeReadOnly("frozen");
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        // Non-strict assignment to a read-only data property silently fails per spec.
        Assert.Equal(BindingOpResult.Ok, env.SetMutableBinding("frozen", JsValue.FromInt32(99), strict: false));

        Assert.True(backing.TryGet("frozen", out var current));
        Assert.Equal(1, current.AsInt32());
    }

    [Fact]
    public void SetMutableBindingStrictOnReadOnlyReportsConstAssignment()
    {
        var backing = new FakeBindingObject();
        backing.DefineMutableData("frozen", JsValue.FromInt32(1), deletable: false);
        backing.MakeReadOnly("frozen");
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.ConstAssignment, env.SetMutableBinding("frozen", JsValue.FromInt32(99), strict: true));
    }

    [Fact]
    public void DeleteBindingRemovesProperty()
    {
        var backing = new FakeBindingObject();
        backing.DefineMutableData("x", JsValue.FromInt32(1), deletable: true);
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.Ok, env.DeleteBinding("x"));
        Assert.False(backing.HasProperty("x"));
    }

    [Fact]
    public void DeleteBindingOnNonConfigurableReportsConstAssignment()
    {
        var backing = new FakeBindingObject();
        backing.DefineMutableData("permanent", JsValue.FromInt32(1), deletable: false);
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.ConstAssignment, env.DeleteBinding("permanent"));
        Assert.True(backing.HasProperty("permanent"));
    }

    [Fact]
    public void DeleteBindingOnMissingPropertyReturnsNotFound()
    {
        var env = new ObjectEnvironmentRecord(new FakeBindingObject(), isWithEnvironment: false, outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.DeleteBinding("ghost"));
    }

    [Fact]
    public void NonWithEnvironmentDoesNotExposeWithBaseObject()
    {
        var backing = new FakeBindingObject { Handle = new ObjectHandle(7, 1) };
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: false, outerEnv: null);

        Assert.False(env.IsWithEnvironment);
        Assert.Null(env.WithBaseObject);
    }

    [Fact]
    public void WithEnvironmentExposesBindingObjectAsThis()
    {
        var backing = new FakeBindingObject { Handle = new ObjectHandle(7, 1) };
        var env = new ObjectEnvironmentRecord(backing, isWithEnvironment: true, outerEnv: null);

        Assert.True(env.IsWithEnvironment);
        Assert.Equal(new ObjectHandle(7, 1), env.WithBaseObject);
    }

    private sealed class FakeBindingObject : IBindingObject
    {
        private readonly Dictionary<string, Entry> _store = new(StringComparer.Ordinal);

        public ObjectHandle? Handle { get; init; }

        public ObjectHandle? AsObjectHandle => Handle;

        public bool HasProperty(string name) => _store.ContainsKey(name);

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
                if (!entry.Writable)
                {
                    return false;
                }

                _store[name] = entry with { Value = value };
                return true;
            }

            _store[name] = new Entry(value, Writable: true, Configurable: true);
            return true;
        }

        public bool DefineMutableData(string name, JsValue value, bool deletable)
        {
            if (_store.TryGetValue(name, out var existing) && !existing.Configurable)
            {
                return false;
            }

            _store[name] = new Entry(value, Writable: true, Configurable: deletable);
            return true;
        }

        public bool DeleteProperty(string name)
        {
            if (!_store.TryGetValue(name, out var entry))
            {
                return true;
            }

            if (!entry.Configurable)
            {
                return false;
            }

            _store.Remove(name);
            return true;
        }

        public void MakeReadOnly(string name)
        {
            _store[name] = _store[name] with { Writable = false };
        }

        private readonly record struct Entry(JsValue Value, bool Writable, bool Configurable);
    }
}
