namespace Floowan.Core.Spine;

/// <summary>Options for generating a simple Master Duel cut-in Spine package.</summary>
public sealed class SpineCutInOptions
{
    /// <summary>Frames per second used for bob timing (Master Duel is smooth around 20–25).</summary>
    public float Framerate { get; init; } = 22f;

    /// <summary>Loop duration in seconds.</summary>
    public float DurationSeconds { get; init; } = 2f;

    /// <summary>Horizontal bob amplitude in Spine units (pixels at scale 1).</summary>
    public float AmplitudeX { get; init; } = 24f;

    /// <summary>Vertical bob amplitude in Spine units.</summary>
    public float AmplitudeY { get; init; } = 36f;

    /// <summary>
    /// Multiplier applied to skeleton width/height so the cut-in renders larger in-game
    /// (community guides often use ~5×).
    /// </summary>
    public float SkeletonSizeScale { get; init; } = 5f;

    /// <summary>Animation name written into the Spine JSON.</summary>
    public string AnimationName { get; init; } = "animation";
}
