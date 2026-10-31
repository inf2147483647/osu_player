namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// Result of an automatic beat analysis of a song.
    /// </summary>
    /// <param name="Bpm">Estimated tempo in beats per minute.</param>
    /// <param name="Confidence">Tempo confidence (0..1), derived from peak separation / engine agreement.</param>
    /// <param name="BeatPhaseMs">Timing-point origin: the bar downbeat (the "1" of each 4/4 measure)
    /// the beat grid is anchored to. Placed at or before 0 so the grid covers the whole track;
    /// osu!'s measure-level effects (beatIndex % 4 == 0) land on real downbeats because of it.</param>
    /// <param name="DetectedBeats">Number of beat transients the BASS_FX engine found (0 when unavailable).</param>
    /// <param name="KiaiSections">Detected high-energy / repeated-chorus ("高潮") sections in milliseconds.</param>
    public sealed record BeatAnalysis(
        double Bpm,
        double Confidence,
        double BeatPhaseMs,
        int DetectedBeats,
        IReadOnlyList<KiaiSection> KiaiSections);

    /// <summary>
    /// A kiai ("高潮时段") section: a contiguous high-energy interval of the song.
    /// </summary>
    /// <param name="StartMs">Section start in track milliseconds.</param>
    /// <param name="EndMs">Section end in track milliseconds.</param>
    public readonly record struct KiaiSection(double StartMs, double EndMs);

    /// <summary>Diagnostic view of both tempo engines, used for tuning/validation.</summary>
    public sealed record BeatAnalysisDetails(
        BeatAnalysis Result,
        double EngineABpm,
        double EngineAConfidence,
        bool EngineAValid,
        double? EngineBBpm,
        int? EngineBBeats,
        double? EngineBIrregularity,
        int FrameCount,
        double Duration,
        double HopRate,
        string? EngineBRejectReason,
        double RawBpm,
        double RawBeatPhaseMs);
}
