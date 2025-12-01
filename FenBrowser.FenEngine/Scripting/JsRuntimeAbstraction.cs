using System;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Minimal runtime abstraction so we can swap the tiny JS-0 engine
    /// for a full interpreter (e.g., NiL.JS) without refactoring callers.
    /// First slice: only the members we already use widely.
    /// </summary>
    public interface IJsRuntime
    {
        void Initialize(IJsHost host);
        void SetDom(LiteElement root);
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

        public void SetDom(LiteElement root)
        {
            try { _inner.SetDom(root); } catch { }
        }

        public void Reset(JsContext ctx)
        {
            try { _inner.Reset(ctx); } catch { }
        }

        public bool RunInline(string code, JsContext ctx)
        {
            try { return _inner.RunInline(code, ctx); } catch { return false; }
        }

        public bool AllowExternalScripts
        {
            get { try { return _inner.AllowExternalScripts; } catch { return false; } }
            set { try { _inner.AllowExternalScripts = value; } catch { } }
        }

        public SandboxPolicy Sandbox
        {
            get { try { return _inner.Sandbox; } catch { return SandboxPolicy.AllowAll; } }
            set { try { _inner.Sandbox = value; } catch { } }
        }

        public void ExecuteBlock(string code, JsContext ctx)
        {
            try { _inner.ExecuteScriptBlock(code, ctx?.BaseUri?.ToString()); } catch { }
        }

        public void RegisterHostFunction(string name, string body)
        {
            try 
            { 
                if (!string.IsNullOrWhiteSpace(name)) 
                    _inner.RegisterUserFunction(name, null); 
            } 
            catch { }
        }

        public string EvaluateExpression(string expr, JsContext ctx)
        {
            try { return _inner.EvalToString(expr); } catch { return null; }
        }
    }

    /// <summary>
    /// Placeholder for a future full JS runtime (e.g., NiL.JS). It is a no-op for now.
    /// This lets us wire flags and call sites incrementally without breaking builds.
    /// </summary>
    public sealed class FullJsRuntimeStub : IJsRuntime
    {
        public FullJsRuntimeStub()
        {
            // C# 5.0 fix: Initialize property in constructor
            Sandbox = SandboxPolicy.AllowAll;
        }

        public void Initialize(IJsHost host) { }
        public void SetDom(LiteElement root) { }
        public void Reset(JsContext ctx) { }
        public bool RunInline(string code, JsContext ctx) { return false; }
        public bool AllowExternalScripts { get; set; }
        public SandboxPolicy Sandbox { get; set; }
        public void ExecuteBlock(string code, JsContext ctx) { }
        public void RegisterHostFunction(string name, string body) { }
        public string EvaluateExpression(string expr, JsContext ctx) { return null; }
    }
}