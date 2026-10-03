using ManagedBass;
using ManagedBass.Fx;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;

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
        private const double comb_max_bpm = 200;
        private const double comb_kernel_ms = 50;
        private const double dp_tightness = 50; // Ellis DP beat-tracker period-prior strength (z-scored envelope)
        private const int splat_subpositions = 16; // quantised fractional positions for the folded comb kernel
        private const int low_band_top = 2; // tempo octave decided by 30..280 Hz (kick/bass/snare body)
        private const double whiten_window_s = 0.3;
        private const double whiten_floor = 0.15;

        // 八度折返的「感知速度」窗口：低于下限翻倍、高于上限减半。
        // 上限原本是 150，用 beatmap/ 里 29 张真实谱面训练后发现它是主要的失分点：
        // 160~198 BPM 的歌（osu! 里非常常见）会被硬折成一半，所以放宽到 200。
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
        private const double kiai_threshold_std_factor = 0.45;
        private const double climax_threshold_std_factor = 0.9;
        private const double min_relative_std = 0.04;
        private const int min_section_cells = 16;    // 8 s
        private const int merge_gap_cells = 12;      // 6 s
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

        private static BeatAnalysisDetails run(byte[] audio)
        {
            List<Frame> frames = decodeFeatureFrames(audio);

            if (frames.Count < min_frames)
            {
                AppLog.Beat.Add($"音频过短无法分析（{frames.Count} 帧）", LogLevel.Important);
                return new BeatAnalysisDetails(new BeatAnalysis(120, 0, 0, 0, Array.Empty<KiaiSection>()),
                    120, 0, false, null, null, null, frames.Count, frames.Count > 0 ? frames[^1].Time : 0, 0, "too-few-frames");
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

            // The tempo engines only pin beats onto real onsets; which beat of the 4/4 measure is
            // the downbeat is a bar-level decision. Re-anchor the grid so that beatIndex % 4 == 0
            // (osu!'s measure-level effects, e.g. MenuSideFlashes outside kiai) lands on the "1".
            double phaseMs = detectDownbeatPhase(frames, bpm, beatPhaseMs);

            IReadOnlyList<KiaiSection> kiai = detectKiaiSections(frames, duration);

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
                engineBRaw is { Valid: true } ? null : engineBRaw?.RejectReason);

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

        private readonly struct Frame
        {
            public readonly float Time;
            public readonly float Total;
            public readonly float Band0, Band1, Band2, Band3, Band4, Band5, Band6;
            public readonly float Chroma0, Chroma1, Chroma2, Chroma3, Chroma4, Chroma5, Chroma6,
                                       Chroma7, Chroma8, Chroma9, Chroma10, Chroma11;

            public Frame(float time, float[] bands, float[] chroma, float total)
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

                    for (int b = 0; b < band_count; b++)
                    {
                        float energy = 0;
                        for (int k = bandStartBin[b]; k <= bandEndBin[b] && k < fft_bins; k++)
                            energy += fft[k];
                        bands[b] = energy;
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

                    frames.Add(new Frame((float)centre, bands, chroma, total));
                }
            }
            finally
            {
                Bass.StreamFree(handle);
            }

            return frames;
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

            // Winner + every alternative hypothesis, folded into the 78..150 perceptual
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

                    // |j| <= radius is smaller than one period at every scanned tempo,
                    // so at most a single wrap is needed (much cheaper than modulo).
                    if (b < 0) b += bins;
                    else if (b >= bins) b -= bins;

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

        #region Downbeat (bar) alignment

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
        /// Two independent cues vote per candidate bar offset:
        ///  - percussive accent: the low register (30..280 Hz, kick/bass/snare body) is loudest on
        ///    the downbeat of most popular productions;
        ///  - harmonic change: downbeats usually coincide with a chord change, measured as the
        ///    chroma distance to the preceding beat.
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

            double[] accent = buildAccentSalience(frames);

            var accentScore = new double[beats_per_bar];
            var harmonyScore = new double[beats_per_bar];
            var counts = new int[beats_per_bar];

            for (int m = 1; m <= lastBeat; m++)
            {
                double position = phase + m * period;
                int offset = m % beats_per_bar;

                accentScore[offset] += peakAt(accent, position);
                harmonyScore[offset] += 1 - beatChromaSimilarity(frames, position, position - period);
                counts[offset]++;
            }

            for (int k = 0; k < beats_per_bar; k++)
            {
                if (counts[k] == 0)
                    return phaseMs;

                accentScore[k] /= counts[k];
                harmonyScore[k] /= counts[k];
            }

            normalise(accentScore);
            normalise(harmonyScore);

            int bestOffset = 0;
            double bestScore = double.MinValue;

            for (int k = 0; k < beats_per_bar; k++)
            {
                // Accent leads; harmonic change only breaks the accent ties (notably beat 1 vs
                // beat 3 when the kick hits both). The 2% margin keeps the existing phase when the
                // evidence is genuinely ambiguous (an even four-on-the-floor pulse).
                double score = 0.6 * accentScore[k] + 0.4 * harmonyScore[k];

                if (score > bestScore * 1.02)
                {
                    bestScore = score;
                    bestOffset = k;
                }
            }

            double downbeatMs = (phase + bestOffset * period) / hopRate * 1000;
            double barMs = period * beats_per_bar / hopRate * 1000;

            while (downbeatMs > 0)
                downbeatMs -= barMs;

            return downbeatMs;
        }

        /// <summary>Per-frame low-register (30..280 Hz) half-wave-rectified log-flux onset strength.</summary>
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

        /// <summary>Cosine similarity of the chroma vectors sampled at two fractional frame positions.</summary>
        private static double beatChromaSimilarity(IReadOnlyList<Frame> frames, double a, double b)
        {
            int ia = Math.Clamp((int)Math.Round(a), 0, frames.Count - 1);
            int ib = Math.Clamp((int)Math.Round(b), 0, frames.Count - 1);

            double dot = 0;
            double normA = 0;
            double normB = 0;

            for (int c = 0; c < chroma_bins; c++)
            {
                double va = frames[ia].GetChroma(c);
                double vb = frames[ib].GetChroma(c);
                dot += va * vb;
                normA += va * va;
                normB += vb * vb;
            }

            double denom = Math.Sqrt(normA * normB);
            return denom > 1e-12 ? dot / denom : 1;
        }

        /// <summary>Scales the values into 0..1 by their own min/max (no-op when flat).</summary>
        private static void normalise(double[] values)
        {
            double min = double.MaxValue;
            double max = double.MinValue;

            foreach (double v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double range = max - min;

            if (range <= 1e-12)
                return;

            for (int i = 0; i < values.Length; i++)
                values[i] = (values[i] - min) / range;
        }

        #endregion

        #region Kiai: chroma SSM + Foote boundaries + energy + repetition

        private readonly struct KiaiCell
        {
            public readonly double Energy;
            public readonly double[] Chroma;

            public KiaiCell(double energy, double[] chroma)
            {
                Energy = energy;
                Chroma = chroma;
            }
        }

        private static IReadOnlyList<KiaiSection> detectKiaiSections(IReadOnlyList<Frame> frames, double duration)
        {
            if (duration <= min_section_cells * grid_period_s)
                return Array.Empty<KiaiSection>();

            int cellCount = (int)(duration / grid_period_s) + 1;
            var energySum = new double[cellCount];
            var chromaSum = new double[cellCount * chroma_bins];
            var cellFrameCount = new int[cellCount];

            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                int cell = Math.Min((int)(f.Time / grid_period_s), cellCount - 1);

                energySum[cell] += f.Total;
                cellFrameCount[cell]++;

                for (int c = 0; c < chroma_bins; c++)
                    chromaSum[cell * chroma_bins + c] += f.GetChroma(c);
            }

            var cells = new KiaiCell[cellCount];

            for (int cell = 0; cell < cellCount; cell++)
            {
                int count = Math.Max(1, cellFrameCount[cell]);
                double energy = energySum[cell] / count;

                var chroma = new double[chroma_bins];
                double norm = 0;

                for (int c = 0; c < chroma_bins; c++)
                {
                    chroma[c] = chromaSum[cell * chroma_bins + c] / count;
                    norm += chroma[c] * chroma[c];
                }

                norm = Math.Sqrt(norm);

                if (norm > 0)
                {
                    for (int c = 0; c < chroma_bins; c++)
                        chroma[c] /= norm;
                }

                cells[cell] = new KiaiCell(energy, chroma);
            }

            double meanEnergy = 0;
            foreach (var cell in cells)
                meanEnergy += cell.Energy;
            meanEnergy /= cellCount;

            double energyVariance = 0;
            foreach (var cell in cells)
            {
                double d = cell.Energy - meanEnergy;
                energyVariance += d * d;
            }

            double energyStd = Math.Sqrt(energyVariance / cellCount);

            if (meanEnergy <= 0 || energyStd / meanEnergy < min_relative_std)
                return Array.Empty<KiaiSection>();

            List<int> boundaries = detectStructuralBoundaries(cells);
            return buildKiaiSections(cells, boundaries, meanEnergy, energyStd, duration);
        }

        /// <summary>
        /// Foote novelty: convolve the chroma self-similarity matrix with a checkerboard
        /// kernel; strong peaks are verse/chorus-style structural boundaries.
        /// </summary>
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

                if (!repeatsElsewhere && !exceptionalClimax)
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
