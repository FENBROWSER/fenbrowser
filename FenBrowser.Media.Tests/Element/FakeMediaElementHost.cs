using FenBrowser.Media.Element;

namespace FenBrowser.Media.Tests.Element;

/// <summary>A child node of the fake element.</summary>
internal sealed class FakeNode(string name, bool isSource, Dictionary<string, string>? attributes = null)
{
    public string Name { get; } = name;
    public bool IsSource { get; } = isSource;
    public Dictionary<string, string> Attributes { get; } = attributes ?? [];

    public override string ToString() => Name;
}

/// <summary>A promise as the fake host sees it.</summary>
internal sealed class FakePromise(int id)
{
    public int Id { get; } = id;
    public string State { get; set; } = "pending";
}

internal sealed class FakeResource : IMediaResource
{
    public FakeResource(MediaFetchRequest request, IMediaResourceClient client)
    {
        Request = request;
        Client = client;
    }

    public MediaFetchRequest Request { get; }
    public IMediaResourceClient Client { get; }
    public bool Disposed { get; private set; }
    public bool FullLoadRequested { get; private set; }
    public List<(MediaTime Target, bool Approximate)> Seeks { get; } = [];
    public (bool Playing, double Rate, bool PreservesPitch, double Volume) LastPlayback { get; private set; }

    public void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume) =>
        LastPlayback = (potentiallyPlaying, playbackRate, preservesPitch, effectiveVolume);

    public void Seek(MediaTime target, bool approximateForSpeed) => Seeks.Add((target, approximateForSpeed));

    public void RequestFullLoad() => FullLoadRequested = true;

    public void Dispose() => Disposed = true;

    /// <summary>Metadata for a 10 s, 320×240 resource that can seek anywhere.</summary>
    public void LoadMetadata(double durationSeconds = 10, int width = 320, int height = 240)
    {
        var duration = double.IsPositiveInfinity(durationSeconds) ? MediaTime.PositiveInfinity : MediaTime.FromSeconds(durationSeconds);
        Client.SeekableChanged(MediaTimeRanges.Single(MediaTime.Zero, duration));
        Client.MetadataAvailable(new MediaResourceMetadata(duration, width, height, []));
    }
}

/// <summary>
/// A scripted host: tasks queue until <see cref="Run"/>, stable-state continuations run
/// after the current task (or at the start of <see cref="Run"/> for script-initiated work),
/// and everything observable is appended to <see cref="Log"/> in order.
/// </summary>
internal sealed class FakeMediaElementHost : IMediaElementHost
{
    private readonly Queue<Action> _tasks = new();
    private readonly List<Action> _stable = [];
    private int _nextPromiseId;

    public bool IsVideo { get; set; } = true;
    public string? SrcAttribute { get; set; }
    public bool HasAutoplayAttribute { get; set; }
    public bool HasLoopAttribute { get; set; }
    public bool HasMutedAttribute { get; set; }
    public string? CrossOriginAttribute { get; set; }
    public string? PreloadAttribute { get; set; }
    public bool IsAllowedToPlay { get; set; } = true;
    public bool DocumentAllowsAutoplay { get; set; } = true;
    public bool Delaying { get; private set; }
    public int RenderInvalidations { get; private set; }
    public bool ReturnNoResource { get; set; }
    public Func<string, bool> MediaMatches { get; set; } = _ => true;

    public List<FakeNode> Children { get; } = [];
    public List<string> Log { get; } = [];
    public List<FakeResource> Resources { get; } = [];
    public FakeResource? Resource => Resources.Count == 0 ? null : Resources[^1];

    public object? FirstChild => Children.Count == 0 ? null : Children[0];

    public object? NextSibling(object child)
    {
        int index = Children.IndexOf((FakeNode)child);
        return index >= 0 && index + 1 < Children.Count ? Children[index + 1] : null;
    }

    public bool IsSourceElement(object node) => ((FakeNode)node).IsSource;

    public string? GetSourceAttribute(object source, string name) =>
        ((FakeNode)source).Attributes.TryGetValue(name, out var value) ? value : null;

    public string? ResolveUrl(string value) =>
        value.Contains("::bad", StringComparison.Ordinal) ? null : "https://example.test/" + value.TrimStart('/');

    public bool MediaQueryMatches(string query) => MediaMatches(query);

    public void QueueTask(Action task) => _tasks.Enqueue(task);

    public void AwaitStableState(Action continuation) => _stable.Add(continuation);

    public void FireEvent(string type) => Log.Add(type);

    public void FireEventAt(object node, string type) => Log.Add($"{type}@{node}");

    public void SetDelayingLoadEvent(bool delaying)
    {
        Delaying = delaying;
        Log.Add(delaying ? "[delay-load]" : "[stop-delay]");
    }

    public void InvalidateRendering(bool sizeChanged) => RenderInvalidations++;

    public object CreatePromise() => new FakePromise(++_nextPromiseId);

    public void ResolvePromise(object promise)
    {
        var p = (FakePromise)promise;
        Assert.Equal("pending", p.State);
        p.State = "resolved";
        Log.Add($"resolve#{p.Id}");
    }

    public void RejectPromise(object promise, PlayRejection reason)
    {
        var p = (FakePromise)promise;
        Assert.Equal("pending", p.State);
        p.State = reason.ToString();
        Log.Add($"reject#{p.Id}:{reason}");
    }

    public IMediaResource? StartResource(MediaFetchRequest request, IMediaResourceClient client)
    {
        Log.Add("[fetch " + request.Url + "]");
        if (ReturnNoResource)
            return null;
        var resource = new FakeResource(request, client);
        Resources.Add(resource);
        return resource;
    }

    /// <summary>Runs stable states, then every queued task (each followed by stable states).</summary>
    public void Run()
    {
        RunStableStates();
        while (_tasks.Count > 0)
        {
            _tasks.Dequeue()();
            RunStableStates();
        }
    }

    /// <summary>Only the stable-state continuations, as at the end of the current script.</summary>
    public void RunStableStates()
    {
        while (_stable.Count > 0)
        {
            var batch = _stable.ToList();
            _stable.Clear();
            foreach (var continuation in batch)
                continuation();
        }
    }

    public int PendingTasks => _tasks.Count;

    /// <summary>The events (no bracketed bookkeeping, no promise entries) since the last call.</summary>
    public List<string> TakeEvents()
    {
        var events = Log.Where(e => !e.StartsWith('[') && !e.StartsWith("resolve#", StringComparison.Ordinal) && !e.StartsWith("reject#", StringComparison.Ordinal)).ToList();
        Log.Clear();
        return events;
    }

    public List<string> TakeLog()
    {
        var log = Log.ToList();
        Log.Clear();
        return log;
    }

    public FakeNode AddSource(string name, string? src, string? type = null, string? media = null)
    {
        var attributes = new Dictionary<string, string>();
        if (src is not null)
            attributes["src"] = src;
        if (type is not null)
            attributes["type"] = type;
        if (media is not null)
            attributes["media"] = media;
        var node = new FakeNode(name, isSource: true, attributes);
        Children.Add(node);
        return node;
    }
}
