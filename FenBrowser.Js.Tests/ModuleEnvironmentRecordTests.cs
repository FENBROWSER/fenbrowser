using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ModuleEnvironmentRecordTests
{
    [Fact]
    public void ModuleThisIsUndefined()
    {
        var env = new ModuleEnvironmentRecord(outerEnv: null);

        Assert.True(env.HasThisBinding);
        Assert.Equal(BindingOpResult.Ok, env.GetThisBinding(out var thisValue));
        Assert.Equal(JsValueTag.Undefined, thisValue.Tag);
    }

    [Fact]
    public void LocalBindingsBehaveLikeDeclarative()
    {
        var env = new ModuleEnvironmentRecord(outerEnv: null);

        Assert.Equal(BindingOpResult.Ok, env.CreateMutableBinding("x", deletable: false));
        Assert.Equal(BindingOpResult.Ok, env.InitializeBinding("x", JsValue.FromInt32(11)));
        Assert.Equal(BindingOpResult.Ok, env.GetBindingValue("x", strict: true, out var value));
        Assert.Equal(11, value.AsInt32());
    }

    [Fact]
    public void CreateImportBindingResolvesThroughTargetEnv()
    {
        // Build a target module env with an initialized export and then import it.
        var target = new ModuleEnvironmentRecord(outerEnv: null);
        target.CreateMutableBinding("exported", deletable: false);
        target.InitializeBinding("exported", JsValue.FromInt32(123));

        var importer = new ModuleEnvironmentRecord(outerEnv: null);
        Assert.Equal(BindingOpResult.Ok, importer.CreateImportBinding("localAlias", target, "exported"));
        Assert.True(importer.IsImportBindingForTest("localAlias"));

        Assert.Equal(BindingOpResult.Ok, importer.GetBindingValue("localAlias", strict: true, out var value));
        Assert.Equal(123, value.AsInt32());
    }

    [Fact]
    public void ImportBindingPropagatesTdzFromTarget()
    {
        // If the target export is still in its TDZ, the importer's read reports the
        // same TDZ result. The interpreter translates this into a ReferenceError per
        // ECMA-262 9.1.1.5.1 step 3.
        var target = new ModuleEnvironmentRecord(outerEnv: null);
        target.CreateMutableBinding("exported", deletable: false);

        var importer = new ModuleEnvironmentRecord(outerEnv: null);
        importer.CreateImportBinding("alias", target, "exported");

        Assert.Equal(BindingOpResult.TdzAccess, importer.GetBindingValue("alias", strict: true, out _));
    }

    [Fact]
    public void CreateImportBindingRejectsCollisionWithExistingLocalOrImport()
    {
        var target = new ModuleEnvironmentRecord(outerEnv: null);
        target.CreateMutableBinding("x", deletable: false);
        target.InitializeBinding("x", JsValue.FromInt32(1));

        var importer = new ModuleEnvironmentRecord(outerEnv: null);
        importer.CreateMutableBinding("local", deletable: false);

        Assert.Equal(BindingOpResult.AlreadyDeclared, importer.CreateImportBinding("local", target, "x"));
        Assert.Equal(BindingOpResult.Ok, importer.CreateImportBinding("alias", target, "x"));
        Assert.Equal(BindingOpResult.AlreadyDeclared, importer.CreateImportBinding("alias", target, "x"));
    }

    [Fact]
    public void AssignmentToImportBindingIsRejected()
    {
        var target = new ModuleEnvironmentRecord(outerEnv: null);
        target.CreateMutableBinding("x", deletable: false);
        target.InitializeBinding("x", JsValue.FromInt32(1));

        var importer = new ModuleEnvironmentRecord(outerEnv: null);
        importer.CreateImportBinding("alias", target, "x");

        Assert.Equal(BindingOpResult.ConstAssignment, importer.SetMutableBinding("alias", JsValue.FromInt32(2), strict: true));
        Assert.Equal(BindingOpResult.NotInitializable, importer.InitializeBinding("alias", JsValue.FromInt32(3)));
    }

    [Fact]
    public void DeleteBindingOnModuleEnvIsRejected()
    {
        var env = new ModuleEnvironmentRecord(outerEnv: null);
        env.CreateMutableBinding("x", deletable: true);
        env.InitializeBinding("x", JsValue.FromInt32(1));

        // Module bindings are never deletable, even when the underlying create call
        // requested deletable=true - the spec asserts DeleteBinding is never used on
        // a module env, so we surface NotInitializable to make the violation visible.
        Assert.Equal(BindingOpResult.NotInitializable, env.DeleteBinding("x"));
        Assert.Equal(BindingOpResult.NotFound, env.DeleteBinding("ghost"));
    }

    [Fact]
    public void NonModuleEnvRecordsReturnNotInitializableFromGetThisBinding()
    {
        // Sanity-check the new base virtual: declarative and object env records have
        // no `this` to surface, so the default implementation reports
        // NotInitializable, prompting the interpreter to walk the outer chain.
        var declarative = new DeclarativeEnvironmentRecord(outerEnv: null);
        Assert.Equal(BindingOpResult.NotInitializable, declarative.GetThisBinding(out _));
    }
}
