using Microsoft.Data.Sqlite;

namespace AqTracker.Core;

internal sealed class ThreadModelIndex
{
    private readonly string _databasePath;
    private DatabaseSignature? _signature;
    private IReadOnlyDictionary<string, string> _models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ThreadModelIndex(string databasePath) => _databasePath = databasePath;

    public IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_databasePath)) return _signature is null ? Empty() : _models;
            var signature = DatabaseSignature.Create(_databasePath);
            if (_signature == signature) return _models;

            var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 1, Pooling = false };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "SELECT id, model FROM threads WHERE model IS NOT NULL AND trim(model) <> ''";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var model = reader.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(model)) models[id] = model;
            }
            _signature = signature;
            _models = models;
            return _models;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            SanitizedLogger.Write("Thread model index skipped: " + ex.GetType().Name);
            return _models;
        }
    }

    private IReadOnlyDictionary<string, string> Empty()
    {
        _signature = null;
        _models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return _models;
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
