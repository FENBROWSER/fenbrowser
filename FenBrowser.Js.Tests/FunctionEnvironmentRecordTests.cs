using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class FunctionEnvironmentRecordTests
{
    [Fact]
    public void UninitializedThisCannotBeRead()
    {
        var env = NewOrdinaryFunctionEnv();

        Assert.True(env.HasThisBinding);
        Assert.Equal(BindingOpResult.TdzAccess, env.GetThisBinding(out _));
    }

    [Fact]
    public void BindThisValueInitializesThis()
    {
        var env = NewOrdinaryFunctionEnv();

        Assert.Equal(BindingOpResult.Ok, env.BindThisValue(JsValue.FromInt32(42)));
        Assert.Equal(ThisBindingStatus.Initialized, env.ThisBindingStatus);
        Assert.Equal(BindingOpResult.Ok, env.GetThisBinding(out var value));
        Assert.Equal(42, value.AsInt32());
    }

    [Fact]
    public void BindThisValueTwiceIsAlreadyDeclared()
    {
        // ECMA-262 9.1.1.3.1 step 3: "If envRec.[[ThisBindingStatus]] is initialized,
        // throw a ReferenceError exception." Re-binding via super(...) is the common
        // trigger of this case.
        var env = NewOrdinaryFunctionEnv();
        env.BindThisValue(JsValue.FromInt32(1));

        Assert.Equal(BindingOpResult.AlreadyDeclared, env.BindThisValue(JsValue.FromInt32(2)));
    }

    [Fact]
    public void ArrowFunctionHasNoThisBinding()
    {
        var env = new FunctionEnvironmentRecord(
            thisBindingStatus: ThisBindingStatus.Lexical,
            functionObject: JsValue.Undefined,
            newTarget: JsValue.Undefined,
            homeObject: null,
            outerEnv: null);

        Assert.False(env.HasThisBinding);
        Assert.False(env.HasSuperBinding);
        Assert.Equal(BindingOpResult.NotInitializable, env.GetThisBinding(out _));
        Assert.Equal(BindingOpResult.NotInitializable, env.BindThisValue(JsValue.FromInt32(1)));
    }

    [Fact]
    public void SuperBindingRequiresHomeObject()
    {
        var withoutHome = NewOrdinaryFunctionEnv();
        Assert.False(withoutHome.HasSuperBinding);

        var withHome = new FunctionEnvironmentRecord(
            thisBindingStatus: ThisBindingStatus.Uninitialized,
            functionObject: JsValue.Undefined,
            newTarget: JsValue.Undefined,
            homeObject: new ObjectHandle(11, 1),
            outerEnv: null);
        Assert.True(withHome.HasSuperBinding);
    }

    [Fact]
    public void DeclarativeBindingsStillWorkOnFunctionEnvRecord()
    {
        // FunctionEnvironmentRecord inherits all the let/const/var binding machinery
        // from DeclarativeEnvironmentRecord - verify the seam survives the subclass.
        var env = NewOrdinaryFunctionEnv();

        Assert.Equal(BindingOpResult.Ok, env.CreateMutableBinding("x", deletable: false));
        Assert.Equal(BindingOpResult.Ok, env.InitializeBinding("x", JsValue.FromInt32(7)));
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("x", strict: true, out var value));
        Assert.Equal(7, value.AsInt32());
    }

    [Fact]
    public void NewTargetIsPreserved()
    {
        var marker = JsValue.FromInt32(123);
        var env = new FunctionEnvironmentRecord(
            thisBindingStatus: ThisBindingStatus.Uninitialized,
            functionObject: JsValue.Undefined,
            newTarget: marker,
            homeObject: null,
            outerEnv: null);

        Assert.Equal(123, env.NewTarget.AsInt32());
    }

    private static FunctionEnvironmentRecord NewOrdinaryFunctionEnv() => new(
        thisBindingStatus: ThisBindingStatus.Uninitialized,
        functionObject: JsValue.Undefined,
        newTarget: JsValue.Undefined,
        homeObject: null,
        outerEnv: null);
}
