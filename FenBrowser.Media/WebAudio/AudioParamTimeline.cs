namespace FenBrowser.Media.WebAudio;

/// <summary>The kinds of automation event WA 1.6.2 defines.</summary>
public enum AutomationEventKind
{
    SetValue,
    LinearRamp,
    ExponentialRamp,
    SetTarget,
    SetValueCurve,
}

/// <summary>Why the timeline refused an event; the realm turns it into the named exception.</summary>
public enum AutomationError
{
    None,

    /// <summary>WA 1.6.2: the event lands inside a SetValueCurve's interval, or a curve would cover an existing event.</summary>
    NotSupported,
}

/// <summary>
/// One AudioParam's automation timeline (WA 1.6.2, 1.6.3): the ordered list of events and
/// the value it produces at any time. Thread-safe: the control thread inserts and cancels
/// under the lock, the rendering thread computes under the same lock once per quantum.
/// </summary>
public sealed class AudioParamTimeline
{
    private readonly struct AutomationEvent
    {
        public AutomationEventKind Kind { get; init; }

        /// <summary>The event's time: the start for SetValue, SetTarget and curves, the end for ramps.</summary>
        public double Time { get; init; }

        /// <summary>The target value: the set value, the ramp's end value, SetTarget's target.</summary>
        public double Value { get; init; }

        public double TimeConstant { get; init; }

        public float[]? Curve { get; init; }

        public double Duration { get; init; }

        /// <summary>
        /// For a curve that cancelAndHoldAtTime cut short: where it stops, while its values
        /// keep mapping onto the original duration (WA 1.6.2 cancelAndHoldAtTime step 5).
        /// </summary>
        public double CurveCutoff { get; init; }

        /// <summary>For a ramp with no event before it when inserted: where it starts from (WA 1.6.2).</summary>
        public double RampStartTime { get; init; }

        public double RampStartValue { get; init; }

        /// <summary>True for the SetValue a curve implies at its end (WA 1.6.2 setValueCurveAtTime).</summary>
        public bool ImpliedByCurve { get; init; }
    }

    private readonly object _gate = new();
    private readonly List<AutomationEvent> _events = [];

    // The value each event starts from: the automation's value at the event's own time,
    // computed from the events before it. Kept in step with _events so a long chain of
    // SetTarget events costs O(1) per sample, not O(n).
    private readonly List<double> _startValues = [];

    private double _defaultValue;

    public AudioParamTimeline(double defaultValue)
    {
        _defaultValue = defaultValue;
    }

    /// <summary>Number of events, for tests and diagnostics.</summary>
    public int Count
    {
        get { lock (_gate) return _events.Count; }
    }

    /// <summary>The value the timeline produces before its first event.</summary>
    public double DefaultValue
    {
        get { lock (_gate) return _defaultValue; }
    }

    public AutomationError SetValueAtTime(double value, double time) =>
        Insert(new AutomationEvent { Kind = AutomationEventKind.SetValue, Value = value, Time = time });

    /// <summary>
    /// <paramref name="callTime"/> and <paramref name="currentValue"/> are where the ramp
    /// starts if nothing comes before it: the context time of the call and the param's
    /// value then.
    /// </summary>
    public AutomationError LinearRampToValueAtTime(double value, double endTime, double callTime, double currentValue) =>
        Insert(new AutomationEvent
        {
            Kind = AutomationEventKind.LinearRamp,
            Value = value,
            Time = endTime,
            RampStartTime = callTime,
            RampStartValue = currentValue,
        });

    public AutomationError ExponentialRampToValueAtTime(double value, double endTime, double callTime, double currentValue) =>
        Insert(new AutomationEvent
        {
            Kind = AutomationEventKind.ExponentialRamp,
            Value = value,
            Time = endTime,
            RampStartTime = callTime,
            RampStartValue = currentValue,
        });

    public AutomationError SetTargetAtTime(double target, double startTime, double timeConstant) =>
        Insert(new AutomationEvent
        {
            Kind = AutomationEventKind.SetTarget,
            Value = target,
            Time = startTime,
            TimeConstant = timeConstant,
        });

    public AutomationError SetValueCurveAtTime(float[] values, double startTime, double duration)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < 2)
            throw new ArgumentException("A value curve needs at least two values.", nameof(values));

        var curve = new AutomationEvent
        {
            Kind = AutomationEventKind.SetValueCurve,
            Time = startTime,
            Duration = duration,
            CurveCutoff = startTime + duration,
            Curve = values,
            Value = values[^1],
        };

        lock (_gate)
        {
            double end = startTime + duration;
            foreach (var existing in _events)
            {
                // No event may fall strictly inside the new curve's interval, and the curve
                // may not start inside another curve's.
                if (existing.Time > startTime && existing.Time < end)
                    return AutomationError.NotSupported;
                if (existing.Kind == AutomationEventKind.SetValueCurve &&
                    ((startTime >= existing.Time && startTime < existing.Time + existing.Duration) ||
                     (existing.Time >= startTime && existing.Time < end)))
                {
                    return AutomationError.NotSupported;
                }
            }

            InsertLocked(curve);

            // WA 1.6.2: "an implicit call to setValueAtTime() is made at time T + D with value V[N - 1]".
            InsertLocked(new AutomationEvent
            {
                Kind = AutomationEventKind.SetValue,
                Time = end,
                Value = values[^1],
                ImpliedByCurve = true,
            });
            Recompute();
            return AutomationError.None;
        }
    }

    /// <summary>
    /// WA 1.6.2 cancelScheduledValues: removes every event at or after <paramref name="cancelTime"/>,
    /// and a curve still running then.
    /// </summary>
    public void CancelScheduledValues(double cancelTime)
    {
        lock (_gate)
        {
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                bool activeCurve = e.Kind == AutomationEventKind.SetValueCurve && e.Time < cancelTime && cancelTime < e.Time + e.Duration;
                if (e.Time >= cancelTime || activeCurve)
                    _events.RemoveAt(i);
            }

            Recompute();
        }
    }

    /// <summary>WA 1.6.2 cancelAndHoldAtTime: cancels after <paramref name="cancelTime"/> and holds the value there.</summary>
    public void CancelAndHoldAtTime(double cancelTime)
    {
        lock (_gate)
        {
            // The held value is what the param outputs, a float; a ramp after the cancel starts
            // from exactly that.
            double held = (float)ValueAtLocked(cancelTime);
            int next = FirstAfterLocked(cancelTime);
            int last = next - 1;

            if (next < _events.Count && IsRamp(_events[next].Kind))
            {
                // A ramp in progress at the cancel time ends there, at the value it had.
                var ramp = _events[next];
                _events[next] = ramp with { Time = cancelTime, Value = held };
                TruncateAfterLocked(next);
            }
            else if (last >= 0 && _events[last].Kind == AutomationEventKind.SetTarget)
            {
                TruncateAfterLocked(last);
                InsertLocked(new AutomationEvent { Kind = AutomationEventKind.SetValue, Time = cancelTime, Value = held });
            }
            else if (last >= 0 && _events[last].Kind == AutomationEventKind.SetValueCurve &&
                     cancelTime <= _events[last].Time)
            {
                // WA 1.6.2: the curve is cut to the duration it had played by the cancel time,
                // which is none - it never happened, and the value before it holds.
                TruncateAfterLocked(last - 1);
            }
            else if (last >= 0 && _events[last].Kind == AutomationEventKind.SetValueCurve &&
                     cancelTime < _events[last].Time + _events[last].Duration)
            {
                _events[last] = _events[last] with { CurveCutoff = cancelTime };
                TruncateAfterLocked(last);
                InsertLocked(new AutomationEvent { Kind = AutomationEventKind.SetValue, Time = cancelTime, Value = held });
            }
            else
            {
                TruncateAfterLocked(last);
            }

            Recompute();
        }
    }

    /// <summary>WA 1.6.3: the timeline's value at <paramref name="time"/>.</summary>
    public double ValueAt(double time)
    {
        lock (_gate)
            return ValueAtLocked(time);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the values at frames
    /// <paramref name="startFrame"/> onward, one frame apart at <paramref name="sampleRate"/>.
    /// Returns true when every value is the same, so a caller may treat the block as constant.
    /// </summary>
    public bool Fill(Span<float> destination, long startFrame, double sampleRate)
    {
        lock (_gate)
        {
            if (_events.Count == 0)
            {
                destination.Fill((float)_defaultValue);
                return true;
            }

            double firstTime = startFrame / sampleRate;

            // Nothing changes inside the block: every event is before it and the last one
            // has settled (a SetValue, or a ramp that has ended).
            var tail = _events[^1];
            if (tail.Time <= firstTime && tail.Kind is AutomationEventKind.SetValue or AutomationEventKind.LinearRamp or AutomationEventKind.ExponentialRamp)
            {
                destination.Fill((float)tail.Value);
                return true;
            }

            bool constant = true;
            float first = 0f;
            for (int i = 0; i < destination.Length; i++)
            {
                float v = (float)ValueAtLocked((startFrame + i) / sampleRate);
                destination[i] = v;
                if (i == 0)
                    first = v;
                else if (v != first)
                    constant = false;
            }

            return constant;
        }
    }

    private AutomationError Insert(AutomationEvent automationEvent)
    {
        lock (_gate)
        {
            foreach (var existing in _events)
            {
                // An event at the curve's own start time lands inside it too.
                if (existing.Kind == AutomationEventKind.SetValueCurve &&
                    automationEvent.Time >= existing.Time &&
                    automationEvent.Time < existing.Time + existing.Duration)
                {
                    return AutomationError.NotSupported;
                }
            }

            InsertLocked(automationEvent);
            Recompute();
            return AutomationError.None;
        }
    }

    // After every event with the same time (WA 1.6.2 "placed in the list after them").
    private void InsertLocked(AutomationEvent automationEvent)
    {
        int index = FirstAfterLocked(automationEvent.Time);
        _events.Insert(index, automationEvent);
    }

    private int FirstAfterLocked(double time)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_events[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private void TruncateAfterLocked(int lastKept)
    {
        int from = lastKept + 1;
        if (from < _events.Count)
            _events.RemoveRange(from, _events.Count - from);
    }

    private void Recompute()
    {
        _startValues.Clear();
        for (int i = 0; i < _events.Count; i++)
            _startValues.Add(ValueAfterPrefix(_events[i].Time, i));
    }

    // The value at time t produced by the first `count` events alone, for a t at or after
    // the last of them.
    private double ValueAfterPrefix(double time, int count)
    {
        if (count == 0)
            return _defaultValue;

        var e = _events[count - 1];
        return e.Kind switch
        {
            AutomationEventKind.SetValue => e.Value,
            AutomationEventKind.LinearRamp or AutomationEventKind.ExponentialRamp => RampValue(count - 1, time),
            AutomationEventKind.SetTarget => TargetValue(e, _startValues[count - 1], time),
            _ => CurveValue(e, time),
        };
    }

    private double ValueAtLocked(double time)
    {
        int next = FirstAfterLocked(time);

        // A ramp whose end is still ahead is in progress from the event before it.
        if (next < _events.Count && IsRamp(_events[next].Kind))
            return RampValue(next, time);

        if (next == 0)
            return _defaultValue;

        var e = _events[next - 1];
        return e.Kind switch
        {
            AutomationEventKind.SetValue => e.Value,
            AutomationEventKind.LinearRamp or AutomationEventKind.ExponentialRamp => e.Value,
            AutomationEventKind.SetTarget => TargetValue(e, _startValues[next - 1], time),
            _ => CurveValue(e, time),
        };
    }

    // WA 1.6.2 linearRampToValueAtTime / exponentialRampToValueAtTime.
    private double RampValue(int index, double time)
    {
        var ramp = _events[index];
        double t1 = ramp.Time;
        double v1 = ramp.Value;
        if (time >= t1)
            return v1;

        double t0, v0;
        if (index == 0)
        {
            t0 = ramp.RampStartTime;
            v0 = ramp.RampStartValue;
        }
        else
        {
            var previous = _events[index - 1];
            t0 = previous.Time;
            v0 = ValueAtOwnTime(index - 1);
        }

        if (time < t0)
            return v0;
        if (t1 <= t0)
            return v1;

        double fraction = (time - t0) / (t1 - t0);
        if (ramp.Kind == AutomationEventKind.LinearRamp)
            return v0 + (v1 - v0) * fraction;

        // An exponential ramp cannot pass through zero or change sign; it holds V0 instead.
        if (v0 == 0 || (v0 < 0) != (v1 < 0))
            return v0;
        return v0 * Math.Pow(v1 / v0, fraction);
    }

    // The value an event has at its own time: where a ramp that follows it starts from.
    private double ValueAtOwnTime(int index)
    {
        var e = _events[index];
        return e.Kind switch
        {
            AutomationEventKind.SetValue => e.Value,
            AutomationEventKind.LinearRamp or AutomationEventKind.ExponentialRamp => e.Value,
            AutomationEventKind.SetTarget => _startValues[index],
            _ => e.Curve![0],
        };
    }

    // WA 1.6.2 setTargetAtTime: v(t) = V1 + (V0 - V1) * exp(-(t - T0) / tau).
    private static double TargetValue(AutomationEvent e, double startValue, double time)
    {
        if (time < e.Time)
            return startValue;
        if (e.TimeConstant == 0)
            return e.Value;
        return e.Value + (startValue - e.Value) * Math.Exp(-(time - e.Time) / e.TimeConstant);
    }

    // WA 1.6.2 setValueCurveAtTime: linear interpolation over N - 1 intervals of the duration.
    private static double CurveValue(AutomationEvent e, double time)
    {
        var curve = e.Curve!;
        int n = curve.Length;
        double end = e.Time + e.Duration;
        if (time >= e.CurveCutoff || time >= end)
            return time >= end ? curve[n - 1] : CurveAt(curve, e, e.CurveCutoff);
        if (time < e.Time)
            return curve[0];
        return CurveAt(curve, e, time);
    }

    private static double CurveAt(float[] curve, AutomationEvent e, double time)
    {
        int n = curve.Length;
        double position = (n - 1) / e.Duration * (time - e.Time);
        int k = (int)Math.Floor(position);
        if (k >= n - 1)
            return curve[n - 1];
        if (k < 0)
            return curve[0];
        return curve[k] + (curve[k + 1] - curve[k]) * (position - k);
    }

    private static bool IsRamp(AutomationEventKind kind) =>
        kind is AutomationEventKind.LinearRamp or AutomationEventKind.ExponentialRamp;
}
