using System.Text.Json;
using Microsoft.JSInterop;

namespace ReviewForge.Service;

public enum BrowserOutcome { Ok, Accepted, Conflict, Invalid, Unauthenticated, Forbidden, RateLimited, Unavailable, NetworkError, Cancelled, NotFound }
public sealed record BrowserResult<T>(BrowserOutcome Outcome, T? Value = default);

/// <summary>Only browser-origin HTTP calls; never injects query/submission services or handles API keys.</summary>
public sealed class DashboardBrowserClient(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _Module;
    private const long MaxResponseStreamBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<BrowserResult<DashboardResponse>> GetDashboardAsync(CancellationToken ct = default)
        => SendAsync<DashboardResponse>("GET", "/reviews/dashboard", null, ct);

    public Task<BrowserResult<RunWorklistResponse>> GetRunWorklistAsync(int offset = 0, int count = 50,
        ReviewForge.Core.Domain.PrKey? pr = null, bool attentionOnly = false, CancellationToken ct = default)
    {
        if (offset is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        if (pr is not null && (string.IsNullOrWhiteSpace(pr.Org) || string.IsNullOrWhiteSpace(pr.Project)
            || string.IsNullOrWhiteSpace(pr.RepositoryId) || pr.PrId < 1)) throw new ArgumentException("Full PR scope required.", nameof(pr));
        var path = FormattableString.Invariant($"/reviews/history?offset={offset}&count={count}&attentionOnly={(attentionOnly ? "true" : "false")}");
        if (pr is not null)
            path += $"&org={Uri.EscapeDataString(pr.Org)}&project={Uri.EscapeDataString(pr.Project)}&repositoryId={Uri.EscapeDataString(pr.RepositoryId)}&prId={pr.PrId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return SendAsync<RunWorklistResponse>("GET", path, null, ct);
    }

    public Task<BrowserResult<SubmitReviewResponse>> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken ct = default)
        => SendAsync<SubmitReviewResponse>("POST", "/reviews", request, ct);

    public Task<BrowserResult<SubmitReviewResponse>> SubmitDraftAsync(SubmitReviewRequest request, CancellationToken ct = default)
        => SendAsync<SubmitReviewResponse>("POST", "/reviews/drafts", request, ct);

    public Task<BrowserResult<DraftCapabilityResponse>> GetDraftCapabilityAsync(CancellationToken ct = default)
        => SendAsync<DraftCapabilityResponse>("GET", "/reviews/drafts/capability", null, ct);

    public Task<BrowserResult<RunStatus>> GetRunStatusAsync(Guid runId, CancellationToken ct = default)
        => SendAsync<RunStatus>("GET", $"/reviews/{runId:D}", null, ct);

    public Task<BrowserResult<RunDetailsResponse>> GetRunDetailsAsync(Guid runId, CancellationToken ct = default)
        => SendAsync<RunDetailsResponse>("GET", $"/reviews/{runId:D}/details", null, ct);

    public Task<BrowserResult<FindingDecisionHistoryResponse>> GetFindingDecisionHistoryAsync(Guid runId, string findingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(findingId) || findingId.Length > 2048) throw new ArgumentException("Finding ID required.", nameof(findingId));
        return SendAsync<FindingDecisionHistoryResponse>("GET", $"/reviews/{runId:D}/decisions/history?findingId={Uri.EscapeDataString(findingId)}", null, ct);
    }

    public Task<BrowserResult<FindingDecisionResponse>> SetFindingDecisionAsync(Guid runId, SetFindingDecisionRequest request, CancellationToken ct = default)
        => SendAsync<FindingDecisionResponse>("POST", $"/reviews/{runId:D}/decisions", request, ct, readDecisionError: true);

    public Task<BrowserResult<BatchFindingDecisionResponse>> SetBatchFindingDecisionsAsync(Guid runId, BatchFindingDecisionRequest request, CancellationToken ct = default)
        => SendAsync<BatchFindingDecisionResponse>("POST", $"/reviews/{runId:D}/decisions/batch", request, ct, readDecisionError: true);

    public Task<BrowserResult<PublishSelectedDraftResult>> PublishSelectedDraftAsync(Guid runId, PublishSelectedDraftRequest request, CancellationToken ct = default)
        => SendAsync<PublishSelectedDraftResult>("POST", $"/reviews/{runId:D}/publish", request, ct, readDecisionError: true);

    public Task<BrowserResult<PublishSelectedDraftResult>> ReconcileDraftAsync(Guid runId, ReconcileDraftRequest request, CancellationToken ct = default)
        => SendAsync<PublishSelectedDraftResult>("POST", $"/reviews/{runId:D}/reconcile", request, ct, readDecisionError: true);

    private async Task<BrowserResult<T>> SendAsync<T>(string method, string path, object? body, CancellationToken ct, bool readDecisionError = false)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            _Module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, "./dashboard-http.js");
            var response = await _Module.InvokeAsync<BrowserHttpResponse>("send", ct, id, method, path, body).AsTask().WaitAsync(ct);
            var outcome = response.Status switch
            {
                200 => BrowserOutcome.Ok, 202 => BrowserOutcome.Accepted, 400 => BrowserOutcome.Invalid,
                401 => BrowserOutcome.Unauthenticated, 403 => BrowserOutcome.Forbidden, 404 => BrowserOutcome.NotFound, 409 => BrowserOutcome.Conflict,
                429 => BrowserOutcome.RateLimited, 503 => BrowserOutcome.Unavailable, _ => BrowserOutcome.NetworkError,
            };
            await using var reference = response.DataStream;
            T? value = default;
            if (outcome is BrowserOutcome.Ok or BrowserOutcome.Accepted ||
                readDecisionError && outcome is BrowserOutcome.Invalid or BrowserOutcome.Conflict or BrowserOutcome.NotFound)
            {
                if (reference is not null)
                {
                    await using var stream = await reference.OpenReadStreamAsync(MaxResponseStreamBytes, ct);
                    value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
                }
                else if (response.Data is { } data) value = data.Deserialize<T>(JsonOptions);
            }
            return new(outcome, value);
        }
        catch (OperationCanceledException)
        {
            if (_Module is not null)
            {
                try { await _Module.InvokeVoidAsync("abort", id); }
                catch (Exception ex) when (ex is JSException or JSDisconnectedException)
                { /* Disconnected browser cannot retain a usable request. */ }
            }
            return new(BrowserOutcome.Cancelled);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException) { return new(BrowserOutcome.NetworkError); }
        catch (JsonException) { return new(BrowserOutcome.NetworkError); }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException) { return new(BrowserOutcome.NetworkError); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_Module is null) return;
        try { await _Module.DisposeAsync(); }
        catch (JSDisconnectedException) { /* Circuit ended; no browser resource remains reachable. */ }
    }

    public sealed record BrowserHttpResponse(int Status, JsonElement? Data)
    {
        public IJSStreamReference? DataStream { get; init; }
    }
}
