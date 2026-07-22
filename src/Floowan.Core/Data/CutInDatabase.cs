using Floowan.Core.Models;
using Microsoft.Data.Sqlite;

namespace Floowan.Core.Data;

/// <summary>
/// Separate SQLite store for discovered Master Duel summon cut-ins (<c>cutin.db</c>).
/// Kept apart from Floowandereeze <c>database.db</c>.
/// </summary>
public sealed class CutInDatabase : IDisposable
{
    public const string DefaultFileName = "cutin.db";

    private readonly SqliteConnection _connection;

    public CutInDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Cut-in database path is required.", nameof(databasePath));

        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Prefer a <c>cutin.db</c> next to the card database; otherwise LocalAppData\Floowan\cutin.db.
    /// </summary>
    public static string ResolveDefaultPath(string? cardDatabasePath = null)
    {
        if (!string.IsNullOrWhiteSpace(cardDatabasePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cardDatabasePath));
            if (!string.IsNullOrWhiteSpace(dir))
                return Path.Combine(dir, DefaultFileName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            DefaultFileName);
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS cut_in (
                cut_in_id INTEGER NOT NULL PRIMARY KEY,
                name VARCHAR(255),
                is_complete INTEGER NOT NULL DEFAULT 0,
                texture_name VARCHAR(255),
                texture_bundle_path VARCHAR(1024),
                atlas_name VARCHAR(255),
                atlas_bundle_path VARCHAR(1024),
                skeleton_name VARCHAR(255),
                skeleton_bundle_path VARCHAR(1024),
                updated_at_utc VARCHAR(40) NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cut_in_complete ON cut_in(is_complete);
            CREATE INDEX IF NOT EXISTS idx_cut_in_name ON cut_in(name);
            """;
        cmd.ExecuteNonQuery();
    }

    public void ReplaceAll(IEnumerable<CutInRecord> rows)
    {
        using var tx = _connection.BeginTransaction();
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM cut_in;";
            clear.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO cut_in (
                    cut_in_id, name, is_complete,
                    texture_name, texture_bundle_path,
                    atlas_name, atlas_bundle_path,
                    skeleton_name, skeleton_bundle_path,
                    updated_at_utc)
                VALUES (
                    $id, $name, $complete,
                    $texName, $texPath,
                    $atlasName, $atlasPath,
                    $skelName, $skelPath,
                    $updated);
                """;
            cmd.Parameters.AddWithValue("$id", row.CutInId);
            cmd.Parameters.AddWithValue("$name", (object?)row.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$complete", row.IsComplete ? 1 : 0);
            cmd.Parameters.AddWithValue("$texName", (object?)row.TextureName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$texPath", (object?)row.TextureBundlePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$atlasName", (object?)row.AtlasName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$atlasPath", (object?)row.AtlasBundlePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$skelName", (object?)row.SkeletonName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$skelPath", (object?)row.SkeletonBundlePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", row.UpdatedAtUtc);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int Count(bool completeOnly = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = completeOnly
            ? "SELECT COUNT(*) FROM cut_in WHERE is_complete = 1;"
            : "SELECT COUNT(*) FROM cut_in;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool HasCutIn(int cutInId, bool completeOnly = true)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = completeOnly
            ? "SELECT 1 FROM cut_in WHERE cut_in_id = $id AND is_complete = 1 LIMIT 1;"
            : "SELECT 1 FROM cut_in WHERE cut_in_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", cutInId);
        return cmd.ExecuteScalar() is not null and not DBNull;
    }

    public CutInRecord? Get(int cutInId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT cut_in_id, name, is_complete,
                   texture_name, texture_bundle_path,
                   atlas_name, atlas_bundle_path,
                   skeleton_name, skeleton_bundle_path,
                   updated_at_utc
            FROM cut_in WHERE cut_in_id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", cutInId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<CutInRecord> List(bool completeOnly = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = completeOnly
            ? """
              SELECT cut_in_id, name, is_complete,
                     texture_name, texture_bundle_path,
                     atlas_name, atlas_bundle_path,
                     skeleton_name, skeleton_bundle_path,
                     updated_at_utc
              FROM cut_in WHERE is_complete = 1 ORDER BY cut_in_id;
              """
            : """
              SELECT cut_in_id, name, is_complete,
                     texture_name, texture_bundle_path,
                     atlas_name, atlas_bundle_path,
                     skeleton_name, skeleton_bundle_path,
                     updated_at_utc
              FROM cut_in ORDER BY cut_in_id;
              """;
        var list = new List<CutInRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(Read(reader));
        return list;
    }

    private static CutInRecord Read(SqliteDataReader reader) => new()
    {
        CutInId = reader.GetInt32(0),
        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
        IsComplete = reader.GetInt32(2) != 0,
        TextureName = reader.IsDBNull(3) ? null : reader.GetString(3),
        TextureBundlePath = reader.IsDBNull(4) ? null : reader.GetString(4),
        AtlasName = reader.IsDBNull(5) ? null : reader.GetString(5),
        AtlasBundlePath = reader.IsDBNull(6) ? null : reader.GetString(6),
        SkeletonName = reader.IsDBNull(7) ? null : reader.GetString(7),
        SkeletonBundlePath = reader.IsDBNull(8) ? null : reader.GetString(8),
        UpdatedAtUtc = reader.IsDBNull(9) ? "" : reader.GetString(9)
    };

    public void Dispose() => _connection.Dispose();
}
