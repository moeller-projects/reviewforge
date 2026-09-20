using System.Net;
using System.Net.Http.Headers;

namespace ReviewForge.Infrastructure.Codex;

/// <summary>
/// Attaches Codex auth headers per request; on 401 force-refreshes once and retries once.
/// </summary>
public sealed class CodexAuthHandler(CodexCredential credential) : DelegatingHandler
{
    public const string BetaHeader = "OpenAI-Beta";
    public const string BetaValue = "responses=experimental";
    public const string AccountHeader = "chatgpt-account-id";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await ApplyHeadersAsync(request, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await ApplyHeadersAsync(request, forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var retryResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return retryResponse;
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage request, bool forceRefresh, CancellationToken ct)
    {
        var token = forceRefresh
            ? await credential.ForceRefreshAsync(ct).ConfigureAwait(false)
            : await credential.GetTokenAsync(ct).ConfigureAwait(false);
        var accountId = await credential.GetAccountIdAsync(ct).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove(BetaHeader);
        request.Headers.TryAddWithoutValidation(BetaHeader, BetaValue);
        request.Headers.Remove(AccountHeader);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation(AccountHeader, accountId);
        }
    }
}