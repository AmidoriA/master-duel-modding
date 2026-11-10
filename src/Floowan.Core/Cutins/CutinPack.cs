using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Floowan.Core.Cutins;

public enum CutinPackPayload
{
    /// <summary>The source video and its settings; rebuilt on import (small).</summary>
    Video,
    /// <summary>The finished Spine content (skeleton, atlases, sheets).</summary>
    Content,
    /// <summary>Another card's existing cut-in (no files).</summary>
    Borrow,
}

public sealed record CutinPackCard(int CardId, string Name, CutinPackPayload Payload, int? Template, int? Source, bool OverrideOfficial);

/// <summary>
/// .cutins packs: a zip with cutins.json and cards/&lt;id&gt;/... Import reads only the files it expects for each card
/// (no paths from the pack are used), and only for the cards the pack lists.
/// </summary>
public static class CutinPack
{
    public const string Format = "floowan-cutins";
    public const int FormatVersion = 1;
    public const string Extension = ".cutins";
    private const string ManifestEntry = "cutins.json";
    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv"];

    public static void Export(CutinRegistry registry, IEnumerable<int> cardIds, string outPath)
    {
        var tmp = outPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var cards = new JsonArray();
            foreach (var id in cardIds)
            {
                var e = registry.Get(id) ?? throw new InvalidOperationException($"Card {id} has no registered cut-in.");
                var card = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = e.Name ?? id.ToString(),
                    ["mode"] = e.Mode,
                    ["override_official"] = e.OverrideOfficial,
                };
                var dir = $"cards/{id}/";
                if (!e.IsCustom)
                {
                    card["payload"] = "borrow";
                    card["src"] = e.Source;
                }
                else
                {
                    card["template"] = e.Template ?? CutinNaming.DefaultTemplate;
                    var content = registry.ContentDir(e);
                    if (VideoFlipbook.ReadVideoSettings(content) is { } video)
                    {
                        card["payload"] = "video";
                        var ext = Path.GetExtension(video.SourceFile).ToLowerInvariant();
                        zip.CreateEntryFromFile(video.SourceFile, dir + "source" + ext, CompressionLevel.NoCompression);
                        zip.CreateEntryFromFile(Path.Combine(content, "video.json"), dir + "video.json");
                        card["video"] = "source" + ext;
                    }
                    else
                    {
                        card["payload"] = "content";
                        foreach (var f in CutinRegistry.ContentFiles)
                            zip.CreateEntryFromFile(Path.Combine(content, f), dir + f,
                                f.EndsWith(".png", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    }
                }
                cards.Add(card);
            }
            var manifest = new JsonObject
            {
                ["format"] = Format,
                ["version"] = FormatVersion,
                ["created"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["cards"] = cards,
            };
            using var s = zip.CreateEntry(ManifestEntry).Open();
            using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
            manifest.WriteTo(w);
        }
        File.Move(tmp, outPath, overwrite: true);
    }

    public static IReadOnlyList<CutinPackCard> ReadCards(string packPath)
    {
        using var zip = ZipFile.OpenRead(packPath);
        return ReadCards(zip);
    }

    private static List<CutinPackCard> ReadCards(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Not a .cutins pack (cutins.json is missing).");
        using var s = entry.Open();
        var root = JsonNode.Parse(s)?.AsObject() ?? throw new InvalidDataException("cutins.json is empty.");
        if (root["format"]?.GetValue<string>() != Format)
            throw new InvalidDataException("Not a .cutins pack.");
        if ((root["version"]?.GetValue<int>() ?? 0) > FormatVersion)
            throw new InvalidDataException("This pack was made by a newer version of the app.");
        var result = new List<CutinPackCard>();
        foreach (var n in root["cards"]?.AsArray() ?? [])
        {
            var o = n!.AsObject();
            var id = o["id"]!.GetValue<int>();
            if (id <= 0 || result.Any(c => c.CardId == id))
                throw new InvalidDataException($"Invalid card id {id} in the pack.");
            var payload = o["payload"]?.GetValue<string>() switch
            {
                "video" => CutinPackPayload.Video,
                "content" => CutinPackPayload.Content,
                "borrow" => CutinPackPayload.Borrow,
                var p => throw new InvalidDataException($"Unknown payload '{p}' for card {id}."),
            };
            var src = o["src"] is JsonValue sv ? sv.GetValue<int>() : (int?)null;
            if (payload == CutinPackPayload.Borrow && src is not > 0)
                throw new InvalidDataException($"Card {id} borrows a cut-in but names no source card.");
            result.Add(new CutinPackCard(id, o["name"]?.GetValue<string>() ?? id.ToString(), payload,
                o["template"] is JsonValue tv ? tv.GetValue<int>() : null, src, o["override_official"]?.GetValue<bool>() ?? false));
        }
        return result;
    }

    /// <summary>
    /// Extract a card's files to <paramref name="workDir"/>: the content files, or the source video + video.json.
    /// Returns the video path for video payloads.
    /// </summary>
    public static string? ExtractCard(string packPath, CutinPackCard card, string workDir)
    {
        using var zip = ZipFile.OpenRead(packPath);
        var listed = ReadCards(zip).FirstOrDefault(c => c.CardId == card.CardId) ?? throw new InvalidDataException($"Card {card.CardId} is not in the pack.");
        Directory.CreateDirectory(workDir);
        var dir = $"cards/{listed.CardId}/";
        void Copy(string name, string dst)
        {
            var e = zip.GetEntry(dir + name) ?? throw new InvalidDataException($"The pack is missing {dir}{name}.");
            e.ExtractToFile(Path.Combine(workDir, dst), overwrite: true);
        }
        switch (listed.Payload)
        {
            case CutinPackPayload.Content:
                foreach (var f in CutinRegistry.ContentFiles)
                    Copy(f, f);
                return null;
            case CutinPackPayload.Video:
                var video = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith(dir + "source.", StringComparison.Ordinal)
                    && VideoExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
                    ?? throw new InvalidDataException($"The pack has no video for card {listed.CardId}.");
                var videoName = "source" + Path.GetExtension(video.Name).ToLowerInvariant();
                Copy(video.Name, "pack-" + videoName);
                Copy("video.json", "pack-video.json");
                return Path.Combine(workDir, "pack-" + videoName);
            default:
                return null;
        }
    }

    /// <summary>Video settings stored with a video payload (extracted as pack-video.json).</summary>
    public static VideoCutinOptions ReadPackVideoOptions(string workDir)
    {
        var p = Path.Combine(workDir, "pack-video.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(p));
        var r = doc.RootElement;
        double D(string k, double d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        return new VideoCutinOptions
        {
            Seconds = Math.Clamp(D("seconds", 2.0), 0.5, 10),
            Fps = Math.Clamp(D("fps", 30), 1, 60),
            FrameHeight = (int)Math.Clamp(D("frame_height", 1080), 64, 2160),
            Start = Math.Max(0, D("start", 0)),
            End = r.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null,
            Overscan = Math.Clamp(D("overscan", 1.03), 0.5, 2),
            FadeIn = Math.Clamp(D("fade_in", 0.08), 0, 1),
            FadeOut = Math.Clamp(D("fade_out", 0.25), 0, 1),
        };
    }
}
