using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DeclarativeEnvironmentRecordTests
{
    [Fact]
    public void HasBindingReturnsFalseForUnknownName()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        Assert.False(env.HasBinding("x"));
    }

    [Fact]
    public void CreateMutableBindingMakesItVisibleButUninitialized()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);

        Assert.Equal(BindingOpResult.Ok, env.CreateMutableBinding("x", deletable: false));
        Assert.True(env.HasBinding("x"));
        Assert.True(env.IsMutableForTest("x"));
        Assert.False(env.IsInitializedForTest("x"));
    }

    [Fact]
    public void CreateMutableBindingTwiceReportsAlreadyDeclared()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: false);

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateMutableBinding("x", deletable: false));
    }

    [Fact]
    public void CreateImmutableBindingTwiceReportsAlreadyDeclared()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateImmutableBinding("k", strict: true);

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.CreateImmutableBinding("k", strict: true));
    }

    [Fact]
    public void GetBindingValueOnUninitializedMutableBindingIsTdz()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: false);

        var result = env.GetBindingValue("x", strict: true, out var value);

        Assert.Equal(BindingOpResult.TdzAccess, result);
        Assert.Equal(JsValueTag.Undefined, value.Tag);
    }

    [Fact]
    public void GetBindingValueOnUninitializedImmutableBindingIsTdz()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateImmutableBinding("k", strict: true);

        var result = env.GetBindingValue("k", strict: true, out _);

        Assert.Equal(BindingOpResult.TdzAccess, result);
    }

    [Fact]
    public void InitializeBindingLiftsTdz()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: false);

        Assert.Equal(BindingOpResult.Ok, env.InitializeBinding("x", JsValue.FromInt32(7)));
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("x", strict: true, out var value));
        Assert.Equal(7, value.AsInt32());
    }

    [Fact]
    public void InitializeBindingOnUnknownNameReturnsNotFound()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.InitializeBinding("ghost", JsValue.Undefined));
    }

    [Fact]
    public void InitializeBindingTwiceReturnsNotInitializable()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: false);
        env.InitializeBinding("x", JsValue.FromInt32(1));

        Assert.Equal(BindingOpResult.NotInitializable, env.InitializeBinding("x", JsValue.FromInt32(2)));
    }

    [Fact]
    public void SetMutableBindingOnImmutableBindingIsConstAssignment()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateImmutableBinding("k", strict: true);
        env.InitializeBinding("k", JsValue.FromInt32(1));

        Assert.Equal(BindingOpResult.ConstAssignment, env.SetMutableBinding("k", JsValue.FromInt32(2), strict: true));
    }

    [Fact]
    public void SetMutableBindingOnUninitializedConstReportsTdzNotConstAssignment()
    {
        // TDZ check has to win - reading or writing a let/const before its initializer
        // ran is a ReferenceError, not a TypeError, per ECMA-262 9.1.1.1.5/9.1.1.1.6.
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateImmutableBinding("k", strict: true);

        Assert.Equal(BindingOpResult.TdzAccess, env.SetMutableBinding("k", JsValue.FromInt32(2), strict: true));
    }

    [Fact]
    public void SetMutableBindingOnMissingBindingIsNotFound()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.SetMutableBinding("ghost", JsValue.FromInt32(1), strict: true));
    }

    [Fact]
    public void DeleteBindingRemovesDeletableBinding()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: true);
        env.InitializeBinding("x", JsValue.FromInt32(1));

        Assert.Equal(BindingOpResult.Ok, env.DeleteBinding("x"));
        Assert.False(env.HasBinding("x"));
    }

    [Fact]
    public void DeleteBindingOnNonDeletableBindingFails()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: false);

        Assert.Equal(BindingOpResult.ConstAssignment, env.DeleteBinding("x"));
        Assert.True(env.HasBinding("x"));
    }

    [Fact]
    public void DeleteBindingOnMissingBindingIsNotFound()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);

        Assert.Equal(BindingOpResult.NotFound, env.DeleteBinding("ghost"));
    }

    [Fact]
    public void OuterEnvIsPreserved()
    {
        var outer = new DeclarativeEnvironmentRecord(outerEnv: null);
        var inner = new DeclarativeEnvironmentRecord(outer);

        Assert.Same(outer, inner.OuterEnv);
        Assert.Null(outer.OuterEnv);
    }

    [Fact]
    public void DeclarativeEnvHasNoThisOrSuperBindingByDefault()
    {
        var env = new DeclarativeEnvironmentRecord(outerEnv: null);

        Assert.False(env.HasThisBinding);
        Assert.False(env.HasSuperBinding);
        Assert.Null(env.WithBaseObject);
    }
}
