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
        EnsureColumn("card", "art_id", "INTEGER");
        EnsureColumn("app_config", "of_card_asset_bundle", "VARCHAR(8)");
        EnsureIndex("idx_card_art_id", "card", "art_id");
    }

    private void EnsureIndex(string indexName, string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {table}({column});";
        cmd.ExecuteNonQuery();
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
       is_overframe, overframe_base_id, art_id
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

    public IReadOnlyList<CardRecord> QueryCards(CardQueryFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var limit = Math.Clamp(filters.Limit, 1, 500);
        var offset = Math.Max(0, filters.Offset);

        using var cmd = _connection.CreateCommand();
        var where = BuildFilterWhere(filters, cmd);
        cmd.CommandText = $@"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id, art_id
FROM card
{where}
ORDER BY id
LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var results = new List<CardRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadCard(reader));
        return results;
    }

    public int CountCards(CardQueryFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        using var cmd = _connection.CreateCommand();
        var where = BuildFilterWhere(filters, cmd);
        cmd.CommandText = $"SELECT COUNT(*) FROM card {where};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CardRecord? GetById(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id, art_id
FROM card WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public CardRecord? GetByBundle(string bundle)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id, art_id
FROM card WHERE bundle = $bundle;";
        cmd.Parameters.AddWithValue("$bundle", bundle);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public CardRecord? GetByArtId(int artId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup,
       is_overframe, overframe_base_id, art_id
FROM card WHERE art_id = $art_id
ORDER BY id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$art_id", artId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public void SetArtId(int cardId, int artId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE card SET art_id = $art_id WHERE id = $id;";
        cmd.Parameters.AddWithValue("$art_id", artId);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates editable card fields in a single transaction.
    /// Rejects missing IDs and unexpected row counts. Does not create or delete cards.
    /// </summary>
    public void UpdateCard(
        int id,
        string name,
        string description,
        string? moddedName,
        string? moddedDescription,
        bool favorite)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Card id must be a positive integer.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Card name is required.", nameof(name));
        if (description is null)
            throw new ArgumentNullException(nameof(description));

        using var tx = _connection.BeginTransaction();
        using var existsCmd = _connection.CreateCommand();
        existsCmd.Transaction = tx;
        existsCmd.CommandText = "SELECT COUNT(*) FROM card WHERE id = $id;";
        existsCmd.Parameters.AddWithValue("$id", id);
        var existing = Convert.ToInt32(existsCmd.ExecuteScalar());
        if (existing != 1)
            throw new InvalidOperationException($"Card id {id} was not found.");

        using var updateCmd = _connection.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = @"
UPDATE card SET
  name = $name,
  description = $description,
  modded_name = $modded_name,
  modded_description = $modded_description,
  favorite = $favorite
WHERE id = $id;";
        updateCmd.Parameters.AddWithValue("$name", name.Trim());
        updateCmd.Parameters.AddWithValue("$description", description);
        updateCmd.Parameters.AddWithValue("$modded_name",
            string.IsNullOrWhiteSpace(moddedName) ? DBNull.Value : moddedName.Trim());
        updateCmd.Parameters.AddWithValue("$modded_description",
            string.IsNullOrWhiteSpace(moddedDescription) ? DBNull.Value : moddedDescription);
        updateCmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        updateCmd.Parameters.AddWithValue("$id", id);

        var rows = updateCmd.ExecuteNonQuery();
        if (rows != 1)
            throw new InvalidOperationException($"Expected to update 1 row for card id {id}, but updated {rows}.");

        tx.Commit();
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
    /// Clears all over-frame flags, then marks cards whose Master Duel art id
    /// appears as a gate <paramref name="entries"/> trigger (Texture2D m_Name).
    /// Returns how many card rows were marked.
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
WHERE art_id = $trigger;";
            cmd.Parameters.AddWithValue("$trigger", (int)triggerId);
            cmd.Parameters.AddWithValue("$base", (int)baseArtId);
            marked += cmd.ExecuteNonQuery();
        }

        return marked;
    }

    /// <summary>Best-effort name for a cut-in id via card.art_id.</summary>
    public string? ResolveCutInName(int cutInId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COALESCE(NULLIF(TRIM(modded_name), ''), name)
            FROM card
            WHERE art_id = $id
            ORDER BY id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", cutInId);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<(int Id, string Bundle)> ListCardBundles()
    {
        using var cmd = _connection.CreateCommand();
        // Newest Master Duel arts (incl. official over-frames) tend to have high data_index.
        cmd.CommandText = "SELECT id, bundle FROM card ORDER BY data_index DESC, id DESC;";
        var results = new List<(int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetInt32(0), reader.GetString(1)));
        return results;
    }

    public int CountCards()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM card;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string BuildFilterWhere(CardQueryFilters filters, SqliteCommand cmd)
    {
        var clauses = new List<string>();

        if (filters.CardId is int cardId)
        {
            clauses.Add("id = $filter_id");
            cmd.Parameters.AddWithValue("$filter_id", cardId);
        }

        var name = filters.NameContains?.Trim() ?? "";
        if (name.Length > 0)
        {
            clauses.Add("(name LIKE $filter_name OR IFNULL(modded_name,'') LIKE $filter_name)");
            cmd.Parameters.AddWithValue("$filter_name", $"%{name}%");
        }

        var description = filters.DescriptionContains?.Trim() ?? "";
        if (description.Length > 0)
        {
            clauses.Add("(description LIKE $filter_desc OR IFNULL(modded_description,'') LIKE $filter_desc)");
            cmd.Parameters.AddWithValue("$filter_desc", $"%{description}%");
        }

        if (filters.Favorite is bool favorite)
            clauses.Add(favorite ? "favorite = 1" : "favorite = 0");

        if (filters.HasBackup is bool hasBackup)
            clauses.Add(hasBackup ? "has_backup = 1" : "has_backup = 0");

        if (filters.HasModdedName is bool hasModdedName)
        {
            clauses.Add(hasModdedName
                ? "(modded_name IS NOT NULL AND TRIM(modded_name) <> '')"
                : "(modded_name IS NULL OR TRIM(modded_name) = '')");
        }

        if (filters.HasModdedDescription is bool hasModdedDescription)
        {
            clauses.Add(hasModdedDescription
                ? "(modded_description IS NOT NULL AND TRIM(modded_description) <> '')"
                : "(modded_description IS NULL OR TRIM(modded_description) = '')");
        }

        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
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
        OverframeBaseId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        ArtId = reader.IsDBNull(11) ? null : reader.GetInt32(11)
    };

    public void Dispose() => _connection.Dispose();
}
