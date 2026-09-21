using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void RegisterGauges_reports_live_delegate_values()
    {
        var depth = 0;
        ReviewForgeTelemetry.RegisterGauges(() => depth, () => 100, () => 7, () => 2);

        var readings = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => l.EnableMeasurementEvents(instrument);
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
            readings[instrument.Name] = measurement);
        listener.Start();

        listener.RecordObservableInstruments();
        Assert.Equal(0, readings["reviewforge.queue.depth"]);
        Assert.Equal(100, readings["reviewforge.queue.capacity"]);
        Assert.Equal(7, readings["reviewforge.claims.active"]);
        Assert.Equal(2, readings["reviewforge.checkout.pool_size"]);

        depth = 42; // the gauge reads the live value, not a snapshot
        listener.RecordObservableInstruments();
        Assert.Equal(42, readings["reviewforge.queue.depth"]);
    }
}
