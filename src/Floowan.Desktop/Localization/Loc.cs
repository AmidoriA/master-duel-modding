using System.IO;
using Floowan.Core.Localization;

namespace Floowan.Desktop.Localization;

/// <summary>App-wide localization facade used by WPF windows.</summary>
public static class Loc
{
    private static LocalizationService? _service;

    public static LocalizationService Service =>
        _service ?? throw new InvalidOperationException("Localization has not been initialized.");

    public static bool IsInitialized => _service is not null;

    public static void Initialize(LocalizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public static string T(string key) => Service.T(key);

    public static string T(string key, params object[] args) => Service.T(key, args);

    public static string Culture => Service.CurrentCulture;

    public static IReadOnlyList<string> AvailableCultures => Service.GetAvailableCultures();

    public static bool SetCulture(string? culture) => Service.SetCulture(culture);

    public static event EventHandler? LanguageChanged
    {
        add
        {
            if (_service is not null)
                _service.LanguageChanged += value;
        }
        remove
        {
            if (_service is not null)
                _service.LanguageChanged -= value;
        }
    }

    public static string LocalesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "locales");

    public static LocalizationService CreateDefault()
    {
        var dir = LocalesDirectory;
        Directory.CreateDirectory(dir);
        return new LocalizationService(dir);
    }
}
