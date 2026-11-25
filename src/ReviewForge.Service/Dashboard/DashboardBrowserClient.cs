using System.Text.Json;
using Microsoft.JSInterop;

namespace ReviewForge.Service;

public enum BrowserOutcome { Ok, Accepted, Conflict, Invalid, Unauthenticated, Forbidden, RateLimited, Unavailable, NetworkError, Cancelled }
public sealed record BrowserResult<T>(BrowserOutcome Outcome, T? Value = default);

/// <summary>Only browser-origin HTTP calls; never injects query/submission services or handles API keys.</summary>
public sealed class DashboardBrowserClient(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _Module;

    public Task<BrowserResult<DashboardResponse>> GetDashboardAsync(CancellationToken ct = default)
        => SendAsync<DashboardResponse>("GET", "/reviews/dashboard", null, ct);

    public Task<BrowserResult<SubmitReviewResponse>> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken ct = default)
        => SendAsync<SubmitReviewResponse>("POST", "/reviews", request, ct);

    private async Task<BrowserResult<T>> SendAsync<T>(string method, string path, object? body, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            _Module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, "./dashboard-http.js");
            var response = await _Module.InvokeAsync<BrowserHttpResponse>("send", ct, id, method, path, body).AsTask().WaitAsync(ct);
            var outcome = response.Status switch
            {
                200 => BrowserOutcome.Ok, 202 => BrowserOutcome.Accepted, 400 => BrowserOutcome.Invalid,
                401 => BrowserOutcome.Unauthenticated, 403 => BrowserOutcome.Forbidden, 409 => BrowserOutcome.Conflict,
                429 => BrowserOutcome.RateLimited, 503 => BrowserOutcome.Unavailable, _ => BrowserOutcome.NetworkError,
            };
            return new(outcome, response.Data is { } data && outcome is BrowserOutcome.Ok or BrowserOutcome.Accepted
                ? data.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) : default);
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_Module is null) return;
        try { await _Module.DisposeAsync(); }
        catch (JSDisconnectedException) { /* Circuit ended; no browser resource remains reachable. */ }
    }

    public sealed record BrowserHttpResponse(int Status, JsonElement? Data);
}
