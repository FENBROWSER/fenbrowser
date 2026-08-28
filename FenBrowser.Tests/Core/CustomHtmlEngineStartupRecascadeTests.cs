using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class CustomHtmlEngineStartupRecascadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScheduleRecascade_WhilePostScriptSnapshotIsPending_DoesNotStartWorker(bool fullRecascade)
    {
        using var engine = new CustomHtmlEngine();
        var document = Document.CreateHtmlDocument();

        SetField(engine, "_activeDom", document.DocumentElement);
        SetField(engine, "_activeBaseUri", new Uri("https://example.test/"));
        SetField(engine, "_activeFetchCss", new Func<Uri, Task<string>>(_ => Task.FromResult(string.Empty)));
        SetField(engine, "_awaitingPostScriptSnapshot", true);

        engine.ScheduleRecascade(fullRecascade);

        Assert.Null(GetField<Task>(engine, "_pendingRecascade"));
        Assert.False(GetField<bool>(engine, "_recascadeWorkerRunning"));
        Assert.False(GetField<bool>(engine, "_recascadeRequested"));
        Assert.True(GetField<bool>(engine, "_postScriptRecascadeDeferred"));
        Assert.Equal(fullRecascade, GetField<bool>(engine, "_postScriptFullRecascadeDeferred"));
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field.GetValue(target);
    }
}
