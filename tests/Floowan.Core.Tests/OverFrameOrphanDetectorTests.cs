using Floowan.Core.Assets;
using Floowan.Core.Models;
using Floowan.Core.Services;

namespace Floowan.Core.Tests;

public class OverFrameOrphanDetectorTests
{
    [Fact]
    public void IsRenderedAsOverframe_MatchesOfCanvasSizeOnly()
    {
        Assert.True(OverFrameOrphanDetector.IsRenderedAsOverframe(
            OverFrameConstants.Width, OverFrameConstants.Height));
        Assert.False(OverFrameOrphanDetector.IsRenderedAsOverframe(512, 512));
        Assert.False(OverFrameOrphanDetector.IsRenderedAsOverframe(704, 512));
        Assert.False(OverFrameOrphanDetector.IsRenderedAsOverframe(0, 0));
    }

    [Theory]
    [InlineData(false, false, false, false, false, OverFrameOrphanDetector.FixKind.None)]
    [InlineData(true, false, false, false, false, OverFrameOrphanDetector.FixKind.GateAndFloowanDb)]
    [InlineData(true, false, false, false, true, OverFrameOrphanDetector.FixKind.GateAndFloowanDb)]
    [InlineData(true, false, false, true, false, OverFrameOrphanDetector.FixKind.DbFlagFromGate)]
    [InlineData(true, false, false, true, true, OverFrameOrphanDetector.FixKind.FloowanDbInGate)]
    [InlineData(true, true, false, true, false, OverFrameOrphanDetector.FixKind.None)]
    [InlineData(true, true, false, true, true, OverFrameOrphanDetector.FixKind.FloowanMemoryOnly)]
    [InlineData(true, true, true, true, true, OverFrameOrphanDetector.FixKind.None)]
    [InlineData(true, true, true, false, false, OverFrameOrphanDetector.FixKind.GateAndFloowanDb)]
    public void Classify_CoversOrphanAndOfficialCases(
        bool liveIsOf,
        bool dbIsOverframe,
        bool dbFloowan,
        bool inGate,
        bool evidence,
        OverFrameOrphanDetector.FixKind expected)
    {
        var kind = OverFrameOrphanDetector.Classify(
            liveIsOverframeSize: liveIsOf,
            dbIsOverframe: dbIsOverframe,
            dbFloowanOverframe: dbFloowan,
            inGate: inGate,
            hasFloowanEvidence: evidence);
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_DoesNotMarkOfficialGatedOfAsFloowanWithoutEvidence()
    {
        // Live OF + already in gate + is_overframe, no Floowan backups → leave alone.
        var kind = OverFrameOrphanDetector.Classify(
            liveIsOverframeSize: true,
            dbIsOverframe: true,
            dbFloowanOverframe: false,
            inGate: true,
            hasFloowanEvidence: false);
        Assert.Equal(OverFrameOrphanDetector.FixKind.None, kind);
    }

    [Fact]
    public void OrphanRepairBatchResult_MessageIncludesCounts()
    {
        var result = OverFrameOrphanRepairBatchResult.Create(
            scanned: 100,
            liveOverframeFound: 12,
            fixedCount: 3,
            gateUpdated: 2,
            skipped: 9,
            failed: 0,
            warnings: Array.Empty<string>(),
            gateBundlePath: "aabbccdd");
        Assert.True(result.Success);
        Assert.Contains("3 fixed", result.Message);
        Assert.Contains("2 gate update", result.Message);
        Assert.Contains("100", result.Message);
        Assert.Contains("aabbccdd", result.Message);
    }
}
