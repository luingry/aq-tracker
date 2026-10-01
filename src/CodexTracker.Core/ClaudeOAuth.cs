using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CodexTracker.Core;

public static class ClaudeOAuthConstants
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ManualRedirect = "https://platform.claude.com/oauth/code/callback";
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string Scopes = "user:profile user:inference";
}

public sealed class ClaudeOAuthAttempt
{
    public string Verifier { get; }
    public string State { get; }
    public string RedirectUri { get; }
    public string Challenge { get; }
    public Uri AuthorizeUri { get; }
    public ClaudeOAuthAttempt(string redirectUri)
    {
        RedirectUri = redirectUri;
        Verifier = RandomValue();
        State = RandomValue();
        using var sha = SHA256.Create();
        Challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(Verifier)));
        var parameters = new Dictionary<string, string>
        {
            ["code"] = "true", ["client_id"] = ClaudeOAuthConstants.ClientId, ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri, ["scope"] = ClaudeOAuthConstants.Scopes,
            ["code_challenge"] = Challenge, ["code_challenge_method"] = "S256", ["state"] = State
        };
        AuthorizeUri = new Uri(ClaudeOAuthConstants.AuthorizeUrl + "?" + string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }
    public bool TryParseManualCode(string? value, out string code)
    {
        code = "";
        if (value is null) return false;
        var parts = value.Trim().Split('#');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !MatchesState(parts[1])) return false;
        code = parts[0];
        return true;
    }
    public bool MatchesState(string? state)
    {
        if (state is null || state.Length != State.Length) return false;
        var difference = 0;
        for (var i = 0; i < state.Length; i++) difference |= state[i] ^ State[i];
        return difference == 0;
    }
    public bool TryValidateCallback(string? method, string? path, string? code, string? state, out string acceptedCode)
    {
        acceptedCode = "";
        if (method != "GET" || path != "/callback" || string.IsNullOrWhiteSpace(code) || !MatchesState(state)) return false;
        acceptedCode = code!;
        return true;
    }
    private static string RandomValue() { var bytes = new byte[32]; using var random = RandomNumberGenerator.Create(); random.GetBytes(bytes); return Base64Url(bytes); }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The listener is opened before launching the browser. All paths release the port.</summary>
public sealed class ClaudeOAuthCallback : IDisposable
{
    private readonly HttpListener _listener = new();
    public ClaudeOAuthAttempt Attempt { get; }
    public ClaudeOAuthCallback()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        Attempt = new ClaudeOAuthAttempt($"http://localhost:{port}/callback");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        try { _listener.Start(); } catch { _listener.Close(); throw; }
    }
    public async Task<string> WaitForCodeAsync(string browserMessage, string invalidMessage, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var registration = timeout.Token.Register(() => _listener.Stop());
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                var valid = Attempt.TryValidateCallback(context.Request.HttpMethod, context.Request.Url?.AbsolutePath,
                    context.Request.QueryString["code"], context.Request.QueryString["state"], out var code);
                context.Response.StatusCode = valid ? 200 : 400;
                context.Response.ContentType = "text/html; charset=utf-8";
                var html = "<!doctype html><meta charset=\"utf-8\"><p>" + WebUtility.HtmlEncode(valid ? browserMessage : invalidMessage) + "</p>";
                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentLength64 = bytes.Length;
                using (var output = context.Response.OutputStream) await output.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                if (valid) return code!;
            }
        }
        catch (Exception error) when (timeout.IsCancellationRequested && (error is HttpListenerException or ObjectDisposedException or InvalidOperationException))
        { throw new OperationCanceledException(timeout.Token); }
    }
    public void Dispose() => _listener.Close();
}
