using System.Net.Http;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Timing;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// The live <see cref="IBeatSyncProvider"/> for the player: its clock is the playing BASS track,
    /// its amplitudes come from the track's FFT, and its control points (BPM + kiai sections) are
    /// produced by <see cref="BeatAnalyser"/> for every newly started song.
    /// </summary>
    public partial class MusicBeatSyncProvider : Component, IBeatSyncProvider
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        private BeatAnalyser? analyser;

        /// <summary>
        /// A stationary clock used while nothing is loaded (IsRunning == false, so effects idle).
        /// </summary>
        private readonly StopwatchClock stoppedClock = new();

        public ControlPointInfo? ControlPoints { get; private set; }

        /// <summary>
        /// The timeline currently driving the effects - map-authored, audio-analysed or hand-edited.
        /// The timing editor reads this and writes back through <see cref="MusicPlayer.ApplyTiming"/>.
        /// </summary>
        public BeatmapTiming? CurrentTiming { get; private set; }

        // Explicit interface implementation to avoid hiding Drawable.Clock; beat-synced
        // containers resolve the clock through IBeatSyncProvider anyway.
        IClock IBeatSyncProvider.Clock => (IClock?)player.CurrentTrack ?? stoppedClock;

        public ChannelAmplitudes CurrentAmplitudes => player.CurrentTrack?.CurrentAmplitudes ?? ChannelAmplitudes.Empty;

        // Analysis status for debug UI.
        public BindableDouble EstimatedBpm { get; } = new BindableDouble(0) { MinValue = 0 };
        public BindableDouble Confidence { get; } = new BindableDouble(0) { MinValue = 0 };
        public BindableBool IsKiaiActive { get; } = new BindableBool();
        public Bindable<string> AnalysisStatus { get; } = new Bindable<string>("等待播放");

        // Matches PlayGeneration's default (0), so the very first Update frame with no
        // song loaded doesn't trigger an empty analysis pass.
        private int lastGeneration;
        private int lastTimingRevision;
        private int serial;

        private static readonly HttpClient http = createHttpClient();

        [BackgroundDependencyLoader]
        private void load()
        {
            analyser = new BeatAnalyser();
        }

        protected override void Update()
        {
            base.Update();

            if (player.PlayGeneration.Value != lastGeneration)
            {
                lastGeneration = player.PlayGeneration.Value;
                beginAnalysis();
            }

            // Timing 编辑器提交后只需重建控制点，不必重新分析音频。
            if (player.TimingRevision.Value != lastTimingRevision)
            {
                lastTimingRevision = player.TimingRevision.Value;

                if (player.CurrentSong.Value?.Timing is { Lines.Count: > 0 } edited)
                    publishMapTiming(edited);
            }

            IsKiaiActive.Value = ControlPoints?.EffectPointAt(((IBeatSyncProvider)this).Clock.CurrentTime).KiaiMode ?? false;
        }

        private void beginAnalysis()
        {
            int mySerial = ++serial;

            // 换歌立刻丢掉上一首的时间轴：分析要几秒，这期间若沿用旧值，timing 编辑分屏
            // 会把上一首的 BPM / 首拍 / kiai 当作当前曲目的数据显示甚至写回（"继承上一首"）。
            CurrentTiming = null;

            // 谱面自带时间轴优先：作者写进 .osu 的 BPM / 首拍 / kiai 比音频分析精确得多，
            // 特效可以做到与谱面完全对齐。
            if (player.CurrentSong.Value?.Timing is { Lines.Count: > 0 } timing)
            {
                publishMapTiming(timing);
                return;
            }

            string? url = player.CurrentStreamUrl;

            // While the real analysis runs, pulse effects at a neutral 120 BPM.
            var preliminary = new ControlPointInfo();
            preliminary.Add(0, new TimingControlPoint { BeatLength = 60000.0 / 120 });
            ControlPoints = preliminary;

            EstimatedBpm.Value = 0;
            Confidence.Value = 0;

            if (string.IsNullOrEmpty(url))
            {
                AppLog.Beat.Add($"无音频地址，跳过节拍分析（generation={player.PlayGeneration.Value}，可能为 VIP 拒绝曲目）");
                AnalysisStatus.Value = "暂无可分析的音频";

                // 没有分析结果可取，给一条中性时间轴，保证 timing 编辑仍然可用。
                CurrentTiming = neutralTiming();
                return;
            }

            AppLog.Beat.Add($"开始节拍分析（serial={mySerial}，{AppLog.SafeUrl(url)}）");
            AnalysisStatus.Value = "节拍分析中…";

            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                byte[] audio;
                try
                {
                    // 本地曲目当前流地址就是磁盘路径，直接读文件即可。
                    audio = File.Exists(url)
                        ? await File.ReadAllBytesAsync(url).ConfigureAwait(false)
                        : await http.GetByteArrayAsync(url).ConfigureAwait(false);

                    AppLog.Beat.Add($"分析音频就绪：{audio.Length / 1024}KB，{sw.ElapsedMilliseconds}ms");
                }
                catch (Exception e)
                {
                    AppLog.Beat.Add($"分析音频下载失败：{AppLog.SafeUrl(url)}，{sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                    Schedule(() =>
                    {
                        if (mySerial != serial)
                            return;

                        AnalysisStatus.Value = $"节拍分析失败：{e.Message}";

                        // 分析拿不到结果，退回中性时间轴（不然这一首的时间轴会一直是空的）。
                        CurrentTiming = neutralTiming();
                        ControlPoints = buildControlPoints(CurrentTiming);
                    });
                    return;
                }

                if (mySerial != serial)
                {
                    AppLog.Beat.Add($"下载完成但已切歌，放弃分析（serial={mySerial}）");
                    return;
                }

                try
                {
                    BeatAnalysis result = await analyser!.AnalyseAsync(audio).ConfigureAwait(false);
                    AppLog.Beat.Add($"分析流程总耗时 {sw.ElapsedMilliseconds}ms");
                    Schedule(() => publish(mySerial, result));
                }
                catch (Exception e)
                {
                    AppLog.Beat.Add($"节拍分析异常（serial={mySerial}），{sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                    Schedule(() =>
                    {
                        if (mySerial != serial)
                            return;

                        AnalysisStatus.Value = $"节拍分析失败：{e.Message}";

                        CurrentTiming = neutralTiming();
                        ControlPoints = buildControlPoints(CurrentTiming);
                    });
                }
            });
        }

        /// <summary>
        /// Applies a beatmap's own timing instead of analysing the audio: every red line becomes a
        /// <see cref="TimingControlPoint"/> at its exact time (so the beat grid, first-beat offset and
        /// BPM changes match the mapper's intent) and every kiai state change becomes an
        /// <see cref="EffectControlPoint"/>.
        /// </summary>
        private void publishMapTiming(BeatmapTiming timing)
        {
            ControlPoints = buildControlPoints(timing);
            CurrentTiming = timing;

            List<BeatTimingLine> tempos = timing.TempoLines.OrderBy(l => l.TimeMs).ToList();

            EstimatedBpm.Value = tempos.Count > 0 ? BeatmapTiming.BpmOf(tempos[0]) : 0;
            Confidence.Value = 1;
            AnalysisStatus.Value = $"谱面 BPM {(tempos.Count > 0 ? BeatmapTiming.BpmOf(tempos[0]) : 0):0.##}"
                                   + $"　首拍 {timing.FirstBeatMs?.ToString("0.#") ?? "-"}ms"
                                   + $"　变速 {tempos.Count}"
                                   + $"　高潮 {timing.KiaiSections.Count}";

            AppLog.Beat.Add($"使用谱面时间轴（不进行音频分析）：BPM "
                            + string.Join("/", tempos.Select(l => BeatmapTiming.BpmOf(l).ToString("0.##"))),
                LogLevel.Important);
        }

        /// <summary>
        /// Turns a timeline into live control points: every red line becomes a
        /// <see cref="TimingControlPoint"/> at its exact time (beat grid, first-beat offset and BPM
        /// changes as authored) and every kiai state change an <see cref="EffectControlPoint"/>.
        /// </summary>
        private static ControlPointInfo buildControlPoints(BeatmapTiming timing)
        {
            var info = new ControlPointInfo();
            bool kiai = false;

            foreach (BeatTimingLine line in timing.Lines.OrderBy(l => l.TimeMs))
            {
                if (line.Uninherited && line.BeatLengthMs > 0)
                {
                    info.Add(line.TimeMs, new TimingControlPoint
                    {
                        BeatLength = line.BeatLengthMs,
                        TimeSignature = new TimeSignature(line.Meter),
                    });
                }

                if (line.Kiai != kiai)
                {
                    kiai = line.Kiai;
                    info.Add(line.TimeMs, new EffectControlPoint { KiaiMode = kiai });
                }
            }

            return info;
        }

        /// <summary>
        /// 中性时间轴（120 BPM、首拍 0、无 kiai）：音频分析拿不到结果时使用，
        /// 这样 timing 编辑分屏仍然可用，且不会沿用上一首的数据。
        /// </summary>
        private static BeatmapTiming neutralTiming()
            => new BeatmapTiming([new BeatTimingLine(0, 60000.0 / 120, 4, true, false)]);

        /// <summary>
        /// Expresses an audio-analysis result as the same timeline model (one tempo line plus kiai
        /// on/off lines) so the timing editor can adjust it just like a map-authored timeline.
        ///
        /// 分析给出的 <see cref="BeatAnalysis.BeatPhaseMs"/> 是「第一个被检测到的拍点」的绝对时间，
        /// 可能落在歌曲中段。osu! 的 <c>BeatSyncedContainer</c> 对 <c>beatIndex &lt; 0</c> 的拍子
        /// 一律不触发（见 MenuSideFlashes），因此把首条红线放在中间会让它之前的整段歌曲没有节拍。
        /// 这里只保留相位、把红线时间归一到 <c>(-拍长, 0]</c>：节拍网格因此向负方向延伸，覆盖整首歌，
        /// 与 osu! 里首条 timing point 常为负 offset 的约定一致。
        /// </summary>
        private static BeatmapTiming timingFromAnalysis(BeatAnalysis result)
        {
            const int meter = 4;

            double beatLength = result.Bpm > 0 ? 60000.0 / result.Bpm : TimingControlPoint.DEFAULT_BEAT_LENGTH;

            double phase = result.BeatPhaseMs % beatLength;

            if (phase > 0)
                phase -= beatLength;

            var lines = new List<BeatTimingLine>
            {
                new(phase, beatLength, meter, true, false),
            };

            foreach (KiaiSection section in result.KiaiSections)
            {
                // 自动检测出的 kiai 边界来自音量/结构变化，通常偏离拍点几百毫秒。这里把开始与结束
                // 都吸附到最近的「小节第一拍」，让 kiai 与小节线（强拍）对齐。
                double start = snapToDownbeat(section.StartMs, phase, beatLength, meter);
                double end = snapToDownbeat(section.EndMs, phase, beatLength, meter);

                // 不足一小节的段落吸附后可能反向或重合，向后补一个小节，保证区间非空。
                if (end <= start)
                    end = start + beatLength * meter;

                lines.Add(new BeatTimingLine(start, 0, meter, false, true));
                lines.Add(new BeatTimingLine(end, 0, meter, false, false));
            }

            return new BeatmapTiming(lines);
        }

        /// <summary>
        /// 把时间吸附到最近的「小节第一拍」：节拍网格由 (phase, beatLength) 决定，
        /// 每 <paramref name="meter"/> 拍构成一个小节，因此小节线为 phase + n × beatLength × meter。
        /// </summary>
        private static double snapToDownbeat(double timeMs, double phase, double beatLength, int meter)
        {
            double barLength = beatLength * meter;
            return phase + Math.Round((timeMs - phase) / barLength) * barLength;
        }

        private void publish(int mySerial, BeatAnalysis result)
        {
            if (mySerial != serial)
            {
                AppLog.Beat.Add($"分析结果过期，丢弃（serial={mySerial}）");
                return;
            }

            // 同一个时间轴模型，编辑器改的就是它（下次提交会覆盖这里的分析结果）。
            CurrentTiming = timingFromAnalysis(result);
            // 控制点直接从该模型生成，保证分析结果与编辑结果走同一条路径（含向负方向延伸的网格）。
            ControlPoints = buildControlPoints(CurrentTiming);

            EstimatedBpm.Value = result.Bpm;
            Confidence.Value = result.Confidence;
            AnalysisStatus.Value = $"BPM {result.Bpm:0}　置信度 {result.Confidence:0.##}　拍点 {result.DetectedBeats}　高潮段 {result.KiaiSections.Count}";
            AppLog.Beat.Add($"节拍结果已应用到特效：BPM {result.Bpm:0}，首拍 {CurrentTiming.FirstBeatMs:0.#}ms，"
                            + $"高潮段 {result.KiaiSections.Count} 个");
        }

        private static HttpClient createHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }
    }
}
