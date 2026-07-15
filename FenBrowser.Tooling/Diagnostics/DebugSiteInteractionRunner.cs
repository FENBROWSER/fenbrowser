using System.Globalization;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tooling;

internal sealed record DebugSiteInteractionRequest(
    string TargetSelector,
    string Text,
    string SubmitSelector,
    int SettleMs);

internal sealed record DebugSiteInteractionRect(
    double X,
    double Y,
    double Width,
    double Height);

internal sealed record DebugSiteInteractionResult
{
    public int SchemaVersion { get; init; } = 1;
    public bool Attempted { get; init; }
    public string Status { get; init; } = "not-run";
    public string Error { get; init; } = string.Empty;
    public string TargetSelector { get; init; } = string.Empty;
    public string SubmitSelector { get; init; } = string.Empty;
    public int TextLength { get; init; }
    public string StartedUtc { get; init; } = string.Empty;
    public string CompletedUtc { get; init; } = string.Empty;
    public string BeforeUrl { get; init; } = string.Empty;
    public string AfterUrl { get; init; } = string.Empty;
    public DebugSiteInteractionRect? TargetRect { get; init; }
    public bool InputTargetFound { get; init; }
    public bool FocusAcquired { get; init; }
    public bool TextAccepted { get; init; }
    public bool SubmitTargetFound { get; init; }
    public bool SubmitAttempted { get; init; }
    public bool NavigationObserved { get; init; }
    public bool RequestObserved { get; init; }
    public bool SubmissionOutcomeObserved { get; init; }
    public bool SubmissionOutcomeSettled { get; init; }
    public int NetworkRequestCountBefore { get; init; }
    public int NetworkRequestCountAfter { get; init; }
    public IReadOnlyList<string> EventRecords { get; init; } = Array.Empty<string>();
    public bool BeforeScreenshotCaptured { get; init; }
    public string BeforeScreenshotError { get; init; } = string.Empty;
    public bool AfterScreenshotCaptured { get; init; }
    public string AfterScreenshotError { get; init; } = string.Empty;
}

internal static class DebugSiteInteractionRunner
{
    internal const string EventMarker = "[fen-interaction-event]|";
    internal const int MaxEventRecords = 256;
    internal const int MaxEventRecordLength = 300;

    private static readonly string InstallEventObserverScript =
        "(function(){" +
        "if(globalThis.__fenInteractionObserverInstalled)return true;" +
        "globalThis.__fenInteractionObserverInstalled=true;" +
        "var types=['pointerdown','mousedown','focus','focusin','keydown','keypress','beforeinput','input','keyup','change','blur','focusout','pointerup','mouseup','click','submit'];" +
        "function emit(phase,event){" +
        "var target=event.target;" +
        "var tag=target&&target.tagName?String(target.tagName):'';" +
        "var id=target&&target.id?String(target.id):'';" +
        "var name=target&&target.name?String(target.name):'';" +
        "console.log('" + EventMarker + "'+String(event.type)+'|'+phase+'|'+tag+'|'+id+'|'+name+'|'+String(event.defaultPrevented));" +
        "}" +
        "types.forEach(function(type){" +
        "document.addEventListener(type,function(event){emit('capture',event);},true);" +
        "document.addEventListener(type,function(event){emit('bubble',event);},false);" +
        "});" +
        "return true;" +
        "})()";

    public static async Task<DebugSiteInteractionResult> RunAsync(
        BrowserHost host,
        DebugSiteInteractionRequest request,
        Func<int> getNetworkRequestCount)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(getNetworkRequestCount);

        var started = DateTime.UtcNow;
        var beforeUrl = host.CurrentUri?.AbsoluteUri ?? string.Empty;
        var navigationIdBefore = host.NavigationLifecycleState?.NavigationId ?? 0;
        var networkBefore = getNetworkRequestCount();
        var targetFound = false;
        var focusAcquired = false;
        var textAccepted = false;
        var submitFound = false;
        var submitAttempted = false;
        var outcomeSettled = false;
        DebugSiteInteractionRect? targetRect = null;
        string error = string.Empty;
        var interactionMessages = new List<string>();
        void CaptureConsoleMessage(string message)
        {
            lock (interactionMessages)
            {
                interactionMessages.Add(message);
            }
        }

        host.ConsoleMessage += CaptureConsoleMessage;
        try
        {
            await host.ExecuteScriptAsync(InstallEventObserverScript).ConfigureAwait(false);

            var targetId = await host.FindElementAsync("css selector", request.TargetSelector).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new InvalidOperationException($"Input target was not found for selector '{request.TargetSelector}'.");
            }

            targetFound = true;
            var rect = await host.GetElementRectAsync(targetId).ConfigureAwait(false);
            targetRect = new DebugSiteInteractionRect(rect.X, rect.Y, rect.Width, rect.Height);
            await host.ClickElementAsync(targetId).ConfigureAwait(false);
            focusAcquired = string.Equals(
                targetId,
                await host.GetActiveElementAsync().ConfigureAwait(false),
                StringComparison.Ordinal);
            if (!focusAcquired)
            {
                throw new InvalidOperationException("The input target did not acquire focus after the click.");
            }

            await host.SendKeysToElementAsync(targetId, request.Text).ConfigureAwait(false);
            var value = await host.GetElementPropertyAsync(targetId, "value").ConfigureAwait(false);
            textAccepted = string.Equals(value?.ToString(), request.Text, StringComparison.Ordinal);
            if (!textAccepted)
            {
                throw new InvalidOperationException("The input value did not match the submitted text.");
            }

            var submitId = await host.FindElementAsync("css selector", request.SubmitSelector).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(submitId))
            {
                throw new InvalidOperationException($"Submit target was not found for selector '{request.SubmitSelector}'.");
            }

            submitFound = true;
            submitAttempted = true;
            await host.ClickElementAsync(submitId).ConfigureAwait(false);
            outcomeSettled = await WaitForSubmissionOutcomeAsync(
                    host,
                    beforeUrl,
                    navigationIdBefore,
                    networkBefore,
                    getNetworkRequestCount,
                    request.SettleMs)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            host.ConsoleMessage -= CaptureConsoleMessage;
        }

        var afterUrl = host.CurrentUri?.AbsoluteUri ?? string.Empty;
        var networkAfter = getNetworkRequestCount();
        var navigationObserved = !string.Equals(beforeUrl, afterUrl, StringComparison.Ordinal);
        var requestObserved = networkAfter > networkBefore;
        var outcomeObserved = navigationObserved || requestObserved;
        if (submitAttempted && outcomeObserved && !outcomeSettled && string.IsNullOrEmpty(error))
        {
            error = $"The submission outcome did not settle within {Math.Max(0, request.SettleMs)} ms.";
        }

        var passed = targetFound && focusAcquired && textAccepted && submitFound && submitAttempted &&
                     outcomeObserved && outcomeSettled && string.IsNullOrEmpty(error);

        IReadOnlyList<string> eventRecords;
        lock (interactionMessages)
        {
            eventRecords = ExtractEventRecords(interactionMessages.ToArray());
        }

        return new DebugSiteInteractionResult
        {
            Attempted = true,
            Status = passed ? "passed" : "failed",
            Error = error,
            TargetSelector = request.TargetSelector,
            SubmitSelector = request.SubmitSelector,
            TextLength = request.Text?.Length ?? 0,
            StartedUtc = started.ToString("O", CultureInfo.InvariantCulture),
            CompletedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            BeforeUrl = beforeUrl,
            AfterUrl = afterUrl,
            TargetRect = targetRect,
            InputTargetFound = targetFound,
            FocusAcquired = focusAcquired,
            TextAccepted = textAccepted,
            SubmitTargetFound = submitFound,
            SubmitAttempted = submitAttempted,
            NavigationObserved = navigationObserved,
            RequestObserved = requestObserved,
            SubmissionOutcomeObserved = outcomeObserved,
            SubmissionOutcomeSettled = outcomeSettled,
            NetworkRequestCountBefore = networkBefore,
            NetworkRequestCountAfter = networkAfter,
            EventRecords = eventRecords
        };
    }

    internal static IReadOnlyList<string> ExtractEventRecords(IEnumerable<string> consoleMessages)
    {
        var records = (consoleMessages ?? Array.Empty<string>())
            .Select(ExtractEventRecord)
            .OfType<string>()
            .ToArray();

        if (records.Length <= MaxEventRecords)
        {
            return records;
        }

        var firstCount = MaxEventRecords / 2;
        return records
            .Take(firstCount)
            .Concat(records.TakeLast(MaxEventRecords - firstCount))
            .ToArray();
    }

    private static string? ExtractEventRecord(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var markerIndex = message.IndexOf(EventMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var record = message[(markerIndex + EventMarker.Length)..]
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return record.Length <= MaxEventRecordLength
            ? record
            : record[..MaxEventRecordLength];
    }

    private static async Task<bool> WaitForSubmissionOutcomeAsync(
        BrowserHost host,
        string beforeUrl,
        long navigationIdBefore,
        int networkBefore,
        Func<int> getNetworkRequestCount,
        int settleMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, settleMs));
        DateTime? requestObservedUtc = null;
        do
        {
            var navigationObserved = !string.Equals(
                host.CurrentUri?.AbsoluteUri ?? string.Empty,
                beforeUrl,
                StringComparison.Ordinal);
            var requestObserved = getNetworkRequestCount() > networkBefore;
            if (requestObserved)
            {
                requestObservedUtc ??= DateTime.UtcNow;
            }

            if (navigationObserved)
            {
                var lifecycle = host.NavigationLifecycleState;
                if (lifecycle != null &&
                    lifecycle.NavigationId > navigationIdBefore &&
                    lifecycle.Phase is FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete
                        or FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed
                        or FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled)
                {
                    return true;
                }
            }
            else if (requestObservedUtc.HasValue &&
                     DateTime.UtcNow - requestObservedUtc.Value >= TimeSpan.FromMilliseconds(500))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
