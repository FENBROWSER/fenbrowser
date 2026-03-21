using FenBrowser.Core.Dom.V2;
using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Narrow runtime adapter surface for the authoritative JavaScriptEngine implementation.
    /// This exists to keep call sites stable without carrying alternate placeholder engines.
    /// </summary>
    public interface IJsRuntime
    {
        void Initialize(IJsHost host);
        void SetDom(Element root);
        void Reset(JsContext ctx);
        bool RunInline(string code, JsContext ctx);
        bool AllowExternalScripts { get; set; }
        SandboxPolicy Sandbox { get; set; }

        // Execute a multi-line script block (e.g. <script> body)
        void ExecuteBlock(string code, JsContext ctx);

        // Register a named host function accessible from script (maps to _userFunctions)
        void RegisterHostFunction(string name, string body);

        // Evaluate an expression and return a stringified result (minimal for diagnostics)
        string EvaluateExpression(string expr, JsContext ctx);
    }

    /// <summary>
    /// Adapter that wraps the existing JavaScriptEngine to satisfy IJsRuntime.
    /// </summary>
    public sealed class JsZeroRuntime : IJsRuntime
    {
        private readonly JavaScriptEngine _inner;

        public JsZeroRuntime(JavaScriptEngine inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
        }

        public void Initialize(IJsHost host)
        {
            // JavaScriptEngine is constructed with a host; nothing to do here.
        }

        public void SetDom(Element root)
        {
            ExecuteSafely(() => _inner.SetDom(root), "SetDom");
        }

        public void Reset(JsContext ctx)
        {
            ExecuteSafely(() => _inner.Reset(ctx), "Reset");
        }

        public bool RunInline(string code, JsContext ctx)
        {
            return ExecuteSafely(() => _inner.RunInline(code, ctx), false, "RunInline");
        }

        public bool AllowExternalScripts
        {
            get => ExecuteSafely(() => _inner.AllowExternalScripts, false, "AllowExternalScripts.get");
            set => ExecuteSafely(() => _inner.AllowExternalScripts = value, "AllowExternalScripts.set");
        }

        public SandboxPolicy Sandbox
        {
            get => ExecuteSafely(() => _inner.Sandbox, SandboxPolicy.AllowAll, "Sandbox.get");
            set => ExecuteSafely(() => _inner.Sandbox = value, "Sandbox.set");
        }

        public void ExecuteBlock(string code, JsContext ctx)
        {
            ExecuteSafely(() => _inner.ExecuteScriptBlock(code, ctx?.BaseUri?.ToString()), "ExecuteBlock");
        }

        public void RegisterHostFunction(string name, string body)
        {
            ExecuteSafely(() =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _inner.RegisterUserFunction(name, null);
                }
            }, "RegisterHostFunction");
        }

        public string EvaluateExpression(string expr, JsContext ctx)
        {
            return ExecuteSafely(() => _inner.EvalToString(expr), null, "EvaluateExpression");
        }

        private static void ExecuteSafely(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[JsZeroRuntime] {operation} failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private static T ExecuteSafely<T>(Func<T> action, T fallback, string operation)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[JsZeroRuntime] {operation} failed: {ex.Message}", LogCategory.JavaScript);
                return fallback;
            }
        }
    }

}
