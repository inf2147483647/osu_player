using System.Globalization;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// kiai ML 特征管线 —— **训练导出与运行时推理共用同一份代码**。
    ///
    /// 流水线（两处入口必须严格一致）：
    /// 1. BASS FFT512 帧（约 86fps）→ 聚合成 0.5s 单元；
    /// 2. 每单元 40 维「原始特征」<see cref="RawDim"/>（对数能量 ×8、起音通量 ×7、起音密度、
    ///    12 维 chroma、12 维 MFCC）；
    /// 3. 各维按**整首歌**做 z-score（总体标准差，常数维置 0）；
    /// 4. 展开 ±<see cref="WindowRadius"/> 单元的上下文窗口（29×40=1160），边缘 clamp，
    ///    喂给 ONNX 逐单元二分类。
    ///
    /// Python 训练端只接收第 2 步的原始特征 CSV，自己复现 3/4 步；导出文件里的
    /// <c># check</c> 行携带 C# 算好的第 4 步向量，训练脚本逐文件断言两边一致，
    /// 避免「训练口径」与「推理口径」静默漂移。
    /// </summary>
    internal static class KiaiFeatureBuilder
    {
        public const double CellPeriodS = 0.5;
        public const int BandCount = 7;
        public const int ChromaBins = 12;
        public const int MelBins = 16;
        public const int MfccCount = 12;

        /// <summary>上下文窗口半径（单元数）：29 单元 ≈ 14.5s，覆盖一整段副歌前后的编曲对比。</summary>
        public const int WindowRadius = 14;
        public const int WindowSize = WindowRadius * 2 + 1;

        // ---- 40 维原始特征布局（FeatureNames 与之一一对应，顺序就是契约）----
        public const int IdxLogTotal = 0;                 // 0
        public const int IdxLogBands = 1;                 // 1..7
        public const int IdxFluxBands = 8;                // 8..14
        public const int IdxOnsetDensity = 15;            // 15
        public const int IdxChroma = 16;                  // 16..27
        public const int IdxMfcc = 28;                    // 28..39
        public const int RawDim = 1 + BandCount + BandCount + 1 + ChromaBins + MfccCount; // 40

        public const int WindowDim = RawDim * WindowSize;

        private const double eps = 1e-8;
        private const double mel_low_hz = 80;
        private const double mel_high_hz = 8000;

        public static readonly IReadOnlyList<string> FeatureNames = buildFeatureNames();

        /// <summary>0.5s 单元：Energy/Chroma 仍供启发式与边界检测使用，Raw 是 ML 的 40 维输入。</summary>
        internal readonly struct Cell
        {
            public readonly double TimeMs;
            public readonly double Energy;
            public readonly double[] Chroma;
            public readonly double[] Raw;

            public Cell(double timeMs, double energy, double[] chroma, double[] raw)
            {
                TimeMs = timeMs;
                Energy = energy;
                Chroma = chroma;
                Raw = raw;
            }
        }

        private static IReadOnlyList<string> buildFeatureNames()
        {
            var names = new List<string>(RawDim) { "e_log_total" };

            for (int b = 0; b < BandCount; b++)
                names.Add($"e_log_b{b}");

            for (int b = 0; b < BandCount; b++)
                names.Add($"flux_b{b}");

            names.Add("onset_density");

            for (int c = 0; c < ChromaBins; c++)
                names.Add($"chroma{c}");

            // MFCC 从 1 开始编号（丢掉 c0，它只是频谱总能量，与 e_log_total 重复）。
            for (int k = 1; k <= MfccCount; k++)
                names.Add($"mfcc{k}");

            return names;
        }

        #region FFT512 → 16 mel 三角滤波器（80Hz..8kHz）

        /// <summary>
        /// 为每个 FFT512 频点列出它对哪些 mel 滤波器有非零三角权重。
        /// 索引是 FFT bin（0..255），每 bin 最多落进 2 个滤波器；解码内层只做乘加。
        /// </summary>
        internal static (int Mel, double Weight)[][] BuildSpectrumMelContributions(int sampleRate)
        {
            double[] edgesHz = new double[MelBins + 2];

            for (int i = 0; i < edgesHz.Length; i++)
            {
                double mel = toMel(mel_low_hz)
                             + (toMel(mel_high_hz) - toMel(mel_low_hz)) * i / (MelBins + 1);
                edgesHz[i] = toHz(mel);
            }

            var contributions = new (int, double)[256][];

            for (int k = 0; k < 256; k++)
            {
                double frequency = (k + 0.5) * sampleRate / 512.0;
                var list = new List<(int, double)>(2);

                for (int m = 0; m < MelBins; m++)
                {
                    double left = edgesHz[m];
                    double peak = edgesHz[m + 1];
                    double right = edgesHz[m + 2];

                    if (frequency <= left || frequency >= right)
                        continue;

                    double weight = frequency < peak
                        ? (frequency - left) / (peak - left)
                        : (right - frequency) / (right - peak);

                    if (weight > 0)
                        list.Add((m, weight));
                }

                contributions[k] = list.ToArray();
            }

            return contributions;
        }

        private static double toMel(double hz) => 2595.0 * Math.Log10(1 + hz / 700.0);
        private static double toHz(double mel) => 700.0 * (Math.Pow(10, mel / 2595.0) - 1);

        #endregion

        #region 帧 → 0.5s 单元 → 40 维原始特征

        /// <summary>
        /// 把约 86fps 的解码帧聚合成 0.5s 单元并计算 40 维原始特征。
        /// 同时产出启发式 kiai 需要的（未取对数的）Energy 与 L2 归一化 chroma。
        /// </summary>
        internal static List<Cell> Build(IReadOnlyList<BeatAnalyser.Frame> frames, double durationS)
        {
            int cellCount = (int)(durationS / CellPeriodS) + 1;

            var totalSum = new double[cellCount];
            var bandSum = new double[cellCount * BandCount];
            var melSum = new double[cellCount * MelBins];
            var chromaSum = new double[cellCount * ChromaBins];
            var fluxSum = new double[cellCount * BandCount];
            var totalPositiveFlux = new double[cellCount];
            var positiveFluxFrames = new int[cellCount];
            var frameCount = new int[cellCount];

            for (int i = 0; i < frames.Count; i++)
            {
                BeatAnalyser.Frame f = frames[i];
                int cell = Math.Min((int)(f.Time / CellPeriodS), cellCount - 1);

                totalSum[cell] += f.Total;
                frameCount[cell]++;

                for (int b = 0; b < BandCount; b++)
                    bandSum[cell * BandCount + b] += f.GetBand(b);

                for (int m = 0; m < MelBins; m++)
                    melSum[cell * MelBins + m] += f.GetMel(m);

                for (int c = 0; c < ChromaBins; c++)
                    chromaSum[cell * ChromaBins + c] += f.GetChroma(c);

                if (i > 0)
                {
                    BeatAnalyser.Frame prev = frames[i - 1];
                    double totalDelta = f.Total - prev.Total;

                    if (totalDelta > 0)
                    {
                        totalPositiveFlux[cell] += totalDelta;
                        positiveFluxFrames[cell]++;
                    }

                    for (int b = 0; b < BandCount; b++)
                    {
                        double delta = f.GetBand(b) - prev.GetBand(b);
                        if (delta > 0)
                            fluxSum[cell * BandCount + b] += delta;
                    }
                }
            }

            // 第二遍：起音密度 = 「总能量正跳变超过本单元平均正跳变 1.5 倍」的帧占比。
            var onsetFrames = new int[cellCount];

            for (int i = 1; i < frames.Count; i++)
            {
                BeatAnalyser.Frame f = frames[i];
                int cell = Math.Min((int)(f.Time / CellPeriodS), cellCount - 1);

                if (positiveFluxFrames[cell] == 0)
                    continue;

                double meanPositive = totalPositiveFlux[cell] / positiveFluxFrames[cell];

                if (f.Total - frames[i - 1].Total > 1.5 * meanPositive)
                    onsetFrames[cell]++;
            }

            // DCT-II 正交归一化常数（M=16 个 mel）。
            double dctNorm = Math.Sqrt(2.0 / MelBins);

            var cells = new List<Cell>(cellCount);

            for (int cell = 0; cell < cellCount; cell++)
            {
                int count = Math.Max(1, frameCount[cell]);
                double timeMs = cell * CellPeriodS * 1000;
                double energy = totalSum[cell] / count;

                var raw = new double[RawDim];

                raw[IdxLogTotal] = Math.Log(energy + eps);

                for (int b = 0; b < BandCount; b++)
                {
                    double band = bandSum[cell * BandCount + b] / count;
                    raw[IdxLogBands + b] = Math.Log(band + eps);
                    raw[IdxFluxBands + b] = Math.Log(1 + fluxSum[cell * BandCount + b] / count);
                }

                raw[IdxOnsetDensity] = (double)onsetFrames[cell] / count;

                var chroma = new double[ChromaBins];
                double chromaNorm = 0;

                for (int c = 0; c < ChromaBins; c++)
                {
                    chroma[c] = chromaSum[cell * ChromaBins + c] / count;
                    chromaNorm += chroma[c] * chroma[c];
                }

                chromaNorm = Math.Sqrt(chromaNorm);

                if (chromaNorm > 0)
                {
                    for (int c = 0; c < ChromaBins; c++)
                        chroma[c] /= chromaNorm;
                }

                Array.Copy(chroma, 0, raw, IdxChroma, ChromaBins);

                // mel 均值 → 对数 → DCT-II，取系数 1..12（c0 丢弃）。
                var logMel = new double[MelBins];

                for (int m = 0; m < MelBins; m++)
                    logMel[m] = Math.Log(melSum[cell * MelBins + m] / count + eps);

                for (int k = 1; k <= MfccCount; k++)
                {
                    double sum = 0;

                    for (int m = 0; m < MelBins; m++)
                        sum += logMel[m] * Math.Cos(Math.PI / MelBins * (m + 0.5) * k);

                    raw[IdxMfcc + k - 1] = dctNorm * sum;
                }

                cells.Add(new Cell(timeMs, energy, chroma, raw));
            }

            return cells;
        }

        #endregion

        #region 标准化 + 上下文窗口（Python 端必须逐位复现）

        /// <summary>按整首歌对每维做 z-score（总体标准差，ddof=0；常数维置 0）。</summary>
        internal static double[][] Standardize(IReadOnlyList<double[]> raw)
        {
            int n = raw.Count;
            var mean = new double[RawDim];
            var std = new double[RawDim];

            for (int t = 0; t < n; t++)
            {
                for (int d = 0; d < RawDim; d++)
                    mean[d] += raw[t][d];
            }

            for (int d = 0; d < RawDim; d++)
                mean[d] /= n;

            for (int t = 0; t < n; t++)
            {
                for (int d = 0; d < RawDim; d++)
                {
                    double delta = raw[t][d] - mean[d];
                    std[d] += delta * delta;
                }
            }

            var z = new double[n][];

            for (int t = 0; t < n; t++)
            {
                z[t] = new double[RawDim];

                for (int d = 0; d < RawDim; d++)
                    z[t][d] = std[d] > 1e-8 ? (raw[t][d] - mean[d]) / Math.Sqrt(std[d] / n) : 0;
            }

            return z;
        }

        /// <summary>
        /// 第 t 个单元的 1160 维窗口：时间从 t-R 到 t+R（边缘 clamp 到首尾单元），
        /// 行主序，先时间后特征。
        /// </summary>
        internal static float[] BuildWindow(double[][] z, int t)
        {
            int n = z.Length;
            var window = new float[WindowDim];
            int offset = 0;

            for (int r = -WindowRadius; r <= WindowRadius; r++)
            {
                int idx = Math.Clamp(t + r, 0, n - 1);
                double[] row = z[idx];

                for (int d = 0; d < RawDim; d++)
                    window[offset++] = (float)row[d];
            }

            return window;
        }

        /// <summary>
        /// 导出/训练一致性校验值：第 checkCell 个单元的窗口向量，按 G17 往返精度序列化。
        /// 训练脚本逐文件复算并断言相等。
        /// </summary>
        internal static (int Cell, float[] Window) BuildParityCheck(IReadOnlyList<Cell> cells)
        {
            double[][] raw = cells.Select(c => (double[])c.Raw.Clone()).ToArray();
            double[][] z = Standardize(raw);
            int checkCell = Math.Min(7, cells.Count - 1);
            return (checkCell, BuildWindow(z, checkCell));
        }

        #endregion

        #region CSV 序列化（导出训练数据；invariant，G17 往返精度）

        internal static string FormatDouble(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

        #endregion
    }
}
