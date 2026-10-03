namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// One line of a .osu <c>[TimingPoints]</c> section, kept with osu!'s semantics:
    /// <c>time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects</c>.
    ///
    ///  - <see cref="Uninherited"/> (红线) = a tempo change: <see cref="BeatLengthMs"/> is the length of
    ///    one beat in milliseconds, so BPM = 60000 / BeatLengthMs;
    ///  - an inherited line (绿线) carries scroll-speed / kiai effects only and has a negative beat length.
    /// </summary>
    public sealed record BeatTimingLine(double TimeMs, double BeatLengthMs, int Meter, bool Uninherited, bool Kiai);

    /// <summary>
    /// The timeline a beatmap author wrote into the .osu file: every tempo change (multiple time/BPM),
    /// the first-beat offset and the kiai flags. Feeding this to the beat-sync provider makes the
    /// player's effects land exactly where the mapper intended, with no audio analysis required.
    /// </summary>
    public sealed record BeatmapTiming(IReadOnlyList<BeatTimingLine> Lines)
    {
        /// <summary>首拍延迟：第一个红线（tempo）的时间，也就是谱面 offset。</summary>
        public double? FirstBeatMs
        {
            get
            {
                foreach (BeatTimingLine line in Lines)
                {
                    if (line.Uninherited)
                        return line.TimeMs;
                }

                return null;
            }
        }

        /// <summary>All tempo changes, in file order (multiple time/BPM).</summary>
        public IEnumerable<BeatTimingLine> TempoLines => Lines.Where(l => l.Uninherited);

        /// <summary>kiai 区间（由 effects 位的状态变化推出）；末尾仍开着时 <c>EndMs</c> 为 null。</summary>
        public IReadOnlyList<(double StartMs, double? EndMs)> KiaiSections
        {
            get
            {
                var sections = new List<(double, double?)>();
                bool active = false;
                double start = 0;

                foreach (BeatTimingLine line in Lines.OrderBy(l => l.TimeMs))
                {
                    if (line.Kiai == active)
                        continue;

                    if (line.Kiai)
                    {
                        active = true;
                        start = line.TimeMs;
                    }
                    else
                    {
                        active = false;
                        sections.Add((start, line.TimeMs));
                    }
                }

                if (active)
                    sections.Add((start, null));

                return sections;
            }
        }

        /// <summary>BPM of a tempo line (60000 / beat length); 0 for inherited lines.</summary>
        public static double BpmOf(BeatTimingLine line) => line.BeatLengthMs > 0 ? 60000.0 / line.BeatLengthMs : 0;
    }
}
