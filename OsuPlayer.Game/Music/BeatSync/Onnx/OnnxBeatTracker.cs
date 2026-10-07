using ManagedBass;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>ONNX 节拍检测的结果。</summary>
    public sealed record OnnxBeatResult(double Bpm, double PhaseMs, int BeatCount);

    /// <summary>
    /// 用 Beat This!（JKU，ISMIR 2024，MIT）的 ONNX 模型做节拍检测。
    ///
    /// 这条路线与 <see cref="BeatAnalyser"/> 的频谱通量方法完全独立，所以它能提供一份真正
    /// 意义上的「第二意见」—— 原生管线里 BASS_FX 那路引擎在训练集上 302 张有 285 张被
    /// irregularity 门槛否掉，融合实际上只剩一路。
    ///
    /// 前端必须与训练时逐项一致（22050Hz 单声道、mean 下混、STFT 1024/441、periodic Hann、
    /// center=True 的 reflect padding、幅度除以 √n_fft、mel 投影、log1p(1000·x)）。参数取自
    /// <see cref="BeatThisAssets"/>，滤波器组直接用模型自带的 mel-filterbank.bin，不自己重算
    /// slaney mel —— 这是这套方案里最容易出错、也最致命的地方。
    ///
    /// 分窗与拼接严格照搬上游的 split_piece / aggregate_prediction（keep_first）：513 帧一窗、
    /// 每侧丢弃 6 帧边界、窗口步进 501、最后一窗左移到贴住结尾，重叠处**先用者胜**。
    /// </summary>
    public static class OnnxBeatTracker
    {
        private static readonly Lazy<Model?> model = new(loadModel, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>模型是否可用（文件就绪且会话能建起来）。设置页与调用方据此决定是否回退。</summary>
        public static bool IsAvailable => model.Value is not null;

        /// <summary>模型不可用时的原因（用于设置页提示）。</summary>
        public static string? UnavailableReason { get; private set; }

        /// <summary>
        /// 分析整段音频并给出 BPM / 首拍 / 拍数。任一环节不可靠时返回 <c>null</c>，
        /// 由调用方回退到原生结果。
        /// </summary>
        public static OnnxBeatResult? Track(byte[] audio)
        {
            Model? m = model.Value;

            if (m is null || audio.Length == 0)
                return null;

            float[] mono = decodeMono22k(audio);

            if (mono.Length < BeatThisAssets.HopLength * 4)
                return null;

            float[] logMel = logMelSpectrogram(mono, out int frames);

            if (frames < 16)
                return null;

            inferBeatLogits(m, logMel, frames, out float[] beatLogits, out float[] downbeatLogits);

            List<double> beats = pickBeats(beatLogits, frames);

            if (beats.Count < 4)
            {
                AppLog.Beat.Add($"ONNX 节拍：只检出 {beats.Count} 个拍点，判定不可靠。", LogLevel.Important);
                return null;
            }

            // 拍间间隔取中位数：模型输出的是 tactus 本身，不需要再做八度仲裁。
            var intervals = new double[beats.Count - 1];

            for (int i = 0; i < intervals.Length; i++)
                intervals[i] = beats[i + 1] - beats[i];

            var sorted = (double[])intervals.Clone();
            Array.Sort(sorted);

            double median = sorted[sorted.Length / 2];

            if (median <= 0)
                return null;

            double bpm = Math.Round(60.0 / median, MidpointRounding.AwayFromZero);

            // 与原生管线同样的量级保护：超出这个范围说明模型没锁住节拍，交给调用方回退。
            if (bpm is < 40 or > 300)
            {
                AppLog.Beat.Add($"ONNX 节拍：中位间隔给出 {bpm:0.##} BPM，超出可信范围，判定不可靠。", LogLevel.Important);
                return null;
            }

            return new OnnxBeatResult(bpm, choosePhaseMs(beats, downbeatLogits, frames), beats.Count);
        }

        /// <summary>
        /// 选一个**小节强拍**作为节拍网格原点，而不是随手取第一个拍点。
        ///
        /// osu! 的小节级特效只在 <c>beatIndex % 4 == 0</c> 时触发（见 MenuSideFlashes），所以红线
        /// 落在小节的第几拍上是有意义的。直接拿 <c>beats[0]</c> 等于随机取小节的第 1~4 拍之一，
        /// 表现就是「首拍错位」——拍点对得上，但每 4 拍的强拍对不上。
        ///
        /// Beat This! 自带 downbeat 头，正好用来定这个小节原点：与上游 <c>_postp_minimal_item</c>
        /// 一样，先把每个重拍吸附到最近的拍点，再取最早的那个。模型没有重拍头时退回第一个拍点。
        /// </summary>
        private static double choosePhaseMs(List<double> beats, float[] downbeatLogits, int frames)
        {
            List<double> downbeats = pickBeats(downbeatLogits, frames);

            if (downbeats.Count == 0)
                return beats[0] * 1000.0;

            double best = beats[0];
            double bestDistance = double.MaxValue;

            foreach (double downbeat in downbeats)
            {
                double nearest = beats[0];
                double nearestDistance = double.MaxValue;

                foreach (double beat in beats)
                {
                    double distance = Math.Abs(beat - downbeat);

                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = beat;
                    }
                }

                if (nearestDistance < bestDistance)
                {
                    bestDistance = nearestDistance;
                    best = nearest;
                }
            }

            return best * 1000.0;
        }

        /// <summary>
        /// 用 ONNX 结果覆盖原生分析里的「速度 + 拍点相位 + 拍数」，kiai 保持原样
        /// （公开数据集里没有 kiai 标注，模型无从提供）。
        ///
        /// 模型未下载、加载失败或结果不可靠时返回 <c>false</c> 并原样输出 <paramref name="native"/>，
        /// 所以调用方可以把它当成纯粹的「可选增强」，不需要自己处理回退。
        /// 这是播放路径与训练评测台共用的唯一一处合并逻辑。
        /// </summary>
        public static bool TryOverride(byte[] audio, BeatAnalysis native, out BeatAnalysis result)
        {
            result = native;

            OnnxBeatResult? onnx;

            try
            {
                onnx = Track(audio);
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"ONNX 节拍分析异常，回退原生结果：{e.Message}", LogLevel.Error, e);
                return false;
            }

            if (onnx is null)
            {
                AppLog.Beat.Add($"ONNX 节拍不可用（{UnavailableReason ?? "结果不可靠"}），沿用原生 BPM {native.Bpm:0.##}。",
                    LogLevel.Important);
                return false;
            }

            AppLog.Beat.Add($"ONNX 节拍结果：BPM {onnx.Bpm:0.##}（原生 {native.Bpm:0.##}），"
                            + $"首拍 {onnx.PhaseMs:0}ms，拍点 {onnx.BeatCount}", LogLevel.Important);

            result = native with
            {
                Bpm = onnx.Bpm,
                BeatPhaseMs = onnx.PhaseMs,
                DetectedBeats = onnx.BeatCount,
            };

            return true;
        }

        #region 音频解码

        /// <summary>
        /// 用 BASS 解码成 22050Hz 单声道 float PCM。
        ///
        /// 两个坑：
        /// 一是 BASS 的 <c>ChannelSetAttribute(Frequency)</c> 对**解码通道不生效**（实测设了之后
        /// <c>info.Frequency</c> 仍是源的 44100），所以重采样必须自己做 —— 否则把 44100 的数据
        /// 当成 22050 喂给模型，等于让模型听 0.5× 速度，输出 BPM 会整体减半。
        /// 二是下混必须用**算术平均**，不能用 ffmpeg -ac 1 那种保能量的 rematrix：特征经过
        /// log1p(1000·x) 之后增益不是常数偏移，会让拍点整体挪位。
        /// </summary>
        private static float[] decodeMono22k(byte[] audio)
        {
            int handle = Bass.CreateStream(audio, 0, audio.Length, BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
                throw new InvalidOperationException($"BASS 解码失败（error {Bass.LastError}）。");

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);

                var samples = new List<float>(1 << 22);
                var buffer = new float[1 << 15];

                while (true)
                {
                    int read = Bass.ChannelGetData(handle, buffer, buffer.Length * sizeof(float));

                    if (read <= 0)
                        break;

                    int count = read / sizeof(float);

                    for (int i = 0; i < count; i++)
                        samples.Add(buffer[i]);
                }

                int channels = Math.Max(1, info.Channels);
                var mono = new float[samples.Count / channels];

                if (channels == 1)
                {
                    samples.CopyTo(mono);
                }
                else
                {
                    for (int f = 0; f < mono.Length; f++)
                    {
                        float sum = 0;

                        for (int c = 0; c < channels; c++)
                            sum += samples[f * channels + c];

                        mono[f] = sum / channels;
                    }
                }

                if (info.Frequency == BeatThisAssets.SampleRate)
                    return mono;

                float[] resampled = resample(mono, info.Frequency, BeatThisAssets.SampleRate);

                AppLog.Beat.Add($"ONNX 前端：{info.Frequency}Hz → {BeatThisAssets.SampleRate}Hz，"
                                + $"{mono.Length} → {resampled.Length} 样本"
                                + $"（{resampled.Length / (double)BeatThisAssets.SampleRate:0.#}s）");

                return resampled;
            }
            finally
            {
                Bass.StreamFree(handle);
            }
        }

        /// <summary>
        /// 多相 windowed-sinc 重采样。参考实现用 soxr；这里用自己的实现，保证行为可预测
        /// （模型卡实测换重采样器只会让 0.37% 的临界拍点翻转，对中位速度没有影响）。
        ///
        /// 每 512 个分数相位预计算一组权重，运行时只做乘加，避免在内层调用 sin/cos。
        /// 降采样时截止频率压到输出 Nyquist，否则会混叠。
        /// </summary>
        private static float[] resample(float[] input, int inputRate, int outputRate)
        {
            if (inputRate == outputRate || input.Length == 0)
                return input;

            const int phases = 512;
            const int halfTaps = 16;

            double ratio = (double)inputRate / outputRate;
            double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate);
            int width = (int)Math.Ceiling(halfTaps * Math.Max(1.0, ratio));
            int taps = 2 * width + 1;

            var table = new float[phases * taps];

            for (int p = 0; p < phases; p++)
            {
                double fraction = (double)p / phases;
                double sum = 0;
                int offset = p * taps;

                for (int k = -width; k <= width; k++)
                {
                    double distance = k - fraction;
                    double taper = Math.Abs(distance) <= width ? 0.5 + 0.5 * Math.Cos(Math.PI * distance / width) : 0;
                    double value = 2 * cutoff * sinc(2 * cutoff * distance) * taper;

                    table[offset + k + width] = (float)value;
                    sum += value;
                }

                // 逐相位归一化直流增益：加窗与截断会让理想核的和略偏离 1。
                if (Math.Abs(sum) > 1e-12)
                {
                    for (int k = -width; k <= width; k++)
                        table[offset + k + width] /= (float)sum;
                }
            }

            int outputLength = (int)((long)input.Length * outputRate / inputRate);
            var output = new float[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                double position = i * ratio;
                int centre = (int)Math.Floor(position);
                int phase = Math.Min(phases - 1, (int)((position - centre) * phases));
                int offset = phase * taps;

                float accumulator = 0;

                for (int k = -width; k <= width; k++)
                {
                    int index = centre + k;

                    if ((uint)index < (uint)input.Length)
                        accumulator += input[index] * table[offset + k + width];
                }

                output[i] = accumulator;
            }

            return output;
        }

        private static double sinc(double x)
            => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

        #endregion

        #region Log-mel 前端

        private static readonly Lazy<Fft> fft = new(() => new Fft(BeatThisAssets.NFft));
        private static readonly Lazy<float[]> filterbank = new(BeatThisAssets.LoadFilterbank, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>periodic Hann：0.5 - 0.5·cos(2πn/N)。</summary>
        private static readonly Lazy<double[]> window = new(() =>
        {
            var w = new double[BeatThisAssets.NFft];

            for (int n = 0; n < w.Length; n++)
                w[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / w.Length);

            return w;
        });

        /// <summary>
        /// 与 torchaudio.train 一致的 log-mel：STFT(n_fft=1024, hop=441, periodic Hann,
        /// center=True 的 reflect padding) → 幅度 / √n_fft → 投影到模型自带的 mel 滤波器组
        /// → log1p(1000·x)。返回行主序 [frames, 128]。
        /// </summary>
        private static float[] logMelSpectrogram(float[] mono, out int frames)
        {
            int n = BeatThisAssets.NFft;
            int half = n / 2;
            int hop = BeatThisAssets.HopLength;
            int bins = BeatThisAssets.FilterbankRows;

            frames = 1 + mono.Length / hop;

            var result = new float[frames * BeatThisAssets.MelBins];

            double[] w = window.Value;
            float[] fb = filterbank.Value;
            var re = new double[n];
            var im = new double[n];
            var magnitude = new double[bins];

            for (int f = 0; f < frames; f++)
            {
                int origin = f * hop;

                for (int k = 0; k < n; k++)
                {
                    re[k] = paddedSample(mono, origin + k, half) * w[k];
                    im[k] = 0;
                }

                Fft fftInstance = fft.Value;
                fftInstance.Forward(re, im);

                // torchaudio 的 normalized='frame_length' 把幅度除以 √n_fft。
                double scale = 1.0 / Math.Sqrt(n);

                for (int k = 0; k < bins; k++)
                    magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * scale;

                int rowBase = f * BeatThisAssets.MelBins;

                for (int k = 0; k < bins; k++)
                {
                    double value = magnitude[k];

                    if (value == 0)
                        continue;

                    int fbBase = k * BeatThisAssets.MelBins;

                    for (int mel = 0; mel < BeatThisAssets.MelBins; mel++)
                        result[rowBase + mel] += (float)(value * fb[fbBase + mel]);
                }

                for (int mel = 0; mel < BeatThisAssets.MelBins; mel++)
                    result[rowBase + mel] = (float)Math.Log(1.0 + BeatThisAssets.LogMultiplier * result[rowBase + mel]);
            }

            return result;
        }

        /// <summary>
        /// center=True 的 reflect padding：左侧镜像 x[half..1]，右侧镜像 x[n-2..n-half-1]（都不重复端点样本）。
        /// </summary>
        private static double paddedSample(float[] mono, int index, int half)
        {
            int source = index - half;

            if (source < 0)
                source = -source;

            int last = mono.Length - 1;

            if (source > last)
                source = 2 * last - source;

            return source >= 0 && source <= last ? mono[source] : 0;
        }

        #endregion

        #region 分窗推理

        /// <summary>
        /// 把整首曲子的 log-mel 按上游 split_piece 切成 513 帧的窗，逐窗推理，再按
        /// aggregate_prediction（keep_first）拼回逐帧 beat logits。返回长度 = 总帧数。
        /// </summary>
        private static void inferBeatLogits(Model m, float[] logMel, int frames,
                                           out float[] beatLogits, out float[] downbeatLogits)
        {
            const int chunk = BeatThisAssets.ChunkSize;
            const int border = BeatThisAssets.BorderSize;
            int mels = BeatThisAssets.MelBins;

            // starts = arange(-border, frames - border, chunk - 2*border)，最后一窗左移贴住结尾。
            var starts = new List<int>();

            for (int s = -border; s < frames - border; s += chunk - 2 * border)
                starts.Add(s);

            if (frames > chunk - 2 * border && starts.Count > 0)
                starts[^1] = frames - (chunk - border);

            beatLogits = new float[frames];
            downbeatLogits = new float[frames];
            Array.Fill(beatLogits, -1000f);
            Array.Fill(downbeatLogits, -1000f);

            Span<float> input = m.Input.Buffer.Span;

            // keep_first：倒序处理，让靠前的窗覆盖靠后的窗。
            for (int index = starts.Count - 1; index >= 0; index--)
            {
                int start = starts[index];

                input.Clear();

                int sourceStart = Math.Max(start, 0);
                int sourceEnd = Math.Min(start + chunk, frames);
                int left = Math.Max(0, -start);

                for (int row = sourceStart; row < sourceEnd; row++)
                {
                    int target = (left + row - sourceStart) * mels;

                    logMel.AsSpan(row * mels, mels).CopyTo(input[target..]);
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(m.InputName, m.Input),
                };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = m.Session.Run(inputs);

                float[] beat = outputs.First(o => o.Name == m.BeatName).AsTensor<float>().ToArray();
                float[]? downbeat = m.DownbeatName.Length > 0
                    ? outputs.First(o => o.Name == m.DownbeatName).AsTensor<float>().ToArray()
                    : null;

                // 丢弃两侧各 border 帧边界（模型没在输入边缘上训练过），正是 split/aggregate 的 slice。
                int copied = 0;

                for (int k = border; k < chunk - border; k++, copied++)
                {
                    int destination = start + border + copied;

                    if ((uint)destination >= (uint)frames)
                        continue;

                    beatLogits[destination] = beat[k];

                    if (downbeat is not null)
                        downbeatLogits[destination] = downbeat[k];
                }
            }
        }

        #endregion

        #region 峰值提取

        /// <summary>
        /// 上游 minimal 后处理：±3 帧内取局部极大、logit &gt; 0、相邻 ≤1 帧的峰合并，再换算成秒。
        /// </summary>
        private static List<double> pickBeats(float[] logits, int frames)
        {
            const int half = BeatThisAssets.PeakKernel / 2;

            var peaks = new List<int>();

            for (int i = 0; i < frames; i++)
            {
                float value = logits[i];

                if (value <= 0)
                    continue;

                bool isLocalMax = true;

                for (int j = Math.Max(0, i - half); j <= Math.Min(frames - 1, i + half); j++)
                {
                    if (logits[j] > value)
                    {
                        isLocalMax = false;
                        break;
                    }
                }

                if (isLocalMax)
                    peaks.Add(i);
            }

            // deduplicate_peaks(width=1)：距离 ≤1 帧的峰取滑动平均。
            var merged = new List<double>();
            int run = 0;
            double accumulator = 0;
            int previous = -1;

            foreach (int peak in peaks)
            {
                if (run > 0 && peak - previous <= BeatThisAssets.DeduplicateWidth)
                {
                    run++;
                    accumulator += (peak - accumulator) / run;
                }
                else
                {
                    if (run > 0)
                        merged.Add(accumulator);

                    run = 1;
                    accumulator = peak;
                }

                previous = peak;
            }

            if (run > 0)
                merged.Add(accumulator);

            for (int i = 0; i < merged.Count; i++)
                merged[i] /= BeatThisAssets.Fps;

            return merged;
        }

        #endregion

        #region 会话

        private static Model? loadModel()
        {
            if (!BeatThisAssets.IsReady)
            {
                UnavailableReason = "模型未下载";
                return null;
            }

            try
            {
                var options = new SessionOptions
                {
                    // 只有 2 个物理核，还要留给 D3D11 渲染线程和 BASS 混音线程；默认按逻辑核
                    // 起满 4 个自旋线程会把它们挤掉。顺序执行避免算子间并行再抢一次核。
                    IntraOpNumThreads = 2,
                    InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };

                var session = new InferenceSession(BeatThisAssets.ModelPath, options);

                string inputName = session.InputMetadata.Keys.First();
                string beatName = session.OutputMetadata.ContainsKey("beat")
                    ? "beat"
                    : session.OutputMetadata.Keys.First();
                string downbeatName = session.OutputMetadata.ContainsKey("downbeat") ? "downbeat" : string.Empty;

                int[] shape =
                [
                    1,
                    BeatThisAssets.ChunkSize,
                    BeatThisAssets.MelBins,
                ];

                var model = new Model(session, inputName, beatName, downbeatName, new DenseTensor<float>(shape));

                AppLog.Beat.Add($"ONNX 节拍模型已加载（输入 {inputName}，拍点输出 {beatName}，"
                                + $"重拍输出 {(downbeatName.Length > 0 ? downbeatName : "无")}）。", LogLevel.Important);
                return model;
            }
            catch (Exception e)
            {
                UnavailableReason = $"模型加载失败：{e.Message}";
                AppLog.Beat.Add($"ONNX 节拍模型加载失败：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        private sealed class Model
        {
            public Model(InferenceSession session, string inputName, string beatName, string downbeatName, DenseTensor<float> input)
            {
                Session = session;
                InputName = inputName;
                BeatName = beatName;
                DownbeatName = downbeatName;
                Input = input;
            }

            public InferenceSession Session { get; }
            public string InputName { get; }
            public string BeatName { get; }

            /// <summary>重拍输出名；模型没有这个头时为空串。</summary>
            public string DownbeatName { get; }

            /// <summary>复用同一个输入张量，避免每窗都重新分配 513×128 的缓冲。</summary>
            public DenseTensor<float> Input { get; }
        }

        #endregion

        /// <summary>radix-2 就地 FFT，N 必须是 2 的幂（这里是 1024）。</summary>
        private sealed class Fft
        {
            private readonly int n;
            private readonly int[] reversed;
            private readonly double[] cos;
            private readonly double[] sin;

            public Fft(int size)
            {
                n = size;
                reversed = new int[n];
                cos = new double[n / 2];
                sin = new double[n / 2];

                int bits = (int)Math.Log2(n);

                for (int i = 0; i < n; i++)
                {
                    int value = i;
                    int result = 0;

                    for (int b = 0; b < bits; b++)
                    {
                        result = (result << 1) | (value & 1);
                        value >>= 1;
                    }

                    reversed[i] = result;
                }

                for (int i = 0; i < n / 2; i++)
                {
                    double angle = -2 * Math.PI * i / n;
                    cos[i] = Math.Cos(angle);
                    sin[i] = Math.Sin(angle);
                }
            }

            public void Forward(double[] re, double[] im)
            {
                for (int i = 0; i < n; i++)
                {
                    int j = reversed[i];

                    if (j <= i)
                        continue;

                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }

                for (int length = 2; length <= n; length <<= 1)
                {
                    int half = length >> 1;
                    int step = n / length;

                    for (int start = 0; start < n; start += length)
                    {
                        for (int k = 0; k < half; k++)
                        {
                            int twiddle = k * step;
                            double wr = cos[twiddle];
                            double wi = sin[twiddle];

                            int a = start + k;
                            int b = a + half;

                            double tr = re[b] * wr - im[b] * wi;
                            double ti = re[b] * wi + im[b] * wr;

                            re[b] = re[a] - tr;
                            im[b] = im[a] - ti;
                            re[a] += tr;
                            im[a] += ti;
                        }
                    }
                }
            }
        }
    }
}
