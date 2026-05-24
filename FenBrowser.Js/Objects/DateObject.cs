namespace FenBrowser.Js.Objects;

public sealed class DateObject : JsObject
{
    public DateObject(double timeValue)
    {
        TimeValue = timeValue;
    }

    public double TimeValue { get; set; }
}
