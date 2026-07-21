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

    public bool? GetCreateBackupFlag()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT create_backup FROM app_config ORDER BY id LIMIT 1;";
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull) return null;
        return Convert.ToInt64(value) != 0;
    }

    public IReadOnlyList<CardRecord> SearchCards(string? query, bool favoritesOnly = false, bool searchDescription = false, int limit = 500)
    {
        query = query?.Trim() ?? "";
        using var cmd = _connection.CreateCommand();

        var clauses = new List<string>();
        if (favoritesOnly)
            clauses.Add("favorite = 1");

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
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup
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
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup
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
SELECT id, name, description, bundle, modded_name, modded_description, data_index, favorite, has_backup
FROM card WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
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
        HasBackup = reader.GetBoolean(8)
    };

    public void Dispose() => _connection.Dispose();
}
