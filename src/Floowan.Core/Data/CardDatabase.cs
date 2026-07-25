using Microsoft.Data.Sqlite;
using Floowan.Core.Models;

namespace Floowan.Core.Data;

/// <summary>
/// Card catalog + user state facade over two SQLite files:
/// <list type="bullet">
/// <item><description><c>database.db</c> (master) — identity/catalog: id, name, description, bundle, data_index, card_type, created_at</description></item>
/// <item><description><c>user.db</c> — app_config and per-card mutable state</description></item>
/// </list>
/// Opens master and ATTACHes user. On first open, migrates legacy user columns/rows from a
/// monolithic master into <c>user.db</c>, then reads/writes user fields only there.
/// Does not strip columns from the shipped master file (avoids binary churn).
/// </summary>
public sealed class CardDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _cardSelectList;

    public string MasterDatabasePath { get; }
    public string UserDatabasePath { get; }

    /// <summary>True when master <c>card.card_type</c> exists (e.g. after Tools DB update / #30).</summary>
    public bool HasCardTypeColumn { get; }

    /// <summary>True when master <c>card.created_at</c> exists (e.g. after Tools DB update / #30).</summary>
    public bool HasCreatedAtColumn { get; }

    public CardDatabase(string databasePath, string? userDatabasePath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Card database not found.", databasePath);

        MasterDatabasePath = Path.GetFullPath(databasePath);
        UserDatabasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(userDatabasePath)
                ? UserDatabasePaths.ResolveDefaultPath()
                : userDatabasePath);

        var userDir = Path.GetDirectoryName(UserDatabasePath);
        if (!string.IsNullOrEmpty(userDir))
            Directory.CreateDirectory(userDir);

        EnsureUserDatabaseFileExists();

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MasterDatabasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        _connection.Open();

        AttachUserDatabase();
        EnsureMasterSchema();
        EnsureUserSchema();
        MigrateLegacyUserDataIfNeeded();

        var masterCols = GetColumnNames("main", "card");
        HasCardTypeColumn = masterCols.Any(c => string.Equals(c, "card_type", StringComparison.OrdinalIgnoreCase));
        HasCreatedAtColumn = masterCols.Any(c => string.Equals(c, "created_at", StringComparison.OrdinalIgnoreCase));
        _cardSelectList = BuildCardSelectList(HasCardTypeColumn, HasCreatedAtColumn);
    }

    /// <summary>
    /// Adds optional master catalog columns when missing.
    /// <list type="bullet">
    /// <item><description><c>card_type</c> — type label inferred from CARD_Desc type lines</description></item>
    /// <item><description><c>created_at</c> — ISO-8601 UTC creation time of the illustration AssetBundle file</description></item>
    /// </list>
    /// </summary>
    private void EnsureMasterSchema()
    {
        EnsureMasterCardColumn("card_type", "TEXT");
        EnsureMasterCardColumn("created_at", "TEXT");
    }

    private void EnsureMasterCardColumn(string column, string typeSql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA main.table_info(card);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE main.card ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates an empty SQLite file when <see cref="UserDatabasePath"/> is missing so ATTACH can open it.
    /// Schema/defaults are applied afterwards by <see cref="EnsureUserSchema"/>.
    /// </summary>
    private void EnsureUserDatabaseFileExists()
    {
        if (File.Exists(UserDatabasePath))
            return;

        using var bootstrap = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = UserDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        bootstrap.Open();
    }

    private void AttachUserDatabase()
    {
        // ATTACH does not reliably accept bound parameters in Microsoft.Data.Sqlite.
        var escaped = UserDatabasePath.Replace("'", "''", StringComparison.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{escaped}' AS user;";
        cmd.ExecuteNonQuery();
    }

    private void EnsureUserSchema()
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS user.schema_meta (
  key TEXT NOT NULL PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS user.app_config (
  id INTEGER PRIMARY KEY,
  mipmap_count INTEGER NOT NULL DEFAULT 1,
  game_path VARCHAR(610) NOT NULL DEFAULT '',
  background_path VARCHAR(610),
  version VARCHAR(100),
  crypto_key VARCHAR(100),
  packer VARCHAR(5) NOT NULL DEFAULT 'lz4',
  create_backup BOOLEAN NOT NULL DEFAULT 1,
  background_mode VARCHAR(10) DEFAULT 'stretched',
  of_card_asset_bundle VARCHAR(8)
);

CREATE TABLE IF NOT EXISTS user.card_state (
  id INTEGER NOT NULL PRIMARY KEY,
  modded_name VARCHAR(255),
  modded_description VARCHAR(255),
  favorite BOOLEAN NOT NULL DEFAULT 0,
  has_backup BOOLEAN NOT NULL DEFAULT 0,
  is_overframe INTEGER NOT NULL DEFAULT 0,
  overframe_base_id INTEGER,
  art_id INTEGER,
  floowan_overframe INTEGER NOT NULL DEFAULT 0,
  overframe_applied_at TEXT,
  overframe_bundle VARCHAR(8)
);

CREATE INDEX IF NOT EXISTS user.idx_card_state_art_id ON card_state(art_id);
";
            cmd.ExecuteNonQuery();
        }

        EnsureUserCardStateColumn("floowan_overframe", "INTEGER NOT NULL DEFAULT 0");
        EnsureUserCardStateColumn("overframe_applied_at", "TEXT");
        EnsureUserCardStateColumn("overframe_bundle", "VARCHAR(8)");
        BackfillFloowanOverframeFromLegacyFlags();
        EnsureUserAppConfigColumn("of_card_asset_bundle", "VARCHAR(8)");
        EnsureUserAppConfigRow();
    }

    private void EnsureUserCardStateColumn(string column, string typeSql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA user.table_info(card_state);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE user.card_state ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time: treat legacy <c>is_overframe</c> rows as Floowan-applied so patch restore
    /// still finds them after <see cref="SyncOverframeFromGate"/> clears live gate flags.
    /// </summary>
    private void BackfillFloowanOverframeFromLegacyFlags()
    {
        if (GetSchemaMeta("floowan_overframe_backfilled") == "1")
            return;

        using var tx = _connection.BeginTransaction();
        try
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE user.card_state
SET floowan_overframe = 1,
    overframe_applied_at = COALESCE(overframe_applied_at, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
WHERE IFNULL(is_overframe, 0) = 1
  AND IFNULL(floowan_overframe, 0) = 0;";
                cmd.ExecuteNonQuery();
            }

            SetSchemaMeta(tx, "floowan_overframe_backfilled", "1");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private void EnsureUserAppConfigColumn(string column, string typeSql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA user.table_info(app_config);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE user.app_config ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }

    private void EnsureUserAppConfigRow()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO user.app_config (id, mipmap_count, game_path, packer, create_backup)
SELECT 1, 1, '', 'lz4', 1
WHERE NOT EXISTS (SELECT 1 FROM user.app_config LIMIT 1);";
        cmd.ExecuteNonQuery();
    }

    private void MigrateLegacyUserDataIfNeeded()
    {
        if (GetSchemaMeta("legacy_user_migrated") == "1")
            return;

        using var tx = _connection.BeginTransaction();
        try
        {
            MigrateLegacyAppConfig(tx);
            MigrateLegacyCardState(tx);
            SetSchemaMeta(tx, "legacy_user_migrated", "1");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private void MigrateLegacyAppConfig(SqliteTransaction tx)
    {
        if (!TableExists("main", "app_config"))
            return;

        // Only copy when user row still looks like a fresh default and master has a row.
        using (var check = _connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = @"
SELECT game_path, of_card_asset_bundle, create_backup, packer, mipmap_count
FROM user.app_config ORDER BY id LIMIT 1;";
            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                var gamePath = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var ofBundle = reader.IsDBNull(1) ? null : reader.GetString(1);
                var createBackup = !reader.IsDBNull(2) && Convert.ToInt64(reader.GetValue(2)) != 0;
                var packer = reader.IsDBNull(3) ? "lz4" : reader.GetString(3);
                var mipmap = reader.IsDBNull(4) ? 1 : Convert.ToInt32(reader.GetValue(4));
                var stillDefault =
                    string.IsNullOrWhiteSpace(gamePath) &&
                    string.IsNullOrWhiteSpace(ofBundle) &&
                    createBackup &&
                    string.Equals(packer, "lz4", StringComparison.OrdinalIgnoreCase) &&
                    mipmap == 1;
                if (!stillDefault)
                    return;
            }
        }

        var masterCols = GetColumnNames("main", "app_config");
        if (masterCols.Count == 0)
            return;

        using var src = _connection.CreateCommand();
        src.Transaction = tx;
        src.CommandText = "SELECT * FROM main.app_config ORDER BY id LIMIT 1;";
        using var row = src.ExecuteReader();
        if (!row.Read())
            return;

        var userCols = GetColumnNames("user", "app_config");
        var shared = masterCols.Where(c => userCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (shared.Count == 0)
            return;

        var assignments = string.Join(", ", shared.Select(c => $"{c} = ${c}"));
        using var upd = _connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            $"UPDATE user.app_config SET {assignments} WHERE id = (SELECT id FROM user.app_config ORDER BY id LIMIT 1);";
        for (var i = 0; i < row.FieldCount; i++)
        {
            var name = row.GetName(i);
            if (!shared.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            upd.Parameters.AddWithValue("$" + name, row.IsDBNull(i) ? DBNull.Value : row.GetValue(i));
        }

        upd.ExecuteNonQuery();
    }

    private void MigrateLegacyCardState(SqliteTransaction tx)
    {
        using (var countCmd = _connection.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM user.card_state;";
            if (Convert.ToInt32(countCmd.ExecuteScalar()) > 0)
                return;
        }

        if (!TableExists("main", "card"))
            return;

        var cols = GetColumnNames("main", "card");
        bool Has(string name) => cols.Contains(name, StringComparer.OrdinalIgnoreCase);

        // Need at least one user-owned column on the legacy master card table.
        if (!Has("favorite") && !Has("has_backup") && !Has("modded_name") &&
            !Has("modded_description") && !Has("is_overframe") && !Has("art_id") &&
            !Has("overframe_base_id"))
            return;

        string Col(string name, string fallbackSql) =>
            Has(name) ? name : fallbackSql;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
INSERT INTO user.card_state (
  id, modded_name, modded_description, favorite, has_backup,
  is_overframe, overframe_base_id, art_id)
SELECT
  id,
  {Col("modded_name", "NULL")},
  {Col("modded_description", "NULL")},
  {Col("favorite", "0")},
  {Col("has_backup", "0")},
  {Col("is_overframe", "0")},
  {Col("overframe_base_id", "NULL")},
  {Col("art_id", "NULL")}
FROM main.card;";
        cmd.ExecuteNonQuery();
    }

    private string? GetSchemaMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM user.schema_meta WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetSchemaMeta(SqliteTransaction tx, string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO user.schema_meta (key, value) VALUES ($key, $value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private bool TableExists(string schema, string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {schema}.sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private List<string> GetColumnNames(string schema, string table)
    {
        var names = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>
    /// Base select list (ordinals 0–11). Optional master columns follow as 12–13 when present;
    /// otherwise NULL placeholders keep <see cref="ReadCard"/> ordinals stable.
    /// </summary>
    private static string BuildCardSelectList(bool hasCardType, bool hasCreatedAt)
    {
        var cardType = hasCardType ? "c.card_type" : "NULL AS card_type";
        var createdAt = hasCreatedAt ? "c.created_at" : "NULL AS created_at";
        return $@"
c.id, c.name, c.description, c.bundle,
u.modded_name, u.modded_description, c.data_index,
IFNULL(u.favorite, 0), IFNULL(u.has_backup, 0),
IFNULL(u.is_overframe, 0), u.overframe_base_id, u.art_id,
{cardType}, {createdAt}";
    }

    private const string CardFromJoin = @"
FROM card c
LEFT JOIN user.card_state u ON u.id = c.id";

    /// <summary>
    /// Replaces all master <c>card</c> catalog rows with <paramref name="rows"/>.
    /// Preserves legacy NOT NULL defaults for favorite/has_backup/is_overframe on master.
    /// Does not modify <c>user.card_state</c>.
    /// </summary>
    public int ReplaceMasterCatalog(IEnumerable<CatalogCardRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsureMasterSchema();

        var list = rows as IList<CatalogCardRow> ?? rows.ToList();
        using var tx = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM main.card;";
            delete.ExecuteNonQuery();
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = @"
INSERT INTO main.card (
  id, name, description, bundle, data_index, card_type, created_at,
  favorite, has_backup, is_overframe
) VALUES (
  $id, $name, $description, $bundle, $data_index, $card_type, $created_at,
  0, 0, 0
);";
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pDesc = insert.Parameters.Add("$description", SqliteType.Text);
        var pBundle = insert.Parameters.Add("$bundle", SqliteType.Text);
        var pIndex = insert.Parameters.Add("$data_index", SqliteType.Integer);
        var pType = insert.Parameters.Add("$card_type", SqliteType.Text);
        var pCreated = insert.Parameters.Add("$created_at", SqliteType.Text);

        var written = 0;
        foreach (var row in list)
        {
            pId.Value = row.Id;
            pName.Value = row.Name;
            pDesc.Value = row.Description;
            pBundle.Value = row.Bundle;
            pIndex.Value = row.DataIndex;
            pType.Value = string.IsNullOrWhiteSpace(row.CardType) ? DBNull.Value : row.CardType;
            pCreated.Value = string.IsNullOrWhiteSpace(row.CreatedAt) ? DBNull.Value : row.CreatedAt;
            insert.ExecuteNonQuery();
            written++;
        }

        tx.Commit();
        return written;
    }

    /// <summary>
    /// Inserts or updates master <c>card</c> rows by primary key <c>id</c>.
    /// Does not delete existing rows or modify <c>user.card_state</c>.
    /// </summary>
    public int UpsertMasterCatalog(IEnumerable<CatalogCardRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsureMasterSchema();

        var list = rows as IList<CatalogCardRow> ?? rows.ToList();
        using var tx = _connection.BeginTransaction();
        using var upsert = _connection.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = @"
INSERT INTO main.card (
  id, name, description, bundle, data_index, card_type, created_at,
  favorite, has_backup, is_overframe
) VALUES (
  $id, $name, $description, $bundle, $data_index, $card_type, $created_at,
  0, 0, 0
)
ON CONFLICT(id) DO UPDATE SET
  name = excluded.name,
  description = excluded.description,
  bundle = excluded.bundle,
  data_index = excluded.data_index,
  card_type = excluded.card_type,
  created_at = excluded.created_at;";
        var pId = upsert.Parameters.Add("$id", SqliteType.Integer);
        var pName = upsert.Parameters.Add("$name", SqliteType.Text);
        var pDesc = upsert.Parameters.Add("$description", SqliteType.Text);
        var pBundle = upsert.Parameters.Add("$bundle", SqliteType.Text);
        var pIndex = upsert.Parameters.Add("$data_index", SqliteType.Integer);
        var pType = upsert.Parameters.Add("$card_type", SqliteType.Text);
        var pCreated = upsert.Parameters.Add("$created_at", SqliteType.Text);

        var written = 0;
        foreach (var row in list)
        {
            pId.Value = row.Id;
            pName.Value = row.Name;
            pDesc.Value = row.Description;
            pBundle.Value = row.Bundle;
            pIndex.Value = row.DataIndex;
            pType.Value = string.IsNullOrWhiteSpace(row.CardType) ? DBNull.Value : row.CardType;
            pCreated.Value = string.IsNullOrWhiteSpace(row.CreatedAt) ? DBNull.Value : row.CreatedAt;
            upsert.ExecuteNonQuery();
            written++;
        }

        tx.Commit();
        return written;
    }

    /// <summary>
    /// Latest non-null <c>created_at</c> in the master catalog (ISO-8601 UTC), or null if none.
    /// </summary>
    public DateTimeOffset? GetLatestCreatedAtUtc()
    {
        EnsureMasterSchema();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(created_at) FROM main.card WHERE created_at IS NOT NULL AND TRIM(created_at) != '';";
        var scalar = cmd.ExecuteScalar();
        if (scalar is null or DBNull)
            return null;
        var text = Convert.ToString(scalar);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }

    public bool HasMasterColumn(string columnName)
    {
        foreach (var name in GetColumnNames("main", "card"))
        {
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string? GetStoredGamePath()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT game_path FROM user.app_config ORDER BY id LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    public void SetStoredGamePath(string gamePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE user.app_config SET game_path = $path WHERE id = (SELECT id FROM user.app_config ORDER BY id LIMIT 1);";
        cmd.Parameters.AddWithValue("$path", gamePath);
        cmd.ExecuteNonQuery();
    }

    public string? GetOfCardAssetBundleId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT of_card_asset_bundle FROM user.app_config ORDER BY id LIMIT 1;";
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull) return null;
        var s = Convert.ToString(value);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public void SetOfCardAssetBundleId(string bundleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE user.app_config SET of_card_asset_bundle = $bundle WHERE id = (SELECT id FROM user.app_config ORDER BY id LIMIT 1);";
        cmd.Parameters.AddWithValue("$bundle", bundleId);
        cmd.ExecuteNonQuery();
    }

    public bool? GetCreateBackupFlag()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT create_backup FROM user.app_config ORDER BY id LIMIT 1;";
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
            clauses.Add("IFNULL(u.favorite, 0) = 1");
        if (overframeOnly)
            clauses.Add("IFNULL(u.is_overframe, 0) = 1");

        if (query.Length >= 1)
        {
            // Exact card id when the query is an integer (Database → Open in Card Art / Over-frame).
            var idMatch = int.TryParse(
                query,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var exactId)
                ? "c.id = $exact_id OR "
                : "";

            if (searchDescription)
            {
                clauses.Add(
                    $"({idMatch}c.name LIKE $q OR c.description LIKE $q OR IFNULL(u.modded_name,'') LIKE $q OR IFNULL(u.modded_description,'') LIKE $q)");
            }
            else
            {
                clauses.Add($"({idMatch}c.name LIKE $q OR IFNULL(u.modded_name,'') LIKE $q)");
            }

            if (idMatch.Length > 0)
                cmd.Parameters.AddWithValue("$exact_id", exactId);
            cmd.Parameters.AddWithValue("$q", $"%{query}%");
        }

        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        cmd.CommandText = $@"
SELECT {_cardSelectList}
{CardFromJoin}
{where}
ORDER BY c.name COLLATE NOCASE
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<CardRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadCard(reader));
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
SELECT {_cardSelectList}
{CardFromJoin}
{where}
ORDER BY c.id
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
        cmd.CommandText = $"SELECT COUNT(*) {CardFromJoin} {where};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CardRecord? GetById(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {_cardSelectList}
{CardFromJoin}
WHERE c.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public CardRecord? GetByBundle(string bundle)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {_cardSelectList}
{CardFromJoin}
WHERE c.bundle = $bundle;";
        cmd.Parameters.AddWithValue("$bundle", bundle);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public CardRecord? GetByArtId(int artId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {_cardSelectList}
{CardFromJoin}
WHERE u.art_id = $art_id
ORDER BY c.id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$art_id", artId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCard(reader) : null;
    }

    public void SetArtId(int cardId, int artId)
    {
        EnsureCardStateRow(cardId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE user.card_state SET art_id = $art_id WHERE id = $id;";
        cmd.Parameters.AddWithValue("$art_id", artId);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes <c>card.card_type</c> for an existing catalog row. Ensures the column exists.
    /// No-ops (returns false) when the master table still has no <c>card_type</c> after ensure,
    /// or when the id is missing. Does not touch <c>user.db</c>.
    /// </summary>
    public bool UpdateCardType(int cardId, string? cardType)
    {
        if (cardId <= 0)
            throw new ArgumentOutOfRangeException(nameof(cardId), "Card id must be a positive integer.");

        EnsureMasterSchema();
        if (!HasMasterColumn("card_type"))
            return false;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
UPDATE main.card SET card_type = $card_type
WHERE id = $id;";
        cmd.Parameters.AddWithValue(
            "$card_type",
            string.IsNullOrWhiteSpace(cardType) ? DBNull.Value : cardType.Trim());
        cmd.Parameters.AddWithValue("$id", cardId);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Updates editable card fields in a single transaction.
    /// Catalog name/description write to master; modded fields and favorite write to user.db.
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

        using var catalogCmd = _connection.CreateCommand();
        catalogCmd.Transaction = tx;
        catalogCmd.CommandText = @"
UPDATE card SET
  name = $name,
  description = $description
WHERE id = $id;";
        catalogCmd.Parameters.AddWithValue("$name", name.Trim());
        catalogCmd.Parameters.AddWithValue("$description", description);
        catalogCmd.Parameters.AddWithValue("$id", id);
        var catalogRows = catalogCmd.ExecuteNonQuery();
        if (catalogRows != 1)
            throw new InvalidOperationException($"Expected to update 1 catalog row for card id {id}, but updated {catalogRows}.");

        EnsureCardStateRow(id, tx);
        using var userCmd = _connection.CreateCommand();
        userCmd.Transaction = tx;
        userCmd.CommandText = @"
UPDATE user.card_state SET
  modded_name = $modded_name,
  modded_description = $modded_description,
  favorite = $favorite
WHERE id = $id;";
        userCmd.Parameters.AddWithValue("$modded_name",
            string.IsNullOrWhiteSpace(moddedName) ? DBNull.Value : moddedName.Trim());
        userCmd.Parameters.AddWithValue("$modded_description",
            string.IsNullOrWhiteSpace(moddedDescription) ? DBNull.Value : moddedDescription);
        userCmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
        userCmd.Parameters.AddWithValue("$id", id);
        var userRows = userCmd.ExecuteNonQuery();
        if (userRows != 1)
            throw new InvalidOperationException($"Expected to update 1 user row for card id {id}, but updated {userRows}.");

        tx.Commit();
    }

    public void SetHasBackup(int cardId, bool hasBackup)
    {
        EnsureCardStateRow(cardId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE user.card_state SET has_backup = $flag WHERE id = $id;";
        cmd.Parameters.AddWithValue("$flag", hasBackup ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    public void SetOverframe(int cardId, bool isOverframe, int? overframeBaseId = null)
    {
        EnsureCardStateRow(cardId);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
UPDATE user.card_state
SET is_overframe = $flag,
    overframe_base_id = $base
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$flag", isOverframe ? 1 : 0);
        cmd.Parameters.AddWithValue("$base", (object?)overframeBaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records that Floowan successfully applied (or removed) an over-frame for this card.
    /// Survives <see cref="SyncOverframeFromGate"/> so Tools can re-apply after an MD patch.
    /// </summary>
    public void SetFloowanOverframe(
        int cardId,
        bool applied,
        int? overframeBaseId = null,
        string? bundleId = null)
    {
        EnsureCardStateRow(cardId);
        using var cmd = _connection.CreateCommand();
        if (applied)
        {
            cmd.CommandText = @"
UPDATE user.card_state
SET is_overframe = 1,
    floowan_overframe = 1,
    overframe_base_id = $base,
    overframe_applied_at = $at,
    overframe_bundle = $bundle
WHERE id = $id;";
            cmd.Parameters.AddWithValue("$base", (object?)overframeBaseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            cmd.Parameters.AddWithValue(
                "$bundle",
                string.IsNullOrWhiteSpace(bundleId) ? DBNull.Value : bundleId);
        }
        else
        {
            cmd.CommandText = @"
UPDATE user.card_state
SET is_overframe = 0,
    floowan_overframe = 0,
    overframe_base_id = NULL,
    overframe_applied_at = NULL,
    overframe_bundle = NULL
WHERE id = $id;";
        }

        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    public bool IsFloowanOverframe(int cardId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT IFNULL(floowan_overframe, 0) FROM user.card_state WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", cardId);
        var value = cmd.ExecuteScalar();
        return value is not null and not DBNull && Convert.ToInt64(value) != 0;
    }

    /// <summary>
    /// Cards Floowan previously applied OF to (for post-patch restore), newest first.
    /// </summary>
    public IReadOnlyList<CardRecord> ListFloowanOverframeCards()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {_cardSelectList}
{CardFromJoin}
WHERE IFNULL(u.floowan_overframe, 0) = 1
ORDER BY IFNULL(u.overframe_applied_at, '') DESC, c.id DESC;";
        var results = new List<CardRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadCard(reader));
        return results;
    }

    /// <summary>
    /// Updates live <c>is_overframe</c> from the gate without wiping Floowan apply memory
    /// (<c>floowan_overframe</c> / applied timestamps / stored base ids for Floowan cards).
    /// Returns how many card rows were marked from gate triggers.
    /// </summary>
    public int SyncOverframeFromGate(IEnumerable<(ushort TriggerId, ushort BaseArtId)> entries)
    {
        // Non-Floowan rows: full clear (live gate is source of truth).
        using (var clearOthers = _connection.CreateCommand())
        {
            clearOthers.CommandText = @"
UPDATE user.card_state
SET is_overframe = 0, overframe_base_id = NULL
WHERE IFNULL(floowan_overframe, 0) = 0;";
            clearOthers.ExecuteNonQuery();
        }

        // Floowan-tracked: clear live flag only; keep base_id for restore-after-patch.
        using (var clearFloowanLive = _connection.CreateCommand())
        {
            clearFloowanLive.CommandText = @"
UPDATE user.card_state
SET is_overframe = 0
WHERE floowan_overframe = 1;";
            clearFloowanLive.ExecuteNonQuery();
        }

        var marked = 0;
        foreach (var (triggerId, baseArtId) in entries)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
UPDATE user.card_state
SET is_overframe = 1, overframe_base_id = $base
WHERE art_id = $trigger;";
            cmd.Parameters.AddWithValue("$trigger", (int)triggerId);
            cmd.Parameters.AddWithValue("$base", (int)baseArtId);
            marked += cmd.ExecuteNonQuery();
        }

        return marked;
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

    private void EnsureCardStateRow(int id, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        if (tx is not null)
            cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO user.card_state (id) VALUES ($id)
ON CONFLICT(id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static string BuildFilterWhere(CardQueryFilters filters, SqliteCommand cmd)
    {
        var clauses = new List<string>();

        if (filters.CardId is int cardId)
        {
            clauses.Add("c.id = $filter_id");
            cmd.Parameters.AddWithValue("$filter_id", cardId);
        }

        var name = filters.NameContains?.Trim() ?? "";
        if (name.Length > 0)
        {
            clauses.Add("(c.name LIKE $filter_name OR IFNULL(u.modded_name,'') LIKE $filter_name)");
            cmd.Parameters.AddWithValue("$filter_name", $"%{name}%");
        }

        var description = filters.DescriptionContains?.Trim() ?? "";
        if (description.Length > 0)
        {
            clauses.Add("(c.description LIKE $filter_desc OR IFNULL(u.modded_description,'') LIKE $filter_desc)");
            cmd.Parameters.AddWithValue("$filter_desc", $"%{description}%");
        }

        if (filters.Favorite is bool favorite)
            clauses.Add(favorite ? "IFNULL(u.favorite, 0) = 1" : "IFNULL(u.favorite, 0) = 0");

        if (filters.HasBackup is bool hasBackup)
            clauses.Add(hasBackup ? "IFNULL(u.has_backup, 0) = 1" : "IFNULL(u.has_backup, 0) = 0");

        if (filters.HasModdedName is bool hasModdedName)
        {
            clauses.Add(hasModdedName
                ? "(u.modded_name IS NOT NULL AND TRIM(u.modded_name) <> '')"
                : "(u.modded_name IS NULL OR TRIM(u.modded_name) = '')");
        }

        if (filters.HasModdedDescription is bool hasModdedDescription)
        {
            clauses.Add(hasModdedDescription
                ? "(u.modded_description IS NOT NULL AND TRIM(u.modded_description) <> '')"
                : "(u.modded_description IS NULL OR TRIM(u.modded_description) = '')");
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
        Favorite = Convert.ToInt64(reader.GetValue(7)) != 0,
        HasBackup = Convert.ToInt64(reader.GetValue(8)) != 0,
        IsOverframe = !reader.IsDBNull(9) && Convert.ToInt64(reader.GetValue(9)) != 0,
        OverframeBaseId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        ArtId = reader.IsDBNull(11) ? null : reader.GetInt32(11),
        CardType = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
        CreatedAt = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null
    };

    public void Dispose()
    {
        try
        {
            using var detach = _connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE user;";
            detach.ExecuteNonQuery();
        }
        catch
        {
            // Closing the connection detaches as well.
        }

        _connection.Dispose();
    }
}
