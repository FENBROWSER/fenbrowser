using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class DynamicImportRuntimeTests
{
    private static void Execute(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        _ = new BytecodeInterpreter().Execute(function);
    }

    [Fact]
    public void OptionsExpressionIsEvaluatedAfterSpecifier()
    {
        Execute(@"
            var log = [];
            import(log.push('specifier'), log.push('options'));
            if (log.length !== 2 || log[0] !== 'specifier' || log[1] !== 'options') {
                throw new Error('wrong evaluation order');
            }
        ");
    }

    [Fact]
    public void AbruptOptionsExpressionPropagatesBeforePromiseCreation()
    {
        Execute(@"
            var caught = false;
            try {
                import('', (function () { throw new Error('options'); })());
            } catch (error) {
                caught = error.message === 'options';
            }
            if (!caught) throw new Error('options throw was not propagated');
        ");
    }

    [Fact]
    public void EnumerableImportAttributesInvokeProxyTraps()
    {
        Execute(@"
            var log = [];
            var attributes = new Proxy({}, {
                ownKeys() { return ['type']; },
                getOwnPropertyDescriptor() {
                    return { configurable: true, enumerable: true, value: 'json' };
                },
                get(target, key) { log.push(key); return 'json'; }
            });
            import('', { with: attributes });
            if (log.length !== 1 || log[0] !== 'type') {
                throw new Error('attributes were not enumerated');
            }
        ");
    }
}
