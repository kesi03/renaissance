using Microsoft.Data.Sqlite;
using Renaissance.Core.Models;

namespace Renaissance.PluginDatabase;

/// <summary>SQLite storage for plugins, scan locations, plugin metadata and crash history.</summary>
public sealed class RenaissanceDatabase : ICrashLog
{
    private readonly string _connectionString;

    public RenaissanceDatabase(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Plugins (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Vendor TEXT NOT NULL DEFAULT '',
                Version TEXT NOT NULL DEFAULT '',
                Type TEXT NOT NULL DEFAULT 'Effects',
                Architecture TEXT NOT NULL,
                HostType TEXT NOT NULL,
                Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                Status TEXT NOT NULL,
                Details TEXT NOT NULL DEFAULT '',
                UniqueId INTEGER NOT NULL DEFAULT 0,
                NumParameters INTEGER NOT NULL DEFAULT 0,
                NumPrograms INTEGER NOT NULL DEFAULT 0,
                NumInputs INTEGER NOT NULL DEFAULT 0,
                NumOutputs INTEGER NOT NULL DEFAULT 0,
                HasEditor INTEGER NOT NULL DEFAULT 0,
                IsSynth INTEGER NOT NULL DEFAULT 0,
                FileSize INTEGER NOT NULL DEFAULT 0,
                LastWriteUtc TEXT NOT NULL DEFAULT '',
                LastScanUtc TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS ScanLocations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS PluginMetadata (
                PluginId INTEGER PRIMARY KEY REFERENCES Plugins(Id) ON DELETE CASCADE,
                IconPath TEXT,
                Favourite INTEGER NOT NULL DEFAULT 0,
                Tags TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS CrashHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PluginPath TEXT NOT NULL,
                PluginName TEXT NOT NULL,
                TimeUtc TEXT NOT NULL,
                Message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CrashHistory_Path ON CrashHistory(PluginPath);
            """);
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- plugins

    private const string SelectPlugins = """
        SELECT p.Id, p.Name, p.Vendor, p.Version, p.Type, p.Architecture, p.HostType, p.Path, p.Status, p.Details,
               p.UniqueId, p.NumParameters, p.NumPrograms, p.NumInputs, p.NumOutputs, p.HasEditor, p.IsSynth,
               p.FileSize, p.LastWriteUtc, p.LastScanUtc, m.IconPath, COALESCE(m.Favourite, 0), COALESCE(m.Tags, '')
        FROM Plugins p LEFT JOIN PluginMetadata m ON m.PluginId = p.Id
        """;

    public List<PluginInfo> GetPlugins()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectPlugins + " ORDER BY p.Name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<PluginInfo>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public PluginInfo? GetPluginByPath(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectPlugins + " WHERE p.Path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private static PluginInfo Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Vendor = r.GetString(2),
        Version = r.GetString(3),
        Type = r.GetString(4),
        Architecture = Enum.Parse<PluginArchitecture>(r.GetString(5)),
        HostType = Enum.Parse<HostType>(r.GetString(6)),
        Path = r.GetString(7),
        Status = Enum.Parse<PluginStatus>(r.GetString(8)),
        Details = r.GetString(9),
        UniqueId = r.GetInt32(10),
        NumParameters = r.GetInt32(11),
        NumPrograms = r.GetInt32(12),
        NumInputs = r.GetInt32(13),
        NumOutputs = r.GetInt32(14),
        HasEditor = r.GetInt32(15) != 0,
        IsSynth = r.GetInt32(16) != 0,
        FileSize = r.GetInt64(17),
        LastWriteUtc = ParseDate(r.GetString(18)),
        LastScanUtc = ParseDate(r.GetString(19)),
        IconPath = r.IsDBNull(20) ? null : r.GetString(20),
        Favourite = r.GetInt32(21) != 0,
        Tags = r.GetString(22),
    };

    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;

    /// <summary>Inserts or updates by path; user metadata (favourite, tags, icon) is preserved.</summary>
    public long UpsertPlugin(PluginInfo p)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Plugins (Name, Vendor, Version, Type, Architecture, HostType, Path, Status, Details, UniqueId,
                                 NumParameters, NumPrograms, NumInputs, NumOutputs, HasEditor, IsSynth, FileSize, LastWriteUtc, LastScanUtc)
            VALUES ($name, $vendor, $version, $type, $arch, $host, $path, $status, $details, $uid,
                    $np, $nprog, $nin, $nout, $editor, $synth, $size, $write, $scan)
            ON CONFLICT(Path) DO UPDATE SET
                Name = excluded.Name, Vendor = excluded.Vendor, Version = excluded.Version, Type = excluded.Type,
                Architecture = excluded.Architecture, HostType = excluded.HostType, Status = excluded.Status,
                Details = excluded.Details, UniqueId = excluded.UniqueId, NumParameters = excluded.NumParameters,
                NumPrograms = excluded.NumPrograms, NumInputs = excluded.NumInputs, NumOutputs = excluded.NumOutputs,
                HasEditor = excluded.HasEditor, IsSynth = excluded.IsSynth, FileSize = excluded.FileSize,
                LastWriteUtc = excluded.LastWriteUtc, LastScanUtc = excluded.LastScanUtc
            RETURNING Id
            """;
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$vendor", p.Vendor);
        cmd.Parameters.AddWithValue("$version", p.Version);
        cmd.Parameters.AddWithValue("$type", p.Type);
        cmd.Parameters.AddWithValue("$arch", p.Architecture.ToString());
        cmd.Parameters.AddWithValue("$host", p.HostType.ToString());
        cmd.Parameters.AddWithValue("$path", p.Path);
        cmd.Parameters.AddWithValue("$status", p.Status.ToString());
        cmd.Parameters.AddWithValue("$details", p.Details);
        cmd.Parameters.AddWithValue("$uid", p.UniqueId);
        cmd.Parameters.AddWithValue("$np", p.NumParameters);
        cmd.Parameters.AddWithValue("$nprog", p.NumPrograms);
        cmd.Parameters.AddWithValue("$nin", p.NumInputs);
        cmd.Parameters.AddWithValue("$nout", p.NumOutputs);
        cmd.Parameters.AddWithValue("$editor", p.HasEditor ? 1 : 0);
        cmd.Parameters.AddWithValue("$synth", p.IsSynth ? 1 : 0);
        cmd.Parameters.AddWithValue("$size", p.FileSize);
        cmd.Parameters.AddWithValue("$write", p.LastWriteUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$scan", p.LastScanUtc.ToString("O"));
        var id = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        p.Id = id;
        return id;
    }

    public void SetStatus(string path, PluginStatus status, string? details = null)
    {
        using var c = Open();
        Exec(c, "UPDATE Plugins SET Status = $s, Details = COALESCE($d, Details) WHERE Path = $p",
            ("$s", status.ToString()), ("$d", details), ("$p", path));
    }

    public void DeletePlugin(string path)
    {
        using var c = Open();
        Exec(c, "DELETE FROM Plugins WHERE Path = $p", ("$p", path));
    }

    // ---------------------------------------------------------------- scan locations

    public List<string> GetScanLocations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Path FROM ScanLocations ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void AddScanLocation(string path)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO ScanLocations (Path) VALUES ($p)", ("$p", path));
    }

    public void RemoveScanLocation(string path)
    {
        using var c = Open();
        Exec(c, "DELETE FROM ScanLocations WHERE Path = $p", ("$p", path));
    }

    // ---------------------------------------------------------------- metadata

    public void SaveMetadata(long pluginId, string? iconPath, bool favourite, string tags)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO PluginMetadata (PluginId, IconPath, Favourite, Tags) VALUES ($id, $icon, $fav, $tags)
            ON CONFLICT(PluginId) DO UPDATE SET IconPath = excluded.IconPath, Favourite = excluded.Favourite, Tags = excluded.Tags
            """,
            ("$id", pluginId), ("$icon", iconPath), ("$fav", favourite ? 1 : 0), ("$tags", tags));
    }

    // ---------------------------------------------------------------- crash history

    public void Record(CrashRecord record)
    {
        using var c = Open();
        Exec(c, "INSERT INTO CrashHistory (PluginPath, PluginName, TimeUtc, Message) VALUES ($p, $n, $t, $m)",
            ("$p", record.PluginPath), ("$n", record.PluginName), ("$t", record.TimeUtc.ToString("O")), ("$m", record.Message));
        Exec(c, "UPDATE Plugins SET Status = 'Crashed', Details = $m WHERE Path = $p AND Status <> 'Disabled'",
            ("$m", record.Message), ("$p", record.PluginPath));
    }

    public List<CrashRecord> GetCrashes(string? pluginPath = null, int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PluginPath, PluginName, TimeUtc, Message FROM CrashHistory "
            + (pluginPath is null ? "" : "WHERE PluginPath = $p ")
            + "ORDER BY Id DESC LIMIT $l";
        if (pluginPath is not null) cmd.Parameters.AddWithValue("$p", pluginPath);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CrashRecord>();
        while (r.Read()) list.Add(new CrashRecord(r.GetString(0), r.GetString(1), ParseDate(r.GetString(2)), r.GetString(3)));
        return list;
    }
}
