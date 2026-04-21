using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FenBrowser.Conformance;

public interface IHtmlTreeOracle
{
    bool IsAvailable { get; }
    string AvailabilityError { get; }
    OracleParseResult Parse(string input, string? fragmentContext);
}

public sealed class OracleParseResult
{
    public bool Success { get; init; }
    public string Tree { get; init; } = "";
    public string Error { get; init; } = "";
}

internal sealed class PythonHtml5LibOracle : IHtmlTreeOracle
{
    private readonly string _pythonExe;
    public bool IsAvailable { get; }
    public string AvailabilityError { get; }

    public PythonHtml5LibOracle(string pythonExe = "python")
    {
        _pythonExe = string.IsNullOrWhiteSpace(pythonExe) ? "python" : pythonExe;
        var probe = RunPython("import html5lib; print('ok')", string.Empty);
        IsAvailable = probe.ExitCode == 0 && probe.StdOut.Contains("ok", StringComparison.OrdinalIgnoreCase);
        AvailabilityError = IsAvailable
            ? string.Empty
            : string.IsNullOrWhiteSpace(probe.StdErr) ? "python/html5lib unavailable" : probe.StdErr.Trim();
    }

    public OracleParseResult Parse(string input, string? fragmentContext)
    {
        var payload = JsonSerializer.Serialize(new
        {
            input = input ?? string.Empty,
            fragmentContext = fragmentContext
        });

        var script = """
import json
import sys
import html5lib

def append_line(lines, depth, text):
    lines.append("| " + ("  " * depth) + text)

def serialize_node(node, depth, lines):
    nt = getattr(node, "nodeType", None)
    if nt == 9:  # Document
        for ch in node.childNodes:
            serialize_node(ch, depth, lines)
        return
    if nt == 11:  # DocumentFragment
        for ch in node.childNodes:
            serialize_node(ch, depth, lines)
        return
    if nt == 1:  # Element
        tag = (getattr(node, "tagName", "") or "").lower()
        append_line(lines, depth, f"<{tag}>")
        attrs = []
        a = getattr(node, "attributes", None)
        if a is not None:
            for i in range(a.length):
                item = a.item(i)
                attrs.append((item.name, item.value))
        attrs.sort(key=lambda x: x[0])
        for (k, v) in attrs:
            append_line(lines, depth + 1, f'{k}="{v}"')
        for ch in node.childNodes:
            serialize_node(ch, depth + 1, lines)
        return
    if nt == 8:  # Comment
        append_line(lines, depth, "<!-- " + (node.data or "") + " -->")
        return
    if nt == 10:  # Doctype
        name = getattr(node, "name", "html") or "html"
        append_line(lines, depth, "<!DOCTYPE " + name + ">")
        return
    if nt == 3:  # Text
        append_line(lines, depth, '"' + (node.data or "") + '"')
        return

def main():
    req = json.loads(sys.stdin.read() or "{}")
    src = req.get("input", "")
    ctx = req.get("fragmentContext")
    if ctx:
        fragment = html5lib.parseFragment(src, container=ctx, treebuilder="dom")
        lines = []
        append_line(lines, 0, "<" + ctx.lower() + ">")
        for ch in fragment.childNodes:
            serialize_node(ch, 1, lines)
    else:
        doc = html5lib.parse(src, treebuilder="dom")
        lines = []
        serialize_node(doc, 0, lines)
    sys.stdout.write(json.dumps({"ok": True, "tree": "\n".join(lines)}))

if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        sys.stdout.write(json.dumps({"ok": False, "error": str(ex)}))
""";

        var result = RunPython(script, payload);
        if (result.ExitCode != 0)
        {
            return new OracleParseResult { Success = false, Error = result.StdErr.Trim() };
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "oracle parse failed" : "oracle parse failed";
                return new OracleParseResult { Success = false, Error = err };
            }

            var tree = root.TryGetProperty("tree", out var treeProp) ? treeProp.GetString() ?? string.Empty : string.Empty;
            return new OracleParseResult { Success = true, Tree = tree };
        }
        catch (Exception ex)
        {
            return new OracleParseResult { Success = false, Error = ex.Message };
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunPython(string script, string stdinPayload)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fen_oracle_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = "\"" + scriptPath + "\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            if (!string.IsNullOrEmpty(stdinPayload))
            {
                proc.StandardInput.Write(stdinPayload);
            }
            proc.StandardInput.Close();
            var stdOut = proc.StandardOutput.ReadToEnd();
            var stdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            return (proc.ExitCode, stdOut, stdErr);
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); }
            catch { }
        }
    }
}
