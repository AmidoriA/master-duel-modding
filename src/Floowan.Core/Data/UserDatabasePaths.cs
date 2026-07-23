namespace Floowan.Core.Data;

/// <summary>
/// Resolves the writable per-user SQLite database path.
/// Placed beside the app executable (same root as <c>backups/</c>), not next to the
/// shipped master <c>database.db</c>, so browsing a different catalog keeps one user store.
/// </summary>
public static class UserDatabasePaths
{
    public const string FileName = "user.db";

    public static string ResolveDefaultPath() =>
        Path.Combine(AppContext.BaseDirectory, FileName);
}
