using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// 全局 BPM 模型推理：onset 包络 → 自相关 tempo 显著度 → 候选集 → 逐候选打分 → 取最优层级。
    ///
    /// 为什么需要它：自相关的最强峰在近半数曲目上是标注节拍的二倍（音乐在次强拍上能量更强），
    /// 传统测速因此系统性选错 metrical 层级。模型的训练目标就是「哪个候选才是真正的节拍层级」，
    /// Ballroom 测试集把 Acc1 从 0.428 提到 0.754。
    ///
    /// 模型缺失、维度不符或任何异常时 <see cref="TryDetect(float[], int, out double)"/> 返回 false，调用方沿用原生结果。
    /// </summary>
    internal static class BpmMl
    {
        private sealed record Loaded(InferenceSession Session, string InputName, BpmConfig Config);

        private static readonly Lazy<Loaded?> model = new(loadModel, LazyThreadSafetyMode.ExecutionAndPublication);

        private static bool availabilityLogged;

        public static bool IsAvailable => model.Value is not null;

        public static string Status => model.Value is { } m
            ? $"BPM 模型已加载（{m.Config.Model}，覆盖阈值 {m.Config.OverrideMargin:0.##}）。"
            : "BPM 模型未安装，使用原生 BPM 估计。";

        /// <summary>
        /// 单声道 PCM → 全局 BPM。注意返回的是「节拍层级」修正后的结果，相位不在这里处理。
        /// </summary>
        public static bool TryDetect(float[] samples, int sampleRate, out double bpm)
        {
            bpm = 0;

            if (model.Value is null)
                return false;

            try
            {
                double[] onset = BpmFeatureBuilder.OnsetEnvelope(samples, sampleRate, out double fps);

                if (onset.Length < 8 || fps <= 0)
                    return false;

                double[] salience = BpmFeatureBuilder.Salience(onset, fps);
                return TryDetect(salience, BpmFeatureBuilder.Candidates(salience), out bpm);
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"BPM ML 推理异常，沿用原生结果：{e.Message}", LogLevel.Error, e);
                return false;
            }
        }

        /// <summary>
        /// 已算好显著度与候选时直接打分（评测里要复用同一份特征算基线，避免重复计算）。
        /// </summary>
        public static bool TryDetect(double[] salience, IReadOnlyList<double> candidates, out double bpm)
        {
            bpm = 0;

            if (model.Value is null || candidates.Count == 0)
                return false;

            float[] features = BpmFeatureBuilder.Features(salience, candidates);
            return TryScore(features, candidates, out bpm);
        }

        /// <summary>
        /// 直接在 C# 原生特征矩阵上打分（训练场评测用）：不重新解码音频，也不重算特征。
        /// features 长度必须是 count * <see cref="BpmFeatureBuilder.FeatureCount"/>。
        /// </summary>
        public static bool TryScore(float[] features, IReadOnlyList<double> candidates, out double bpm)
        {
            bpm = 0;

            Loaded? m = model.Value;

            if (m is null || candidates.Count == 0)
                return false;

            try
            {
                int count = candidates.Count;

                if (features.Length != count * BpmFeatureBuilder.FeatureCount)
                    return false;

                var input = new DenseTensor<float>(new[] { count, BpmFeatureBuilder.FeatureCount });
                features.CopyTo(input.Buffer.Slice(0, features.Length));

                float[] probabilities;

                using (var results = m.Session.Run(new[]
                       {
                           NamedOnnxValue.CreateFromTensor(m.InputName, input),
                       }))
                {
                    probabilities = extractPositiveProbabilities(results, count);
                }

                if (probabilities.Length != count)
                    return false;

                // 保守覆盖：领先不够就保留“显著度最强的候选”（即传统解），避免把本就正确的
                // Quickstep 之类改成错的（实测这一条把 Acc1 从 0.717 拉回 0.754）。
                int best = 0;

                for (int i = 1; i < probabilities.Length; i++)
                {
                    if (probabilities[i] > probabilities[best])
                        best = i;
                }

                // 倍频抑制：实际使用里只出现「偏快一倍」，没有「偏慢一半」。
                // 若当前结果恰好是某个更低候选的 2×/4×，且那个低候选也有足够概率、与首选差距不大，
                // 就降到低层级；最多降两级（256→128→64）。
                int chosen = best == 0 || probabilities[best] - probabilities[0] >= m.Config.OverrideMargin ? best : 0;

                for (int round = 0; round < 2; round++)
                {
                    int lower = -1;

                    foreach (double factor in new[] { 2.0, 4.0 })
                    {
                        double target = candidates[chosen] / factor;

                        if (target < BpmFeatureBuilder.BpmMin)
                            continue;

                        int candidate = -1;
                        double bestRelativeError = 0.011;

                        for (int i = 0; i < candidates.Count; i++)
                        {
                            if (i == chosen)
                                continue;

                            double relativeError = Math.Abs(candidates[i] - target) / target;

                            if (relativeError < bestRelativeError)
                            {
                                bestRelativeError = relativeError;
                                candidate = i;
                            }
                        }

                        if (candidate < 0)
                            continue;

                        if (probabilities[candidate] < m.Config.DoublingProbabilityMin
                            || probabilities[best] - probabilities[candidate] > m.Config.DoublingGapMax)
                            continue;

                        if (lower < 0 || probabilities[candidate] > probabilities[lower])
                            lower = candidate;
                    }

                    if (lower < 0)
                        break;

                    chosen = lower;
                }

                bpm = candidates[chosen];

                return bpm > 0;
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"BPM ML 推理异常，沿用原生结果：{e.Message}", LogLevel.Error, e);
                return false;
            }
        }

        /// <summary>从 ONNX 输出里找 2K 概率张量（zipmap=False 的 [K,2]）取第 1 列；否则找 K 个标签。</summary>
        private static float[] extractPositiveProbabilities(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int k)
        {
            float[][] floatOutputs = results
                .Where(v => v.Value is Tensor<float>)
                .Select(v => v.AsTensor<float>().ToArray())
                .ToArray();

            float[]? paired = floatOutputs.FirstOrDefault(o => o.Length == k * 2);

            if (paired is not null)
            {
                var probs = new float[k];

                for (int i = 0; i < k; i++)
                    probs[i] = paired[i * 2 + 1];

                return probs;
            }

            float[]? single = floatOutputs.FirstOrDefault(o => o.Length == k);

            if (single is not null)
                return single;

            long[]? labels = results
                .Where(v => v.Value is Tensor<long>)
                .Select(v => v.AsTensor<long>().ToArray())
                .FirstOrDefault(o => o.Length == k);

            if (labels is not null)
            {
                var probs = new float[k];

                for (int i = 0; i < k; i++)
                    probs[i] = labels[i] > 0 ? 1f : 0f;

                return probs;
            }

            throw new InvalidDataException("ONNX 输出里找不到 2K 概率或 K 标签张量。");
        }

        private static Loaded? loadModel()
        {
            string? path = BpmAssets.ModelPath;

            if (path is null)
            {
                logOnce("BPM ML 模型未安装（models/bpm/bpm.onnx），使用原生 BPM 估计。");
                return null;
            }

            try
            {
                BpmConfig config = BpmAssets.LoadConfig();

                if (!config.MatchesBuilder)
                {
                    AppLog.Beat.Add($"BPM 模型特征维度 {config.FftN}/{config.Hop} 与当前管线 "
                                    + $"{BpmFeatureBuilder.FftN}/{BpmFeatureBuilder.Hop} 不一致，回退原生。",
                        LogLevel.Error);
                    return null;
                }

                var options = new SessionOptions
                {
                    IntraOpNumThreads = 2,
                    InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };

                var session = new InferenceSession(path, options);
                string inputName = session.InputMetadata.Keys.First();

                logOnce($"BPM ML 模型已加载：{Path.GetFileName(path)}（{config.Model}）。");
                return new Loaded(session, inputName, config);
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"BPM ML 模型加载失败，回退原生：{e.Message}", LogLevel.Error, e);
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
