using System.Globalization;
using System.IO.Compression;
using System.Text;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music.BeatSync.Onnx;
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

        /// <summary>
        /// 一张谱面的评估结果。
        ///
        /// <c>BarPhaseErrorMs</c> 是「小节偏差」：把 (检测相位 - 真值首拍) 对**小节长**取模后折进
        /// ±半小节。它和 <c>PhaseErrorMs</c> 的区别在于后者按拍长折叠，看不见整拍错位 —— 而
        /// osu! 的小节级特效依赖 <c>beatIndex % 4 == 0</c>，整拍错位正是「首拍错位」的来源。
        /// </summary>
        public sealed record MapResult(string Name, double TruthBpm, double DetectedBpm, double BpmError,
            double PhaseErrorMs, double BarPhaseErrorMs, bool OctaveError, int TruthKiaiCount, int DetectedKiaiCount, double KiaiIou);

        private const double kiai_boundary_tolerance_ms = 500;

        /// <summary>
        /// 把目录下所有 .osz 的 kiai ML 训练数据导出为每首一个 CSV（0.5s 单元 × 40 维原始特征 +
        /// 真值标签）。这是 <c>tools/kiai/train_kiai.py</c> 的输入；推理端不经过这个方法，
        /// 但特征由同一个 <see cref="KiaiFeatureBuilder"/> 构造。
        /// </summary>
        public static async Task<string> ExportKiaiDataAsync(string mapDirectory, string outputDirectory,
                                                             CancellationToken ct = default)
        {
            string[] maps = Directory.GetFiles(mapDirectory, "*.osz");
            Array.Sort(maps, StringComparer.OrdinalIgnoreCase);

            Directory.CreateDirectory(outputDirectory);

            int written = 0;
            int skipped = 0;
            int parallelism = Math.Max(1, Environment.ProcessorCount / 2);

            var counters = new int[maps.Length]; // 1=written, -1=skipped

            await Parallel.ForEachAsync(Enumerable.Range(0, maps.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            }, async (index, _) =>
            {
                string map = maps[index];
                string name = Path.GetFileNameWithoutExtension(map);

                try
                {
                    (MapTruth truth, byte[] audio) = load(map);

                    if (truth.TempoPoints.Count == 0 || audio.Length == 0)
                    {
                        counters[index] = -1;
                        return;
                    }

                    var rows = await new BeatAnalyser().ExtractKiaiFeaturesAsync(audio).ConfigureAwait(false);

                    if (rows.TimesMs.Count == 0)
                    {
                        counters[index] = -1;
                        return;
                    }

                    IReadOnlyList<(double Start, double? End)> truthKiai = kiaiSections(truth.Points);

                    double[][] raw = rows.Features.Select(f => (double[])f.Clone()).ToArray();
                    double[][] z = KiaiFeatureBuilder.Standardize(raw);
                    int checkCell = Math.Min(7, raw.Length - 1);
                    float[] checkWindow = KiaiFeatureBuilder.BuildWindow(z, checkCell);

                    string outputPath = Path.Combine(outputDirectory, $"{sanitizeFileName(name)}.csv");

                    var sb = new StringBuilder();
                    sb.AppendLine("# kiai feature export v1");
                    sb.AppendLine($"# song={name}");
                    sb.AppendLine($"# duration_s={KiaiFeatureBuilder.FormatDouble(rows.DurationS)}");
                    sb.AppendLine($"# cell_period_s={KiaiFeatureBuilder.CellPeriodS.ToString(CultureInfo.InvariantCulture)}");
                    sb.AppendLine($"# features={string.Join(",", KiaiFeatureBuilder.FeatureNames)}");
                    // 训练/推理一致性校验：C# 算好的「标准化后窗口向量」，Python 逐位复算并断言。
                    sb.AppendLine("# check," + checkCell + ","
                                  + string.Join(",", checkWindow.Select(v => KiaiFeatureBuilder.FormatDouble(v))));
                    sb.AppendLine("time_ms,label," + string.Join(",", KiaiFeatureBuilder.FeatureNames));

                    for (int cell = 0; cell < rows.TimesMs.Count; cell++)
                    {
                        double center = rows.TimesMs[cell] + KiaiFeatureBuilder.CellPeriodS * 500;
                        int label = truthKiai.Any(s => center >= s.Start && (s.End is null || center < s.End)) ? 1 : 0;

                        sb.Append(KiaiFeatureBuilder.FormatDouble(rows.TimesMs[cell]));
                        sb.Append(',').Append(label);

                        for (int d = 0; d < KiaiFeatureBuilder.RawDim; d++)
                            sb.Append(',').Append(KiaiFeatureBuilder.FormatDouble(rows.Features[cell][d]));

                        sb.AppendLine();
                    }

                    await File.WriteAllTextAsync(outputPath, sb.ToString(), ct).ConfigureAwait(false);
                    counters[index] = 1;
                }
                catch (Exception e)
                {
                    counters[index] = -1;
                    AppLog.Beat.Add($"导出：{name} 失败：{e.Message}", LogLevel.Error, e);
                }
            });

            written = counters.Count(c => c == 1);
            skipped = counters.Count(c => c == -1);

            string summary = $"kiai 特征导出完成：{written} 首 → {outputDirectory}（跳过/失败 {skipped} 首）。";
            AppLog.Beat.Add(summary, LogLevel.Important);
            return summary;
        }

        private static string sanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }

        /// <summary>跑完整个目录并写出报告，返回报告文本。</summary>
        /// <param name="directory">存放 .osz 的目录。</param>
        /// <param name="reportPath">报告写入路径。</param>
        /// <param name="useOnnx">
        /// true 时用 Beat This! 的 ONNX 结果覆盖「测速 + 拍点相位」（kiai 仍来自原生管线），
        /// 这样两种检测模式可以在同一个评测台上直接对比。
        /// </param>
        /// <param name="ct">取消令牌。</param>
        public static async Task<string> RunAsync(string directory, string reportPath, bool useOnnx = false, CancellationToken ct = default)
        {
            string[] maps = Directory.GetFiles(directory, "*.osz");
            Array.Sort(maps, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine($"# 自动 BPM / kiai 训练报告");
            sb.AppendLine($"# 谱面目录：{directory}");
            sb.AppendLine($"# BPM 检测模式：{(useOnnx ? "ONNX（Beat This!）" : "原生")}");
            // 报告必须自描述模型是否生效，否则很容易把「加了模型」和「没加模型」的两份
            // 报告混着比（踩过：仓库里那份 302 报告就因此对不上当前配置）。
            sb.AppendLine($"# BPM 层级模型：{(Onnx.BpmMl.IsAvailable ? "已启用" : "未安装（用原生 BPM）")}");
            sb.AppendLine($"# 谱面数量：{maps.Length}");
            sb.AppendLine();

            // 批量评测**串行**执行。原先是 ProcessorCount/2 并行，但 BASS 的解码流是全局资源，
            // 而 BpmFeatureBuilder.OnsetEnvelope 内部已经用了 Parallel.For —— 外层再并行会在 2 物理核上
            // 超订，并与全局 BASS 状态竞争：实测 100 张跑到 82 张时 Bass.CreateStream 开始返回
            // error Init，剩下的一路失败、整批白跑。串行慢一些，但稳定且结果可信。
            const int parallelism = 1;

            var lines = new string[maps.Length];
            var collected = new MapResult?[maps.Length];
            var results = new List<MapResult>();

            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, maps.Length), new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = ct,
                }, async (index, _) =>
                {
                    string map = maps[index];
                    string name = Path.GetFileNameWithoutExtension(map);

                    try
                    {
                        (MapTruth truth, byte[] audio) = load(map);

                        if (truth.TempoPoints.Count == 0 || audio.Length == 0)
                        {
                            lines[index] = $"{name}\t跳过（没有红线或没有音频）";
                            return;
                        }

                        // 每个任务一个实例：不去赌分析器是否无状态，构造开销可忽略。
                        BeatAnalysisDetails details = await new BeatAnalyser().AnalyseDetailedAsync(audio).ConfigureAwait(false);

                        // 与播放路径共用同一处合并逻辑，保证「评测里量到的」就是「实际用的」。
                        BeatAnalysis detected = details.Result;

                        if (useOnnx && OnnxBeatTracker.TryOverride(audio, detected, out BeatAnalysis onnxResult))
                            detected = onnxResult;

                        MapResult result = compare(truth, detected, details.Duration);

                        collected[index] = result;
                        lines[index] = format(result);
                    }
                    catch (Exception e)
                    {
                        lines[index] = $"{name}\t失败：{e.Message}";
                        AppLog.Beat.Add($"训练：{name} 分析失败：{e.Message}", LogLevel.Error, e);
                    }
                });
            }
            catch (Exception e)
            {
                // 批量跑到一半炸掉也要留下已完成的证据：报告写在 finally 里，不让整批结果消失。
                AppLog.Beat.Add($"批量评测中断（已完成 {collected.Count(c => c is not null)}/{maps.Length} 张），"
                                + $"仍写出部分报告：{e.Message}", LogLevel.Error, e);
            }

            foreach (string line in lines)
            {
                if (line is { Length: > 0 })
                    sb.AppendLine(line);
            }

            foreach (MapResult? result in collected)
            {
                if (result is not null)
                    results.Add(result);
            }

            sb.AppendLine();
            sb.AppendLine(formatSummary(results));

            File.WriteAllText(reportPath, sb.ToString());
            AppLog.Beat.Add($"训练报告已写入：{reportPath}", LogLevel.Important);

            return sb.ToString();
        }

        /// <summary>
        /// 单曲 BPM 推理（任务书阶段 6 的 CLI 示例）：输出 BPM 与分阶段耗时。
        /// 默认跑 3 次，用来把冷启动（JIT/模型加载）和热路径分开看。
        /// </summary>
        public static async Task<string> DetectBpmAsync(string file, int iterations = 3)
        {
            byte[] audio = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"# 单曲 BPM 推理：{file}");

            for (int i = 0; i < Math.Max(1, iterations); i++)
            {
                var total = System.Diagnostics.Stopwatch.StartNew();
                var stage = System.Diagnostics.Stopwatch.StartNew();

                (float[] samples, int sampleRate) = BeatAnalyser.DecodeMonoSamples(audio);
                long decodeMs = stage.ElapsedMilliseconds;

                stage.Restart();
                double[] onset = BpmFeatureBuilder.OnsetEnvelope(samples, sampleRate, out double fps);
                long onsetMs = stage.ElapsedMilliseconds;

                stage.Restart();
                double[] salience = BpmFeatureBuilder.Salience(onset, fps);
                var candidates = BpmFeatureBuilder.Candidates(salience);
                long salienceMs = stage.ElapsedMilliseconds;

                stage.Restart();
                bool ok = Onnx.BpmMl.TryDetect(salience, candidates, out double bpm);
                long modelMs = stage.ElapsedMilliseconds;
                total.Stop();

                sb.AppendLine($"第 {i + 1} 次：BPM {(ok ? bpm.ToString("0.##") : "-")}"
                              + $"  总 {total.ElapsedMilliseconds}ms"
                              + $"［解码 {decodeMs}｜特征 {onsetMs}｜显著度 {salienceMs}｜模型 {modelMs}］"
                              + $"  基线候选 {(candidates.Count > 0 ? candidates[0].ToString("0.##") : "-")}"
                              + $"  {samples.Length / (double)sampleRate:0.0}s / {sampleRate}Hz");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Ballroom 风格的全局 BPM 评测：目录下递归找 *.wav，真值取同名 .bpm（找不到则去
        /// <paramref name="labelDirectory"/> 按文件名取）。同时给出「显著度最强候选」（传统基线）
        /// 与 ML 模型两条结果，用来验证 C# 特征管线与 Python 训练口径一致 —— 若两者对不上，
        /// 说明移植有偏差，必须先修口径再看模型。
        /// </summary>
        public static async Task<string> EvaluateBpmAsync(string audioDirectory, string? labelDirectory,
                                                          string reportPath, CancellationToken ct = default)
        {
            string[] files = Directory.GetFiles(audioDirectory, "*.wav", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            int parallelism = Math.Max(1, Environment.ProcessorCount / 2);
            var lines = new string[files.Length];
            var collected = new List<BpmEvalRow>();

            await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            }, async (index, _) =>
            {
                string file = files[index];
                string name = Path.GetFileNameWithoutExtension(file);

                try
                {
                    string? labelPath = Path.ChangeExtension(file, ".bpm");

                    if (!File.Exists(labelPath))
                    {
                        labelPath = labelDirectory is null ? null : Path.Combine(labelDirectory, name + ".bpm");

                        if (labelPath is not null && !File.Exists(labelPath))
                            labelPath = null;
                    }

                    if (labelPath is null)
                    {
                        lines[index] = $"{name}\t跳过（无 .bpm 真值）";
                        return;
                    }

                    double truth = double.Parse(File.ReadAllText(labelPath).Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    byte[] audio = await File.ReadAllBytesAsync(file, _).ConfigureAwait(false);

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    (float[] samples, int sampleRate) = BeatAnalyser.DecodeMonoSamples(audio);

                    double[] onset = BpmFeatureBuilder.OnsetEnvelope(samples, sampleRate, out double fps);
                    double[] salience = BpmFeatureBuilder.Salience(onset, fps);
                    var candidates = BpmFeatureBuilder.Candidates(salience);
                    double baseline = candidates.Count > 0 ? candidates[0] : 0;

                    bool ok = Onnx.BpmMl.TryDetect(salience, candidates, out double modelBpm);
                    watch.Stop();

                    var row = new BpmEvalRow(name, truth, baseline, ok ? modelBpm : double.NaN,
                        watch.ElapsedMilliseconds, candidates.Count);
                    collected.Add(row);

                    AppLog.Beat.Add($"BPM 评测 {name}：真值 {truth:0.##} 基线 {baseline:0.##} "
                                    + $"模型 {(ok ? modelBpm.ToString("0.##") : "-")} {watch.ElapsedMilliseconds}ms");

                    lines[index] = $"{name}\t真值 {truth:0.##}\t基线 {baseline:0.##}\t"
                                   + $"模型 {(ok ? modelBpm.ToString("0.##") : "-")}\t{watch.ElapsedMilliseconds}ms";
                }
                catch (Exception e)
                {
                    lines[index] = $"{name}\t失败：{e.Message}";
                    AppLog.Beat.Add($"BPM 评测 {name} 失败：{e.Message}", LogLevel.Error, e);
                }
            });

            var sb = new StringBuilder();
            sb.AppendLine("# 全局 BPM 评测报告（Ballroom）");
            sb.AppendLine($"# 音频目录：{audioDirectory}");
            sb.AppendLine($"# 真值目录：{labelDirectory ?? "（与音频同目录的 .bpm）"}");
            sb.AppendLine($"# 模型：{(Onnx.BpmMl.IsAvailable ? Onnx.BpmMl.Status : "未安装")}");
            sb.AppendLine($"# 曲目数：{collected.Count}");
            sb.AppendLine();

            foreach (string line in lines)
            {
                if (line is { Length: > 0 })
                    sb.AppendLine(line);
            }

            sb.AppendLine();
            sb.AppendLine(formatBpmSummary(collected));

            File.WriteAllText(reportPath, sb.ToString());
            AppLog.Beat.Add($"BPM 评测报告已写入：{reportPath}", LogLevel.Important);

            return sb.ToString();
        }

        private readonly record struct BpmEvalRow(string Name, double Truth, double Baseline, double Model,
                                                 long ElapsedMs, int Candidates);

        private static bool acc1(double pred, double truth) => Math.Abs(pred - truth) <= 0.04 * truth;

        private static bool acc2(double pred, double truth)
        {
            double best = double.MaxValue;

            foreach (double k in new[] { 1.0, 2.0, 0.5, 3.0, 1.0 / 3 })
                best = Math.Min(best, Math.Abs(pred * k - truth));

            return best <= 0.04 * truth;
        }

        private static string formatBpmSummary(List<BpmEvalRow> rows)
        {
            var sb = new StringBuilder();

            if (rows.Count == 0)
                return "（没有可用样本）";

            int baselineAcc1 = 0, modelAcc1 = 0, baselineAcc2 = 0, modelAcc2 = 0;
            int modelAnswered = 0;
            long totalMs = 0, maxMs = 0;

            foreach (BpmEvalRow row in rows)
            {
                if (acc1(row.Baseline, row.Truth))
                    baselineAcc1++;

                if (acc2(row.Baseline, row.Truth))
                    baselineAcc2++;

                totalMs += row.ElapsedMs;

                if (row.ElapsedMs > maxMs)
                    maxMs = row.ElapsedMs;

                if (double.IsNaN(row.Model))
                    continue;

                modelAnswered++;

                if (acc1(row.Model, row.Truth))
                    modelAcc1++;

                if (acc2(row.Model, row.Truth))
                    modelAcc2++;
            }

            int n = rows.Count;
            sb.AppendLine("== 汇总 ==");
            sb.AppendLine($"样本数：{n}（模型给出结果 {modelAnswered}）");
            sb.AppendLine($"基线 Acc1：{(double)baselineAcc1 / n:0.000}（{baselineAcc1}/{n}）  "
                          + $"模型 Acc1：{(double)modelAcc1 / n:0.000}（{modelAcc1}/{n}）");
            sb.AppendLine($"基线 Acc2：{(double)baselineAcc2 / n:0.000}（{baselineAcc2}/{n}）  "
                          + $"模型 Acc2：{(double)modelAcc2 / n:0.000}（{modelAcc2}/{n}）");
            sb.AppendLine($"单曲平均推理（解码+特征+模型）：{(double)totalMs / n:0.0}ms  最大 {maxMs}ms");
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

            // 小节偏差：改成按小节折叠，于是「差了整数拍」不再被吞掉 —— 这正是首拍错位的量。
            // 按 osu! 约定，红线通常落在小节强拍上，所以真值首拍可以当作小节原点。
            double barLength = matchedBeatLength * 4;
            double barPhase = (detected.BeatPhaseMs - matchedTime) % barLength;

            if (barPhase > barLength / 2)
                barPhase -= barLength;
            if (barPhase < -barLength / 2)
                barPhase += barLength;

            IReadOnlyList<(double Start, double? End)> truthKiai = kiaiSections(truth.Points);
            IReadOnlyList<(double Start, double? End)> detectedKiai = kiaiSections(detected);

            return new MapResult(truth.Name, truthBpm, detected.Bpm, bpmError, Math.Abs(phase), Math.Abs(barPhase),
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
               + $"\t首拍误差 {r.PhaseErrorMs:0}ms\t小节偏差 {r.BarPhaseErrorMs:0}ms"
               + $"\tkiai {r.TruthKiaiCount} → {r.DetectedKiaiCount}（IoU {r.KiaiIou:0.00}）";

        private static string formatSummary(IReadOnlyList<MapResult> results)
        {
            if (results.Count == 0)
                return "没有可评估的谱面。";

            int bpmExact = results.Count(r => r.BpmError < 1);
            int octave = results.Count(r => r.OctaveError);
            double meanBpmError = results.Average(r => r.BpmError);
            double medianPhase = results.Select(r => r.PhaseErrorMs).OrderBy(v => v).ElementAt(results.Count / 2);
            double medianBarPhase = results.Select(r => r.BarPhaseErrorMs).OrderBy(v => v).ElementAt(results.Count / 2);
            double meanIou = results.Average(r => r.KiaiIou);
            int kiaiCountMatch = results.Count(r => r.TruthKiaiCount == r.DetectedKiaiCount);

            var sb = new StringBuilder();
            sb.AppendLine("== 汇总 ==");
            sb.AppendLine($"BPM 完全正确：{bpmExact}/{results.Count}（八度错 {octave} 张）");
            sb.AppendLine($"BPM 平均绝对误差：{meanBpmError:0.##}");
            sb.AppendLine($"首拍误差中位数：{medianPhase:0}ms");
            sb.AppendLine($"小节偏差中位数：{medianBarPhase:0}ms（≥半拍说明整拍错位）");
            sb.AppendLine($"kiai 段数一致：{kiaiCountMatch}/{results.Count}；平均 IoU（边界容差 {kiai_boundary_tolerance_ms:0}ms）：{meanIou:0.00}");
            return sb.ToString();
        }
    }
}
