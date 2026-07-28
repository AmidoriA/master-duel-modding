namespace Floowan.Core.Data;

/// <summary>
/// Resolves the shipped master card catalog path.
/// Always beside the app executable (<see cref="AppContext.BaseDirectory"/>),
/// same root as <c>user.db</c>, <c>backups/</c>, and third-party notices.
/// </summary>
public static class MasterDatabasePaths
{
    public const string FileName = "database.db";

    public static string ResolveDefaultPath() =>
        Path.Combine(AppContext.BaseDirectory, FileName);
}

