namespace FenBrowser.Media.WebAudio;

/// <summary>WA 1.6.1 <c>AutomationRate</c>.</summary>
public enum AutomationRate
{
    ARate,
    KRate,
}

/// <summary>
/// The rendering-thread half of an <c>AudioParam</c> (WA 1.6). Each quantum it computes the
/// intrinsic value from the timeline, adds whatever is connected to it (mixed down to
/// mono), and clamps to the nominal range (WA 1.6.3 "Computation of Value").
/// </summary>
public sealed class AudioParamKernel
{
    private readonly float[] _values;
    private readonly AudioBus _monoBus;
    private float _currentValue;

    internal AudioParamKernel(AudioNodeKernel owner, string name, float defaultValue, float minValue, float maxValue, AutomationRate rate, bool rateFixed, int frames)
    {
        _values = new float[frames];
        _monoBus = new AudioBus(1, frames);
        Owner = owner;
        Name = name;
        DefaultValue = defaultValue;
        MinValue = minValue;
        MaxValue = maxValue;
        Rate = rate;
        RateFixed = rateFixed;
        Timeline = new AudioParamTimeline(defaultValue);
        _currentValue = defaultValue;
    }

    public AudioNodeKernel Owner { get; }

    public string Name { get; }

    public float DefaultValue { get; }

    public float MinValue { get; }

    public float MaxValue { get; }

    /// <summary>Changed only through a control message; see <see cref="RateFixed"/>.</summary>
    public AutomationRate Rate { get; set; }

    /// <summary>True for params whose rate the spec pins (AudioBufferSourceNode's playbackRate and detune).</summary>
    public bool RateFixed { get; }

    public AudioParamTimeline Timeline { get; }

    /// <summary>Audio-rate connections into the param (WA 1.5.3 connect(AudioParam)).</summary>
    public List<AudioConnection> Connections { get; } = [];

    /// <summary>
    /// WA 1.6 [[current value]]: the intrinsic value at the start of the latest quantum, which
    /// the <c>value</c> getter reads. The control thread also writes it from the setter.
    /// </summary>
    public float CurrentValue
    {
        get => Volatile.Read(ref _currentValue);
        set => Volatile.Write(ref _currentValue, value);
    }

    /// <summary>True when every value of the latest quantum is the same.</summary>
    public bool IsConstant { get; private set; } = true;

    /// <summary>The latest quantum's computed values; for a k-rate param every entry is the same.</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>The value to use for a k-rate read, or when the block is constant.</summary>
    public float FirstValue => _values[0];

    internal void Compute(long frame, float sampleRate)
    {
        bool constant;
        if (Rate == AutomationRate.KRate)
        {
            float v = (float)Timeline.ValueAt(frame / (double)sampleRate);
            _values.AsSpan().Fill(v);
            constant = true;
        }
        else
        {
            constant = Timeline.Fill(_values, frame, sampleRate);
        }

        CurrentValue = _values[0];

        if (Connections.Count > 0)
        {
            PullInputs();
            if (!_monoBus.IsSilent)
            {
                var input = _monoBus.Channel(0);
                if (Rate == AutomationRate.KRate)
                {
                    float add = input[0];
                    for (int i = 0; i < _values.Length; i++)
                        _values[i] += add;
                }
                else
                {
                    for (int i = 0; i < _values.Length; i++)
                        _values[i] += input[i];
                    constant = false;
                }
            }
        }

        float min = MinValue, max = MaxValue;
        for (int i = 0; i < _values.Length; i++)
        {
            float v = _values[i];
            // WA 1.6.3: a NaN computed value becomes the default value.
            if (float.IsNaN(v))
                _values[i] = v = DefaultValue;
            if (v < min)
                _values[i] = min;
            else if (v > max)
                _values[i] = max;
        }

        IsConstant = constant;
    }

    // WA 1.6.3 step 3: the connected outputs are mixed down to mono under the speaker rules.
    private void PullInputs()
    {
        _monoBus.Reset(1);
        foreach (var c in Connections)
        {
            if (c.Node.IsSilentOutput)
                continue;
            var bus = c.Node.Outputs[c.Output].Bus;
            ChannelMixing.MixInto(bus, _monoBus, ChannelInterpretation.Speakers);
        }
    }
}
