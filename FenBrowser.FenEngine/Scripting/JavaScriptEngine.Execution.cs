using System;
using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Scripting
{
    public sealed partial class JavaScriptEngine
    {
        private enum JavaScriptExecutionKind
        {
            Eval,
            Inline,
            EventHandler,
            ScriptBlock,
            PageScript
        }

        private sealed class RuntimeScriptExecutionResult
        {
            public RuntimeScriptExecutionResult(FenValue value, Exception exception, string outcome)
            {
                Value = value;
                Exception = exception;
                Outcome = outcome ?? "success";
            }

            public FenValue Value { get; }
            public Exception Exception { get; }
            public string Outcome { get; }
            public bool Succeeded => Exception == null &&
                                     Value.Type != JsValueType.Error &&
                                     Value.Type != JsValueType.Throw;
        }

        public JavaScriptRuntimeProfile RuntimeProfile { get; private set; } = JavaScriptRuntimeProfile.Balanced;

        public JavaScriptEngine(IJsHost host, JavaScriptRuntimeProfile runtimeProfile)
            : this(host)
        {
            ApplyRuntimeProfile(runtimeProfile);
        }

        public void ApplyRuntimeProfile(JavaScriptRuntimeProfile runtimeProfile)
        {
            RuntimeProfile = runtimeProfile ?? JavaScriptRuntimeProfile.Balanced;
            if (_fenRuntime == null)
            {
                return;
            }

            var currentDomRoot = _domRoot;
            var currentBaseUri = _ctx?.BaseUri;

            InitRuntime();

            if (_historyBridge != null)
            {
                _fenRuntime.SetHistoryBridge(_historyBridge);
            }

            if (currentDomRoot != null)
            {
                SyncDomContext(currentDomRoot, currentBaseUri);
            }
        }

        private PermissionManager CreatePermissionManagerForProfile()
        {
            var grantedPermissions = JsPermissions.StandardWeb;
            if (RuntimeProfile.AllowDynamicCodeEvaluation)
            {
                grantedPermissions |= JsPermissions.Eval;
            }

            return new PermissionManager(grantedPermissions);
        }

        private IResourceLimits CreateResourceLimitsForProfile()
        {
            return RuntimeProfile.UseSandboxedResourceLimits
                ? new SandboxedResourceLimits()
                : new DefaultResourceLimits();
        }

        private void ApplyRuntimeProfilePostInitialization()
        {
            if (!RuntimeProfile.FreezeIntrinsicPrototypes || _fenRuntime == null)
            {
                return;
            }

            try
            {
                _fenRuntime.ApplyPrototypeHardening();
            }
            catch (Exception ex)
            {
                FenLogger.Warn(
                    $"[JavaScriptEngine] Prototype hardening failed for profile '{RuntimeProfile.Name}': {ex.Message}",
                    LogCategory.JsExecution);
            }
        }

        private RuntimeScriptExecutionResult ExecuteRuntimeScript(
            string script,
            JavaScriptExecutionKind kind,
            string sourceName,
            bool allowReturn = false)
        {
            var normalizedSourceName = NormalizeExecutionSourceName(kind, sourceName);
            var correlationId = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            var preview = RuntimeProfile.EnableStructuredExecutionLogs
                ? BuildScriptDiagnosticPreview(script)
                : null;

            FenValue normalizedValue = FenValue.Undefined;
            Exception executionException = null;
            string outcome = "success";
            string errorMessage = null;

            try
            {
                if (_fenRuntime == null)
                {
                    normalizedValue = FenValue.FromError("JavaScript runtime is not initialized.");
                    outcome = "engine-uninitialized";
                    errorMessage = normalizedValue.ToString();
                    return new RuntimeScriptExecutionResult(normalizedValue, null, outcome);
                }

                WarnOnLargeScript(kind, normalizedSourceName, script);

                var rawResult = _fenRuntime.ExecuteSimple(script ?? string.Empty, normalizedSourceName, allowReturn);
                normalizedValue = NormalizeExecutionResult(rawResult);

                if (normalizedValue.Type == JsValueType.Throw)
                {
                    outcome = "throw";
                    errorMessage = normalizedValue.ToString();
                }
                else if (normalizedValue.Type == JsValueType.Error)
                {
                    outcome = "error";
                    errorMessage = normalizedValue.ToString();
                }

                return new RuntimeScriptExecutionResult(normalizedValue, null, outcome);
            }
            catch (Exception ex)
            {
                executionException = ex;
                outcome = "exception";
                errorMessage = ex.GetBaseException().Message;
                normalizedValue = FenValue.FromError(errorMessage);
                return new RuntimeScriptExecutionResult(normalizedValue, ex, outcome);
            }
            finally
            {
                stopwatch.Stop();
                LogScriptExecution(
                    correlationId,
                    kind,
                    normalizedSourceName,
                    script,
                    preview,
                    normalizedValue,
                    outcome,
                    errorMessage,
                    executionException,
                    stopwatch.ElapsedMilliseconds,
                    FenObject.GetAllocatedBytes());
            }
        }

        private void WarnOnLargeScript(JavaScriptExecutionKind kind, string sourceName, string script)
        {
            if (!RuntimeProfile.EnableExecutionLogging || RuntimeProfile.LargeScriptWarningBytes <= 0)
            {
                return;
            }

            var scriptLength = script?.Length ?? 0;
            if (scriptLength < RuntimeProfile.LargeScriptWarningBytes)
            {
                return;
            }

            FenLogger.Warn(
                $"[JS-EXEC] Large script queued: kind={kind} source={sourceName} length={scriptLength}",
                LogCategory.JsExecution);
        }

        private void LogScriptExecution(
            string correlationId,
            JavaScriptExecutionKind kind,
            string sourceName,
            string script,
            string preview,
            FenValue value,
            string outcome,
            string errorMessage,
            Exception exception,
            long durationMs,
            long allocatedBytes)
        {
            if (!RuntimeProfile.EnableExecutionLogging)
            {
                return;
            }

            var logLevel = string.Equals(outcome, "success", StringComparison.Ordinal)
                ? LogLevel.Info
                : LogLevel.Warn;
            var resultType = DescribeResultType(value);
            var message =
                $"[JS-EXEC] outcome={outcome} kind={kind.ToString().ToLowerInvariant()} source={sourceName} duration={durationMs}ms result={resultType}";

            var entry = new LogEntry
            {
                Category = LogCategory.JsExecution,
                Level = logLevel,
                Message = message,
                Exception = exception
            }
            .WithCorrelation(correlationId)
            .WithPerformance(durationMs, allocatedBytes);

            if (RuntimeProfile.EnableStructuredExecutionLogs)
            {
                entry.WithData("profile", RuntimeProfile.Name);
                entry.WithData("sourceKind", kind.ToString().ToLowerInvariant());
                entry.WithData("sourceName", sourceName);
                entry.WithData("scriptLength", script?.Length ?? 0);
                entry.WithData("outcome", outcome);
                entry.WithData("resultType", resultType);
                entry.WithData("preview", preview ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    entry.WithData("error", errorMessage);
                }
            }

            LogManager.Log(entry);

            if (RuntimeProfile.WriteExecutionArtifacts)
            {
                DiagnosticPaths.AppendRootText("js_execution_trace.jsonl", entry.ToJson() + Environment.NewLine);
            }
        }

        private static FenValue NormalizeExecutionResult(IValue rawResult)
        {
            if (rawResult is not FenValue result)
            {
                return FenValue.Undefined;
            }

            while (result.Type == JsValueType.ReturnValue)
            {
                if (result.ToNativeObject() is FenValue unwrapped)
                {
                    result = unwrapped;
                    continue;
                }

                break;
            }

            return result;
        }

        private static object ConvertExecutionResultToHostValue(FenValue result)
        {
            if (result.IsNumber)
            {
                return result.ToNumber();
            }

            if (result.IsString)
            {
                return result.ToString();
            }

            if (result.IsBoolean)
            {
                return result.ToBoolean();
            }

            if (result.IsUndefined)
            {
                return null;
            }

            return result;
        }

        private static string NormalizeExecutionSourceName(JavaScriptExecutionKind kind, string sourceName)
        {
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                return sourceName;
            }

            return kind switch
            {
                JavaScriptExecutionKind.Eval => "eval.js",
                JavaScriptExecutionKind.Inline => "inline-script",
                JavaScriptExecutionKind.EventHandler => "inline-event-handler",
                JavaScriptExecutionKind.ScriptBlock => "script-block",
                JavaScriptExecutionKind.PageScript => "page-script",
                _ => "script"
            };
        }

        private static string DescribeResultType(FenValue value)
        {
            if (value.IsUndefined)
            {
                return "undefined";
            }

            if (value.IsNull)
            {
                return "null";
            }

            if (value.IsFunction)
            {
                return "function";
            }

            if (value.IsObject)
            {
                return "object";
            }

            return value.Type.ToString();
        }

        private static string BuildInlineSourceName(string evt, string target)
        {
            if (string.IsNullOrWhiteSpace(evt))
            {
                return "inline-script";
            }

            return $"inline:{evt}@{(string.IsNullOrWhiteSpace(target) ? "unknown" : target)}";
        }
    }
}
