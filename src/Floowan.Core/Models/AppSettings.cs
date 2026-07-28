namespace Floowan.Core.Models;

public sealed class AppSettings
{
    public string? GamePath { get; set; }
    public bool CreateBackup { get; set; } = true;
    public string Packer { get; set; } = "lz4";
    public int PreferredCardWidth { get; set; } = 512;
    public int PreferredCardHeight { get; set; } = 512;
}
