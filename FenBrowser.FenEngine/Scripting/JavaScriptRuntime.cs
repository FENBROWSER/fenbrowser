using System;

namespace FenBrowser.FenEngine.Scripting;

public sealed class JavaScriptRuntime
{
    private readonly JavaScriptEngine _engine;

    public JavaScriptRuntime(JavaScriptEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public object Execute(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _engine.Evaluate(code);
    }
}
