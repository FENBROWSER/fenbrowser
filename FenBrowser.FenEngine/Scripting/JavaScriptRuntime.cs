namespace FenBrowser.FenEngine.Scripting;

public class JavaScriptRuntime
{
    public void Execute(string code)
    {
        // TODO: Integrate Jint or V8
        System.Console.WriteLine($"Executing JS: {code}");
    }
}
