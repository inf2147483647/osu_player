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
using OsuPlayer.Game.Music.BeatSync.Onnx;
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

        /// <summary>
        /// <see cref="CurrentTiming"/> 属于哪首曲目（hash）。切歌后 <see cref="CurrentTiming"/> 要等到下一帧
        /// <c>beginAnalysis()</c> 才清空，在那之前的窗口里它仍是上一首的时间轴 —— 打开 timing 编辑分屏前
        /// 必须比对归属，否则会把上一首的 BPM / 首拍 / kiai 当成当前曲目的数据展示并写回。
        /// </summary>
        public string? TimingOwnerHash { get; private set; }

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
        private BpmDetectionMode lastBpmMode;

        private static readonly HttpClient http = createHttpClient();

        [BackgroundDependencyLoader]
        private void load()
        {
            analyser = new BeatAnalyser();

            // 记住启动时的模式，免得第一帧就把它当成「用户刚切换过」而白跑一次分析。
            lastBpmMode = player.BpmMode.Value;
        }

        protected override void Update()
        {
            base.Update();

            if (player.PlayGeneration.Value != lastGeneration)
            {
                lastGeneration = player.PlayGeneration.Value;
                beginAnalysis();
            }

            // 切换检测模式后立刻按新模式重跑一次：不然要等到下一首才生效。
            if (player.BpmMode.Value != lastBpmMode)
            {
                lastBpmMode = player.BpmMode.Value;

                if (player.CurrentSong.Value is not null)
                {
                    AppLog.Beat.Add($"BPM 检测模式切换为 {lastBpmMode}，重新分析当前曲目。", LogLevel.Important);
                    beginAnalysis();
                }
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

            // 在更新线程上取一次模式：分析跑在线程池线程，Bindable 不是线程安全的。
            BpmDetectionMode mode = player.BpmMode.Value;

            // 换歌立刻丢掉上一首的时间轴：分析要几秒，这期间若沿用旧值，timing 编辑分屏
            // 会把上一首的 BPM / 首拍 / kiai 当作当前曲目的数据显示甚至写回（"继承上一首"）。
            setTiming(null);

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
                setTiming(neutralTiming());
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
                    audio = await downloadAudioAsync(url).ConfigureAwait(false);

                    AppLog.Beat.Add($"分析音频就绪：{audio.Length / 1024}KB，{sw.ElapsedMilliseconds}ms");
                }
                catch (Exception e)
                {
                    AppLog.Beat.Add($"分析音频下载失败：{AppLog.SafeUrl(url)}，{sw.ElapsedMilliseconds}ms，{e.GetType().Name}", LogLevel.Error, e);
                    Schedule(() =>
                    {
                        if (mySerial != serial)
                            return;

                        AnalysisStatus.Value = $"节拍分析失败：{e.Message}";

                        // 分析拿不到结果，退回中性时间轴（不然这一首的时间轴会一直是空的）。
                        BeatmapTiming fallback = neutralTiming();
                        setTiming(fallback);
                        ControlPoints = buildControlPoints(fallback);
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

                    if (mode == BpmDetectionMode.Onnx)
                    {
                        // 两阶段发布：Beat This! 在这台机器上要跑约 70 秒（i7-7500U，FP32），
                        // 让用户先等模型再出结果等于每次切歌白等一分钟。先把原生结果秒发出去
                        // （特效立刻有正确网格可用），模型在同一后台任务里继续跑，完成后再用
                        // 模型的节拍/重拍静默替换 —— 对用户只是一次无感知的「跳格校准」。
                        int revisionAtNative = 0;
                        Schedule(() =>
                        {
                            revisionAtNative = player.TimingRevision.Value;
                            publish(mySerial, result, "原生", "ONNX 精修中…");
                        });

                        bool refined = OnnxBeatTracker.TryOverride(audio, result, out BeatAnalysis onnxResult);

                        if (refined)
                            result = onnxResult;

                        AppLog.Beat.Add($"分析流程总耗时 {sw.ElapsedMilliseconds}ms（原生先发，ONNX {(refined ? "精修完成" : "不可用")}）");
                        // 精修到达前用户若已手动编辑过时间轴，不得覆盖他的修改。
                        Schedule(() => publish(mySerial, result, refined ? "ONNX" : "原生", timingRevisionBefore: revisionAtNative));
                    }
                    else
                    {
                        AppLog.Beat.Add($"分析流程总耗时 {sw.ElapsedMilliseconds}ms");
                        Schedule(() => publish(mySerial, result, "原生"));
                    }
                }
                catch (Exception e)
                {
                    AppLog.Beat.Add($"节拍分析异常（serial={mySerial}），{sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                    Schedule(() =>
                    {
                        if (mySerial != serial)
                            return;

                        AnalysisStatus.Value = $"节拍分析失败：{e.Message}";

                        BeatmapTiming fallback = neutralTiming();
                        setTiming(fallback);
                        ControlPoints = buildControlPoints(fallback);
                    });
                }
            });
        }

        /// <summary>
        /// 统一设置时间轴及其归属曲目。所有对 <see cref="CurrentTiming"/> 的赋值都必须走这里，
        /// 否则 <see cref="TimingOwnerHash"/> 会与实际内容脱节，切歌窗口期的归属校验就失效了。
        /// </summary>
        private void setTiming(BeatmapTiming? timing)
        {
            CurrentTiming = timing;
            TimingOwnerHash = timing is null ? null : player.CurrentSong.Value?.Hash;
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
            setTiming(timing);

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
        /// osu! 的 <c>BeatSyncedContainer</c> 对 <c>beatIndex &lt; 0</c> 的拍子一律不触发
        /// （见 MenuSideFlashes），而小节级特效又只在 <c>beatIndex % 4 == 0</c> 时触发，所以红线
        /// 必须同时做到两件事：时间 ≤ 0（网格向负方向延伸、覆盖整首歌），且落在**小节强拍**上。
        ///
        /// 关键：归一化必须除以**小节长**而不是拍长。原生管线给出的 <see cref="BeatAnalysis.BeatPhaseMs"/>
        /// 是已经按小节对齐过的（<c>detectDownbeatPhase</c> 把它按小节回退到 ≤ 0），只差整数个小节；
        /// 若按拍长取模，就会把原点整体推移整数拍 —— 小节旋转了，<c>% 4</c> 的「第 1 拍」落到真正的
        /// 第 2/3/4 拍上，表现就是「首拍错位」。按小节取模既保留对齐，又仍然落在 (-小节长, 0]。
        /// </summary>
        private static BeatmapTiming timingFromAnalysis(BeatAnalysis result)
        {
            const int meter = 4;

            double beatLength = result.Bpm > 0 ? 60000.0 / result.Bpm : TimingControlPoint.DEFAULT_BEAT_LENGTH;
            double barLength = beatLength * meter;

            double phase = result.BeatPhaseMs % barLength;

            if (phase > 0)
                phase -= barLength;

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

        private void publish(int mySerial, BeatAnalysis result, string source, string? statusSuffix = null, int? timingRevisionBefore = null)
        {
            if (mySerial != serial)
            {
                AppLog.Beat.Add($"分析结果过期，丢弃（serial={mySerial}）");
                return;
            }

            // 两阶段发布的第二阶段：精修跑完期间用户手动改过时间轴就别覆盖。
            if (timingRevisionBefore is int revision && player.TimingRevision.Value != revision)
            {
                AppLog.Beat.Add($"精修结果到达前时间轴已被手动编辑（revision {revision} → {player.TimingRevision.Value}），保留手动结果。");
                return;
            }

            // 同一个时间轴模型，编辑器改的就是它（下次提交会覆盖这里的分析结果）。
            BeatmapTiming analysed = timingFromAnalysis(result);
            setTiming(analysed);
            // 控制点直接从该模型生成，保证分析结果与编辑结果走同一条路径（含向负方向延伸的网格）。
            ControlPoints = buildControlPoints(analysed);

            EstimatedBpm.Value = result.Bpm;
            Confidence.Value = result.Confidence;
            AnalysisStatus.Value = $"{source} BPM {result.Bpm:0}　置信度 {result.Confidence:0.##}　拍点 {result.DetectedBeats}　高潮段 {result.KiaiSections.Count}"
                                   + (statusSuffix is null ? string.Empty : $"　{statusSuffix}");
            AppLog.Beat.Add($"节拍结果已应用到特效（{source}）：BPM {result.Bpm:0}，首拍 {analysed.FirstBeatMs:0.#}ms，"
                            + $"高潮段 {result.KiaiSections.Count} 个");
        }

        /// <summary>
        /// 取分析用的音频字节：本地曲目当前流地址就是磁盘路径，直接读文件；在线曲目走 HTTP 下载。
        /// http:// 失败时换 https:// 重试一次 —— KuGou 部分域名的 http 直链在某些网络/系统栈下
        /// 连不上，而签名 URL 的签名只覆盖路径与参数，换协议不影响鉴权。
        /// </summary>
        private static async Task<byte[]> downloadAudioAsync(string url)
        {
            if (File.Exists(url))
                return await File.ReadAllBytesAsync(url).ConfigureAwait(false);

            try
            {
                return await http.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            catch (Exception e) when (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Beat.Add($"分析音频 HTTP 下载失败（{e.GetType().Name}：{e.Message}），改用 HTTPS 重试");
                return await http.GetByteArrayAsync("https://" + url["http://".Length..]).ConfigureAwait(false);
            }
        }

        private static HttpClient createHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }
    }
}
