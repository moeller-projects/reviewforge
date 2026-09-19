using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReviewForge.Infrastructure.Codex;

/// <summary>
/// Codex OAuth file credential (~/.codex/auth.json). Returns a cached access token;
/// refreshes via the token endpoint when expiring within <see cref="RefreshSkew"/> and
/// persists rotated tokens back to the same file atomically (temp + move).
/// </summary>
public sealed class CodexCredential
{
    public const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _Clock;
    private readonly SemaphoreSlim _Gate = new(1, 1);
    private readonly HttpClient _Http;

    private readonly string _Path;

    private AuthFile? _Cached;

    public CodexCredential(string credentialPath, HttpMessageHandler? httpHandler = null, TimeProvider? clock = null)
    {
        _Path = credentialPath;
        _Http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        _Clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _Gate.WaitAsync(ct);
        try
        {
            _Cached ??= Load(_Path);

            if (!ExpiringSoon(_Cached.Tokens.AccessToken, _Clock))
            {
                return _Cached.Tokens.AccessToken;
            }

            var refreshed = await RefreshAsync(_Cached.Tokens.RefreshToken, _Cached.Tokens.AccountId, ct);
            _Cached = refreshed;
            PersistAtomic(_Path, refreshed);
            return refreshed.Tokens.AccessToken;
        }
        finally
        {
            _Gate.Release();
        }
    }

    public async Task<string?> GetAccountIdAsync(CancellationToken ct)
    {
        await _Gate.WaitAsync(ct);
        try
        {
            _Cached ??= Load(_Path);
            return _Cached.Tokens.AccountId;
        }
        finally
        {
            _Gate.Release();
        }
    }

    /// <summary>Forces a refresh regardless of expiry (used after a 401).</summary>
    public async Task<string> ForceRefreshAsync(CancellationToken ct)
    {
        await _Gate.WaitAsync(ct);
        try
        {
            _Cached ??= Load(_Path);
            var refreshed = await RefreshAsync(
                _Cached.Tokens.RefreshToken,
                _Cached.Tokens.AccountId,
                ct);
            _Cached = refreshed;
            PersistAtomic(_Path, refreshed);
            return refreshed.Tokens.AccessToken;
        }
        finally
        {
            _Gate.Release();
        }
    }

    private async Task<AuthFile> RefreshAsync(string refreshToken, string? accountId, CancellationToken ct)
    {
        using var response = await HttpClientJsonExtensions.PostAsJsonAsync(_Http, TokenEndpoint, new
        {
            client_id = ClientId,
            grant_type = "refresh_token",
            refresh_token = refreshToken,
        }, ct);

        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenSet>(ct)
                     ?? throw new InvalidOperationException("empty token refresh response");
        var preservedTokens = new TokenSet
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            AccountId = tokens.AccountId ?? accountId,
        };

        return new AuthFile(preservedTokens, _Clock.GetUtcNow().ToString("O"));
    }

    internal static AuthFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AuthFile>(json)
               ?? throw new InvalidOperationException($"invalid credential file: {path}");
    }

    internal static void PersistAtomic(string path, AuthFile auth)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(auth, new JsonSerializerOptions {WriteIndented = true}));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>True when the JWT expires within the refresh skew (or is unreadable — refresh then).</summary>
    internal static bool ExpiringSoon(string accessToken, TimeProvider clock)
    {
        var exp = TryReadJwtExpiry(accessToken);
        return exp is null || exp.Value - clock.GetUtcNow() < RefreshSkew;
    }

    internal static DateTimeOffset? TryReadJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64())
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- file format + helpers (internal for tests) ----

    internal sealed class TokenSet
    {
        [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public required string RefreshToken { get; init; }
        [JsonPropertyName("account_id")] public string? AccountId { get; init; }
    }

    internal sealed record AuthFile(
        [property: JsonPropertyName("tokens")] TokenSet Tokens,
        [property: JsonPropertyName("last_refresh")]
        string? LastRefresh);
}