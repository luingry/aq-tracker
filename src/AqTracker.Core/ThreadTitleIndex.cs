using Microsoft.Data.Sqlite;

namespace AqTracker.Core;

/// <summary>Small read-only cache over the local Codex thread store. It avoids one app-server request per chat.</summary>
internal sealed class ThreadTitleIndex
{
    private readonly string _databasePath;
    private DatabaseSignature? _signature;
    private IReadOnlyDictionary<string, string> _titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ThreadTitleIndex(string databasePath) => _databasePath = databasePath;

    public IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_databasePath)) return _titles;
            var signature = DatabaseSignature.Create(_databasePath);
            if (_signature == signature) return _titles;
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 1, Pooling = false };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandTimeout = 1;
                schemaCommand.CommandText = "PRAGMA table_info(threads)";
                using var schema = schemaCommand.ExecuteReader();
                while (schema.Read()) columns.Add(schema.GetString(1));
            }
            var hasName = columns.Contains("name");
            var hasTitle = columns.Contains("title");
            if (!hasName && !hasTitle) return _titles;
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = hasName && hasTitle
                ? "SELECT id, COALESCE(NULLIF(trim(name), ''), NULLIF(trim(title), '')) FROM threads WHERE COALESCE(NULLIF(trim(name), ''), NULLIF(trim(title), '')) IS NOT NULL"
                : hasName
                    ? "SELECT id, name FROM threads WHERE name IS NOT NULL AND trim(name) <> ''"
                    : "SELECT id, title FROM threads WHERE title IS NOT NULL AND trim(title) <> ''";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title)) titles[id] = title;
            }
            _signature = signature;
            _titles = titles;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            SanitizedLogger.Write("Thread title index skipped: " + ex.GetType().Name);
        }
        return _titles;
    }

    private readonly record struct FilePartSignature(bool Exists, long Length, long LastWriteUtcTicks)
    {
        public static FilePartSignature Create(string path)
        {
            if (!File.Exists(path)) return new(false, 0, 0);
            var info = new FileInfo(path);
            return new(true, info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    private readonly record struct DatabaseSignature(FilePartSignature Main, FilePartSignature Wal)
    {
        public static DatabaseSignature Create(string path) => new(FilePartSignature.Create(path), FilePartSignature.Create(path + "-wal"));
    }
}
