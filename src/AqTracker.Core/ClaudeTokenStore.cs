using System.Security.Cryptography;
using System.Text.Json;

namespace AqTracker.Core;

public sealed record ClaudeTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public interface IClaudeTokenStore
{
    ClaudeTokens? Load();
    void Save(ClaudeTokens tokens);
    void Delete();
}

public sealed class ClaudeTokenStore : IClaudeTokenStore
{
    private readonly string _path;
    public ClaudeTokenStore(string path) => _path = path;
    public ClaudeTokens? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            try
            {
                var tokens = JsonSerializer.Deserialize<ClaudeTokens>(plain);
                return tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken) && !string.IsNullOrWhiteSpace(tokens.RefreshToken) ? tokens : null;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            SanitizedLogger.Write("Claude token load failed: " + error.GetType().Name);
            return null;
        }
    }
    public void Save(ClaudeTokens tokens)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try { AtomicFile.Write(_path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }
    public void Delete() { if (File.Exists(_path)) File.Delete(_path); }
}

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
