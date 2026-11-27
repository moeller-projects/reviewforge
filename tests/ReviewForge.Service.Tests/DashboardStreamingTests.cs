using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.JSInterop;
using ReviewForge.Core.Domain;
using Xunit;
using TestContext = Xunit.TestContext;

namespace ReviewForge.Service.Tests;

public sealed class DashboardStreamingTests
{
    [Fact]
    public async Task Large_run_details_are_deserialized_from_a_bounded_stream_and_resources_are_released()
    {
        var id = Guid.NewGuid();
        var finding = new FindingPayloadDto("rule", "Large finding", "high", "bug", new string('x', 64 * 1024), null, null, null, false);
        var details = new RunDetailsResponse(new(id, new("o", "p", "r", 1), RunState.Completed, null,
            DateTimeOffset.UtcNow, RunKind.ReviewDraft), "head", true, null,
            [new("finding", finding, finding, null, false, null, 0, false, null, true, true)]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(bytes.Length > 32 * 1024);
        await using var context = new BunitContext();
        var payload = new TestStreamReference(bytes);
        context.JSInterop.SetupModule("./dashboard-http.js")
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, null) { DataStream = payload });
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);

        var result = await client.GetRunDetailsAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(BrowserOutcome.Ok, result.Outcome);
        Assert.Equal(finding.Description, Assert.Single(result.Value!.Findings).Candidate!.Description);
        Assert.True(payload.Disposed);
        Assert.False(payload.Stream.CanRead);
        Assert.Equal(TestContext.Current.CancellationToken, payload.ReadToken);
        Assert.InRange(payload.ReadLimit, bytes.Length, 16 * 1024 * 1024);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("stream-error")]
    [InlineData("too-large")]
    public async Task Unreadable_stream_returns_a_transport_error_instead_of_faulting_the_refresh_loop(string failure)
    {
        await using var context = new BunitContext();
        var payload = new TestStreamReference(Encoding.UTF8.GetBytes("not-json"))
        {
            ThrowOnOpen = failure == "stream-error" ? new IOException("transport disconnected") : null,
            ReportedLength = failure == "too-large" ? long.MaxValue : null,
        };
        context.JSInterop.SetupModule("./dashboard-http.js")
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, null) { DataStream = payload });
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);

        var result = await client.GetRunDetailsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(BrowserOutcome.NetworkError, result.Outcome);
        Assert.Null(result.Value);
        Assert.True(payload.Disposed);
    }

    [Fact]
    public async Task Authentication_error_does_not_read_a_stream_but_still_releases_its_reference()
    {
        await using var context = new BunitContext();
        var payload = new TestStreamReference([]);
        context.JSInterop.SetupModule("./dashboard-http.js")
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(401, null) { DataStream = payload });
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);

        Assert.Equal(BrowserOutcome.Unauthenticated,
            (await client.GetRunDetailsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).Outcome);
        Assert.False(payload.Opened);
        Assert.True(payload.Disposed);
    }

    [Fact]
    public async Task Interrupted_stream_releases_reference_and_aborts_the_browser_request()
    {
        await using var context = new BunitContext();
        var payload = new TestStreamReference([]) { ThrowOnOpen = new OperationCanceledException() };
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, null) { DataStream = payload });
        module.SetupVoid("abort", _ => true).SetVoidResult();
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);

        var result = await client.GetRunDetailsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(BrowserOutcome.Cancelled, result.Outcome);
        Assert.True(payload.Disposed);
        Assert.Single(module.Invocations["abort"]);
    }

    // Single-boundary interop fixture: keeping this SDK-specific double here avoids adding
    // ASP.NET/JSInterop dependencies to the shared, Core-only testing layer.
    private sealed class TestStreamReference(byte[] bytes) : IJSStreamReference
    {
        public MemoryStream Stream { get; } = new(bytes, writable: false);
        public long? ReportedLength { get; init; }
        public long Length => ReportedLength ?? bytes.Length;
        public Exception? ThrowOnOpen { get; init; }
        public bool Disposed { get; private set; }
        public bool Opened { get; private set; }
        public long ReadLimit { get; private set; }
        public CancellationToken ReadToken { get; private set; }

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened = true;
            ReadLimit = maxAllowedSize;
            ReadToken = cancellationToken;
            if (ThrowOnOpen is { } error) throw error;
            if (Length > maxAllowedSize) throw new ArgumentOutOfRangeException(nameof(maxAllowedSize));
            return ValueTask.FromResult<Stream>(Stream);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
