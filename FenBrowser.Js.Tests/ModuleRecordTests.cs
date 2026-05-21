using FenBrowser.Js.Modules;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ModuleRecordTests
{
    [Fact]
    public void NewModuleRecordStartsUnlinkedWithoutErrors()
    {
        var module = new FakeModule(realmId: 0);

        Assert.Equal(ModuleStatus.Unlinked, module.Status);
        Assert.Null(module.Environment);
        Assert.Equal(JsValueTag.Undefined, module.Namespace.Tag);
        Assert.False(module.HasEvaluationError);
    }

    [Fact]
    public void LinkSucceedsAndTransitionsStatusForwards()
    {
        var module = new FakeModule(realmId: 0);

        module.Link();

        Assert.Equal(ModuleStatus.Linked, module.Status);
    }

    [Fact]
    public void EvaluateMarksEvaluatedAndReturnsValue()
    {
        var module = new FakeModule(realmId: 0, evaluationResult: JsValue.FromInt32(42));
        module.Link();

        var result = module.Evaluate();

        Assert.Equal(ModuleStatus.Evaluated, module.Status);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void RecordedEvaluationErrorMarksFailedState()
    {
        var module = new FakeModule(realmId: 0)
        {
            FailWith = JsValue.FromString("ReferenceError: x is not defined"),
        };

        module.Link();
        module.Evaluate();

        Assert.Equal(ModuleStatus.Evaluated, module.Status);
        Assert.True(module.HasEvaluationError);
    }

    [Fact]
    public void ResolvedBindingNamespaceSentinelIsRecognized()
    {
        var module = new FakeModule(realmId: 0);
        var binding = new ResolvedBinding(module, ResolvedBinding.NamespaceBindingName);

        Assert.True(binding.IsNamespaceBinding);
    }

    [Fact]
    public void ResolvedBindingNamedTargetIsNotNamespace()
    {
        var module = new FakeModule(realmId: 0);
        var binding = new ResolvedBinding(module, "x");

        Assert.False(binding.IsNamespaceBinding);
        Assert.Equal("x", binding.BindingName);
    }

    private sealed class FakeModule : ModuleRecord
    {
        private readonly JsValue _evaluationResult;

        public FakeModule(int realmId, JsValue evaluationResult = default) : base(realmId)
        {
            _evaluationResult = evaluationResult;
        }

        public JsValue FailWith { get; set; } = JsValue.Undefined;

        public override IReadOnlyList<string> GetExportedNames(HashSet<ModuleRecord>? exportStarSet = null)
            => Array.Empty<string>();

        public override ResolvedBinding? ResolveExport(string exportName, HashSet<(ModuleRecord, string)>? resolveSet = null)
            => null;

        public override void Link()
        {
            TransitionStatus(ModuleStatus.Linking);
            TransitionStatus(ModuleStatus.Linked);
        }

        public override JsValue Evaluate()
        {
            if (FailWith.Tag != JsValueTag.Undefined)
            {
                RecordEvaluationError(FailWith);
                return JsValue.Undefined;
            }

            TransitionStatus(ModuleStatus.Evaluating);
            TransitionStatus(ModuleStatus.Evaluated);
            return _evaluationResult;
        }
    }
}
