using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;
using KiaiCell = OsuPlayer.Game.Music.BeatSync.KiaiFeatureBuilder.Cell;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// kiai 帧分类器推理：0.5s 单元 × 40 维原始特征 → 逐单元 z-score → ±14 单元上下文窗口
    /// （29×40=1160）→ sklearn MLP 导出的 ONNX 给出 kiai 概率 → 游程/合并/最短段/边界吸附后处理。
    ///
    /// 模型由 <c>tools/kiai/train_kiai.py</c> 用 302 张真实谱面的 [TimingPoints] kiai 真值按
    /// GroupKFold 训练导出；模型缺失或任何环节异常时 <see cref="TryDetect"/> 返回 false，
    /// 调用方退回能量+chroma 启发式，最坏情况等于旧行为。
    /// </summary>
    public static class KiaiMl
    {
        private sealed record Loaded(InferenceSession Session, string InputName, KiaiConfig Config);

        private static readonly Lazy<Loaded?> model = new(loadModel, LazyThreadSafetyMode.ExecutionAndPublication);

        private static bool availabilityLogged;

        /// <summary>模型是否就绪（设置页/日志用）。</summary>
        public static bool IsAvailable => model.Value is not null;

        /// <summary>
        /// 用 ML 模型检测 kiai 段。模型不可用或推理失败返回 false（<paramref name="sections"/> 为空），
        /// 由调用方回退启发式。
        /// </summary>
        internal static bool TryDetect(IReadOnlyList<BeatAnalyser.Frame> frames, double durationS,
                                       out IReadOnlyList<KiaiSection> sections)
        {
            sections = Array.Empty<KiaiSection>();

            Loaded? m = model.Value;

            if (m is null)
                return false;

            try
            {
                if (durationS <= m.Config.MinCells * KiaiFeatureBuilder.CellPeriodS)
                {
                    sections = Array.Empty<KiaiSection>();
                    return true;
                }

                List<KiaiCell> cells = KiaiFeatureBuilder.Build(frames, durationS);

                if (cells.Count == 0)
                    return false;

                double[][] raw = cells.Select(c => (double[])c.Raw.Clone()).ToArray();
                double[][] z = KiaiFeatureBuilder.Standardize(raw);

                int n = cells.Count;
                int windowDim = (m.Config.WindowRadius * 2 + 1) * m.Config.RawDim;

                if (windowDim != KiaiFeatureBuilder.WindowDim)
                {
                    AppLog.Beat.Add($"kiai 模型输入维度 {windowDim} 与特征管线 {KiaiFeatureBuilder.WindowDim} 不一致，"
                                    + "可能是用旧版特征训练的模型，回退启发式。", LogLevel.Error);
                    return false;
                }

                var input = new DenseTensor<float>(new[] { n, windowDim });

                for (int t = 0; t < n; t++)
                {
                    float[] window = KiaiFeatureBuilder.BuildWindow(z, t);
                    window.CopyTo(input.Buffer.Slice(t * windowDim, windowDim));
                }

                float[] probabilities;

                using (var results = m.Session.Run(new[]
                       {
                           NamedOnnxValue.CreateFromTensor(m.InputName, input),
                       }))
                {
                    probabilities = extractPositiveProbabilities(results, n);
                }

                if (probabilities.Length != n)
                    return false;

                sections = sectionsFromProbabilities(cells, probabilities, m.Config, durationS);
                return true;
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"kiai ML 推理异常，回退启发式：{e.Message}", LogLevel.Error, e);
                return false;
            }
        }

        /// <summary>
        /// 从 ONNX 输出里找扁平长度为 2N 的概率张量（skl2onnx zipmap=False 的 "probabilities"，
        /// 行主序 [N,2]）取第 1 列；找不到再找 N 个 0/1 的输出标签。
        /// </summary>
        private static float[] extractPositiveProbabilities(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int n)
        {
            float[][] floatOutputs = results
                .Where(v => v.Value is Tensor<float>)
                .Select(v => v.AsTensor<float>().ToArray())
                .ToArray();

            // [N,2] 概率张量：先按名字挑，再退而取任何长度 = 2N 的。
            float[]? paired = floatOutputs.FirstOrDefault(o => o.Length == n * 2);

            if (paired is not null)
            {
                var probs = new float[n];

                for (int i = 0; i < n; i++)
                    probs[i] = paired[i * 2 + 1];

                return probs;
            }

            float[]? single = floatOutputs.FirstOrDefault(o => o.Length == n);

            if (single is not null)
                return single;

            long[]? labels = results
                .Where(v => v.Value is Tensor<long>)
                .Select(v => v.AsTensor<long>().ToArray())
                .FirstOrDefault(o => o.Length == n);

            if (labels is not null)
            {
                var probs = new float[n];

                for (int i = 0; i < n; i++)
                    probs[i] = labels[i] > 0 ? 1f : 0f;

                return probs;
            }

            throw new InvalidDataException("ONNX 输出里找不到 2N 概率或 N 标签张量。");
        }

        /// <summary>
        /// 概率 → 段落：阈值游程 → 合并短空隙 → 最短段过滤 → 结构边界吸附 → 两侧 padding。
        /// 与启发式 <c>buildKiaiSections</c> 的后处理保持同一套规则（5s 最短、3s 合并、1s 外扩），
        /// 训练脚本在 OOF 预测上用完全相同的参数选阈值。
        /// </summary>
        private static IReadOnlyList<KiaiSection> sectionsFromProbabilities(
            IReadOnlyList<KiaiCell> cells, IReadOnlyList<float> probabilities, KiaiConfig config, double durationS)
        {
            int n = cells.Count;

            var runs = new List<(int first, int last)>();
            int? runStart = null;

            for (int i = 0; i < n; i++)
            {
                bool active = probabilities[i] >= config.Threshold;

                if (active && runStart is null)
                    runStart = i;
                else if (!active && runStart is not null)
                {
                    runs.Add((runStart.Value, i - 1));
                    runStart = null;
                }
            }

            if (runStart is not null)
                runs.Add((runStart.Value, n - 1));

            if (runs.Count == 0)
                return Array.Empty<KiaiSection>();

            var merged = new List<(int first, int last)> { runs[0] };

            for (int i = 1; i < runs.Count; i++)
            {
                var previous = merged[^1];
                var current = runs[i];

                if (current.first - previous.last - 1 <= config.MergeGapCells)
                    merged[^1] = (previous.first, current.last);
                else
                    merged.Add(current);
            }

            List<int> boundaries = config.SnapBoundaries
                ? BeatAnalyser.DetectStructuralBoundaries(cells)
                : new List<int>();

            var sections = new List<KiaiSection>();

            foreach ((int first, int last) in merged)
            {
                if (last - first + 1 < config.MinCells)
                    continue;

                int snappedFirst = config.SnapBoundaries ? snapEdge(first, boundaries, snapBackward: true) : first;
                int snappedLast = config.SnapBoundaries ? snapEdge(last, boundaries, snapBackward: false) : last;

                int startCell = Math.Max(0, snappedFirst - config.PaddingCells);
                int endCell = Math.Min(n - 1, snappedLast + 1 + config.PaddingCells);

                double startMs = Math.Max(0, startCell * KiaiFeatureBuilder.CellPeriodS * 1000);
                double endMs = Math.Min(durationS * 1000, endCell * KiaiFeatureBuilder.CellPeriodS * 1000);

                sections.Add(new KiaiSection(startMs, endMs));
            }

            return sections;
        }

        private const int snap_window_cells = 6; // ±3s，与启发式一致

        private static int snapEdge(int cell, IReadOnlyList<int> boundaries, bool snapBackward)
        {
            int best = cell;
            int bestDistance = snap_window_cells;

            foreach (int boundary in boundaries)
            {
                int distance = snapBackward ? cell - boundary : boundary - cell;

                if (distance is >= 0 and <= snap_window_cells && distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = boundary;
                }
            }

            return best;
        }

        private static Loaded? loadModel()
        {
            string? path = KiaiAssets.ModelPath;

            if (path is null)
            {
                logOnce("kiai ML 模型未安装（models/kiai/kiai.onnx），使用启发式高潮检测。");
                return null;
            }

            try
            {
                var options = new SessionOptions
                {
                    // 同 Beat This!：2 物理核，顺序执行，不抢渲染/混音线程。
                    IntraOpNumThreads = 2,
                    InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };

                var session = new InferenceSession(path, options);
                string inputName = session.InputMetadata.Keys.First();
                KiaiConfig config = KiaiAssets.LoadConfig();

                logOnce($"kiai ML 模型已加载：{Path.GetFileName(path)}（阈值 {config.Threshold:0.##}）。");
                return new Loaded(session, inputName, config);
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"kiai ML 模型加载失败，回退启发式：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        private static void logOnce(string message)
        {
            if (availabilityLogged)
                return;

            availabilityLogged = true;
            AppLog.Beat.Add(message, LogLevel.Important);
        }
    }
}
