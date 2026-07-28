namespace Floowan.Core.Data;

/// <summary>
/// Resolves the writable per-user SQLite database path.
/// Always beside the app executable (<see cref="AppContext.BaseDirectory"/>),
/// same root as the shipped master <c>database.db</c> and <c>backups/</c>.
/// </summary>
public static class UserDatabasePaths
{
    public const string FileName = "user.db";

    public static string ResolveDefaultPath() =>
        Path.Combine(AppContext.BaseDirectory, FileName);
}

