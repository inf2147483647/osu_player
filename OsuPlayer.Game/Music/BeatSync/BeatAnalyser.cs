using ManagedBass;
using ManagedBass.Fx;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;
using KiaiCell = OsuPlayer.Game.Music.BeatSync.KiaiFeatureBuilder.Cell;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// Analyses a fully-downloaded audio file on a background thread-pool thread.
    ///
    /// Tempo is produced primarily by a spectral engine:
    ///  A) 7-band log-spaced spectral-flux envelope (native BASS FFT512 at ~86 fps);
    ///     autocorrelation peaks propose candidate tempos with their octave families,
    ///     and a full-song comb-filter grid search scores how strongly onsets align to
    ///     each grid — this resolves half/double-time octave confusion and supplies the
    ///     beat-grid phase (effects land on real beats);
    ///  B) BASS_FX beat transient decoder runs independently and only acts as a
    ///     confidence confirmation when its median tempo agrees.
    ///
    /// Kiai ("高潮") sections use a 12-bin chroma self-similarity matrix: Foote
    /// checkerboard-kernel novelty supplies structural boundaries, broadband energy
    /// supplies intensity, and cross-section similarity verifies the section repeats
    /// elsewhere (the defining property of a chorus), with an exceptional-loudness
    /// escape hatch for one-off climaxes.
    ///
    /// Designed to stay fast on weak dual-core CPUs: two streaming decode passes over
    /// the file, O(n) features and a ~1MB similarity matrix per song (~1-2 s total).
    /// </summary>
    public sealed class BeatAnalyser
    {
        private const int band_count = 7;
        private const int chroma_bins = 12;

        // Log-spaced rhythm band edges in Hz (kick fundamental → hi-hat attack).
        private static readonly double[] band_edges_hz = { 30, 70, 140, 280, 560, 1100, 2200, 5000 };
        // Low bands (kick/snare body) dominate beat tracking; energetic hi-hat 8th/16th
        // content in the top bands must not be allowed to win octave doublings.
        private static readonly double[] band_weights = { 1.5, 1.5, 1.2, 1.0, 0.7, 0.5, 0.4 };

        private const double chroma_low_hz = 90;
        private const double chroma_high_hz = 4000;

        private const double comb_min_bpm = 55;
        // 上限 200。第三次尝试抬到 300（2026-10，bench2 独立集 98 张）仍然失败，而且更彻底：
        // 把 dpBeatTrack 的密度因子先改成有界形式（clamp 到 [0.5,1.5]）再抬上限，结果是
        //   BPM 完全正确 64/98 → 17/98、八度错 23 → 66、MAE 26.99 → 84.07。
        // 结论：抬上限本身就会让速度整体右移 —— 折回 200 以内的那一步一直在做有用的工作，
        // 真正的层级判断需要一个按 osu! BPM 分布训练的前端，靠放宽上限解决不了。
        // 下面是更早两次同样失败的记录，留着避免再走一遍：
        //   1) 只放开上限：>200 折半从 39 张降到 13 张，但慢歌开始过冲，MAE 38.0 -> 41.3；
        //   2) 再让八度对手（×2/×1/2/×3/2/×2/3）进 DP 评分：过冲变本加厉，慢歌被推到
        //      2.9~4×（70→280、95→280、81→252），MAE 恶化到 47.7、命中率 49 -> 35。
        private const double comb_max_bpm = 200;
        private const double comb_kernel_ms = 50;
        private const double dp_tightness = 50; // Ellis DP beat-tracker period-prior strength (z-scored envelope)
        private const int splat_subpositions = 16; // quantised fractional positions for the folded comb kernel
        private const int low_band_top = 2; // tempo octave decided by 30..280 Hz (kick/bass/snare body)
        private const double whiten_window_s = 0.3;
        private const double whiten_floor = 0.15;

        // 八度折返的「感知速度」窗口：低于下限翻倍、高于上限减半。
        // 上限原本是 150，29 张时放宽到 200；跟着 comb_max_bpm 一起抬到 300 的尝试同样失败
        // （见上面的记录），所以维持 200。下限维持 78：训练集里 60~100 BPM 的歌普遍被报高
        // （70→142、82→167），说明它们已经过度上折，再抬下限只会更糟。
        private const double preferred_min_bpm = 78;
        private const double preferred_max_bpm = 200;

        private const double b_max_irregularity = 0.08;
        private const int b_min_beats = 16;

        private const int min_frames = 1024;
        private const double silence_level = 1e-6;

        // Kiai grid / kernel parameters.
        private const double grid_period_s = 0.5;
        private const int foote_kernel_side = 12;   // 6 s of context each side
        private const double boundary_threshold_std = 0.75;
        private const int boundary_min_distance = 8; // 4 s
        private const int snap_window_cells = 6;     // ±3 s
        // 试过把「进入候选」的门槛抬到 0.65σ（想向 0.7σ 的保留门槛靠拢、减少伪段）：段数一致
        // 13/100 -> 8/100 反而更差，IoU 0.23 -> 0.25 基本没动。kiai 的段数偏差不是这个门槛造成的，
        // 所以维持 0.45σ。
        private const double kiai_threshold_std_factor = 0.45;
        private const double climax_threshold_std_factor = 0.9;
        // 用 beatmap/ 训练后加的第三档门槛：位于 0.45σ（进入 kiai 候选）与 0.9σ（"特别高的高潮段"）
        // 之间。原来候选段必须要么"在别处重复出现"、要么达到 0.9σ 才成立，把大量只出现一次、
        // 能量却是明显抬升的副歌整段丢掉了（训练集里真值 10~13 段常常只检出 1~6 段）。
        private const double kiai_accept_std_factor = 0.7;
        private const double min_relative_std = 0.04;
        // 16(8s) -> 10(5s)：不少谱面用很短的 kiai 爆发（2~4 小节），8s 的下限直接把它们滤掉。
        private const int min_section_cells = 10;    // 5 s
        // 12(6s) -> 6(3s)：kiai 之间 3~6s 的间奏原来会被并成一段，导致段数远少于真值。
        private const int merge_gap_cells = 6;       // 3 s
        private const int section_padding_cells = 2; // 1 s
        private const double repetition_similarity = 0.72;

        /// <summary>
        /// Runs the blocking decode/analysis on a thread-pool thread.
        /// Decode channels are self-contained (not connected to the mixer) and BASS itself
        /// is thread-safe, so this never blocks the framework audio/mixer thread.
        /// </summary>
        public Task<BeatAnalysis> AnalyseAsync(byte[] audio)
            => Task.Run(() => run(audio).Result);

        public Task<BeatAnalysisDetails> AnalyseDetailedAsync(byte[] audio)
            => Task.Run(() => run(audio));

        /// <summary>
        /// 训练数据导出：只解码并构造 0.5s kiai 特征单元（不跑测速/DP，导出快得多）。
        /// 返回 (单元时间ms, 40 维原始特征, 时长s)；标签由训练器按真值 kiai 区间另贴。
        /// 与 <see cref="run"/> 走同一个 <see cref="KiaiFeatureBuilder"/>，训练/推理特征严格同源。
        /// </summary>
        public Task<(IReadOnlyList<double> TimesMs, IReadOnlyList<double[]> Features, double DurationS)> ExtractKiaiFeaturesAsync(byte[] audio)
            => Task.Run(() =>
            {
                List<Frame> frames = decodeFeatureFrames(audio);

                if (frames.Count < min_frames)
                    return (Array.Empty<double>(), Array.Empty<double[]>(), 0.0);

                double duration = frames[^1].Time;
                List<KiaiCell> cells = KiaiFeatureBuilder.Build(frames, duration);

                IReadOnlyList<double> times = cells.Select(c => c.TimeMs).ToList();
                IReadOnlyList<double[]> features = cells.Select(c => (double[])c.Raw.Clone()).ToList();

                return (times, features, duration);
            });

        private static BeatAnalysisDetails run(byte[] audio)
        {
            List<Frame> frames = decodeFeatureFrames(audio);

            if (frames.Count < min_frames)
            {
                AppLog.Beat.Add($"音频过短无法分析（{frames.Count} 帧）", LogLevel.Important);
                return new BeatAnalysisDetails(new BeatAnalysis(120, 0, 0, 0, Array.Empty<KiaiSection>()),
                    120, 0, false, null, null, null, frames.Count, frames.Count > 0 ? frames[^1].Time : 0, 0, "too-few-frames",
                    120, 0);
            }

            double duration = frames[^1].Time;
            double span = duration - frames[0].Time;
            double hopRate = span > 0 ? (frames.Count - 1) / span : 0;

            TempoEstimate engineA = estimateTempo(frames);
            // Engine B is passed to fusion whenever it produced a tempo; its beat-grid
            // irregularity is handled as a vote weight there, not a hard reject.
            BeatEstimate? engineBRaw = decodeBassFxBeats(audio, duration);
            BeatEstimate? engineB = engineBRaw is { Bpm: > 0 } ? engineBRaw : null;

            (double bpm, double confidence, double beatPhaseMs) = fuseTempo(engineA, engineB);

            // Ballroom 698 首真值训练的全局 BPM 模型：自相关最强峰在近半数曲目上是标注节拍的
            // 二倍，原生测速会系统性选错 metrical 层级，这一步专门修层级。模型只决定 BPM，
            // 相位仍由下面的网格拟合给出；模型缺失/异常时完全沿用原生结果。
            if (Onnx.BpmMl.IsAvailable)
            {
                (float[] samples, int sampleRate) = DecodeMonoSamples(audio);

                if (Onnx.BpmMl.TryDetect(samples, sampleRate, out double modelBpm)
                    && Math.Abs(modelBpm - bpm) > 0.5)
                {
                    AppLog.Beat.Add($"BPM 层级修正：原生 {bpm:0.##} → 模型 {modelBpm:0.##}");
                    bpm = modelBpm;
                }
            }

            // 融合之后再仲裁一次八度/三连音：DP 在带 3-against-2 感觉的音乐上会锁到真 tactus 的
            // 2/3 或 1/2，而两个引擎会一致地同意（说明投票救不了），只有直接考察「网格对起音的
            // 解释力」才翻得正。顺带把相位精修一遍（也用于下面的重拍对齐）。
            double fusedBpm = bpm;
            (bpm, beatPhaseMs) = refineTempoByGridFit(frames, hopRate, bpm, beatPhaseMs);

            if (Math.Abs(bpm - fusedBpm) > 0.5)
                AppLog.Beat.Add($"网格仲裁：融合值 {fusedBpm:0.##} → {bpm:0.##}");

            // The tempo engines only pin beats onto real onsets; which beat of the 4/4 measure is
            // the downbeat is a bar-level decision. Re-anchor the grid so that beatIndex % 4 == 0
            // (osu!'s measure-level effects, e.g. MenuSideFlashes outside kiai) lands on the "1".
            double phaseMs = detectDownbeatPhase(frames, bpm, beatPhaseMs);

            // 优先用 302 张谱面真值训练的轻量帧分类器（ONNX，单首推理毫秒级）；
            // 模型缺失/异常时退回 chroma+能量启发式，最坏情况与旧行为一致。
            IReadOnlyList<KiaiSection> kiai = Onnx.KiaiMl.TryDetect(frames, duration,
                out IReadOnlyList<KiaiSection>? mlKiai)
                ? mlKiai
                : detectKiaiSections(frames, duration);

            var result = new BeatAnalysis(
                // Most songs are produced at an integer BPM; snap the fused estimate to a whole
                // number so effects and the displayed BPM run on a clean grid.
                Math.Round(bpm, MidpointRounding.AwayFromZero),
                Math.Round(Math.Clamp(confidence, 0, 1), 3),
                Math.Round(phaseMs),
                engineB?.BeatCount ?? 0,
                kiai);

            var details = new BeatAnalysisDetails(result,
                Math.Round(engineA.Bpm, 2), Math.Round(engineA.Confidence, 3), engineA.Valid,
                engineBRaw is { Bpm: > 0 } ? Math.Round(engineBRaw.Bpm, 2) : null,
                engineBRaw?.RawBeatCount,
                engineBRaw is { Bpm: > 0 } ? Math.Round(engineBRaw.Irregularity, 3) : null,
                frames.Count, Math.Round(duration, 1), Math.Round(hopRate, 1),
                engineBRaw is { Valid: true } ? null : engineBRaw?.RejectReason,
                bpm, beatPhaseMs);

            AppLog.Beat.Add($"分析完成：bpm={result.Bpm} conf={result.Confidence} "
                           + $"beatPhase={Math.Round(beatPhaseMs)}ms downbeat={result.BeatPhaseMs}ms "
                           + $"beats={result.DetectedBeats} kiai={result.KiaiSections.Count} | "
                           + $"A={details.EngineABpm}(conf={details.EngineAConfidence},valid={details.EngineAValid}) "
                           + $"B={(details.EngineBBpm?.ToString() ?? "-")}(irr={details.EngineBIrregularity?.ToString() ?? "-"},"
                           + $"raw={details.EngineBBeats?.ToString() ?? "-"},reject={details.EngineBRejectReason ?? "-"}) "
                           + $"| frames={details.FrameCount} dur={details.Duration}s hop={details.HopRate}",
                LogLevel.Important);

            return details;
        }

        #region Decode / features

        internal readonly struct Frame
        {
            public readonly float Time;
            public readonly float Total;
            public readonly float Band0, Band1, Band2, Band3, Band4, Band5, Band6;
            public readonly float Chroma0, Chroma1, Chroma2, Chroma3, Chroma4, Chroma5, Chroma6,
                                       Chroma7, Chroma8, Chroma9, Chroma10, Chroma11;
            // 16-bin mel 能量只喂给 kiai ML 特征（聚合成 0.5s 单元后做 DCT 得 MFCC），
            // 不参与现有节拍/启发式 kiai 的任何计算。
            public readonly float Mel0, Mel1, Mel2, Mel3, Mel4, Mel5, Mel6, Mel7,
                                      Mel8, Mel9, Mel10, Mel11, Mel12, Mel13, Mel14, Mel15;

            public Frame(float time, float[] bands, float[] chroma, float[] mel, float total)
            {
                Time = time;
                Total = total;
                Band0 = bands[0];
                Band1 = bands[1];
                Band2 = bands[2];
                Band3 = bands[3];
                Band4 = bands[4];
                Band5 = bands[5];
                Band6 = bands[6];
                Chroma0 = chroma[0];
                Chroma1 = chroma[1];
                Chroma2 = chroma[2];
                Chroma3 = chroma[3];
                Chroma4 = chroma[4];
                Chroma5 = chroma[5];
                Chroma6 = chroma[6];
                Chroma7 = chroma[7];
                Chroma8 = chroma[8];
                Chroma9 = chroma[9];
                Chroma10 = chroma[10];
                Chroma11 = chroma[11];
                Mel0 = mel[0];
                Mel1 = mel[1];
                Mel2 = mel[2];
                Mel3 = mel[3];
                Mel4 = mel[4];
                Mel5 = mel[5];
                Mel6 = mel[6];
                Mel7 = mel[7];
                Mel8 = mel[8];
                Mel9 = mel[9];
                Mel10 = mel[10];
                Mel11 = mel[11];
                Mel12 = mel[12];
                Mel13 = mel[13];
                Mel14 = mel[14];
                Mel15 = mel[15];
            }

            public float GetBand(int i) => i switch
            {
                0 => Band0,
                1 => Band1,
                2 => Band2,
                3 => Band3,
                4 => Band4,
                5 => Band5,
                _ => Band6,
            };

            public float GetChroma(int i) => i switch
            {
                0 => Chroma0,
                1 => Chroma1,
                2 => Chroma2,
                3 => Chroma3,
                4 => Chroma4,
                5 => Chroma5,
                6 => Chroma6,
                7 => Chroma7,
                8 => Chroma8,
                9 => Chroma9,
                10 => Chroma10,
                _ => Chroma11,
            };

            public float GetMel(int i) => i switch
            {
                0 => Mel0,
                1 => Mel1,
                2 => Mel2,
                3 => Mel3,
                4 => Mel4,
                5 => Mel5,
                6 => Mel6,
                7 => Mel7,
                8 => Mel8,
                9 => Mel9,
                10 => Mel10,
                11 => Mel11,
                12 => Mel12,
                13 => Mel13,
                14 => Mel14,
                _ => Mel15,
            };
        }

        // Native BASS FFT512 decoding. FFT2048 advances a full 2048-sample window per call on
        // decode channels (~21.5 fps), which quantises beat lags around 120 BPM to ~9 integer
        // values; FFT512 advances 512 samples (~86 fps) — 4x finer lag grid at zero CPU cost
        // since the transform runs in native BASS. 512-point bins are ~86 Hz wide, plenty for
        // the 30 Hz..5 kHz rhythm bands and coarse 12-bin kiai chroma.
        private const int fft_bins = 256;

        private static List<Frame> decodeFeatureFrames(byte[] audio)
        {
            int handle = Bass.CreateStream(audio, 0, audio.Length, BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
                throw new InvalidOperationException($"BASS decode channel creation failed (error {Bass.LastError}).");

            var frames = new List<Frame>(16384);
            var fft = new float[fft_bins];
            var bands = new float[band_count];
            var chroma = new float[chroma_bins];
            var mel = new float[KiaiFeatureBuilder.MelBins];

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);

                int[] bandStartBin = new int[band_count];
                int[] bandEndBin = new int[band_count];

                for (int b = 0; b < band_count; b++)
                {
                    bandStartBin[b] = Math.Clamp((int)(band_edges_hz[b] * 512 / info.Frequency), 1, fft_bins - 1);
                    bandEndBin[b] = Math.Clamp((int)(band_edges_hz[b + 1] * 512 / info.Frequency), 1, fft_bins - 1);
                }

                int chromaStartBin = Math.Clamp((int)(chroma_low_hz * 512 / info.Frequency), 1, fft_bins - 1);
                int chromaEndBin = Math.Clamp((int)(chroma_high_hz * 512 / info.Frequency), 1, fft_bins - 1);

                // 每个 FFT 频点对 mel 三角滤波器的非零贡献（稀疏，每点最多落在 2 个滤波器里），
                // 循环外算一次，解码内层只做乘加。
                (int Mel, double Weight)[][] melContrib = KiaiFeatureBuilder.BuildSpectrumMelContributions(info.Frequency);

                double totalLength = Bass.ChannelBytes2Seconds(handle, Bass.ChannelGetLength(handle, PositionFlags.Bytes));

                while (true)
                {
                    long before = Bass.ChannelGetPosition(handle, PositionFlags.Bytes);

                    int consumed = Bass.ChannelGetData(handle, fft, (int)(DataFlags.FFT512 | DataFlags.FFTRemoveDC));

                    if (consumed <= 0)
                        break;

                    long after = Bass.ChannelGetPosition(handle, PositionFlags.Bytes);
                    double centre = Bass.ChannelBytes2Seconds(handle, (before + after) / 2);

                    if (centre >= totalLength - 0.01)
                        break;

                    Array.Clear(bands);
                    Array.Clear(chroma);
                    Array.Clear(mel);

                    for (int b = 0; b < band_count; b++)
                    {
                        float energy = 0;
                        for (int k = bandStartBin[b]; k <= bandEndBin[b] && k < fft_bins; k++)
                            energy += fft[k];
                        bands[b] = energy;
                    }

                    for (int k = 0; k < melContrib.Length; k++)
                    {
                        (int melBin, double weight)[] contributions = melContrib[k];

                        for (int i = 0; i < contributions.Length; i++)
                            mel[contributions[i].melBin] += (float)(contributions[i].weight * fft[k]);
                    }

                    float total = 0;

                    for (int k = chromaStartBin; k <= chromaEndBin && k < fft_bins; k++)
                    {
                        double frequency = (k + 0.5) * info.Frequency / 512.0;
                        double midi = 69 + 12 * Math.Log2(frequency / 440.0);
                        int pitchClass = ((int)Math.Round(midi) % chroma_bins + chroma_bins) % chroma_bins;
                        chroma[pitchClass] += fft[k];
                        total += fft[k];
                    }

                    frames.Add(new Frame((float)centre, bands, chroma, mel, total));
                }
            }
            finally
            {
                Bass.StreamFree(handle);
            }

            return frames;
        }

        /// <summary>
        /// 解码为单声道 float 采样，供 BPM 模型的 onset/自相关管线使用。
        /// 与 <see cref="decodeFeatureFrames"/> 各自独立解码：两者时间基准不同（这里是采样点，
        /// 那边是 BASS 的 FFT512 帧中心），混用会引入对不齐的误差。
        /// </summary>
        internal static (float[] Samples, int SampleRate) DecodeMonoSamples(byte[] audio)
        {
            int handle = Bass.CreateStream(audio, 0, audio.Length, BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
                throw new InvalidOperationException($"BASS decode channel creation failed (error {Bass.LastError}).");

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);
                int channels = Math.Max(1, info.Channels);
                var buffer = new float[1 << 16];
                var mono = new List<float>(1 << 20);

                while (true)
                {
                    int consumed = Bass.ChannelGetData(handle, buffer, buffer.Length * sizeof(float));

                    if (consumed <= 0)
                        break;

                    int count = consumed / sizeof(float);

                    if (channels == 1)
                    {
                        for (int i = 0; i < count; i++)
                            mono.Add(buffer[i]);
                    }
                    else
                    {
                        int frameCount = count / channels;

                        for (int f = 0; f < frameCount; f++)
                        {
                            float sum = 0;

                            for (int c = 0; c < channels; c++)
                                sum += buffer[f * channels + c];

                            mono.Add(sum / channels);
                        }
                    }
                }

                return (mono.ToArray(), info.Frequency);
            }
            finally
            {
                Bass.StreamFree(handle);
            }
        }

        #endregion

        #region Tempo engine: multiband spectral flux + comb-filter beat alignment

        private readonly record struct TempoHypothesis(double Bpm, double Score, double PhaseMs);

        private readonly record struct TempoEstimate(double Bpm, double Confidence, bool Valid, double PhaseMs, IReadOnlyList<TempoHypothesis> Candidates);

        private static TempoEstimate estimateTempo(IReadOnlyList<Frame> frames)
        {
            int n = frames.Count;

            double meanTotal = 0;
            for (int i = 0; i < n; i++)
                meanTotal += frames[i].Total;
            meanTotal /= n;

            if (meanTotal < silence_level)
                return new TempoEstimate(120, 0, false, 0, Array.Empty<TempoHypothesis>());

            // Per-band mean level, used to normalise the log-flux contributions.
            var bandMeans = new double[band_count];

            for (int b = 0; b < band_count; b++)
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                    sum += frames[i].GetBand(b);
                bandMeans[b] = Math.Max(sum / n, 1e-9);
            }

            // Per-band half-wave-rectified log-flux rises, then DIXON ADAPTIVE WHITENING:
            // divide each rise by the local mean level (±whiten_window_s) plus a floor
            // tied to the strongest rise in the band. Sustained, machine-constant 8th/16th
            // patterns (piano comping, hats, bass 8ths — even inside the low bands) divide
            // down toward ~1 and stop masquerading as the beat; sparse but strong drum
            // onsets over quieter surroundings keep their salience. This is the key step
            // that prevents "most consistent subdivision wins" over whole-song averaging.
            var rises = new double[band_count][];
            var whitened = new double[band_count][];

            int window = Math.Max(1, (int)(whiten_window_s * 86));

            for (int b = 0; b < band_count; b++)
            {
                rises[b] = new double[n];
                whitened[b] = new double[n];

                double maxRise = 1e-9;

                for (int i = 1; i < n; i++)
                {
                    double current = Math.Log(1 + 10 * frames[i].GetBand(b) / bandMeans[b]);
                    double previous = Math.Log(1 + 10 * frames[i - 1].GetBand(b) / bandMeans[b]);
                    double rise = Math.Max(0, current - previous);
                    rises[b][i] = rise;
                    if (rise > maxRise) maxRise = rise;
                }

                double floor = whiten_floor * maxRise;
                double running = 0;

                for (int i = 0; i < n; i++)
                {
                    if (i == 0)
                    {
                        for (int j = 0; j <= window && j < n; j++)
                            running += rises[b][j];
                    }
                    else
                    {
                        int add = i + window;
                        if (add < n) running += rises[b][add];
                        int remove = i - window - 1;
                        if (remove >= 0) running -= rises[b][remove];
                    }

                    int count = Math.Min(n - 1, i + window) - Math.Max(0, i - window) + 1;
                    whitened[b][i] = rises[b][i] / (running / count + floor);
                }
            }

            // TWO envelopes: the beat is defined by the low register (kick/bass/snare
            // body, 30..280 Hz). The all-band envelope only votes when the low register
            // carries no usable pulse.
            double[] lowFlux = new double[n];
            double[] fullFlux = new double[n];

            for (int i = 1; i < n; i++)
            {
                for (int b = 0; b < band_count; b++)
                {
                    fullFlux[i] += band_weights[b] * whitened[b][i];
                    if (b <= low_band_top)
                        lowFlux[i] += band_weights[b] * whitened[b][i];
                }
            }

            // Real frame hop rate from timestamps (do not assume a sample rate).
            double span = frames[n - 1].Time - frames[0].Time;
            double hopRate = span > 0 ? (n - 1) / span : 86;

            if (n / 3.0 < hopRate * 60 / comb_min_bpm)
                return new TempoEstimate(120, 0, false, 0, Array.Empty<TempoHypothesis>());

            // DP path scores (density-weighted z-score scale): a convincing drum pulse
            // sits around 2+, an absent/ambiguous pulse below ~1.4.
            const double pulse_score_threshold = 1.4;

            (double bestBpm, double bestScore, double bestPhase, var lowCandidates) = scanTempo(lowFlux, hopRate);
            List<(double bpm, double score, double phase)> allCandidates = lowCandidates;

            if (bestScore < pulse_score_threshold)
            {
                (double fb, double fs, double fp, var fc) = scanTempo(fullFlux, hopRate);

                // Full-band candidates join the vote even if they do not win outright.
                allCandidates = lowCandidates.Concat(fc).ToList();

                if (fs > bestScore)
                {
                    bestBpm = fb;
                    bestScore = fs;
                    bestPhase = fp;
                }
            }

            bool valid = bestScore >= pulse_score_threshold;

            TempoHypothesis foldHypothesis(double bpm, double score, double phaseFrames)
            {
                double phaseMs = phaseFrames / hopRate * 1000;

                while (bpm < preferred_min_bpm)
                {
                    bpm *= 2;
                    phaseMs %= 60000 / bpm;
                }

                while (bpm > preferred_max_bpm)
                    bpm /= 2;

                return new TempoHypothesis(bpm, score, phaseMs);
            }

            // Winner + every alternative hypothesis, folded into the 78..300 perceptual
            // window; fusion votes across them together with the independent BASS_FX engine.
            var hypotheses = new List<TempoHypothesis> { foldHypothesis(bestBpm, bestScore, bestPhase) };

            foreach ((double bpm, double score, double phase) in allCandidates)
            {
                if (score <= 0) continue;
                var h = foldHypothesis(bpm, score, phase);
                if (hypotheses.All(x => Math.Abs(x.Bpm - h.Bpm) / h.Bpm > 0.03))
                    hypotheses.Add(h);
            }

            double topWeight = hypotheses[0].Score;
            double secondWeight = hypotheses.Count > 1 ? hypotheses[1].Score : 0;
            double confidence = Math.Clamp(0.3 + 1.2 * (topWeight - secondWeight) / Math.Max(topWeight, 0.2), 0.1, 0.9);

            return new TempoEstimate(hypotheses[0].Bpm, confidence, valid, hypotheses[0].PhaseMs, hypotheses);
        }

        /// <summary>
        /// Two-stage tempo induction:
        /// 1. Coarse Klapuri-style comb proposal (1 BPM steps, fixed 50 ms Gaussian
        /// kernel). The kernel is fold-accumulated by frame position modulo the period
        /// (O(n·kernel) per tempo instead of O(n·period·kernel)), which makes a 146-tempi
        /// scan of a 4-minute song cost milliseconds.
        /// 2. Ellis-style DP beat tracking around every strong coarse peak. The static
        /// grid cannot tell a tatum/phrase pattern from the beat (a 3-over-4 bass riff or
        /// constant 8th piano comping scores as strongly as the drum pulse); Viterbi
        /// places beats on the strongest local onsets while a log-spacing penalty keeps
        /// consecutive beats near the prior period. Paths are scored by explained onset
        /// energy PER SECOND (not per beat — mean-per-beat systematically prefers
        /// half-tempo paths whose fewer beats all land on the loudest spikes). Reported
        /// tempo comes from the median inter-beat interval of the winning path.
        /// </summary>
        private static (double bpm, double score, double phase, List<(double bpm, double score, double phase)> coarse)
            scanTempo(double[] flux, double hopRate)
        {
            int n = flux.Length;

            double meanFlux = 0;
            double varFlux = 0;
            for (int i = 0; i < n; i++)
                meanFlux += flux[i];
            meanFlux /= n;
            for (int i = 0; i < n; i++)
            {
                double d = flux[i] - meanFlux;
                varFlux += d * d;
            }

            double stdFlux = Math.Sqrt(varFlux / n);

            if (stdFlux < 1e-9)
                return (120, 0, 0, new List<(double, double, double)>());

            var z = new double[n];
            for (int i = 0; i < n; i++)
                z[i] = (flux[i] - meanFlux) / stdFlux;

            // Fixed-width Gaussian splat kernel, pre-integrated against 16 fractional
            // sub-frame positions (no exp() inside the hot loops).
            double sigma = comb_kernel_ms / 1000.0 * hopRate;
            int radius = (int)Math.Ceiling(2.5 * sigma);
            var splatWeights = new double[splat_subpositions, radius * 2 + 1];
            double gaussianEnergy = 0;

            for (int j = -radius; j <= radius; j++)
            {
                double w = Math.Exp(-0.5 * j * j / (sigma * sigma));
                gaussianEnergy += w * w;

                for (int s = 0; s < splat_subpositions; s++)
                {
                    double frac = (s + 0.5) / splat_subpositions;
                    double dist = j - frac;
                    splatWeights[s, j + radius] = Math.Exp(-0.5 * dist * dist / (sigma * sigma));
                }
            }

            var coarse = new List<(double bpm, double score, double phase)>();

            for (double bpm = comb_min_bpm; bpm <= comb_max_bpm + 0.01; bpm += 2)
            {
                double score = foldedCombScore(z, bpm, hopRate, n, radius, splatWeights, gaussianEnergy);
                score *= tempoPrior(bpm);
                coarse.Add((bpm, score, 0));
            }

            var localMaxima = new List<(double bpm, double score)>();

            for (int i = 1; i < coarse.Count - 1; i++)
            {
                if (coarse[i].score >= coarse[i - 1].score && coarse[i].score > coarse[i + 1].score)
                    localMaxima.Add((coarse[i].bpm, coarse[i].score));
            }

            var dpResults = new List<(double bpm, double score, double phase)>();

            // 曾把每个粗扫峰的八度对手（×2/×1/2/×3/2/×2/3）也送进 DP，想借密度因子让真正的
            // tactus 参赛取胜；302 张实测为负结果（慢歌被推到 2.9~4×，MAE 38.0 -> 47.7），撤销。
            foreach ((double centreBpm, _) in localMaxima.OrderByDescending(m => m.score).Take(6))
                dpResults.Add(dpBeatTrack(z, centreBpm, hopRate));

            // Adjacent coarse peaks (±1.5%) all converge onto the same DP path; merge so
            // the rival score is not computed against duplicate hypotheses.
            var deduped = new List<(double bpm, double score, double phase)>();

            foreach ((double bpm, double score, double phase) in dpResults.OrderByDescending(r => r.score))
            {
                if (score == double.MinValue) continue;
                if (deduped.Any(r => Math.Abs(r.bpm - bpm) / bpm < 0.015)) continue;
                deduped.Add((bpm, score, phase));
            }

            double bestScore = double.MinValue;
            double bestBpm = 120;
            double bestPhase = 0;

            foreach ((double bpm, double score, double phase) in deduped)
            {
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBpm = bpm;
                    bestPhase = phase;
                }
            }

            if (bestScore == double.MinValue)
                (bestBpm, bestScore, bestPhase) = coarse.MaxBy(c => c.score);

            return (bestBpm, bestScore, bestPhase, deduped);
        }

        /// <summary>
        /// Fold-accumulated comb score: every frame splats its z-scored energy into a
        /// periodic bin grid (Gaussian kernel, periodic wrap), and the best bin gives the
        /// comb response. Equal to shifting the kernel over every grid phase, but runs in
        /// O(n·kernelRadius) per tempo regardless of the period.
        /// </summary>
        private static double foldedCombScore(double[] z, double bpm, double hopRate, int n, int radius, double[,] splatWeights, double gaussianEnergy)
        {
            double period = hopRate * 60 / bpm;
            int bins = (int)Math.Ceiling(period);
            var acc = new double[bins];

            double pos = 0;

            for (int i = 0; i < n; i++, pos += 1)
            {
                if (pos >= period)
                    pos -= period;

                double zi = z[i];

                int baseBin = (int)pos;
                int sub = Math.Min(splat_subpositions - 1, (int)((pos - baseBin) * splat_subpositions));

                for (int j = -radius; j <= radius; j++)
                {
                    int b = baseBin + j;

                    // 按周期取模叠加。这里必须允许多次环绕：原先「单次环绕足够」的写法依赖
                    // radius < 周期，而上限提到 300 后，300 BPM 处周期只剩约 17 帧、kernel
                    // 半径约 11 帧，单次环绕不再成立，高分速度的得分会算错。
                    b %= bins;
                    if (b < 0) b += bins;

                    acc[b] += zi * splatWeights[sub, j + radius];
                }
            }

            // Peak above the fold mean: z is zero-mean globally, but subtract explicitly so
            // a constant background offset cannot inflate dense (fast) tempi.
            double accMean = 0;
            for (int b = 0; b < bins; b++)
                accMean += acc[b];
            accMean /= bins;

            double max = double.MinValue;
            for (int b = 0; b < bins; b++)
            {
                if (acc[b] > max)
                    max = acc[b];
            }

            return (max - accMean) / Math.Sqrt(n / period * gaussianEnergy);
        }

        /// <summary>
        /// Ellis-style dynamic-programming beat tracker with a fixed period prior.
        /// Returns tempo from the median beat interval, the per-beat path score
        /// (mean onset strength plus accumulated spacing penalty) and phase in frames.
        /// </summary>
        private static (double bpm, double score, double phaseFrame) dpBeatTrack(double[] z, double priorBpm, double hopRate)
        {
            int n = z.Length;
            double tau = hopRate * 60 / priorBpm;
            int winMin = Math.Max(2, (int)Math.Floor(tau * 0.5));
            int winMax = Math.Max(winMin + 1, (int)Math.Ceiling(tau * 2.0));

            var cumulative = new double[n];
            var backlink = new int[n];

            for (int t = 0; t < n; t++)
            {
                int to = t - winMin;

                if (to < 0)
                {
                    // Warm-up region: a path may start here at zero predecessor cost.
                    cumulative[t] = z[t];
                    backlink[t] = -1;
                    continue;
                }

                int from = Math.Max(0, t - winMax);
                double best = 0;
                int bestFrom = -1;

                for (int f = from; f <= to; f++)
                {
                    double logRatio = Math.Log((t - f) / tau);
                    double candidate = cumulative[f] - dp_tightness * logRatio * logRatio;

                    if (candidate > best)
                    {
                        best = candidate;
                        bestFrom = f;
                    }
                }

                cumulative[t] = z[t] + best;
                backlink[t] = bestFrom;
            }

            int endFrame = n - 1;
            double endBest = double.MinValue;

            for (int t = Math.Max(0, n - winMax * 2); t < n; t++)
            {
                if (cumulative[t] > endBest)
                {
                    endBest = cumulative[t];
                    endFrame = t;
                }
            }

            var beats = new List<int>();

            for (int cur = endFrame; cur >= 0; cur = backlink[cur])
                beats.Add(cur);

            if (beats.Count < 4)
                return (priorBpm, double.MinValue, 0);

            beats.Sort();

            // Drop the warm-up seed region; a path starting beat is a predecessor-less
            // spike and need not sit one period from the next beat.
            beats.RemoveAt(0);

            if (beats.Count < 4)
                return (priorBpm, double.MinValue, 0);

            var intervals = new double[beats.Count - 1];
            for (int i = 1; i < beats.Count; i++)
                intervals[i - 1] = beats[i] - beats[i - 1];

            var sortedIntervals = (double[])intervals.Clone();
            Array.Sort(sortedIntervals);
            double median = sortedIntervals[sortedIntervals.Length / 2];

            if (median <= 0)
                return (priorBpm, double.MinValue, 0);

            double bpm = hopRate * 60 / median;

            if (bpm is < comb_min_bpm or > comb_max_bpm)
                return (priorBpm, double.MinValue, 0);

            // Per-beat path score: mean onset strength at beats plus the mean transition
            // penalty the prior had to pay. Comparable across candidate tempi.
            double strengthSum = 0;
            foreach (int beat in beats)
                strengthSum += z[beat];

            double meanStrength = strengthSum / beats.Count;
            double meanPenalty = (endBest - strengthSum) / beats.Count;

            double deviationSum = 0;
            foreach (double interval in intervals)
                deviationSum += Math.Abs(interval - median) / median;

            double irregularity = deviationSum / intervals.Length;
            // Explain energy per SECOND: multiplying by beat density (bpm/120) stops the
            // scorer preferring half-tempo paths whose few beats all hit the loudest spikes.
            //
            // 试过把它改成有界形式（clamp 到 [0.5,1.5]）以便安全地抬高 comb_max_bpm：
            // bench2 上完全正确 64/98 → 17/98，比无界时更差。故维持原样（见 comb_max_bpm 处的记录）。
            double score = (meanStrength + meanPenalty)
                           * (1 - Math.Clamp(2 * irregularity, 0, 0.6))
                           * (bpm / 120.0)
                           * tempoPrior(bpm);

            double phaseFrame = beats[0] % median;

            return (bpm, score, phaseFrame);
        }

        private static double tempoPrior(double bpm)
            // Very mild centring prior (~118 BPM) only breaks genuine ties.
            => 1 + 0.06 * Math.Exp(-0.5 * Math.Pow((bpm - 118) / 60.0, 2));

        #endregion

        #region Tempo engine B: BASS_FX beat transients

        private sealed class BeatEstimate
        {
            public required double Bpm;
            public required double PhaseMs;
            public required int BeatCount;
            public required int RawBeatCount;
            public required double Irregularity;
            public required bool Valid;
            public required string RejectReason;
        }

        private static BeatEstimate? decodeBassFxBeats(byte[] audio, double duration)
        {
            int handle = Bass.CreateStream(audio, 0, audio.Length, BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
                return new BeatEstimate
                {
                    Bpm = 0, PhaseMs = 0, BeatCount = 0, RawBeatCount = 0,
                    Irregularity = 0, Valid = false, RejectReason = $"stream-create({Bass.LastError})",
                };

            var beatTimes = new List<double>(1024);
            BPMBeatProcedure callback = (_, beatPosition, _) => beatTimes.Add(beatPosition);

            try
            {
                double totalSeconds = Bass.ChannelBytes2Seconds(handle, Bass.ChannelGetLength(handle, PositionFlags.Bytes));

                if (!BassFx.BPMBeatDecodeGet(handle, 0, totalSeconds, BassFlags.Default, callback, IntPtr.Zero))
                    return new BeatEstimate
                    {
                        Bpm = 0, PhaseMs = 0, BeatCount = 0, RawBeatCount = beatTimes.Count,
                        Irregularity = 0, Valid = false, RejectReason = $"decode-get({Bass.LastError})",
                    };
            }
            finally
            {
                Bass.StreamFree(handle);
            }

            if (beatTimes.Count < b_min_beats || duration <= 0)
                return new BeatEstimate
                {
                    Bpm = 0, PhaseMs = 0, BeatCount = 0, RawBeatCount = beatTimes.Count,
                    Irregularity = 0, Valid = false, RejectReason = "too-few-beats",
                };

            var intervals = new List<double>(beatTimes.Count - 1);

            for (int i = 1; i < beatTimes.Count; i++)
            {
                double interval = beatTimes[i] - beatTimes[i - 1];

                // Keep plausible beat intervals (0.25 s .. 1.2 s ⇒ 50..240 BPM);
                // fills/breaks occasionally produce double/half gaps.
                if (interval is > 0.25 and < 1.2)
                    intervals.Add(interval);
            }

            if (intervals.Count < b_min_beats / 2)
                return new BeatEstimate
                {
                    Bpm = 0, PhaseMs = beatTimes[0] * 1000, BeatCount = intervals.Count, RawBeatCount = beatTimes.Count,
                    Irregularity = 0, Valid = false, RejectReason = "too-few-intervals",
                };

            intervals.Sort();
            double median = intervals[intervals.Count / 2];

            double deviationSum = 0;
            int regularCount = 0;

            foreach (double interval in intervals)
            {
                double relative = Math.Abs(interval - median) / median;
                deviationSum += relative;

                if (relative < 0.15)
                    regularCount++;
            }

            double meanDeviation = deviationSum / intervals.Count;
            bool regular = meanDeviation <= b_max_irregularity && regularCount >= intervals.Count * 0.6;

            return new BeatEstimate
            {
                Bpm = foldPreferred(60 / median),
                PhaseMs = beatTimes[0] * 1000,
                BeatCount = intervals.Count,
                RawBeatCount = beatTimes.Count,
                Irregularity = meanDeviation,
                Valid = regular,
                RejectReason = regular ? string.Empty : $"irregular(dev={meanDeviation:F2},regular={regularCount}/{intervals.Count})",
            };
        }

        #endregion

        #region Fusion

        private static double foldPreferred(double bpm)
        {
            while (bpm < preferred_min_bpm)
                bpm *= 2;
            while (bpm > preferred_max_bpm)
                bpm /= 2;

            return Math.Clamp(bpm, comb_min_bpm, comb_max_bpm);
        }

        private const double vote_cluster_tolerance = 0.06;

        private static (double bpm, double confidence, double phaseMs) fuseTempo(TempoEstimate a, BeatEstimate? b)
        {
            if (!a.Valid || a.Candidates.Count == 0)
            {
                if (b is { Bpm: > 0 })
                    return (foldPreferred(b.Bpm), Math.Clamp(1 - b.Irregularity * 3, 0.5, 0.9), b.PhaseMs);

                return (120, 0, 0);
            }

            // 两个独立引擎一致时直接采信。A（谱通量自相关 + 梳状滤波）和 B（BASS_FX 拍点解码器）
            // 是完全不同的方法，同时落在同一个速度（±2%）比任何投票组合都可靠 —— 训练集里
            // 1849048 的日志是 A=132.5、B=132.9，却因为 A 的多个折叠候选把一个错误速度堆成了
            // 最高分而输出 103；这条短路正是为这种「一致却被推翻」的情况准备的。
            if (b is { Bpm: > 0, Irregularity: < 0.35 } && Math.Abs(a.Bpm - b.Bpm) / a.Bpm <= 0.02)
                return (a.Bpm, Math.Clamp(0.75 + (1 - b.Irregularity) * 0.2, 0.5, 0.95), a.PhaseMs);

            // Joint weighted vote over engine A's DP hypotheses and the independent
            // BASS_FX tempo. A regular BASS_FX grid (low irregularity) is strong evidence
            // exactly in the cases the spectral-flux tracker locks onto a tatum/phrase
            // pattern (e.g. melodic bass riffs); a jittery grid only gets a weak vote.
            var votes = new List<(double bpm, double weight, double phaseMs, bool fromEngineB)>();

            foreach (TempoHypothesis h in a.Candidates)
                votes.Add((h.Bpm, h.Score, h.PhaseMs, false));

            if (b is { Bpm: > 0 })
            {
                double bWeight = Math.Clamp(3.2 - b.Irregularity * 10, 0.5, 3.0);
                votes.Add((foldPreferred(b.Bpm), bWeight, b.PhaseMs, true));
            }

            // Greedy clusters by descending weight; merge within ±6% of the cluster seed
            // (seed-based, not centroid-based, to avoid a chain of hypotheses spanning a
            // much wider tempo range than one tempo estimate).
            double strongestAWeight = votes.Where(v => !v.fromEngineB).Select(v => v.weight).DefaultIfEmpty(0).Max();

            var clusters = new List<(double weightSum, double weightedBpm, double weightCount, double seedBpm, double bestAWeight, double aPhaseMs, bool hasEngineA, bool hasTopA)>();

            foreach (var vote in votes.OrderByDescending(v => v.weight))
            {
                int found = -1;

                for (int i = 0; i < clusters.Count; i++)
                {
                    if (Math.Abs(clusters[i].seedBpm - vote.bpm) / clusters[i].seedBpm <= vote_cluster_tolerance)
                    {
                        found = i;
                        break;
                    }
                }

                bool isTopA = !vote.fromEngineB && Math.Abs(vote.weight - strongestAWeight) < 1e-9;

                if (found < 0)
                {
                    clusters.Add((vote.weight, vote.weight * vote.bpm, vote.weight, vote.bpm,
                                  vote.fromEngineB ? double.MinValue : vote.weight,
                                  vote.fromEngineB ? 0 : vote.phaseMs, !vote.fromEngineB, isTopA));
                }
                else
                {
                    var c = clusters[found];
                    c.weightSum += vote.weight;
                    c.weightedBpm += vote.weight * vote.bpm;
                    c.weightCount += vote.weight;
                    c.hasTopA |= isTopA;

                    if (!vote.fromEngineB && vote.weight > c.bestAWeight)
                    {
                        c.bestAWeight = vote.weight;
                        c.aPhaseMs = vote.phaseMs;
                    }

                    c.hasEngineA |= !vote.fromEngineB;
                    clusters[found] = c;
                }
            }

            var winner = clusters.MaxBy(c => c.weightSum);
            double winnerBpm = winner.weightedBpm / winner.weightCount;
            double winnerPhase = winner.hasEngineA ? winner.aPhaseMs : b!.PhaseMs;

            double rivalWeight = clusters.Where(c => c != winner)
                                         .Select(c => c.weightSum)
                                         .DefaultIfEmpty(0)
                                         .Max();

            double confidence = Math.Clamp(0.45 + 1.3 * (winner.weightSum - rivalWeight) / winner.weightSum, 0.2, 0.95);

            // A winner that only exists because the second engine out-voted engine A's own
            // strongest hypothesis is inherently shaky — flag it as low confidence.
            if (!winner.hasTopA)
                confidence = Math.Min(confidence, 0.55);

            return (winnerBpm, confidence, winnerPhase);
        }

        #endregion

        #region 网格-起音仲裁

        /// <summary>
        /// 仲裁候选。真 tactus 一定落在融合值的这些整数比之内；1.5 与 2/3 对应三连音层面的混淆，
        /// 3 与 1/3 覆盖极端情况。
        /// </summary>
        private static readonly double[] grid_fit_ratios = { 1.0, 2.0, 0.5, 1.5, 2.0 / 3.0, 3.0, 1.0 / 3.0 };

        /// <summary>
        /// 推翻融合值所需的 F 值优势。100 张基准上扫过 0.04 / 0.08 / 0.15：0.15 会挡掉全部修正
        /// （等于没开仲裁）；0.04 修得最狠（MAE 26.2、八度错 4）但把本来正确的改坏，全量 302 上
        /// 「完全正确」49 -> 40；0.08 是最稳的点 —— 全量 302 上完全正确守住 49、首拍 71 -> 65ms，
        /// 同时 MAE 38.0 -> 32.3、八度错 12 -> 7（基准集上）。
        ///
        /// 另外试过一道「融合值自身 F 已 ≥0.9 就不换八度」的保护，实测完全无效：被判错改的那些
        /// 谱面，其融合值的 F 本来就低于 0.9 —— F 判据不足以区分「本来就对」和「真的折半」。
        /// </summary>
        private const double grid_fit_margin = 0.08;

        /// <summary>
        /// 对拍速做八度/三连音仲裁，只改速度、相位沿用融合值。
        ///
        /// DP 求的是「最省力的周期路径」，在带 3-against-2 / shuffle 感觉的音乐上会锁到真 tactus
        /// 的 2/3 或 1/2 上 —— 100 张基准里就有 13 张落在 2/3、14 张落在 1/2，而且两个引擎还
        /// 一致地同意（说明融合投票救不了）。这里改问一个更本质的问题：
        /// **哪个周期网格是「上强下弱」的**，也就是网格点上的起音包络明显强于网格中点。
        /// </summary>
        private static (double bpm, double phaseMs) refineTempoByGridFit(IReadOnlyList<Frame> frames, double hopRate,
                                                                         double bpm, double phaseMs)
        {
            int n = frames.Count;

            if (n < 64 || hopRate <= 0 || bpm <= 0)
                return (bpm, phaseMs);

            double[] envelope = buildOnsetEnvelope(frames);

            // 包络的幅度尺度因曲而异，护栏的余量必须相对它来定，所以先取一个标准差当尺度。
            double average = 0;

            for (int i = 0; i < n; i++)
                average += envelope[i];

            average /= n;

            double variance = 0;

            for (int i = 0; i < n; i++)
            {
                double d = envelope[i] - average;
                variance += d * d;
            }

            double deviation = Math.Sqrt(variance / n);

            if (deviation < 1e-9)
                return (bpm, phaseMs);

            // 起音峰 + 邻域：网格点与起音只要落进彼此 ±tol 帧就算命中（帧长约 10ms，±2 帧 ≈ ±22ms）。
            const int tolerance = 2;
            var nearOnset = new bool[n];
            var onsets = new List<int>();

            for (int i = 1; i < n - 1; i++)
            {
                if (envelope[i] < average + 0.5 * deviation)
                    continue;

                if (envelope[i] >= envelope[i - 1] && envelope[i] > envelope[i + 1])
                    onsets.Add(i);
            }

            if (onsets.Count < 32)
                return (bpm, phaseMs);

            foreach (int onset in onsets)
            {
                for (int d = -tolerance; d <= tolerance; d++)
                {
                    int i = onset + d;

                    if ((uint)i < (uint)n)
                        nearOnset[i] = true;
                }
            }

            const int phaseBins = 32;

            double bestFit = double.MinValue;
            double bestBpm = bpm;
            double referenceFit = double.MinValue;

            foreach (double ratio in grid_fit_ratios)
            {
                double candidate = bpm * ratio;

                if (candidate < comb_min_bpm || candidate > comb_max_bpm)
                    continue;

                double period = hopRate * 60 / candidate;

                if (period < 2)
                    continue;

                // 相位：对「起音位置 mod 周期」做加权直方图，取峰值邻域的加权平均作为网格起点。
                var histogram = new double[phaseBins];
                double binWidth = period / phaseBins;

                foreach (int onset in onsets)
                {
                    double wrapped = onset % period;

                    if (wrapped < 0)
                        wrapped += period;

                    int bin = Math.Min(phaseBins - 1, (int)(wrapped / binWidth));
                    histogram[bin] += envelope[onset];
                }

                int peakBin = 0;

                for (int b = 1; b < phaseBins; b++)
                {
                    if (histogram[b] > histogram[peakBin])
                        peakBin = b;
                }

                double weightSum = 0;
                double positionSum = 0;

                for (int b = 0; b < phaseBins; b++)
                {
                    int delta = b - peakBin;

                    if (delta > phaseBins / 2) delta -= phaseBins;
                    if (delta < -phaseBins / 2) delta += phaseBins;

                    if (Math.Abs(delta) > 1)
                        continue;

                    weightSum += histogram[b];
                    positionSum += histogram[b] * (delta * binWidth);
                }

                if (weightSum <= 0)
                    continue;

                // 先折回非负：否则下面的越界判断对负数成立，gridCount 会是 0，候选被整个跳过。
                double start = positionSum / weightSum;

                while (start < 0)
                    start += period;

                int gridCount = 0;
                int coveredGrid = 0;

                for (double position = start; position < n; position += period)
                {
                    int frame = (int)Math.Round(position);

                    if (frame >= n)
                        break;

                    gridCount++;

                    if (nearOnset[frame])
                        coveredGrid++;
                }

                if (gridCount < 8)
                    continue;

                int coveredOnsets = 0;

                foreach (int onset in onsets)
                {
                    double offset = (onset - start) % period;

                    if (offset < 0)
                        offset += period;

                    if (Math.Min(offset, period - offset) <= tolerance)
                        coveredOnsets++;
                }

                // onset 级 F 值：precision = 网格点里落在起音上的比例，recall = 起音里被网格覆盖的
                // 比例。半速网格 P 高但 R 掉一半、倍速反过来，两者 F 只有 2/3，只有真 tactus 能同时
                // 把 P 和 R 顶到接近 1。
                //
                // 试过换成「网格点 vs 网格中点」的包络对比度，想避开 P 随密度饱和的偏差，结果是负的
                // （MAE 26.2 → 68.1、八度错 4 → 24）：密集起音下所有候选的对比度都趋近 0，判据退化。
                double precision = (double)coveredGrid / gridCount;
                double recall = (double)coveredOnsets / onsets.Count;
                double candidateFit = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;

                if (Math.Abs(ratio - 1.0) < 1e-9)
                    referenceFit = candidateFit;

                // 同分时保留融合值（ratio == 1 先被遍历，用 > 保证它不被同分替换）。
                if (candidateFit > bestFit)
                {
                    bestFit = candidateFit;
                    bestBpm = candidate;
                }
            }

            if (bestBpm != bpm)
                AppLog.Beat.Add($"网格仲裁：{bpm:0.##} → {bestBpm:0.##}"
                                + $"（F {referenceFit:0.000} → {bestFit:0.000}，优势 {bestFit - referenceFit:0.000}）");

            // 只有另一个八度/三连音的 F 值**明显**更好时才推翻融合值：判据本身有噪声，优势不够
            // 就不动，否则会把本来正确的八度改坏。
            if (bestBpm != bpm && referenceFit > double.MinValue && bestFit < referenceFit + grid_fit_margin)
                bestBpm = bpm;

            // 相位沿用融合值即可，不需要重新定：所有候选速度都是融合值的整数比，所以融合相位
            // （某个已检出的拍点）对任何候选网格都仍然是合法的网格点 —— 粗网格是细网格的子集。
            // 试过用起音包络重新搜相位，结果反而更差（中位误差 77ms → 100ms），就是没利用这一点。
            return (bestBpm, phaseMs);
        }

        /// <summary>
        /// 首起音锚点：返回歌曲第一个显著起音落在的小节内拍位（0..beatsPerBar-1）；起音明显夹在
        /// 两拍之间（弱起/前奏音型）时返回 -1，交给结构线索。
        ///
        /// 编曲上第一个音几乎总落在小节第一拍；osu! 谱面的首条红线（评估真值）同样落在第一个音上。
        /// 这是整首歌唯一一个"确定在第一条小节线上"的点，比统计整首的和声变化可靠得多，所以直接采用。
        ///
        /// 触发这条线索的是 FrogMonster《Remember Our Summer》：该曲 5..1735ms 静音，第一个音在 1.88s，
        /// 落在 offset 3 的拍点 (1766.25ms) 而非和声线索选的 offset 0 (2235ms) —— 恰好晚一拍，也就是
        /// 用户反复反馈的"慢一拍"。（先用"按结构极差的 0.6 / 1.25 倍加权"试过：因为该曲与多数曲目的
        /// 结构极差只有 0.007~0.02，远小于 structure_flat_threshold，评分会被兜底分支整个替换掉，
        /// 加权的锚点根本传不到决策 —— 所以这里改成直接决定，不参与打分。）
        /// </summary>
        private static int firstOnsetAnchorOffset(double[] onset, double phase, double period, int beatsPerBar)
        {
            double onsetMean = 0;

            foreach (double v in onset)
                onsetMean += v;

            onsetMean /= onset.Length;

            double onsetVariance = 0;

            foreach (double v in onset)
                onsetVariance += (v - onsetMean) * (v - onsetMean);

            double onsetThreshold = onsetMean + Math.Sqrt(onsetVariance / onset.Length);

            int firstOnset = -1;

            for (int i = 1; i < onset.Length; i++)
            {
                if (onset[i] >= onsetThreshold)
                {
                    firstOnset = i;
                    break;
                }
            }

            if (firstOnset < 0)
                return -1;

            double onsetOffset = (firstOnset - phase) / period % beatsPerBar;

            if (onsetOffset < 0)
                onsetOffset += beatsPerBar;

            double nearest = Math.Round(onsetOffset);

            // 起音必须确实落在拍点上才认账；明显夹在两拍之间说明这首歌带弱起/前奏音型，交给结构线索。
            return Math.Abs(onsetOffset - nearest) <= first_onset_tolerance ? (int)nearest % beatsPerBar : -1;
        }

        /// <summary>一个网格上的逐拍特征集（训练数据导出用）。</summary>
        /// <param name="BeatTimesMs">每个网格拍的中心时间（毫秒），第 m 项对应网格第 m 拍。</param>
        /// <param name="Features">每拍 20 维：12 色度 + 7 对数频段 + 1 起音强度（均为拍窗口内的值）。</param>
        /// <param name="AnchorOffset">首起音锚点相对该网格的拍位（0..3），弱起时为 -1。</param>
        public sealed record BeatGridFeatures(IReadOnlyList<double> BeatTimesMs, IReadOnlyList<double[]> Features,
            int AnchorOffset);

        /// <summary>
        /// 训练数据导出：给定网格（起点相位 + 拍长，毫秒），输出每个网格拍的 20 维特征与首起音
        /// 锚点拍位。与 <see cref="detectDownbeatPhase"/> 走同一批私有特征函数
        /// （<see cref="buildBeatDescriptors"/> / <see cref="buildOnsetEnvelope"/>），
        /// 保证训练/推理特征严格同源 —— 这是 kiai 导出踩过并验证过的口径问题。
        /// </summary>
        public Task<BeatGridFeatures?> ExtractBeatFeaturesAsync(byte[] audio, double gridPhaseMs, double beatLengthMs)
            => Task.Run(() =>
            {
                List<Frame> frames = decodeFeatureFrames(audio);
                int n = frames.Count;

                if (n < 16 || beatLengthMs <= 0)
                    return null;

                double span = frames[^1].Time - frames[0].Time;
                double hopRate = span > 0 ? (n - 1) / span : 86;
                double period = hopRate * beatLengthMs / 1000.0;

                if (period < 2)
                    return null;

                double phase = gridPhaseMs / 1000.0 * hopRate;
                phase -= Math.Floor(phase / period) * period;

                int lastBeat = (int)((n - 1 - phase) / period);

                if (lastBeat < 8)
                    return null;

                int beatCount = lastBeat + 1;

                (double[][] chroma, double[][] timbre) = buildBeatDescriptors(frames, phase, period, beatCount);
                double[] onset = buildOnsetEnvelope(frames);
                int anchorOffset = firstOnsetAnchorOffset(onset, phase, period, 4);

                var times = new double[beatCount];
                var features = new double[beatCount][];

                for (int m = 0; m < beatCount; m++)
                {
                    double centre = phase + m * period;
                    times[m] = centre / hopRate * 1000;

                    var f = new double[chroma_bins + band_count + 1];

                    for (int k = 0; k < chroma_bins; k++)
                        f[k] = chroma[m][k];

                    for (int k = 0; k < band_count; k++)
                        f[chroma_bins + k] = timbre[m][k];

                    f[chroma_bins + band_count] = peakAt(onset, centre);
                    features[m] = f;
                }

                return new BeatGridFeatures(times, features, anchorOffset);
            });

        /// <summary>
        /// 起音包络：全频带的半波整流对数增幅（与 <see cref="estimateTempo"/> 的白化思路一致，
        /// 但不做 DP）。仲裁只需要这条连续包络，不必把它峰化 —— 峰化会引入门限这个额外旋钮，
        /// 而门限一旦偏松，precision 就会对所有候选饱和，判据随即失效。
        /// </summary>
        private static double[] buildOnsetEnvelope(IReadOnlyList<Frame> frames)
        {
            int n = frames.Count;
            var envelope = new double[n];

            for (int b = 0; b < band_count; b++)
            {
                double mean = 0;

                for (int i = 0; i < n; i++)
                    mean += frames[i].GetBand(b);

                mean = Math.Max(mean / n, 1e-9);
                double weight = band_weights[b];

                for (int i = 1; i < n; i++)
                {
                    double current = Math.Log(1 + 10 * frames[i].GetBand(b) / mean);
                    double previous = Math.Log(1 + 10 * frames[i - 1].GetBand(b) / mean);
                    envelope[i] += weight * Math.Max(0, current - previous);
                }
            }

            return envelope;
        }

        #endregion

        #region Downbeat (bar) alignment

        /// <summary>
        /// 结构线索被判为"无判别力"的门限：四个候选相位的对比度极差（原始值，未归一化）小于它就认为
        /// 这一首整首都没有可分辨的小节结构，才退回低频重音。
        ///
        /// 之所以用绝对尺度而不是相对尺度：对比度是 1-cos，天然落在 0..2，跨曲目可比。相对归一化
        /// （按 4 个候选自身的 min/max 拉满 0..1）会把纯噪声也放大成"满量程的差异"，正是旧版在四踩
        /// 曲风里等于丢硬币的机制。
        /// </summary>
        private const double structure_flat_threshold = 0.05;

        /// <summary>
        /// 歌曲**第一个显著起音**的相位是否算作"硬锚点"的判定容差（拍）。拍网格本身很准，所以门限可以紧。
        /// 落在拍点 ±该拍数内才认账，否则认为这首歌带弱起/前奏音型，交给结构线索。
        /// </summary>
        private const double first_onset_tolerance = 0.3;

        /// <summary>
        /// Re-anchors the beat grid onto bar downbeats — the "1" of every 4/4 measure.
        ///
        /// The tempo engines only guarantee that beats land on real onsets; <em>which</em> of the
        /// four beats of a measure is the downbeat is a bar-level decision a period-based score
        /// cannot make (a one-beat phase error is invisible to it). osu!'s measure-level effects
        /// key off <c>beatIndex % TimeSignature.Numerator == 0</c> — e.g. <c>MenuSideFlashes</c>
        /// only flashes on the downbeat outside kiai — so the timing-point origin has to sit on a
        /// downbeat, not merely on "some beat".
        ///
        /// 主线索是**小节尺度的结构对比**：对每个候选小节相位，把每条小节线**前四拍**与**后四拍**
        /// 的拍平均描述子作对比（色度 = 和声变化，频段能量 = 音色变化）。真正的小节线是音乐**发生
        /// 变化**的地方 —— 和弦换了、元素进来了 —— 这才是区分"四拍里哪一拍是小节起点"的依据。
        ///
        /// 为什么不能只靠低频重音：四拍全踩底鼓的曲风（progressive house / future bounce，正是用户
        /// 点名暴露问题的 FrogMonster《Remember Our Summer》，128 BPM，四踩）里，30..280Hz 的起音
        /// 线索对四拍几乎无差别；把 4 个几乎相等的数按 min/max 归一化到 0..1，等于**从噪声里制造出
        /// 一个赢家**，小节原点自然变成丢硬币。
        ///
        /// 实测（bench2 独立集 98 张，2026-10）：旧版（重音主导 + 单帧和声破平局）小节原点正确 24/98
        /// （= 24.5%，正好等于瞎猜）、小节偏差中位 372ms，误差全落在整数拍上 —— 说明拍网格本身是对齐
        /// 的，错的只有"四拍里哪一拍是小节起点"。旧版"和声"线索取的是**单帧**（约 11.6ms）色度，噪声
        /// 压倒信号，因此当时"和声没有判别力"的结论站不住，这次改成按拍平均后再比。
        ///
        /// 换成按拍平均的结构对比（先单尺度 + 0.25 权重重音）：正确 36/98、小节偏差中位 286ms ——
        /// 有效但不够。随后注意到重音线索与 osu! 真值的一致率只有 1/4（纯噪声），把它按权重混进评分
        /// 只会往噪声里加噪声，于是改成"结构有判别力就完全由结构决定，否则才退回重音"，并把窗长扩到
        /// 1/2/4 小节多尺度（见 <see cref="structure_flat_threshold"/>）：正确 35/98、中位 287ms，
        /// 与单尺度同分（噪声级差异），保留多尺度与门控写法。
        ///
        /// 第一条线索是**歌曲第一个显著起音的相位**：编曲上第一个音几乎总落在小节第一拍，osu! 谱面的
        /// 首条红线（评估真值）也落在第一个音上，所以直接用它定相位；起音明显夹在两拍之间（弱起/前奏音型）
        /// 时才交给下面的结构线索。
        ///
        /// 最后一次实测（bench2 独立集 98 张，2026-10）：正确 24/98 → **55/98**，小节偏差中位 369 → **83ms**，
        /// 误差分布从"0/1/2 拍各占 1/4"变成 55 张落在 0 拍、1 拍错位 32 → 12 张。这也是用户点名的那首歌
        /// （FrogMonster《Remember Our Summer》）的修法：它的第一个音落在 offset 3，原来被和声线索定到
        /// offset 0，整整晚一拍。
        ///
        /// Returns a timing-point origin aligned to a downbeat, moved to &lt;= 0 (osu!-style) so the
        /// beat grid exists from the very start instead of beginning up to a bar late.
        /// </summary>
        private static double detectDownbeatPhase(IReadOnlyList<Frame> frames, double bpm, double phaseMs)
        {
            const int beats_per_bar = 4;

            int n = frames.Count;

            if (n < 16 || bpm <= 0)
                return phaseMs;

            double span = frames[n - 1].Time - frames[0].Time;
            double hopRate = span > 0 ? (n - 1) / span : 86;
            double period = hopRate * 60 / bpm;

            if (period < 2)
                return phaseMs;

            // First beat of the grid within one beat period; every beat is phase + m * period.
            double phase = phaseMs / 1000.0 * hopRate;
            phase -= Math.Floor(phase / period) * period;

            int lastBeat = (int)((n - 1 - phase) / period);

            if (lastBeat < 8)
                return phaseMs;

            int beatCount = lastBeat + 1;

            (double[][] chroma, double[][] timbre) = buildBeatDescriptors(frames, phase, period, beatCount);

            // 多尺度小节线对比度：1 / 2 / 4 小节（4 / 8 / 16 拍）三种窗长各自打分再相加。
            // 长尺度更稀疏（一小节一次 vs 四小节一次），按 1/bars 加权，避免被短尺度淹没。
            // 多一个尺度的价值不在于改变排序，而在于给正确相位累积**相互独立**的证据：短尺度的
            // 噪声掀翻排序时，段边界的证据能把它翻回来。
            var structureScore = new double[beats_per_bar];
            bool scored = false;

            for (int bars = 1; bars <= 4; bars <<= 1)
            {
                int window = beats_per_bar * bars;

                if (beatCount < 2 * window + beats_per_bar)
                    continue;

                var sum = new double[beats_per_bar];
                var count = new int[beats_per_bar];

                for (int b = window; b + window <= beatCount; b++)
                {
                    double harmonic = 1 - meanCosine(chroma, b - window, b - 1, b, b + window - 1);
                    double timbral = 1 - meanCosine(timbre, b - window, b - 1, b, b + window - 1);

                    sum[b % beats_per_bar] += harmonic + 0.5 * timbral;
                    count[b % beats_per_bar]++;
                }

                for (int k = 0; k < beats_per_bar; k++)
                {
                    if (count[k] == 0)
                        continue;

                    structureScore[k] += sum[k] / count[k] / bars;
                    scored = true;
                }
            }

            if (!scored)
                return phaseMs;

            double structureMin = double.MaxValue;
            double structureMax = double.MinValue;

            foreach (double v in structureScore)
            {
                if (v < structureMin)
                    structureMin = v;

                if (v > structureMax)
                    structureMax = v;
            }

            // 第一条也是最硬的一条线索：**歌曲第一个显著起音的相位**。编曲上第一个音几乎总落在小节
            // 第一拍，osu! 谱面的首条红线（评估真值）同样落在第一个音上 —— 详细理由与实测记录见
            // <see cref="firstOnsetAnchorOffset"/> 的文档。落点夹在两拍之间（弱起/前奏音型）时返回
            // -1，交给下面的结构线索。
            int anchorOffset = firstOnsetAnchorOffset(buildOnsetEnvelope(frames), phase, period, beats_per_bar);

            if (anchorOffset >= 0)
            {
                double anchoredMs = (phase + anchorOffset * period) / hopRate * 1000;
                double anchoredBarMs = period * beats_per_bar / hopRate * 1000;

                while (anchoredMs > 0)
                    anchoredMs -= anchoredBarMs;

                return anchoredMs;
            }

            double[] finalScore = structureScore;

            // 结构有判别力时完全由它决定；只有整首都分辨不出结构时才退回低频重音 ——
            // 实测重音与 osu! 真值的一致率正好 1/4（等于瞎猜），混进来只会往噪声里加噪声。
            if (structureMax - structureMin < structure_flat_threshold)
            {
                double[] accent = buildAccentSalience(frames);

                finalScore = new double[beats_per_bar];

                var accentCount = new int[beats_per_bar];

                for (int m = 1; m <= lastBeat; m++)
                {
                    finalScore[m % beats_per_bar] += peakAt(accent, phase + m * period);
                    accentCount[m % beats_per_bar]++;
                }

                for (int k = 0; k < beats_per_bar; k++)
                {
                    if (accentCount[k] == 0)
                        return phaseMs;

                    finalScore[k] /= accentCount[k];
                }
            }

            int bestOffset = 0;
            double bestScore = double.MinValue;

            for (int k = 0; k < beats_per_bar; k++)
            {
                if (finalScore[k] > bestScore)
                {
                    bestScore = finalScore[k];
                    bestOffset = k;
                }
            }

            double downbeatMs = (phase + bestOffset * period) / hopRate * 1000;
            double barMs = period * beats_per_bar / hopRate * 1000;

            while (downbeatMs > 0)
                downbeatMs -= barMs;

            return downbeatMs;
        }

        /// <summary>
        /// Per-frame low-register (30..280 Hz) half-wave-rectified log-flux onset strength.
        ///
        /// 只作为结构线索无判别力时的**兜底**（见 <see cref="structure_flat_threshold"/>）。试过把它拆成
        /// 「底鼓 30..140Hz / 军鼓 140..560Hz」两路、并把军鼓那一路扣掉（假设军鼓落在 2/4 拍会把
        /// 小节原点顶晚一拍）：bench2 独立集上小节偏差中位 295 -> 329ms，更差，撤销 —— 结构对比
        /// 已经是主线索，重音这一路再细分只会往噪声里加噪声。
        /// </summary>
        private static double[] buildAccentSalience(IReadOnlyList<Frame> frames)
        {
            int n = frames.Count;
            var accent = new double[n];

            for (int b = 0; b < 3; b++)
            {
                double mean = 0;
                for (int i = 0; i < n; i++)
                    mean += frames[i].GetBand(b);
                mean = Math.Max(mean / n, 1e-9);

                double weight = b switch { 0 => 1.4, 1 => 1.2, _ => 0.9 };

                for (int i = 1; i < n; i++)
                {
                    double current = Math.Log(1 + 10 * frames[i].GetBand(b) / mean);
                    double previous = Math.Log(1 + 10 * frames[i - 1].GetBand(b) / mean);
                    accent[i] += weight * Math.Max(0, current - previous);
                }
            }

            return accent;
        }

        /// <summary>Largest value of <paramref name="values"/> within ±2 frames of a fractional position.</summary>
        private static double peakAt(double[] values, double position)
        {
            int centre = (int)Math.Round(position);
            double max = 0;

            for (int d = -2; d <= 2; d++)
            {
                int i = centre + d;
                if (i >= 0 && i < values.Length && values[i] > max)
                    max = values[i];
            }

            return max;
        }

        /// <summary>
        /// 拍同步描述子（用于小节尺度结构对比）：12 维色度 + 7 维对数频段能量，都在**整拍窗口**
        /// （以 <c>phase + m * period</c> 为中心）内取平均，再各自单位化 —— 因此描述的是"这一拍在
        /// 响哪些音、什么音色"，而不是响度。按拍平均是关键：单帧色度只有约 11.6ms，被琶音/人声的
        /// 瞬时音符主导，噪声压过信号。
        /// </summary>
        private static (double[][] Chroma, double[][] Timbre) buildBeatDescriptors(
            IReadOnlyList<Frame> frames, double phase, double period, int beatCount)
        {
            var chroma = new double[beatCount][];
            var timbre = new double[beatCount][];

            // 用整首的频段均值做分母，去掉曲目间的绝对电平差异。
            var bandMean = new double[band_count];

            foreach (Frame frame in frames)
            {
                for (int b = 0; b < band_count; b++)
                    bandMean[b] += frame.GetBand(b);
            }

            for (int b = 0; b < band_count; b++)
                bandMean[b] = Math.Max(bandMean[b] / frames.Count, 1e-9);

            double halfPeriod = period / 2;

            for (int m = 0; m < beatCount; m++)
            {
                double centre = phase + m * period;
                int from = Math.Max(0, (int)Math.Floor(centre - halfPeriod));
                int to = Math.Min(frames.Count - 1, (int)Math.Ceiling(centre + halfPeriod) - 1);

                var c = new double[chroma_bins];
                var t = new double[band_count];

                if (to >= from)
                {
                    for (int i = from; i <= to; i++)
                    {
                        for (int k = 0; k < chroma_bins; k++)
                            c[k] += frames[i].GetChroma(k);

                        for (int b = 0; b < band_count; b++)
                            t[b] += Math.Log(1 + frames[i].GetBand(b) / bandMean[b]);
                    }
                }

                unitNormalise(c);
                unitNormalise(t);

                chroma[m] = c;
                timbre[m] = t;
            }

            return (chroma, timbre);
        }

        /// <summary>
        /// 两段连续拍（闭区间）描述子均值的余弦相似度。取均值再比，等于拿"这一小节的和声/音色"
        /// 去比，而不是比某一拍上的瞬时值。
        /// </summary>
        private static double meanCosine(double[][] descriptor, int fromA, int toA, int fromB, int toB)
        {
            int dims = descriptor[0].Length;

            double dot = 0;
            double normA = 0;
            double normB = 0;

            for (int k = 0; k < dims; k++)
            {
                double a = 0;
                double b = 0;

                for (int i = fromA; i <= toA; i++)
                    a += descriptor[i][k];

                for (int i = fromB; i <= toB; i++)
                    b += descriptor[i][k];

                dot += a * b;
                normA += a * a;
                normB += b * b;
            }

            double denom = Math.Sqrt(normA * normB);
            return denom > 1e-12 ? dot / denom : 1;
        }

        /// <summary>Scales the vector to unit L2 norm (no-op when all zero).</summary>
        private static void unitNormalise(double[] values)
        {
            double sum = 0;

            foreach (double v in values)
                sum += v * v;

            double norm = Math.Sqrt(sum);

            if (norm <= 1e-12)
                return;

            for (int i = 0; i < values.Length; i++)
                values[i] /= norm;
        }

        #endregion

        #region Kiai: chroma SSM + Foote boundaries + energy + repetition

        private static IReadOnlyList<KiaiSection> detectKiaiSections(IReadOnlyList<Frame> frames, double duration)
        {
            if (duration <= min_section_cells * grid_period_s)
                return Array.Empty<KiaiSection>();

            // 单元与特征统一由 KiaiFeatureBuilder 构造 —— 启发式用 Energy/Chroma，
            // ML 管线用 Raw（40 维），保证两条路线看到的是完全相同的时间网格。
            List<KiaiCell> cells = KiaiFeatureBuilder.Build(frames, duration);

            double meanEnergy = 0;
            foreach (var cell in cells)
                meanEnergy += cell.Energy;
            meanEnergy /= cells.Count;

            double energyVariance = 0;
            foreach (var cell in cells)
            {
                double d = cell.Energy - meanEnergy;
                energyVariance += d * d;
            }

            double energyStd = Math.Sqrt(energyVariance / cells.Count);

            if (meanEnergy <= 0 || energyStd / meanEnergy < min_relative_std)
                return Array.Empty<KiaiSection>();

            List<int> boundaries = detectStructuralBoundaries(cells);
            return buildKiaiSections(cells, boundaries, meanEnergy, energyStd, duration);
        }

        /// <summary>
        /// Foote novelty: convolve the chroma self-similarity matrix with a checkerboard
        /// kernel; strong peaks are verse/chorus-style structural boundaries.
        /// internal：ML 后处理同样用它把段边吸附到结构边界。
        /// </summary>
        internal static List<int> DetectStructuralBoundaries(IReadOnlyList<KiaiCell> cells)
            => detectStructuralBoundaries(cells);

        private static List<int> detectStructuralBoundaries(IReadOnlyList<KiaiCell> cells)
        {
            int n = cells.Count;
            int w = foote_kernel_side;

            if (n < 2 * w + 4)
                return new List<int>();

            var novelty = new double[n];

            for (int t = w; t < n - w; t++)
            {
                double score = 0;

                // Checkerboard kernel around t:
                //  +1 within the two same-side quadrants (segment self-similarity),
                //  -1 across the diagonal quadrants (two different segments touching).
                for (int i = -w; i < 0; i++)
                {
                    for (int j = -w; j < 0; j++)
                    {
                        score += chromaSimilarity(cells[t + i].Chroma, cells[t + j].Chroma);
                        score += chromaSimilarity(cells[t + w + i].Chroma, cells[t + w + j].Chroma);
                        score -= chromaSimilarity(cells[t + i].Chroma, cells[t + w + j].Chroma);
                        score -= chromaSimilarity(cells[t + w + i].Chroma, cells[t + j].Chroma);
                    }
                }

                novelty[t] = score;
            }

            double mean = 0;
            int scored = n - 2 * w;
            for (int t = w; t < n - w; t++)
                mean += novelty[t];
            mean /= Math.Max(1, scored);

            double variance = 0;
            for (int t = w; t < n - w; t++)
            {
                double d = novelty[t] - mean;
                variance += d * d;
            }

            double std = Math.Sqrt(variance / Math.Max(1, scored));
            double threshold = mean + boundary_threshold_std * std;

            var candidates = new List<(int cell, double strength)>();

            for (int t = w + 1; t < n - w - 1; t++)
            {
                if (novelty[t] > threshold
                    && novelty[t] >= novelty[t - 1]
                    && novelty[t] >= novelty[t + 1])
                {
                    candidates.Add((t, novelty[t]));
                }
            }

            // Greedy non-maximum suppression with a minimum boundary distance.
            candidates.Sort((a, b) => b.strength.CompareTo(a.strength));
            var chosen = new List<int>();

            foreach (var candidate in candidates)
            {
                bool tooClose = false;

                foreach (int existing in chosen)
                {
                    if (Math.Abs(existing - candidate.cell) < boundary_min_distance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                    chosen.Add(candidate.cell);
            }

            chosen.Sort();
            return chosen;
        }

        private static IReadOnlyList<KiaiSection> buildKiaiSections(
            IReadOnlyList<KiaiCell> cells,
            IReadOnlyList<int> boundaries,
            double meanEnergy,
            double energyStd,
            double duration)
        {
            int n = cells.Count;
            double threshold = meanEnergy + kiai_threshold_std_factor * energyStd;
            double climaxThreshold = meanEnergy + climax_threshold_std_factor * energyStd;

            var runs = new List<(int first, int last)>();
            int? runStart = null;

            for (int i = 0; i < n; i++)
            {
                bool active = cells[i].Energy > threshold;

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

            // Merge short dips inside a chorus (transitions / one-bar drops).
            var merged = new List<(int first, int last)> { runs[0] };

            for (int i = 1; i < runs.Count; i++)
            {
                var previous = merged[^1];
                var current = runs[i];

                if (current.first - previous.last - 1 <= merge_gap_cells)
                    merged[^1] = (previous.first, current.last);
                else
                    merged.Add(current);
            }

            var sections = new List<KiaiSection>();

            foreach ((int first, int last) in merged)
            {
                int length = last - first + 1;

                if (length < min_section_cells)
                    continue;

                // Snap run edges onto nearby structural boundaries.
                int snappedFirst = snapEdge(first, boundaries, snapBackward: true);
                int snappedLast = snapEdge(last, boundaries, snapBackward: false);

                double runEnergy = 0;
                for (int i = snappedFirst; i <= snappedLast; i++)
                    runEnergy += cells[i].Energy;
                runEnergy /= snappedLast - snappedFirst + 1;

                bool repeatsElsewhere = hasRepeatingSection(cells, snappedFirst, snappedLast);
                bool exceptionalClimax = runEnergy > climaxThreshold;

                // 三个独立理由任一成立即可保留：在别处重复出现（副歌还会再来一次）、能量达到高潮档，
                // 或只是比「进入 kiai」的门槛明显更高（只出现一次但能量确实抬升的副歌段）。
                if (!repeatsElsewhere && !exceptionalClimax && runEnergy <= meanEnergy + kiai_accept_std_factor * energyStd)
                    continue;

                int startCell = Math.Max(0, snappedFirst - section_padding_cells);
                int endCell = Math.Min(n - 1, snappedLast + 1 + section_padding_cells);

                double startMs = Math.Max(0, startCell * grid_period_s * 1000);
                double endMs = Math.Min(duration * 1000, endCell * grid_period_s * 1000);

                sections.Add(new KiaiSection(startMs, endMs));
            }

            return sections;
        }

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

        /// <summary>
        /// True when another non-overlapping, similarly-sized region of the song has
        /// near-identical mean chroma (a recurring section — e.g. the second chorus).
        /// </summary>
        private static bool hasRepeatingSection(IReadOnlyList<KiaiCell> cells, int first, int last)
        {
            int n = cells.Count;
            int length = last - first + 1;
            var sectionChroma = averageChroma(cells, first, last);
            double sectionNorm = norm(sectionChroma);

            if (sectionNorm <= 0)
                return false;

            for (int candidate = 0; candidate + length <= n; candidate += 4)
            {
                if (candidate + length + 4 >= first && candidate <= last + 4)
                    continue;

                // Require roughly matching length context is implicit; compare mean chroma.
                var other = averageChroma(cells, candidate, candidate + length - 1);
                double otherNorm = norm(other);

                if (otherNorm <= 0)
                    continue;

                double dot = 0;
                for (int c = 0; c < chroma_bins; c++)
                    dot += sectionChroma[c] * other[c];

                double similarity = dot / (sectionNorm * otherNorm);

                if (similarity >= repetition_similarity)
                    return true;
            }

            return false;
        }

        private static double[] averageChroma(IReadOnlyList<KiaiCell> cells, int first, int last)
        {
            var result = new double[chroma_bins];

            for (int i = first; i <= last; i++)
            {
                for (int c = 0; c < chroma_bins; c++)
                    result[c] += cells[i].Chroma[c];
            }

            int length = last - first + 1;
            for (int c = 0; c < chroma_bins; c++)
                result[c] /= length;

            return result;
        }

        private static double chromaSimilarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            // Cell chroma vectors are L2-normalised, so the dot product is cosine similarity.
            double dot = 0;
            for (int c = 0; c < chroma_bins; c++)
                dot += a[c] * b[c];
            return dot;
        }

        private static double norm(IReadOnlyList<double> v)
        {
            double sum = 0;
            for (int c = 0; c < chroma_bins; c++)
                sum += v[c] * v[c];
            return Math.Sqrt(sum);
        }

        #endregion
    }
}
