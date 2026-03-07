using System;
using System.Threading;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs;

public class GeolocationApiTests : IDisposable
{
    public GeolocationApiTests()
    {
        GeolocationAPI.SetPermission(true);
    }

    public void Dispose()
    {
        GeolocationAPI.SetPermission(false);
    }

    [Fact]
    public void WatchPosition_ReturnsDistinctIds()
    {
        var geolocation = GeolocationAPI.CreateGeolocationObject();
        var watchPosition = geolocation.Get("watchPosition").AsFunction();
        var clearWatch = geolocation.Get("clearWatch").AsFunction();
        var success = FenValue.FromFunction(new FenFunction("success", (args, _) => FenValue.Undefined));

        var id1 = (int)watchPosition.Invoke(new[] { success }, (IExecutionContext)null).ToNumber();
        var id2 = (int)watchPosition.Invoke(new[] { success }, (IExecutionContext)null).ToNumber();

        try
        {
            Assert.True(id1 > 0);
            Assert.True(id2 > 0);
            Assert.NotEqual(id1, id2);
        }
        finally
        {
            clearWatch.Invoke(new[] { FenValue.FromNumber(id1) }, (IExecutionContext)null);
            clearWatch.Invoke(new[] { FenValue.FromNumber(id2) }, (IExecutionContext)null);
        }
    }

    [Fact]
    public void WatchPosition_FiresUntilCleared()
    {
        var geolocation = GeolocationAPI.CreateGeolocationObject();
        var watchPosition = geolocation.Get("watchPosition").AsFunction();
        var clearWatch = geolocation.Get("clearWatch").AsFunction();
        using var callbackEvent = new ManualResetEventSlim(false);
        var callbackCount = 0;

        var success = FenValue.FromFunction(new FenFunction("success", (args, _) =>
        {
            if (Interlocked.Increment(ref callbackCount) >= 2)
            {
                callbackEvent.Set();
            }
            return FenValue.Undefined;
        }));

        var options = new FenObject();
        options.Set("timeout", FenValue.FromNumber(250));
        var watchId = (int)watchPosition.Invoke(new[] { success, FenValue.Undefined, FenValue.FromObject(options) }, (IExecutionContext)null).ToNumber();

        try
        {
            Assert.True(callbackEvent.Wait(TimeSpan.FromSeconds(2)), "Expected watchPosition to fire repeatedly.");
            var callbacksBeforeClear = Volatile.Read(ref callbackCount);

            clearWatch.Invoke(new[] { FenValue.FromNumber(watchId) }, (IExecutionContext)null);
            Thread.Sleep(350);

            Assert.True(callbacksBeforeClear >= 2);
            Assert.Equal(callbacksBeforeClear, Volatile.Read(ref callbackCount));
        }
        finally
        {
            clearWatch.Invoke(new[] { FenValue.FromNumber(watchId) }, (IExecutionContext)null);
        }
    }

    [Fact]
    public void WatchPosition_PermissionDenied_InvokesErrorCallback()
    {
        GeolocationAPI.SetPermission(false);
        var geolocation = GeolocationAPI.CreateGeolocationObject();
        var watchPosition = geolocation.Get("watchPosition").AsFunction();
        string errorMessage = null;

        var error = FenValue.FromFunction(new FenFunction("error", (args, _) =>
        {
            if (args.Length > 0 && args[0].IsObject)
            {
                errorMessage = args[0].AsObject()?.Get("message").ToString();
            }
            return FenValue.Undefined;
        }));

        var watchId = (int)watchPosition.Invoke(new[] { FenValue.Undefined, error }, (IExecutionContext)null).ToNumber();

        Assert.True(watchId > 0);
        Assert.Equal("Geolocation permission denied", errorMessage);
    }
}
