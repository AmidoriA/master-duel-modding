using Microsoft.Data.Sqlite;
using Floowan.Core.Models;

namespace Floowan.Core.Data;

public sealed class CardDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public CardDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Card database not found.", databasePath);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        _connection.Open();
        EnsureOverframeSchema();
    }

    private void EnsureOverframeSchema()
    {
        EnsureColumn("card", "is_overframe", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("card", "overframe_base_id", "INTEGER");
        EnsureColumn("app_config", "of_card_asset_bundle", "VARCHAR(8)");
    }

    private void EnsureColumn(string table, string column, string typeSql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }

    public string? GetStoredGamePath()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT game_path FROM app_config ORDER BY id LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    public void SetStoredGamePath(string gamePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE app_config SET game_path = $path WHERE id = (SELECT id FROM app_config ORDER BY id LIMIT 1);";
        cmd.Parameters.AddWithValue("$path", gamePath);
        cmd.ExecuteNonQuery();
    }

    public string? GetOfCardAssetBundleId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT of_card_asset_bundle FROM app_config ORDER BY id LIMIT 1;";
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull) return null;
        var s = Convert.ToString(value);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public void SetOfCardAssetBundleId(string bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE app_config SET of_card_asset_bundle = $bundle WHERE id = (SELECT id FROM app_config ORDER BY id LIMIT 1);";
        cmd.Parameters.AddWithValue("$bundle", bundleId);
        cmd.ExecuteNonQuery();
    }

    public bool? GetCreateBackupFlag()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT create_backup FROM app_config ORDER BY id LIMIT 1;";
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull) return null;
        return Convert.ToInt64(value) != 0;
    }

    public IReadOnlyList<CardRecord> SearchCards(
        string? query,
        bool favoritesOnly = false,
        bool searchDescription = false,
        bool overframeOnly = false,
        int limit = 500)
    {
        query = query?.Trim() ?? "";
        using var cmd = _connection.CreateCommand();

        var clauses = new List<string>();
        if (favoritesOnly)
            clauses.Add("favorite = 1");
        if (overframeOnly)
            clauses.Add("is_overframe = 1");

        if (query.Length >= 1)
        {
            if (searchDescription)
                clauses.Add("(name LIKE $q OR description LIKE $q OR IFNULL(modded_name,'') LIKE $q)");
            else
                clauses.Add("(name LIKE $q OR IFNULL(modded_name,'') LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{query}%");
        }

        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        cmd.CommandText = $@"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id
FROM card
{where}
ORDER BY name COLLATE NOCASE
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<CardRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadCard(reader));
        }

        return results;
    }

    public CardRecord? GetById(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id
FROM card WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public void SetHasBackup(int cardId, bool hasBackup)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE card SET has_backup = $flag WHERE id = $id;";
        cmd.Parameters.AddWithValue("$flag", hasBackup ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    public void SetOverframe(int cardId, bool isOverframe, int? overframeBaseId = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
UPDATE card
SET is_overframe = $flag,
    overframe_base_id = $base
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$flag", isOverframe ? 1 : 0);
        cmd.Parameters.AddWithValue("$base", (object?)overframeBaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears all over-frame flags, then marks cards present in the gate payload.
    /// Returns how many cards were marked.
    /// </summary>
    public int SyncOverframeFromGate(IEnumerable<(ushort TriggerId, ushort BaseArtId)> entries)
    {
        using var clear = _connection.CreateCommand();
        clear.CommandText = "UPDATE card SET is_overframe = 0, overframe_base_id = NULL;";
        clear.ExecuteNonQuery();

        var marked = 0;
        foreach (var (triggerId, baseArtId) in entries)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
UPDATE card
SET is_overframe = 1, overframe_base_id = $base
WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", (int)triggerId);
            cmd.Parameters.AddWithValue("$base", (int)baseArtId);
            marked += cmd.ExecuteNonQuery();
        }

        return marked;
    }

    public int CountCards()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM card;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static CardRecord ReadCard(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        Description = reader.GetString(2),
        Bundle = reader.GetString(3),
        ModdedName = reader.IsDBNull(4) ? null : reader.GetString(4),
        ModdedDescription = reader.IsDBNull(5) ? null : reader.GetString(5),
        DataIndex = reader.GetInt32(6),
        Favorite = reader.GetBoolean(7),
        HasBackup = reader.GetBoolean(8),
        IsOverframe = !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
        OverframeBaseId = reader.IsDBNull(10) ? null : reader.GetInt32(10)
    };

    public void Dispose() => _connection.Dispose();
}
