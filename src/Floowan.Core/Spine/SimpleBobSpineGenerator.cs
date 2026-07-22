using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;

namespace Floowan.Core.Spine;

/// <summary>
/// Generates a minimal Spine 4.0 skeleton that gently pans/bobs a single region attachment.
/// No Spine editor or Python required.
/// </summary>
public sealed class SimpleBobSpineGenerator : ISpineCutInGenerator
{
    public SpineCutInAssets FromSingleImage(
        string imagePath,
        string assetBaseName,
        SpineCutInOptions? options = null,
        string? textureName = null,
        string? skeletonName = null,
        string? atlasName = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("Cut-in image was not found.", imagePath);
        if (string.IsNullOrWhiteSpace(assetBaseName))
            throw new ArgumentException("Asset base name is required.", nameof(assetBaseName));

        options ??= new SpineCutInOptions();
        var texName = string.IsNullOrWhiteSpace(textureName) ? assetBaseName : textureName!;
        var skelName = string.IsNullOrWhiteSpace(skeletonName) ? assetBaseName + "JS" : skeletonName!;
        var atlName = string.IsNullOrWhiteSpace(atlasName) ? texName : atlasName!;

        using var image = Image.Load(imagePath);
        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Cut-in image has invalid dimensions.");

        var attachment = "art";
        var atlas = BuildAtlas(texName, width, height, attachment);
        var json = BuildSkeletonJson(width, height, attachment, options);

        return new SpineCutInAssets
        {
            AssetBaseName = assetBaseName,
            TextureName = texName,
            SkeletonName = skelName,
            AtlasName = atlName,
            AtlasText = atlas,
            SkeletonJson = json,
            TexturePngPath = Path.GetFullPath(imagePath),
            Width = width,
            Height = height
        };
    }

    public SpineCutInAssets FromImageSequence(
        IReadOnlyList<string> imagePaths,
        string assetBaseName,
        SpineCutInOptions? options = null,
        string? textureName = null,
        string? skeletonName = null,
        string? atlasName = null)
    {
        throw new NotImplementedException(
            "Image-sequence cut-ins are reserved for a later release (spine_sequence-style attachment switching).");
    }

    private static string BuildAtlas(string pageName, int width, int height, string regionName)
    {
        // libgdx/Spine atlas format (one page, one region covering the full page).
        var sb = new StringBuilder();
        sb.Append(pageName).Append(".png").Append('\n');
        sb.Append("size: ").Append(width).Append(',').Append(height).Append('\n');
        sb.Append("format: RGBA8888").Append('\n');
        sb.Append("filter: Linear,Linear").Append('\n');
        sb.Append("repeat: none").Append('\n');
        sb.Append(regionName).Append('\n');
        sb.Append("  rotate: false").Append('\n');
        sb.Append("  xy: 0, 0").Append('\n');
        sb.Append("  size: ").Append(width).Append(", ").Append(height).Append('\n');
        sb.Append("  orig: ").Append(width).Append(", ").Append(height).Append('\n');
        sb.Append("  offset: 0, 0").Append('\n');
        sb.Append("  index: -1").Append('\n');
        return sb.ToString();
    }

    private static string BuildSkeletonJson(int width, int height, string attachment, SpineCutInOptions options)
    {
        var skelW = Math.Max(1, (int)Math.Round(width * options.SkeletonSizeScale));
        var skelH = Math.Max(1, (int)Math.Round(height * options.SkeletonSizeScale));
        var framerate = Math.Clamp(options.Framerate, 1f, 60f);
        var duration = Math.Clamp(options.DurationSeconds, 0.5f, 10f);
        var frameCount = Math.Max(4, (int)Math.Round(duration * framerate));
        // Keep keyframe count modest; Spine interpolates between translates.
        var keyCount = Math.Min(frameCount, 45);

        var translates = new List<object>(keyCount);
        for (var i = 0; i < keyCount; i++)
        {
            var t = i / (float)framerate;
            var phase = i / (float)(keyCount - 1) * MathF.PI * 2f;
            var x = MathF.Sin(phase) * options.AmplitudeX;
            var y = MathF.Sin(phase * 2f) * options.AmplitudeY * 0.5f;
            translates.Add(new Dictionary<string, object>
            {
                ["time"] = RoundTime(t),
                ["x"] = RoundCoord(x),
                ["y"] = RoundCoord(y)
            });
        }

        // Close the loop back to origin so playback is seamless.
        translates.Add(new Dictionary<string, object>
        {
            ["time"] = RoundTime(keyCount / framerate),
            ["x"] = 0,
            ["y"] = 0
        });

        var root = new Dictionary<string, object?>
        {
            ["skeleton"] = new Dictionary<string, object>
            {
                ["hash"] = "floowan-bob",
                ["spine"] = "4.0.64",
                ["x"] = 0,
                ["y"] = 0,
                ["width"] = skelW,
                ["height"] = skelH,
                ["images"] = "",
                ["audio"] = ""
            },
            ["bones"] = new object[]
            {
                new Dictionary<string, object> { ["name"] = "root" }
            },
            ["slots"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "art",
                    ["bone"] = "root",
                    ["attachment"] = attachment
                }
            },
            ["skins"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "default",
                    ["attachments"] = new Dictionary<string, object>
                    {
                        ["art"] = new Dictionary<string, object>
                        {
                            [attachment] = new Dictionary<string, object>
                            {
                                ["width"] = skelW,
                                ["height"] = skelH
                            }
                        }
                    }
                }
            },
            ["animations"] = new Dictionary<string, object>
            {
                [options.AnimationName] = new Dictionary<string, object>
                {
                    ["bones"] = new Dictionary<string, object>
                    {
                        ["root"] = new Dictionary<string, object>
                        {
                            ["translate"] = translates
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static object RoundTime(float time)
    {
        var floored = MathF.Floor(time * 10000f) / 10000f;
        if (MathF.Abs(floored - MathF.Round(floored)) < 0.00005f)
            return (int)MathF.Round(floored);
        return Math.Round(floored, 4);
    }

    private static object RoundCoord(float value)
    {
        var rounded = Math.Round(value, 2);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.0001)
            return (int)Math.Round(rounded);
        return rounded;
    }
}
