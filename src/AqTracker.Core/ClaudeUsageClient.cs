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
    /// <summary>Fastest cadence of the usage poll (and the app's timer tick).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    /// <summary>While Claude is idle the quota barely moves: polling slower lets the endpoint's request budget refill for active periods.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);
    // The usage endpoint has a request budget (bursts of ~10, refilled roughly once a minute) and answers 429 without
    // Retry-After once it is spent. A fixed fast poll would turn most requests into 429s, and the old exponential backoff
    // (up to 10 min per attempt) froze the quota for half an hour. Instead the pace adapts: each 429 slows it by one tick
    // (up to MaxBackoff) and every few successes speed it up again, so it settles at the fastest rate the server accepts.
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2), MaxRetryAfter = TimeSpan.FromMinutes(15);
    private const int SuccessesBeforeSpeedUp = 3;
    private TimeSpan _pace = PollInterval;
    private int _successStreak;
    // Timer ticks land a few ms before or after the scheduled retry; without slack a retry due "now" waits a whole extra tick.
    private static readonly TimeSpan TickSlack = TimeSpan.FromSeconds(2), PersistInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepeatFailureLogInterval = TimeSpan.FromMinutes(30), SlowRequest = TimeSpan.FromSeconds(10);
    // .NET Framework tries each resolved address in turn: when IPv6 is advertised but unreachable, the IPv4 fallback
    // only starts after the ~21 s TCP connect timeout, so the request timeout must leave room for it.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private DateTimeOffset _lastSlowLoggedAt;
    /// <summary>A snapshot older than this is reported as stale even when no poll reported a failure.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(3);
    private int _failures, _unreadableLoads, _networkFailures, _failureCount;
    private bool _credentialsUnreadable, _stale = true, _stuckReported, _skipIPv6 = true;
    private DateTimeOffset _nextAttempt, _inFlightSince, _lastFailureLoggedAt, _persistedAt;
    private string? _lastFailure;
    public ClaudeConnectionState State { get; private set; }
    public RateLimitSnapshot? Snapshot { get; private set; }
    // Age-based so a poll that silently stops succeeding can never keep old numbers looking current.
    public bool IsStale => _stale || Snapshot is null || _clock() - Snapshot.ReceivedAt > MaxSnapshotAge;
    public DateTimeOffset NextAttemptAt => _nextAttempt;
    /// <summary>Current interval between usage polls, adapted to the endpoint's rate limit.</summary>
    public TimeSpan Pace => _pace;

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

    /// <param name="idle">Claude has no active, foreground or unread work: a reading younger than <see cref="IdleInterval"/> is kept.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken, bool onlyIfStale = false, bool idle = false)
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
            if (_clock() + TickSlack < _nextAttempt) return;
            // A sharing/DPAPI failure during sign-in must not permanently disconnect
            // a client whose credentials are still safely persisted on disk.
            if (_tokens is null && (State == ClaudeConnectionState.Disconnected || _credentialsUnreadable))
            {
                _tokens = _store.Load();
                if (_tokens is null)
                {
                    NoteUnreadableCredentials();
                    _nextAttempt = _clock() + MaxBackoff;
                    return;
                }
                _unreadableLoads = 0; _credentialsUnreadable = false;
                State = ClaudeConnectionState.Connected;
                SanitizedLogger.Write("Claude credentials recovered after initial load was unavailable.");
            }
            if (_tokens is null || Snapshot is not null && !IsStale &&
                (onlyIfStale && _clock() - Snapshot.ReceivedAt <= PollInterval || idle && _clock() - Snapshot.ReceivedAt + TickSlack < IdleInterval)) return;
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
        PrepareConnection(request);
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
        PrepareConnection(request);
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
        if ((int)response.StatusCode == 429)
        {
            _pace = _pace + PollInterval < MaxBackoff ? _pace + PollInterval : MaxBackoff;
            _successStreak = 0;
            var retryAfter = RetryAfter(response);
            _nextAttempt = _clock() + (retryAfter > _pace ? retryAfter.Value : _pace);
            Fail("usage HTTP 429", quietFirst: true);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            Backoff();
            Fail("usage HTTP " + (int)response.StatusCode);
            return;
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var snapshot = ClaudeUsageParser.Parse(document.RootElement, _clock());
        // The cache only seeds the next startup: at a 20 s cadence, skip the flushed disk write while nothing changed.
        if (Snapshot is null || !Snapshot.Windows.SequenceEqual(snapshot.Windows) || snapshot.ReceivedAt - _persistedAt >= PersistInterval)
        {
            AtomicFile.Write(_snapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot));
            _persistedAt = snapshot.ReceivedAt;
        }
        Snapshot = snapshot; _stale = false; State = ClaudeConnectionState.Connected;
        _failures = 0; _networkFailures = 0;
        if (++_successStreak >= SuccessesBeforeSpeedUp && _pace > PollInterval)
        {
            _pace -= PollInterval; _successStreak = 0;
            if (_pace == PollInterval) SanitizedLogger.Write("Claude usage back to the " + (int)PollInterval.TotalSeconds + "s poll.");
        }
        _nextAttempt = _clock() + _pace;
        // A lone 429 is routine while the pace settles; only a longer outage earns a recovery line.
        if (_lastFailure is not null && _failureCount > 1)
            SanitizedLogger.Write("Claude usage recovered after " + _failureCount + " failed attempt(s); pace " + (int)_pace.TotalSeconds + "s.");
        _lastFailure = null; _failureCount = 0;
    }
    private void Fail(string reason, bool quietFirst = false)
    {
        _failureCount++;
        if (quietFirst && _failureCount == 1) { _lastFailure = reason; _lastFailureLoggedAt = default; return; }
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
        if (_handler is not null) return;
        // Whichever address family is preferred just failed: try the other one on the next attempt.
        _skipIPv6 = !_skipIPv6;
        SanitizedLogger.Write(_skipIPv6 ? "Claude connections now skip IPv6." : "Claude connections now allow IPv6 (IPv4 failed).");
        if (++_networkFailures < NetworkFailuresBeforeNewConnection) return;
        _networkFailures = 0;
        var previous = _http;
        _http = CreateHttpClient();
        previous.Dispose();
        SanitizedLogger.Write("Claude HTTP connection recreated after repeated network failures.");
    }
    private void PrepareConnection(HttpRequestMessage request)
    {
        if (_handler is null && request.RequestUri is { } uri) IPv4FirstConnections.Apply(uri, _skipIPv6);
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
    // A transient failure does not mark the quota stale by itself: the reading keeps its value until it ages past
    // MaxSnapshotAge, so one sporadic 429 between two good polls does not flash "outdated".
    private void Backoff(TimeSpan? retryAfter = null)
    {
        _failures = Math.Min(_failures + 1, 4);
        var delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, PollInterval.Ticks << (_failures - 1)));
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
