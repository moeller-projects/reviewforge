using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
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

    [Fact]
    public async Task Stage_duration_uses_only_bounded_tags()
    {
        var recordedTags = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.stage.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                recordedTags.Add(tag);
            }
        });
        listener.Start();

        var pipeline = new ReviewPipeline([new NoOpStage()], NullLogger<ReviewPipeline>.Instance);
        await pipeline.RunAsync(
            new ReviewContext(new PrKey("org", "project", "repository", 1), DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Contains(recordedTags, tag => tag.Key == ReviewForgeTelemetry.TagStage);
        Assert.Contains(recordedTags, tag => tag.Key == ReviewForgeTelemetry.TagResult);
        Assert.DoesNotContain(recordedTags, tag => tag.Key == ReviewForgeTelemetry.TagRepoId);
    }

    [Fact]
    public async Task Requested_cancellation_is_not_recorded_as_failure()
    {
        string? result = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.stage.duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == ReviewForgeTelemetry.TagResult)
                {
                    result = tag.Value?.ToString();
                }
            }
        });
        listener.Start();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pipeline = new ReviewPipeline([new CancellingStage()], NullLogger<ReviewPipeline>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(
            new ReviewContext(new PrKey("org", "project", "repository", 1), DateTimeOffset.UtcNow),
            cts.Token));

        Assert.Equal("cancelled", result);
    }

    private sealed class CancellingStage : IReviewStage
    {
        public string Name => "cancel";
        public int Order => 10;
        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }


    private sealed class NoOpStage : IReviewStage
    {
        public string Name => "noop";
        public int Order => 10;
        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
