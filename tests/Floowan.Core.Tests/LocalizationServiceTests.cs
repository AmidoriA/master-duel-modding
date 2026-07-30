using Floowan.Core.Localization;

namespace Floowan.Core.Tests;

public class LocalizationServiceTests
{
    [Fact]
    public void LoadYaml_FlattensNestedKeys()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", """
            app:
              title: Hello
            status:
              ready: Ready
            """);

        var loc = new LocalizationService(dir.Path);
        Assert.Equal("Hello", loc.T("app.title"));
        Assert.Equal("Ready", loc.T("status.ready"));
    }

    [Fact]
    public void MissingKey_FallsBackToEnUs()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", """
            only:
              en: English only
            shared:
              label: English shared
            """);
        dir.Write("th-TH.yaml", """
            shared:
              label: ไทย
            """);

        var loc = new LocalizationService(dir.Path);
        loc.SetCulture("th-TH");
        Assert.Equal("ไทย", loc.T("shared.label"));
        Assert.Equal("English only", loc.T("only.en"));
    }

    [Fact]
    public void MissingLocaleFile_FallsBackToEnUsCulture()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", "app:\n  title: English\n");

        var loc = new LocalizationService(dir.Path);
        // Request a culture with no file — resolve to en-US.
        Assert.False(loc.SetCulture("zz-ZZ")); // same as en-US after resolve → false if already en-US
        Assert.Equal(LocalizationService.FallbackCulture, loc.CurrentCulture);
        Assert.Equal("English", loc.T("app.title"));
    }

    [Fact]
    public void MissingLocalesDirectory_DoesNotThrow_AndReturnsKey()
    {
        var missing = Path.Combine(Path.GetTempPath(), "floowan-locales-missing-" + Guid.NewGuid().ToString("N"));
        var loc = new LocalizationService(missing);
        Assert.Equal(LocalizationService.FallbackCulture, loc.CurrentCulture);
        Assert.Equal("missing.key", loc.T("missing.key"));
        Assert.Contains(LocalizationService.FallbackCulture, loc.GetAvailableCultures());
    }

    [Fact]
    public void DiscoverAvailableCultures_FromYamlFilenames()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", "a: 1\n");
        dir.Write("th-TH.yaml", "a: 2\n");
        dir.Write("ja.yml", "a: 3\n");
        dir.Write("readme.txt", "ignore\n");

        var loc = new LocalizationService(dir.Path);
        var cultures = loc.GetAvailableCultures();
        Assert.Contains("en-US", cultures);
        Assert.Contains("th-TH", cultures);
        Assert.Contains("ja", cultures);
        Assert.DoesNotContain("readme", cultures);
    }

    [Fact]
    public void LanguageOnlyCode_MatchesRegionalLocale()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", "x: en\n");
        dir.Write("th-TH.yaml", "x: th\n");

        var loc = new LocalizationService(dir.Path);
        Assert.True(loc.SetCulture("th"));
        Assert.Equal("th-TH", loc.CurrentCulture);
        Assert.Equal("th", loc.T("x"));
    }

    [Fact]
    public void FormatArgs_AreApplied()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", "status:\n  loaded: \"Cards: {0}, installs: {1}\"\n");

        var loc = new LocalizationService(dir.Path);
        Assert.Equal("Cards: 12, installs: 3", loc.T("status.loaded", 12, 3));
    }

    [Fact]
    public void Reload_PicksUpNewlyAddedLocaleFile()
    {
        using var dir = new TempLocalesDir();
        dir.Write("en-US.yaml", "a: 1\n");
        var loc = new LocalizationService(dir.Path);
        Assert.DoesNotContain("fr-FR", loc.GetAvailableCultures());

        dir.Write("fr-FR.yaml", "a: bonjour\n");
        loc.Reload();
        Assert.Contains("fr-FR", loc.GetAvailableCultures());
        loc.SetCulture("fr-FR");
        Assert.Equal("bonjour", loc.T("a"));
    }

    private sealed class TempLocalesDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "floowan-loc-" + Guid.NewGuid().ToString("N"));

        public TempLocalesDir() => Directory.CreateDirectory(Path);

        public void Write(string fileName, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), contents);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
