using Floowan.Core.Imaging;

namespace Floowan.Core.Backup;

/// <summary>
/// Serializable Custom OF editor stage (layers + transforms) saved beside card backups
/// so an already over-framed card can be re-opened for editing.
/// </summary>
public sealed class CustomOverframeStageState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Frame style name (solid or OfGradient*); compose maps via <see cref="CardFrameTemplates.ToOfGradientStyle"/>.</summary>
    public string FrameStyle { get; set; } = nameof(CardFrameStyle.Effect);

    public float SubjectScale { get; set; } = 1.5f;
    public int SubjectOffsetX { get; set; }
    public int SubjectOffsetY { get; set; }

    public float BackgroundScale { get; set; } = 1.0f;
    public int BackgroundOffsetX { get; set; }
    public int BackgroundOffsetY { get; set; }

    public bool BackgroundIsCardArt { get; set; }
    public bool SubjectIsFromCardArt { get; set; }

    public bool HasBackground { get; set; }
    public bool HasSubject { get; set; }
}
