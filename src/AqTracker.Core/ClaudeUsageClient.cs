using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AqTracker.Core;

public enum ClaudeConnectionState { Disconnected, Connected, Reconnect }

public static class ClaudeUsageParser
{
    public static RateLimitSnapshot Parse(JsonElement root, DateTimeOffset receivedAt)
    {
        var windows = new List<QuotaWindow>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            Add("five_hour", 300, "5h");
            Add("seven_day", 10080, "7d");
        }
        return new(windows, null, null, null, receivedAt);
        void Add(string name, int minutes, string label)
        {
            if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("utilization", out var usage)) return;
            var hasUsage = usage.ValueKind != JsonValueKind.Null;
            double used = 0;
            if (hasUsage && (usage.ValueKind != JsonValueKind.Number || !usage.TryGetDouble(out used) ||
                !Net48Compatibility.IsFinite(used) || used < 0 || used > 100)) return;
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("resets_at", out var node) && node.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) reset = at;
            windows.Add(new("claude:" + name, label, used, reset, minutes, HasUsage: hasUsage));
        }
    }
}

/// <summary>Own OAuth integration, serialized to prevent refresh-token rotation and disconnect races.</summary>
public sealed class ClaudeUsageClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IClaudeTokenStore _store;
    private readonly string _version, _snapshotPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeTokens? _tokens;
    private int _failures;
    private DateTimeOffset _nextAttempt;
    public ClaudeConnectionState State { get; private set; }
    public RateLimitSnapshot? Snapshot { get; private set; }
    public bool IsStale { get; private set; } = true;
    public DateTimeOffset NextAttemptAt => _nextAttempt;

    public ClaudeUsageClient(IClaudeTokenStore store, string snapshotPath, string version,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store; _snapshotPath = snapshotPath; _version = version;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _tokens = store.Load();
        State = _tokens is null ? ClaudeConnectionState.Disconnected : ClaudeConnectionState.Connected;
        try { if (File.Exists(snapshotPath)) Snapshot = JsonSerializer.Deserialize<RateLimitSnapshot>(File.ReadAllText(snapshotPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public async Task ExchangeCodeAsync(ClaudeOAuthAttempt attempt, string code, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await RequestTokensAsync(new { grant_type = "authorization_code", code, redirect_uri = attempt.RedirectUri,
                client_id = ClaudeOAuthConstants.ClientId, code_verifier = attempt.Verifier, state = attempt.State }, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _store.Save(tokens);
            _tokens = tokens;
            State = ClaudeConnectionState.Connected;
            _nextAttempt = default; _failures = 0;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Delete();
            _tokens = null; Snapshot = null; IsStale = true;
            State = ClaudeConnectionState.Disconnected;
            if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath);
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken, bool onlyIfStale = false)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (_tokens is null || _clock() < _nextAttempt || onlyIfStale && Snapshot is not null && !IsStale && _clock() - Snapshot.ReceivedAt <= TimeSpan.FromSeconds(60)) return;
            var refreshed = false;
            if (_tokens.ExpiresAt - _clock() < TimeSpan.FromMinutes(5))
            {
                if (!await RefreshTokenAsync(cancellationToken).ConfigureAwait(false)) return;
                refreshed = true;
            }
            using var first = await ReadUsageAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed && first.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            { RequireReconnect(); return; }
            if (first.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!await RefreshTokenAsync(cancellationToken).ConfigureAwait(false)) return;
                using var retry = await ReadUsageAsync(cancellationToken).ConfigureAwait(false);
                if (retry.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) { RequireReconnect(); return; }
                await ApplyUsageAsync(retry).ConfigureAwait(false);
            }
            else await ApplyUsageAsync(first).ConfigureAwait(false);
        }
        catch (TransientClaudeException) { Backoff(); }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or UnauthorizedAccessException)
        { IsStale = true; Backoff(); SanitizedLogger.Write("Claude refresh error: " + error.GetType().Name); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Backoff(); }
        finally { _gate.Release(); }
    }

    private async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await RequestTokensAsync(new { grant_type = "refresh_token", refresh_token = _tokens!.RefreshToken,
                client_id = ClaudeOAuthConstants.ClientId }, _tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
            // Persist rotation before publishing the new access token to concurrent callers.
            _store.Save(tokens); _tokens = tokens; return true;
        }
        catch (ClaudeAuthenticationException) { RequireReconnect(); return false; }
    }

    private async Task<ClaudeTokens> RequestTokensAsync(object payload, string? previousRefresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeOAuthConstants.TokenUrl)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) throw new TransientClaudeException();
        if (!response.IsSuccessStatusCode) throw new ClaudeAuthenticationException();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()) || !root.TryGetProperty("expires_in", out var expires) ||
            expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds <= 0) throw new ClaudeAuthenticationException();
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : previousRefresh;
        if (string.IsNullOrWhiteSpace(refresh)) throw new ClaudeAuthenticationException();
        return new(access.GetString()!, refresh!, _clock().AddSeconds(seconds));
    }

    private async Task<HttpResponseMessage> ReadUsageAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeOAuthConstants.UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("aq-tracker/" + _version);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
    private async Task ApplyUsageAsync(HttpResponseMessage response)
    {
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) { Backoff(); return; }
        if (!response.IsSuccessStatusCode) { IsStale = true; return; }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var snapshot = ClaudeUsageParser.Parse(document.RootElement, _clock());
        AtomicFile.Write(_snapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot));
        Snapshot = snapshot; IsStale = false; State = ClaudeConnectionState.Connected;
        _failures = 0; _nextAttempt = default;
    }
    private void Backoff() { IsStale = true; _failures = Math.Min(_failures + 1, 5); _nextAttempt = _clock().AddSeconds(Math.Min(600, 60 * Math.Pow(2, _failures - 1))); }
    private void RequireReconnect()
    {
        _tokens = null; IsStale = true; State = ClaudeConnectionState.Reconnect;
        _store.Delete();
    }
    public void Dispose() { _http.Dispose(); }
    private sealed class TransientClaudeException : Exception { }
    private sealed class ClaudeAuthenticationException : Exception { }
}
