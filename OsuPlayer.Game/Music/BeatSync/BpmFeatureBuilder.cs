using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// 全局 BPM 估计的特征契约（C# 侧）。
    ///
    /// 与 <c>tools/bpm/bpm_common.py</c> 逐条对应，训练（Python）与推理（这里）必须同口径，
    /// 否则模型会静默劣化 —— 与 <see cref="KiaiFeatureBuilder"/> 同一套纪律。
    ///
    /// 管线：
    ///   单声道 float 采样 → 分帧 STFT(1024/hop256, Hann) → log(1+幅度)
    ///   → 逐帧半波整流频谱通量 → 去局部均值 + 归一化 = onset 包络
    ///   → 无偏自相关 → 谐波求和得 tempo 显著度（BPM 网格 40–260 步长 0.5）
    ///   → 显著度峰派生候选 → 逐候选 18 维特征
    ///
    /// 最后 4 维专门区分「真慢歌」与「被抬高一倍」：低频（20–200Hz，kick/bass）各自的
    /// tempo 显著度比，以及色度（和声）在候选拍长/一倍/一半上的平均变化量之比。
    /// 起音包络对这两者不敏感 —— 慢歌的八分音符照样有能量，只有低频重复周期和
    /// 和声切换周期能说明「哪一层才是真正的拍」。
    ///
    /// 注意：这里刻意不复用 BASS 的 FFT512 —— 那只有 86fps，在 200BPM 处一个 lag 就跨好几 BPM，
    /// 而评估容差是 4%·gt。1024/256 在 44.1k 下是 172fps，与 Python 端一致。
    /// </summary>
    internal static class BpmFeatureBuilder
    {
        public const int FftN = 1024;
        public const int Hop = 256;
        public const double BpmMin = 40.0;
        public const double BpmMax = 260.0;
        public const double BpmStep = 0.5;
        public const int Harmonics = 4;
        public const int MaxCandidates = 20;
        public const double MinCandidateSeparation = 3.0;
        public const int CandidatePeaks = 5;
        public const int FeatureCount = 18;

        private const double low_band_hz = 200;
        private const double chroma_low_hz = 90;
        private const double chroma_high_hz = 2000;
        private const int chroma_bins = 12;
        private const double ratio_epsilon = 1e-3;

        /// <summary>BPM 网格点数（与 Python 的 np.arange(40, 260+eps, 0.5) 等长）。</summary>
        public static readonly int BpmGridLength = (int)Math.Round((BpmMax - BpmMin) / BpmStep) + 1;

        /// <summary>metrical 相对层级，顺序与 config.json 的 metrical_ratios 一致。</summary>
        public static readonly double[] MetricalRatios = { 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1.5, 2.0, 3.0, 4.0 };

        private const int peaks_min_distance_bins = 8; // round(4.0 / BpmStep)

        public static double BpmAt(int index) => BpmMin + index * BpmStep;

        /// <summary>
        /// 一次 STFT 同时产出的三样东西：全频带 onset、低频 onset（20–200Hz）和逐帧色度。
        /// 训练与推理都只走这一个入口，保证两边口径完全一致。
        /// </summary>
        public sealed record RhythmFeatures(double[] Onset, double Fps, double[] LowOnset,
                                            float[] Chroma, int ChromaFrames);

        /// <summary>频谱通量 onset 包络；fps = 采样率 / hop。</summary>
        public static double[] OnsetEnvelope(float[] samples, int sampleRate, out double fps)
        {
            RhythmFeatures rhythm = Analyse(samples, sampleRate);
            fps = rhythm.Fps;
            return rhythm.Onset;
        }

        /// <summary>单次分帧 STFT：全频带/低频频谱通量 + 12 维色度（逐帧能量归一化由消费者做）。</summary>
        public static RhythmFeatures Analyse(float[] samples, int sampleRate)
        {
            double fps = (double)sampleRate / Hop;
            int frames = 1 + (samples.Length - FftN) / Hop;

            if (frames < 4)
                return new RhythmFeatures(new double[1], fps, new double[1], Array.Empty<float>(), 0);

            // np.hanning 是对称窗（分母 N-1），不是周期窗。
            var window = new double[FftN];

            for (int i = 0; i < FftN; i++)
                window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftN - 1));

            int bins = FftN / 2 + 1;
            int lowBinMax = Math.Clamp((int)Math.Round(low_band_hz * FftN / sampleRate), 2, bins - 1);
            int[] pitchClass = buildPitchClassTable(sampleRate);
            var flux = new double[frames - 1];
            var lowFlux = new double[frames - 1];
            var chroma = new float[frames * chroma_bins];

            // 帧循环按块并行：每块内部自己算 log 幅度、自己写自己区间的 flux，
            // 块之间没有共享写入，结果与串行逐位相同（同一帧无论谁算都是同一串浮点运算）。
            // 这是这一段的绝对瓶颈（每帧 513 次 sqrt+log），在 2 核机器上并行几乎是白拿的一倍。
            const int block = 2048;
            int blockCount = (frames + block - 1) / block;

            Parallel.For(0, blockCount, b =>
            {
                int start = b * block;
                int end = Math.Min(frames, start + block);
                var re = new double[FftN];
                var im = new double[FftN];
                var prev = new double[bins];
                var current = new double[bins];

                computeLogMagnitude(re, im, samples, window, start, prev);
                accumulateChroma(prev, pitchClass, chroma, start);

                for (int t = start + 1; t < end; t++)
                {
                    computeLogMagnitude(re, im, samples, window, t, current);
                    accumulateChroma(current, pitchClass, chroma, t);

                    double sum = 0;

                    for (int k = 0; k < bins; k++)
                    {
                        double d = current[k] - prev[k];

                        if (d > 0)
                            sum += d;
                    }

                    flux[t - 1] = sum;

                    double lowSum = 0;

                    for (int k = 1; k <= lowBinMax; k++)
                    {
                        double d = current[k] - prev[k];

                        if (d > 0)
                            lowSum += d;
                    }

                    lowFlux[t - 1] = lowSum;
                    (prev, current) = (current, prev);
                }
            });

            int windowFrames = Math.Max(1, (int)Math.Round(fps));

            return new RhythmFeatures(normaliseOnset(flux, windowFrames), fps,
                normaliseOnset(lowFlux, windowFrames), chroma, frames);
        }

        /// <summary>去局部均值后整流，再按总体标准差归一化（与 Python 的 _moving_average 一致）。</summary>
        private static double[] normaliseOnset(double[] flux, int windowFrames)
        {
            double[] local = MovingAverage(flux, windowFrames);
            var onset = new double[flux.Length];
            double total = 0;

            for (int i = 0; i < flux.Length; i++)
            {
                double v = flux[i] - local[i];
                onset[i] = v > 0 ? v : 0;
                total += onset[i];
            }

            double mean = total / onset.Length;
            double variance = 0;

            for (int i = 0; i < onset.Length; i++)
            {
                double d = onset[i] - mean;
                variance += d * d;
            }

            variance /= onset.Length;
            double std = Math.Sqrt(variance);

            if (std > 1e-8)
            {
                for (int i = 0; i < onset.Length; i++)
                    onset[i] /= std;
            }

            return onset;
        }

        /// <summary>把第 t 帧的频谱能量按音高类累进 12 维色度（幅度从 log(1+mag) 还原）。</summary>
        private static void accumulateChroma(double[] logMagnitude, int[] pitchClass, float[] chroma, int frame)
        {
            int offset = frame * chroma_bins;

            for (int k = 0; k < pitchClass.Length; k++)
            {
                int pitch = pitchClass[k];

                if (pitch < 0)
                    continue;

                chroma[offset + pitch] += (float)(Math.Exp(logMagnitude[k]) - 1);
            }
        }

        /// <summary>FFT bin → 音高类（90–2000Hz），范围外记 -1。</summary>
        private static int[] buildPitchClassTable(int sampleRate)
        {
            int bins = FftN / 2 + 1;
            var table = new int[bins];

            for (int k = 0; k < bins; k++)
            {
                double hz = (double)k * sampleRate / FftN;

                if (hz < chroma_low_hz || hz > chroma_high_hz)
                {
                    table[k] = -1;
                    continue;
                }

                double midi = 69 + 12 * Math.Log2(hz / 440.0);
                table[k] = ((int)Math.Round(midi) % chroma_bins + chroma_bins) % chroma_bins;
            }

            return table;
        }

        /// <summary>第 t 帧的 log(1+幅度谱)，写入 <paramref name="destination"/>（长度 513）。</summary>
        private static void computeLogMagnitude(double[] re, double[] im, float[] samples, double[] window,
                                                int t, double[] destination)
        {
            int offset = t * Hop;

            for (int i = 0; i < FftN; i++)
            {
                re[i] = samples[offset + i] * window[i];
                im[i] = 0;
            }

            fft(re, im);

            for (int k = 0; k < destination.Length; k++)
                destination[k] = Math.Log(1 + Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
        }

        /// <summary>居中滑动平均，边界用收缩窗口（与 Python 的 _moving_average 一致）。</summary>
        private static double[] MovingAverage(double[] a, int w)
        {
            int n = a.Length;
            var cumsum = new double[n + 1];

            for (int i = 0; i < n; i++)
                cumsum[i + 1] = cumsum[i] + a[i];

            var output = new double[n];

            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - w);
                int hi = Math.Min(n, i + w + 1);
                output[i] = (cumsum[hi] - cumsum[lo]) / (hi - lo);
            }

            return output;
        }

        /// <summary>无偏自相关 r[lag]/r[0]，lag = 0..maxLag（直接计算，量级只有 1e6 次乘加）。</summary>
        public static double[] Autocorrelation(double[] onset, int maxLag)
        {
            int n = onset.Length;
            var ac = new double[maxLag + 1];

            for (int lag = 0; lag <= maxLag; lag++)
            {
                int count = n - lag;
                double sum = 0;

                for (int t = 0; t < count; t++)
                    sum += onset[t] * onset[t + lag];

                ac[lag] = count > 0 ? sum / count : 0;
            }

            if (ac[0] > 1e-12)
            {
                for (int i = 0; i <= maxLag; i++)
                    ac[i] /= ac[0];
            }

            return ac;
        }

        /// <summary>谐波求和得到 BPM 网格上的 tempo 显著度曲线。</summary>
        public static double[] Salience(double[] onset, double fps)
        {
            int maxLag = (int)Math.Round(60.0 * fps / BpmMin) + 1;
            double[] ac = Autocorrelation(onset, maxLag);
            var salience = new double[BpmGridLength];

            for (int i = 0; i < BpmGridLength; i++)
            {
                double lag = 60.0 * fps / BpmAt(i);
                double sum = 0;

                for (int h = 1; h <= Harmonics; h++)
                {
                    double hl = lag * h;

                    if (hl <= maxLag)
                        sum += Interp(ac, hl) / h;
                }

                salience[i] = sum;
            }

            return salience;
        }

        private static double Interp(double[] ac, double position)
        {
            if (position < 0)
                return 0;

            int lower = (int)Math.Floor(position);

            if (lower >= ac.Length)
                return ac[^1];

            double frac = position - lower;
            int upper = Math.Min(lower + 1, ac.Length - 1);
            return ac[lower] * (1 - frac) + ac[upper] * frac;
        }

        /// <summary>BPM 网格上的线性插值取值；超出 [40,260] 返回 0。</summary>
        public static double SalienceAt(double[] salience, double bpm)
        {
            if (bpm < BpmMin - 1e-9 || bpm > BpmMax + 1e-9)
                return 0;

            return Interp(salience, (bpm - BpmMin) / BpmStep);
        }

        /// <summary>局部极大值（含平台），按显著度降序、彼此至少相隔 minDistanceBins 个网格点。</summary>
        private static List<int> FindPeaks(double[] salience, int minDistanceBins)
        {
            var peaks = new List<int>();

            for (int i = 1; i < salience.Length - 1; i++)
            {
                if (salience[i] >= salience[i - 1] && salience[i] > salience[i + 1])
                    peaks.Add(i);
            }

            peaks.Sort((a, b) => salience[b].CompareTo(salience[a]));
            var kept = new List<int>();

            foreach (int i in peaks)
            {
                bool far = true;

                foreach (int j in kept)
                {
                    if (Math.Abs(i - j) < minDistanceBins)
                    {
                        far = false;
                        break;
                    }
                }

                if (far)
                    kept.Add(i);
            }

            return kept;
        }

        /// <summary>候选 BPM：显著度前若干名峰的 metrical 亲属（含自身），去重后按显著度降序截断。</summary>
        public static List<double> Candidates(double[] salience)
        {
            List<int> peaks = FindPeaks(salience, peaks_min_distance_bins);
            var pool = new List<double>();

            if (peaks.Count > 0)
            {
                for (int p = 0; p < Math.Min(CandidatePeaks, peaks.Count); p++)
                {
                    double best = BpmAt(peaks[p]);

                    foreach (double ratio in new[] { 1.0, 2.0, 0.5, 3.0, 1.0 / 3, 4.0, 0.25, 1.5, 2.0 / 3 })
                        pool.Add(RefineBpm(salience, best * ratio));
                }
            }
            else
            {
                int best = 0;

                for (int i = 1; i < salience.Length; i++)
                {
                    if (salience[i] > salience[best])
                        best = i;
                }

                pool.Add(BpmAt(best));
            }

            pool.RemoveAll(b => b < BpmMin || b > BpmMax);
            pool.Sort((a, b) => SalienceAt(salience, b).CompareTo(SalienceAt(salience, a)));

            var kept = new List<double>();

            foreach (double b in pool)
            {
                bool far = true;

                foreach (double c in kept)
                {
                    if (Math.Abs(b - c) < MinCandidateSeparation)
                    {
                        far = false;
                        break;
                    }
                }

                if (far)
                    kept.Add(b);
            }

            return kept.Count > MaxCandidates ? kept.GetRange(0, MaxCandidates) : kept;
        }

        /// <summary>
        /// 候选吸附到最近的网格点。Python 端扫描过抛物线细化（USE_REFINE），结论是反而降召回，
        /// 所以两侧都关掉，保持候选落在网格上。
        /// </summary>
        private static double RefineBpm(double[] salience, double bpm)
        {
            if (bpm < BpmMin || bpm > BpmMax)
                return bpm;

            int index = (int)Math.Round((bpm - BpmMin) / BpmStep);
            index = Math.Clamp(index, 0, salience.Length - 1);
            return BpmAt(index);
        }

        /// <summary>逐候选 18 维特征，按候选顺序拼成一段连续的 float（供 ONNX 输入）。</summary>
        public static float[] Features(double[] salience, IReadOnlyList<double> candidates, RhythmFeatures rhythm)
        {
            var features = new float[candidates.Count * FeatureCount];
            double max = 0;

            foreach (double v in salience)
            {
                if (v > max)
                    max = v;
            }

            if (max <= 1e-12)
                max = 1.0;

            double[] lowSalience = Salience(rhythm.LowOnset, rhythm.Fps);
            double maxLow = 0;

            foreach (double v in lowSalience)
            {
                if (v > maxLow)
                    maxLow = v;
            }

            if (maxLow <= 1e-12)
                maxLow = 1.0;

            for (int rank = 0; rank < candidates.Count; rank++)
            {
                double bpm = candidates[rank];
                double self = SalienceAt(salience, bpm);
                int index = rank * FeatureCount;

                features[index] = (float)(self / max);

                for (int r = 0; r < MetricalRatios.Length; r++)
                    features[index + 1 + r] = (float)(SalienceAt(salience, bpm * MetricalRatios[r]) / max);

                double half = SalienceAt(salience, bpm * 0.5);
                double dbl = SalienceAt(salience, bpm * 2.0);
                features[index + 9] = (float)((self - 0.5 * (half + dbl)) / max);
                features[index + 10] = (float)Math.Log2(bpm / 120.0);
                features[index + 11] = rank;

                int i0 = Math.Max(0, (int)Math.Floor((bpm * 0.96 - BpmMin) / BpmStep));
                int i1 = Math.Min(salience.Length - 1, (int)Math.Ceiling((bpm * 1.04 - BpmMin) / BpmStep));
                double localMean = self;

                if (i1 >= i0)
                {
                    double sum = 0;

                    for (int i = i0; i <= i1; i++)
                        sum += salience[i];

                    localMean = sum / (i1 - i0 + 1);
                }

                features[index + 12] = (float)(localMean > 1e-12 ? self / localMean : 1.0);
                features[index + 13] = (float)(self / max);

                // 低频：kick/bass 只按真正的拍重复。候选若被抬高一倍，它在半拍处同样强，
                // 所以「候选 BPM 的低频显著度 / 两倍 BPM 的低频显著度」能从正面证伪倍频。
                double lowSelf = SalienceAt(lowSalience, bpm);
                double lowDouble = SalienceAt(lowSalience, bpm * 2.0);
                features[index + 14] = (float)(lowSelf / maxLow);
                features[index + 15] = (float)ratio(lowSelf, lowDouble);

                // 和声：和弦通常按拍切换。候选拍长上的色度变化应当明显大于两倍拍长
                // （同一和弦内部）和半拍（和弦还没换）。三个比值一起交给分类器。
                double changeSelf = meanChromaChange(rhythm, bpm);
                double changeDouble = meanChromaChange(rhythm, bpm * 2.0);
                double changeHalf = meanChromaChange(rhythm, bpm * 0.5);
                features[index + 16] = (float)ratio(changeSelf, changeDouble);
                features[index + 17] = (float)ratio(changeSelf, changeHalf);
            }

            return features;
        }

        /// <summary>log2 比值，带下限保护并截断到 [-4,4]（避免静音段产生极端值）。</summary>
        private static double ratio(double a, double b)
        {
            double value = Math.Log2((a + ratio_epsilon) / (b + ratio_epsilon));
            return Math.Clamp(value, -4, 4);
        }

        /// <summary>候选拍长上相邻两拍的平均色度变化量（1 - 余弦相似度）。</summary>
        private static double meanChromaChange(RhythmFeatures rhythm, double bpm)
        {
            if (rhythm.ChromaFrames < 2 || bpm <= 0 || rhythm.Fps <= 0)
                return 0;

            double lag = 60.0 * rhythm.Fps / bpm;

            if (lag < 1.5)
                return 0;

            int lagFrames = (int)Math.Round(lag);
            double sum = 0;
            int count = 0;

            for (int t = 0; t + lagFrames < rhythm.ChromaFrames; t += lagFrames)
            {
                int a = t * chroma_bins;
                int b = (t + lagFrames) * chroma_bins;
                double dot = 0, normA = 0, normB = 0;

                for (int k = 0; k < chroma_bins; k++)
                {
                    double x = rhythm.Chroma[a + k];
                    double y = rhythm.Chroma[b + k];
                    dot += x * y;
                    normA += x * x;
                    normB += y * y;
                }

                if (normA < 1e-9 || normB < 1e-9)
                    continue;

                sum += 1 - dot / Math.Sqrt(normA * normB);
                count++;
            }

            return count > 0 ? sum / count : 0;
        }

        #region FFT

        private static readonly double[] twiddleRe = new double[FftN / 2];
        private static readonly double[] twiddleIm = new double[FftN / 2];

        static BpmFeatureBuilder()
        {
            for (int i = 0; i < FftN / 2; i++)
            {
                double angle = -2 * Math.PI * i / FftN;
                twiddleRe[i] = Math.Cos(angle);
                twiddleIm[i] = Math.Sin(angle);
            }
        }

        /// <summary>原地基 2 迭代 FFT（长度固定为 <see cref="FftN"/>，预置旋转因子）。</summary>
        private static void fft(double[] re, double[] im)
        {
            int n = re.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;

                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;

                j ^= bit;

                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len / 2;
                int step = n / len;

                for (int start = 0; start < n; start += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wRe = twiddleRe[k * step];
                        double wIm = twiddleIm[k * step];
                        int a = start + k;
                        int b = a + half;
                        double xRe = re[b] * wRe - im[b] * wIm;
                        double xIm = re[b] * wIm + im[b] * wRe;
                        re[b] = re[a] - xRe;
                        im[b] = im[a] - xIm;
                        re[a] += xRe;
                        im[a] += xIm;
                    }
                }
            }
        }

        #endregion
    }
}
