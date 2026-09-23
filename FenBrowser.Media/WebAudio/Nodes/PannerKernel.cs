namespace FenBrowser.Media.WebAudio;

/// <summary>WA 1.23 PanningModelType.</summary>
public enum PanningModel
{
    EqualPower,
    Hrtf,
}

/// <summary>WA 1.23 DistanceModelType.</summary>
public enum DistanceModel
{
    Linear,
    Inverse,
    Exponential,
}

/// <summary>
/// WA 1.24 AudioListener: the position and orientation every panner of the context is heard
/// from. A graph node with no inputs or outputs, so its a-rate params are computed each
/// quantum before any panner reads them.
/// </summary>
public sealed class ListenerKernel : AudioNodeKernel
{
    internal ListenerKernel(AudioGraph graph)
        : base(graph, 0, 0, 1, ChannelCountMode.Explicit, ChannelInterpretation.Speakers)
    {
        PositionX = AddParam("positionX", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        PositionY = AddParam("positionY", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        PositionZ = AddParam("positionZ", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        ForwardX = AddParam("forwardX", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        ForwardY = AddParam("forwardY", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        ForwardZ = AddParam("forwardZ", -1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        UpX = AddParam("upX", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        UpY = AddParam("upY", 1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        UpZ = AddParam("upZ", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
    }

    public AudioParamKernel PositionX { get; }
    public AudioParamKernel PositionY { get; }
    public AudioParamKernel PositionZ { get; }
    public AudioParamKernel ForwardX { get; }
    public AudioParamKernel ForwardY { get; }
    public AudioParamKernel ForwardZ { get; }
    public AudioParamKernel UpX { get; }
    public AudioParamKernel UpY { get; }
    public AudioParamKernel UpZ { get; }

    internal bool IsConstant =>
        PositionX.IsConstant && PositionY.IsConstant && PositionZ.IsConstant &&
        ForwardX.IsConstant && ForwardY.IsConstant && ForwardZ.IsConstant &&
        UpX.IsConstant && UpY.IsConstant && UpZ.IsConstant;

    protected override void Process(long frame)
    {
    }
}

/// <summary>
/// WA 1.23 PannerNode: the source placed relative to the context's listener. The panning
/// follows the spec's azimuth and elevation computation (WA 6.2) and its equal-power law;
/// the distance and cone gains are WA 6.3 and 6.4. HRTF panning is a synthetic spherical head
/// (<see cref="SphericalHeadHrtf"/>, design WA-D10) rather than a measured response set.
/// </summary>
public sealed class PannerKernel : AudioNodeKernel
{
    public PannerKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.ClampedMax, ChannelInterpretation.Speakers)
    {
        PositionX = AddParam("positionX", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        PositionY = AddParam("positionY", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        PositionZ = AddParam("positionZ", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        OrientationX = AddParam("orientationX", 1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        OrientationY = AddParam("orientationY", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        OrientationZ = AddParam("orientationZ", 0f, float.MinValue, float.MaxValue, AutomationRate.ARate);
    }

    public AudioParamKernel PositionX { get; }
    public AudioParamKernel PositionY { get; }
    public AudioParamKernel PositionZ { get; }
    public AudioParamKernel OrientationX { get; }
    public AudioParamKernel OrientationY { get; }
    public AudioParamKernel OrientationZ { get; }

    public PanningModel PanningModel { get; set; }

    private SphericalHeadHrtf? _hrtf;

    public DistanceModel DistanceModel { get; set; } = DistanceModel.Inverse;

    public double RefDistance { get; set; } = 1;

    public double MaxDistance { get; set; } = 10000;

    public double RolloffFactor { get; set; } = 1;

    public double ConeInnerAngle { get; set; } = 360;

    public double ConeOuterAngle { get; set; } = 360;

    public double ConeOuterGain { get; set; }

    internal override AudioNodeKernel? ExtraDependency => Graph.Listener;

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(2);
        if (input.IsSilent)
            return;

        var listener = Graph.Listener;
        bool constant = listener.IsConstant &&
            PositionX.IsConstant && PositionY.IsConstant && PositionZ.IsConstant &&
            OrientationX.IsConstant && OrientationY.IsConstant && OrientationZ.IsConstant;

        int n = output.Frames;
        var left = output.Channel(0);
        var right = output.Channel(1);
        bool stereo = input.ChannelCount >= 2;
        var inL = input.Channel(0);
        var inR = stereo ? input.Channel(1) : inL;

        if (PanningModel == PanningModel.Hrtf)
        {
            ProcessHrtf(listener, constant, inL, inR, left, right, n);
            output.MarkNotSilent();
            return;
        }

        double azimuth = 0, gain = 1;
        for (int i = 0; i < n; i++)
        {
            if (i == 0 || !constant)
            {
                int k = constant ? 0 : i;
                var source = new Vec(PositionX.Values[k], PositionY.Values[k], PositionZ.Values[k]);
                var orientation = new Vec(OrientationX.Values[k], OrientationY.Values[k], OrientationZ.Values[k]);
                var listenerPosition = new Vec(listener.PositionX.Values[k], listener.PositionY.Values[k], listener.PositionZ.Values[k]);
                var forward = new Vec(listener.ForwardX.Values[k], listener.ForwardY.Values[k], listener.ForwardZ.Values[k]);
                var up = new Vec(listener.UpX.Values[k], listener.UpY.Values[k], listener.UpZ.Values[k]);
                azimuth = Azimuth(source, listenerPosition, forward, up);
                gain = DistanceGain((source - listenerPosition).Length) * ConeGain(source, orientation, listenerPosition);
            }

            // The panned sample and the distance/cone gain are each a float before they meet,
            // as every engine's float pipeline has them, so a static source's output is exact.
            PanEqualPower(azimuth, stereo, inL[i], inR[i], out float l, out float r);
            float g = (float)gain;
            left[i] = l * g;
            right[i] = r * g;
        }

        output.MarkNotSilent();
    }

    // HRTF: the head is pointed once per quantum (as engines do for their convolution
    // kernels); the distance and cone gain still follow every frame of an automated position.
    private void ProcessHrtf(ListenerKernel listener, bool constant, Span<float> inL, Span<float> inR, Span<float> left, Span<float> right, int n)
    {
        _hrtf ??= new SphericalHeadHrtf(Graph.SampleRate);
        var source = new Vec(PositionX.Values[0], PositionY.Values[0], PositionZ.Values[0]);
        var listenerPosition = new Vec(listener.PositionX.Values[0], listener.PositionY.Values[0], listener.PositionZ.Values[0]);
        var forward = new Vec(listener.ForwardX.Values[0], listener.ForwardY.Values[0], listener.ForwardZ.Values[0]);
        var up = new Vec(listener.UpX.Values[0], listener.UpY.Values[0], listener.UpZ.Values[0]);
        _hrtf.SetDirection(Azimuth(source, listenerPosition, forward, up), Elevation(source, listenerPosition, forward, up));
        _hrtf.Process(inL, inR, left, right, n);

        double gain = 1;
        for (int i = 0; i < n; i++)
        {
            if (i == 0 || !constant)
            {
                int k = constant ? 0 : i;
                var at = new Vec(PositionX.Values[k], PositionY.Values[k], PositionZ.Values[k]);
                var orientation = new Vec(OrientationX.Values[k], OrientationY.Values[k], OrientationZ.Values[k]);
                var from = new Vec(listener.PositionX.Values[k], listener.PositionY.Values[k], listener.PositionZ.Values[k]);
                gain = DistanceGain((at - from).Length) * ConeGain(at, orientation, from);
            }

            float g = (float)gain;
            left[i] *= g;
            right[i] *= g;
        }
    }

    // WA 6.2 "Azimuth and Elevation": the elevation half, which only HRTF panning hears.
    internal static double Elevation(Vec source, Vec listener, Vec forward, Vec up)
    {
        var sourceListener = (source - listener).Normalized();
        if (sourceListener.IsZero)
            return 0;

        var listenerRight = forward.Cross(up).Normalized();
        var upAligned = listenerRight.Cross(forward.Normalized());
        double elevation = 90 - 180 * Math.Acos(Math.Clamp(sourceListener.Dot(upAligned), -1, 1)) / Math.PI;
        if (elevation > 90)
            elevation = 180 - elevation;
        else if (elevation < -90)
            elevation = -180 - elevation;
        return double.IsNaN(elevation) ? 0 : elevation;
    }

    // WA 6.2 "Azimuth and Elevation": the azimuth half.
    internal static double Azimuth(Vec source, Vec listener, Vec forward, Vec up)
    {
        var sourceListener = (source - listener).Normalized();
        if (sourceListener.IsZero)
            return 0;

        var listenerRight = forward.Cross(up).Normalized();
        var forwardNormalized = forward.Normalized();
        var upAligned = listenerRight.Cross(forwardNormalized);
        double upProjection = sourceListener.Dot(upAligned);
        var projected = (sourceListener - upAligned * upProjection).Normalized();

        double azimuth = 180 * Math.Acos(Math.Clamp(projected.Dot(listenerRight), -1, 1)) / Math.PI;
        double frontBack = projected.Dot(forwardNormalized);
        if (frontBack < 0)
            azimuth = 360 - azimuth;
        if (azimuth >= 0 && azimuth <= 270)
            azimuth = 90 - azimuth;
        else
            azimuth = 450 - azimuth;
        return double.IsNaN(azimuth) ? 0 : azimuth;
    }

    // WA 6.3 "Equal-power panning".
    private static void PanEqualPower(double azimuth, bool stereo, float inL, float inR, out float left, out float right)
    {
        azimuth = Math.Clamp(azimuth, -180, 180);
        if (azimuth < -90)
            azimuth = -180 - azimuth;
        else if (azimuth > 90)
            azimuth = 180 - azimuth;

        if (!stereo)
        {
            double x = (azimuth + 90) / 180;
            left = (float)(inL * Math.Cos(x * Math.PI / 2));
            right = (float)(inL * Math.Sin(x * Math.PI / 2));
            return;
        }

        if (azimuth <= 0)
        {
            double x = (azimuth + 90) / 90;
            left = (float)(inL + inR * Math.Cos(x * Math.PI / 2));
            right = (float)(inR * Math.Sin(x * Math.PI / 2));
        }
        else
        {
            double x = azimuth / 90;
            left = (float)(inL * Math.Cos(x * Math.PI / 2));
            right = (float)(inR + inL * Math.Sin(x * Math.PI / 2));
        }
    }

    // WA 6.4 "Distance Effects".
    internal double DistanceGain(double distance)
    {
        double reference = RefDistance, max = MaxDistance, rolloff = RolloffFactor;
        switch (DistanceModel)
        {
            case DistanceModel.Linear:
            {
                double d = Math.Clamp(distance, Math.Min(reference, max), Math.Max(reference, max));
                if (max == reference)
                    return 1 - Math.Clamp(rolloff, 0, 1);
                return 1 - Math.Clamp(rolloff, 0, 1) * (d - reference) / (max - reference);
            }

            case DistanceModel.Exponential:
            {
                double d = Math.Max(distance, reference);
                return reference == 0 ? 0 : Math.Pow(d / reference, -rolloff);
            }

            default:
            {
                double d = Math.Max(distance, reference);
                double denominator = reference + rolloff * (d - reference);
                return denominator == 0 ? 0 : reference / denominator;
            }
        }
    }

    // WA 6.5 "Sound Cones".
    internal double ConeGain(Vec source, Vec orientation, Vec listener)
    {
        if (orientation.IsZero || (ConeInnerAngle == 360 && ConeOuterAngle == 360))
            return 1;

        var sourceToListener = (listener - source).Normalized();
        var normalizedOrientation = orientation.Normalized();
        double angle = 180 * Math.Acos(Math.Clamp(sourceToListener.Dot(normalizedOrientation), -1, 1)) / Math.PI;
        double absAngle = Math.Abs(angle);
        double absInner = Math.Abs(ConeInnerAngle) / 2;
        double absOuter = Math.Abs(ConeOuterAngle) / 2;
        if (absAngle <= absInner)
            return 1;
        if (absAngle >= absOuter)
            return ConeOuterGain;
        double x = (absAngle - absInner) / (absOuter - absInner);
        return (1 - x) + ConeOuterGain * x;
    }
}

/// <summary>A 3-vector for the panner's geometry.</summary>
internal readonly record struct Vec(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public bool IsZero => X == 0 && Y == 0 && Z == 0;

    public Vec Normalized()
    {
        double length = Length;
        return length == 0 || double.IsNaN(length) ? new Vec(0, 0, 0) : new Vec(X / length, Y / length, Z / length);
    }

    public double Dot(Vec o) => X * o.X + Y * o.Y + Z * o.Z;

    public Vec Cross(Vec o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s, a.Z * s);
}
