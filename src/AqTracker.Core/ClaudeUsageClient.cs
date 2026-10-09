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
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _refreshDeadline;
    private HttpClient _http;
    private readonly IClaudeTokenStore _store;
    private readonly string _version, _snapshotPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeTokens? _tokens;
    private const int UnreadableLoadsBeforeReconnect = 3, NetworkFailuresBeforeNewConnection = 2;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1), RepeatFailureLogInterval = TimeSpan.FromMinutes(30), SlowRequest = TimeSpan.FromSeconds(10);
    // .NET Framework tries each resolved address in turn: when IPv6 is advertised but unreachable, the IPv4 fallback
    // only starts after the ~21 s TCP connect timeout, so the request timeout must leave room for it.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private DateTimeOffset _lastSlowLoggedAt;
    /// <summary>A snapshot older than this is reported as stale even when no poll reported a failure.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(5);
    private int _failures, _unreadableLoads, _networkFailures, _failureCount;
    private bool _credentialsUnreadable, _stale = true, _stuckReported;
    private DateTimeOffset _nextAttempt, _inFlightSince, _lastFailureLoggedAt;
    private string? _lastFailure;
    public ClaudeConnectionState State { get; private set; }
    public RateLimitSnapshot? Snapshot { get; private set; }
    // Age-based so a poll that silently stops succeeding can never keep old numbers looking current.
    public bool IsStale => _stale || Snapshot is null || _clock() - Snapshot.ReceivedAt > MaxSnapshotAge;
    public DateTimeOffset NextAttemptAt => _nextAttempt;

    public ClaudeUsageClient(IClaudeTokenStore store, string snapshotPath, string version,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null, TimeSpan? refreshDeadline = null)
    {
        _store = store; _snapshotPath = snapshotPath; _version = version;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _handler = handler;
        // Above three sequential requests (usage, token rotation, retry): it only fires when a request ignores its own timeout.
        _refreshDeadline = refreshDeadline ?? RequestTimeout + RequestTimeout + RequestTimeout + TimeSpan.FromSeconds(15);
        _http = CreateHttpClient();
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
            _nextAttempt = default; _failures = 0; _unreadableLoads = 0; _credentialsUnreadable = false;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Delete();
            _tokens = null; Snapshot = null; _stale = true;
            _unreadableLoads = 0; _credentialsUnreadable = false;
            State = ClaudeConnectionState.Disconnected;
            if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath);
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken, bool onlyIfStale = false)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { ReportIfStuck(); return; }
        // Hard deadline for the whole refresh (token rotation, usage read and body), so a request that never
        // completes cannot hold the gate and silently freeze every later poll.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_refreshDeadline);
        var token = deadline.Token;
        _inFlightSince = _clock(); _stuckReported = false;
        try
        {
            if (_clock() < _nextAttempt) return;
            // A sharing/DPAPI failure during sign-in must not permanently disconnect
            // a client whose credentials are still safely persisted on disk.
            if (_tokens is null && (State == ClaudeConnectionState.Disconnected || _credentialsUnreadable))
            {
                _tokens = _store.Load();
                if (_tokens is null)
                {
                    NoteUnreadableCredentials();
                    _nextAttempt = _clock().AddSeconds(60);
                    return;
                }
                _unreadableLoads = 0; _credentialsUnreadable = false;
                State = ClaudeConnectionState.Connected;
                SanitizedLogger.Write("Claude credentials recovered after initial load was unavailable.");
            }
            if (_tokens is null || onlyIfStale && Snapshot is not null && !IsStale && _clock() - Snapshot.ReceivedAt <= TimeSpan.FromSeconds(60)) return;
            var refreshed = false;
            if (_tokens.ExpiresAt - _clock() < TimeSpan.FromMinutes(5))
            {
                if (!await RefreshTokenAsync(token).ConfigureAwait(false)) return;
                refreshed = true;
            }
            using var first = await ReadUsageAsync(token).ConfigureAwait(false);
            if (refreshed && first.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            { RequireReconnect(); return; }
            if (first.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (!await RefreshTokenAsync(token).ConfigureAwait(false)) return;
                using var retry = await ReadUsageAsync(token).ConfigureAwait(false);
                if (retry.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) { RequireReconnect(); return; }
                await ApplyUsageAsync(retry).ConfigureAwait(false);
            }
            else await ApplyUsageAsync(first).ConfigureAwait(false);
        }
        catch (TransientClaudeException error) { Backoff(error.RetryAfter); Fail("token endpoint HTTP " + error.Status); }
        catch (HttpRequestException error) { Backoff(); Fail("network " + Describe(error)); NoteNetworkFailure(); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { Backoff(); Fail(error.GetType().Name); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { Backoff(); Fail("timeout"); NoteNetworkFailure(); }
        finally { _inFlightSince = default; _gate.Release(); }
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
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) throw new TransientClaudeException((int)response.StatusCode, RetryAfter(response));
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
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (elapsed.Elapsed >= SlowRequest && DateTimeOffset.UtcNow - _lastSlowLoggedAt >= RepeatFailureLogInterval)
        {
            _lastSlowLoggedAt = DateTimeOffset.UtcNow;
            SanitizedLogger.Write("Claude usage request took " + (int)elapsed.Elapsed.TotalSeconds + "s (slow connection setup, e.g. unreachable IPv6 before IPv4 fallback).");
        }
        return response;
    }
    private async Task ApplyUsageAsync(HttpResponseMessage response)
    {
        // Every unsuccessful answer backs off and is logged: polling a 429 every minute only prolongs the limit,
        // and an unlogged failure is how the quota froze without a trace.
        if (!response.IsSuccessStatusCode)
        {
            Backoff((int)response.StatusCode == 429 ? RetryAfter(response) : null);
            Fail("usage HTTP " + (int)response.StatusCode);
            return;
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var snapshot = ClaudeUsageParser.Parse(document.RootElement, _clock());
        AtomicFile.Write(_snapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot));
        Snapshot = snapshot; _stale = false; State = ClaudeConnectionState.Connected;
        _failures = 0; _nextAttempt = default; _networkFailures = 0;
        if (_lastFailure is not null) SanitizedLogger.Write("Claude usage recovered after " + _failureCount + " failed attempt(s).");
        _lastFailure = null; _failureCount = 0;
    }
    private void Fail(string reason)
    {
        _failureCount++;
        var now = _clock();
        if (reason == _lastFailure && now - _lastFailureLoggedAt < RepeatFailureLogInterval) return;
        _lastFailure = reason; _lastFailureLoggedAt = now;
        SanitizedLogger.Write("Claude usage refresh failed (" + reason + "); attempt " + _failureCount + ", next retry " +
            _nextAttempt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ".");
    }
    private void ReportIfStuck()
    {
        if (_inFlightSince == default || _stuckReported || _clock() - _inFlightSince < _refreshDeadline + _refreshDeadline) return;
        _stuckReported = true;
        SanitizedLogger.Write("Claude refresh still running since " + _inFlightSince.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "; polls are being skipped.");
    }
    // Repeated network failures can come from a pooled connection broken by sleep or a network change; start a fresh pool.
    private void NoteNetworkFailure()
    {
        if (++_networkFailures < NetworkFailuresBeforeNewConnection || _handler is not null) return;
        _networkFailures = 0;
        var previous = _http;
        _http = CreateHttpClient();
        previous.Dispose();
        SanitizedLogger.Write("Claude HTTP connection recreated after repeated network failures.");
    }
    private HttpClient CreateHttpClient()
    {
        var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = RequestTimeout;
        return client;
    }
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - _clock() : null);
        return delay is { } value && value > TimeSpan.Zero ? (value > MaxRetryAfter ? MaxRetryAfter : value) : null;
    }
    private static string Describe(HttpRequestException error) =>
        error.InnerException is WebException web ? "WebException " + web.Status : error.InnerException?.GetType().Name ?? nameof(HttpRequestException);
    /// <summary>
    /// Credentials present on disk that keep failing to load are not a transient glitch: report "reconnect required"
    /// instead of silently showing an ever older snapshot as if the account were fine. The file is left untouched and
    /// keeps being retried, so a later successful read returns to Connected without another sign-in.
    /// </summary>
    private void NoteUnreadableCredentials()
    {
        if (!_store.HasStoredCredentials()) { _unreadableLoads = 0; return; }
        if (++_unreadableLoads < UnreadableLoadsBeforeReconnect || _credentialsUnreadable) return;
        _credentialsUnreadable = true; _stale = true; State = ClaudeConnectionState.Reconnect;
        SanitizedLogger.Write("Claude credentials exist but cannot be read after " + _unreadableLoads + " attempts; reconnect required.");
    }
    private void Backoff(TimeSpan? retryAfter = null)
    {
        _stale = true; _failures = Math.Min(_failures + 1, 5);
        var delay = TimeSpan.FromSeconds(Math.Min(600, 60 * Math.Pow(2, _failures - 1)));
        _nextAttempt = _clock() + (retryAfter > delay ? retryAfter.Value : delay);
    }
    private void RequireReconnect()
    {
        SanitizedLogger.Write("Claude reconnect required: authentication rejected.");
        _tokens = null; _stale = true; State = ClaudeConnectionState.Reconnect;
        _store.Delete();
    }
    public void Dispose() { _http.Dispose(); }
    private sealed class TransientClaudeException(int status, TimeSpan? retryAfter) : Exception
    {
        public int Status { get; } = status;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
    private sealed class ClaudeAuthenticationException : Exception { }
}
