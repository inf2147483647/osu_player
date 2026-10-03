using System.Globalization;
using System.IO.Compression;
using System.Text;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// 用真实谱面（.osz）离线评估/训练自动 BPM 与 kiai 检测。
    ///
    /// 每张 .osz 里都有作者手写的 [TimingPoints]，这就是标准答案：
    /// <c>time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects</c>
    /// —— 红线的 time/beatLength 给出 BPM 与首拍，effects 的最低位给出 kiai。
    /// 训练流程就是：解析标准答案 → 对同一段音频跑 <see cref="BeatAnalyser"/> → 算误差 → 调参。
    ///
    /// 必须跑在已经初始化过 BASS 的进程里（分析器靠 BASS 解码），因此由桌面端用
    /// <c>--train-beatmaps</c> 参数在启动后调用，见 OsuPlayer.Desktop/Program.cs。
    /// </summary>
    public static class BeatmapTraining
    {
        /// <summary>谱面里的一条 TimingPoint（只保留训练用得上的字段）。</summary>
        public sealed record TimingPoint(double TimeMs, double BeatLengthMs, bool Uninherited, bool Kiai);

        /// <summary>一张谱面的标准答案。</summary>
        public sealed record MapTruth(string Name, IReadOnlyList<TimingPoint> Points, string AudioFileName)
        {
            /// <summary>红线（变速点），按时间排序。</summary>
            public IReadOnlyList<TimingPoint> TempoPoints => Points.Where(p => p.Uninherited).ToList();
        }

        /// <summary>一张谱面的评估结果。</summary>
        public sealed record MapResult(string Name, double TruthBpm, double DetectedBpm, double BpmError,
            double PhaseErrorMs, bool OctaveError, int TruthKiaiCount, int DetectedKiaiCount, double KiaiIou);

        private const double kiai_boundary_tolerance_ms = 500;

        /// <summary>跑完整个目录并写出报告，返回报告文本。</summary>
        public static async Task<string> RunAsync(string directory, string reportPath, CancellationToken ct = default)
        {
            string[] maps = Directory.GetFiles(directory, "*.osz");
            Array.Sort(maps, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine($"# 自动 BPM / kiai 训练报告");
            sb.AppendLine($"# 谱面目录：{directory}");
            sb.AppendLine($"# 谱面数量：{maps.Length}");
            sb.AppendLine();

            var analyser = new BeatAnalyser();
            var results = new List<MapResult>();

            foreach (string map in maps)
            {
                ct.ThrowIfCancellationRequested();

                string name = Path.GetFileNameWithoutExtension(map);

                try
                {
                    (MapTruth truth, byte[] audio) = load(map);

                    if (truth.TempoPoints.Count == 0 || audio.Length == 0)
                    {
                        sb.AppendLine($"{name}\t跳过（没有红线或没有音频）");
                        continue;
                    }

                    BeatAnalysisDetails details = await analyser.AnalyseDetailedAsync(audio).ConfigureAwait(false);
                    MapResult result = compare(truth, details.Result, details.Duration);

                    results.Add(result);
                    sb.AppendLine(format(result));
                }
                catch (Exception e)
                {
                    sb.AppendLine($"{name}\t失败：{e.Message}");
                    AppLog.Beat.Add($"训练：{name} 分析失败：{e.Message}", LogLevel.Error, e);
                }
            }

            sb.AppendLine();
            sb.AppendLine(formatSummary(results));

            File.WriteAllText(reportPath, sb.ToString());
            AppLog.Beat.Add($"训练报告已写入：{reportPath}", LogLevel.Important);

            return sb.ToString();
        }

        /// <summary>解析一张 .osz：标准答案 + 音频字节。</summary>
        private static (MapTruth Truth, byte[] Audio) load(string path)
        {
            using var zip = ZipFile.OpenRead(path);

            ZipArchiveEntry osuEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                                       ?? throw new InvalidOperationException("压缩包里没有 .osu");

            string text;

            using (var reader = new StreamReader(osuEntry.Open(), Encoding.UTF8))
                text = reader.ReadToEnd();

            string audioFileName = "audio.mp3";
            var points = new List<TimingPoint>();
            bool inTimingPoints = false;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                if (line.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase))
                {
                    audioFileName = line["AudioFilename:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith('['))
                {
                    inTimingPoints = line.Equals("[TimingPoints]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inTimingPoints || line.Length == 0)
                    continue;

                string[] parts = line.Split(',');

                if (parts.Length < 8)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength)
                    || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int effects))
                    continue;

                points.Add(new TimingPoint(time, beatLength, parts[6].Trim() == "1", (effects & 1) == 1));
            }

            ZipArchiveEntry? audioEntry =
                zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, audioFileName, StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));

            byte[] audio = Array.Empty<byte>();

            if (audioEntry is not null)
            {
                using var stream = audioEntry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                audio = buffer.ToArray();
            }

            return (new MapTruth(Path.GetFileNameWithoutExtension(path), points, audioFileName), audio);
        }

        /// <summary>把检测结果和标准答案对齐打分。</summary>
        private static MapResult compare(MapTruth truth, BeatAnalysis detected, double songDurationMs)
        {
            IReadOnlyList<TimingPoint> tempos = truth.TempoPoints;

            // 谱面常有多条红线（中途变速）。取「覆盖时长最长」的那条作为整首歌的主速度做评估
            // —— 逐条挑最接近的会把某个只占几秒的变速段当成答案，指标就没有意义了。
            double truthBpm = dominantBpm(tempos, songDurationMs);

            // 主速度段的第一条红线给出首拍相位。
            TimingPoint phaseReference = tempos.FirstOrDefault(t => Math.Abs(60000.0 / t.BeatLengthMs - truthBpm) < 0.5, tempos[0]);

            double matchedBeatLength = phaseReference.BeatLengthMs > 0 ? phaseReference.BeatLengthMs : 60000.0 / truthBpm;
            double matchedTime = phaseReference.TimeMs;

            double bpmError = Math.Abs(detected.Bpm - truthBpm);

            bool octaveError = bpmError >= 1 && isOctaveRelated(detected.Bpm, truthBpm);

            // 首拍误差：折进半拍以内（差了整数拍在听觉上是等价的）。
            double phase = (detected.BeatPhaseMs - matchedTime) % matchedBeatLength;

            if (phase > matchedBeatLength / 2)
                phase -= matchedBeatLength;
            if (phase < -matchedBeatLength / 2)
                phase += matchedBeatLength;

            IReadOnlyList<(double Start, double? End)> truthKiai = kiaiSections(truth.Points);
            IReadOnlyList<(double Start, double? End)> detectedKiai = kiaiSections(detected);

            return new MapResult(truth.Name, truthBpm, detected.Bpm, bpmError, Math.Abs(phase),
                octaveError, truthKiai.Count, detectedKiai.Count,
                intersectionOverUnion(truthKiai, detectedKiai, tolerance: kiai_boundary_tolerance_ms));
        }

        /// <summary>覆盖时长最长的红线 BPM；没有有效红线时取第一条。</summary>
        private static double dominantBpm(IReadOnlyList<TimingPoint> tempos, double songDurationMs)
        {
            double bestBpm = tempos[0].BeatLengthMs > 0 ? 60000.0 / tempos[0].BeatLengthMs : 120;
            double bestDuration = -1;

            for (int i = 0; i < tempos.Count; i++)
            {
                if (tempos[i].BeatLengthMs <= 0)
                    continue;

                double end = i + 1 < tempos.Count ? tempos[i + 1].TimeMs : Math.Max(songDurationMs, tempos[i].TimeMs);
                double duration = end - tempos[i].TimeMs;

                if (duration > bestDuration)
                {
                    bestDuration = duration;
                    bestBpm = 60000.0 / tempos[i].BeatLengthMs;
                }
            }

            return bestBpm;
        }

        /// <summary>两个 BPM 是否互为整数倍（八度/三连音层面的节拍层级混淆）。</summary>
        private static bool isOctaveRelated(double detected, double truth)
        {
            foreach (double ratio in new[] { 2.0, 3.0, 4.0, 1 / 2.0, 1 / 3.0, 1 / 4.0, 2 / 3.0, 3 / 2.0 })
            {
                if (Math.Abs(detected - truth * ratio) < 1)
                    return true;
            }

            return false;
        }

        /// <summary>由 kiai 标记推出区间（effects 最低位为真即为 kiai 段）。</summary>
        private static IReadOnlyList<(double Start, double? End)> kiaiSections(IReadOnlyList<TimingPoint> points)
            => buildSections(points.OrderBy(p => p.TimeMs).Select(p => (p.TimeMs, p.Kiai)));

        /// <summary>把分析结果的 kiai 区间拆成「进入 / 离开」两个标记再统一成区间，保证和标准答案同一套算法。</summary>
        private static IReadOnlyList<(double Start, double? End)> kiaiSections(BeatAnalysis analysis)
        {
            var flags = new List<(double Time, bool Kiai)>();

            foreach (KiaiSection section in analysis.KiaiSections)
            {
                flags.Add((section.StartMs, true));
                flags.Add((section.EndMs, false));
            }

            return buildSections(flags.OrderBy(f => f.Time));
        }

        private static IReadOnlyList<(double Start, double? End)> buildSections(IEnumerable<(double Time, bool Kiai)> flags)
        {
            var sections = new List<(double, double?)>();
            bool active = false;
            double start = 0;

            foreach ((double time, bool kiai) in flags)
            {
                if (kiai == active)
                    continue;

                if (kiai)
                {
                    active = true;
                    start = time;
                }
                else
                {
                    active = false;
                    sections.Add((start, time));
                }
            }

            if (active)
                sections.Add((start, null));

            return sections;
        }

        /// <summary>kiai 区间的交并比：把边界按容差放宽后再算重叠，1 表示几乎完全一致。</summary>
        private static double intersectionOverUnion(IReadOnlyList<(double Start, double? End)> a,
                                                    IReadOnlyList<(double Start, double? End)> b, double tolerance)
        {
            if (a.Count == 0 && b.Count == 0)
                return 1;
            if (a.Count == 0 || b.Count == 0)
                return 0;

            double union = 0;
            double intersection = 0;

            foreach ((double start, double? end) in a)
                union += (end ?? start) - start;

            foreach ((double start, double? end) in b)
                union += (end ?? start) - start;

            foreach ((double aStart, double? aEnd) in a)
            {
                foreach ((double bStart, double? bEnd) in b)
                {
                    double overlap = Math.Min(aEnd ?? bStart + tolerance, bEnd ?? aStart + tolerance)
                                     - Math.Max(aStart - tolerance, bStart - tolerance);

                    if (overlap > 0)
                        intersection += overlap;
                }
            }

            return union > 0 ? Math.Clamp(intersection / union, 0, 1) : 0;
        }

        private static string format(MapResult r)
            => $"{r.Name}\tBPM {r.TruthBpm:0.##} → {r.DetectedBpm:0.##}（误差 {r.BpmError:0.##}{(r.OctaveError ? "，八度错" : string.Empty)}）"
               + $"\t首拍误差 {r.PhaseErrorMs:0}ms"
               + $"\tkiai {r.TruthKiaiCount} → {r.DetectedKiaiCount}（IoU {r.KiaiIou:0.00}）";

        private static string formatSummary(IReadOnlyList<MapResult> results)
        {
            if (results.Count == 0)
                return "没有可评估的谱面。";

            int bpmExact = results.Count(r => r.BpmError < 1);
            int octave = results.Count(r => r.OctaveError);
            double meanBpmError = results.Average(r => r.BpmError);
            double medianPhase = results.Select(r => r.PhaseErrorMs).OrderBy(v => v).ElementAt(results.Count / 2);
            double meanIou = results.Average(r => r.KiaiIou);
            int kiaiCountMatch = results.Count(r => r.TruthKiaiCount == r.DetectedKiaiCount);

            var sb = new StringBuilder();
            sb.AppendLine("== 汇总 ==");
            sb.AppendLine($"BPM 完全正确：{bpmExact}/{results.Count}（八度错 {octave} 张）");
            sb.AppendLine($"BPM 平均绝对误差：{meanBpmError:0.##}");
            sb.AppendLine($"首拍误差中位数：{medianPhase:0}ms");
            sb.AppendLine($"kiai 段数一致：{kiaiCountMatch}/{results.Count}；平均 IoU（边界容差 {kiai_boundary_tolerance_ms:0}ms）：{meanIou:0.00}");
            return sb.ToString();
        }
    }
}
