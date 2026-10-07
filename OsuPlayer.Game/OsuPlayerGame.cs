using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Performance;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Statistics;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Screens.Edit;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using OsuPlayer.Game.Music.BeatSync;
using OsuPlayer.Game.Screens;

namespace OsuPlayer.Game
{
    public partial class OsuPlayerGame : OsuPlayerGameBase
    {
        [Cached]
        private readonly MusicPlayer musicPlayer = new MusicPlayer();

        /// <summary>
        /// osu!'s notification system, transplanted wholesale: the temporary toast tray and the
        /// permanent history overlay are the same component. Anywhere in the tree can resolve
        /// <see cref="INotificationOverlay"/> and post a <see cref="osu.Game.Overlays.Notifications.Notification"/>.
        /// Toggled with osu!'s standard <c>GlobalAction.ToggleNotifications</c> binding (Ctrl+N).
        /// </summary>
        [Cached(typeof(INotificationOverlay))]
        private readonly NotificationOverlay notifications = new NotificationOverlay
        {
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
        };

        /// <summary>
        /// osu!'s overlay theme colours. Several osu! controls (SwitchButton, DialogButton, …) resolve
        /// this from their parent tree; osu! ships it per-screen, so the game caches one for every
        /// overlay we host. Pink matches the design draft.
        /// </summary>
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Pink);

        /// <summary>
        /// Beat divisor used by osu!'s timing controls. In osu! the editor caches this; our timing
        /// split pane hosts <see cref="osu.Game.Screens.Edit.Timing.MetronomeDisplay"/> directly, so we
        /// provide the same dependency here (Ctrl-held subdivision of the metronome).
        /// </summary>
        [Cached]
        private readonly BindableBeatDivisor beatDivisor = new BindableBeatDivisor();

        private readonly MusicBeatSyncProvider beatSyncProvider = new MusicBeatSyncProvider();

        private ScreenStack screenStack = null!;

        /// <summary>
        /// 训练模式目录（命令行 <c>--train-beatmaps &lt;目录&gt;</c>）：启动后用该目录下的真实谱面
        /// 评估自动 BPM / kiai 检测，报告写完就退出，不进主界面。见 <see cref="OsuPlayer.Game.Music.BeatSync.BeatmapTraining"/>。
        /// 追加 <c>--train-onnx</c> 则改用 Beat This! 的 ONNX 结果测速，用来对比两种检测模式。
        /// 追加 <c>--export-kiai-data &lt;目录&gt;</c> 则只导出 kiai ML 训练特征 CSV（每首一个文件），
        /// 供 tools/kiai/train_kiai.py 训练帧分类器。
        /// </summary>
        private readonly string? trainingDirectory;

        private readonly string? trainingReportPath;

        private readonly bool trainingOnnx;

        private readonly string? kiaiExportDirectory;

        /// <summary>BPM 评测模式（<c>--eval-bpm &lt;目录&gt;</c>）：递归评测该目录下的 *.wav，
        /// 真值取同名 .bpm，或用 <c>--bpm-labels &lt;目录&gt;</c> 指定。见 <see cref="BeatmapTraining.EvaluateBpmAsync"/>。</summary>
        private readonly string? bpmEvalDirectory;

        private readonly string? bpmLabelDirectory;

        /// <summary>单曲 BPM 推理（<c>--detect-bpm &lt;音频文件&gt;</c>）：打印 BPM 与分阶段耗时。</summary>
        private readonly string? bpmDetectFile;

        /// <summary>评论诊断（<c>--comments &lt;hash&gt; &lt;mixsongid&gt; [歌名]</c>）：抓一页评论打印出来。
        /// 评论是纯网络接口、不便单测，用它验证真实的请求签名与解析路径。</summary>
        private readonly string[]? commentProbe;

        /// <summary>
        /// 超级诊断模式（设置页「调试模式」开关，或启动参数 <c>--diagnostics</c>）：
        /// 全量日志 + osu! 原生性能分析 + 性能叠加层 + 周期性线程/GC 快照。
        /// 见 <see cref="applyDiagnostics"/>。
        /// </summary>

        public OsuPlayerGame(string[]? args = null)
        {
            if (args is null)
                return;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--train-beatmaps" && i + 1 < args.Length)
                    trainingDirectory = args[i + 1];
                else if (args[i].StartsWith("--train-beatmaps=", StringComparison.Ordinal))
                    trainingDirectory = args[i]["--train-beatmaps=".Length..];
                else if (args[i] == "--train-onnx")
                    trainingOnnx = true;
                else if (args[i] == "--export-kiai-data" && i + 1 < args.Length)
                    kiaiExportDirectory = args[i + 1];
                else if (args[i].StartsWith("--export-kiai-data=", StringComparison.Ordinal))
                    kiaiExportDirectory = args[i]["--export-kiai-data=".Length..];
                else if (args[i] == "--eval-bpm" && i + 1 < args.Length)
                    bpmEvalDirectory = args[i + 1];
                else if (args[i].StartsWith("--eval-bpm=", StringComparison.Ordinal))
                    bpmEvalDirectory = args[i]["--eval-bpm=".Length..];
                else if (args[i] == "--bpm-labels" && i + 1 < args.Length)
                    bpmLabelDirectory = args[i + 1];
                else if (args[i].StartsWith("--bpm-labels=", StringComparison.Ordinal))
                    bpmLabelDirectory = args[i]["--bpm-labels=".Length..];
                else if (args[i] == "--detect-bpm" && i + 1 < args.Length)
                    bpmDetectFile = args[i + 1];
                else if (args[i].StartsWith("--detect-bpm=", StringComparison.Ordinal))
                    bpmDetectFile = args[i]["--detect-bpm=".Length..];
                else if (args[i] == "--comments" && i + 2 < args.Length)
                    commentProbe = args.Skip(i + 1).Take(args.Length - i - 1).ToArray();
                else if (args[i] == "--diagnostics")
                    musicPlayer.DebugMode.Value = true;
            }

            if (trainingDirectory is not null)
            {
                string full = Path.GetFullPath(trainingDirectory);
                trainingReportPath = Path.Combine(Path.GetDirectoryName(full) ?? ".", "beatmap-training-report.txt");
            }
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // Register the playback engine before the screen stack, so every screen can
            // resolve it. The beat-sync provider is cached one level deeper (see
            // BeatSyncProviderContainer) so it shadows OsuGameBase's beatmap-backed one.
            Content.Add(musicPlayer);
            Content.Add(new BeatSyncProviderContainer(beatSyncProvider,
                screenStack = new ScreenStack { RelativeSizeAxes = Axes.Both }));

            // Added last so toasts and the history overlay draw above the whole screen stack.
            Content.Add(notifications);

            // 右下角帧率：直接复用 osu! 本体的 FPSCounter（Anchor.BottomRight + 边距 5，与 osu! 一致），
            // 它自己读 OsuSetting.ShowFpsDisplay 控制显隐，并显示 FPS 与帧时间，不再另造一个计数器。
            // 挂在缩放容器之外（Add 而不是 Content.Add），边距与字号保持固定像素，不随界面缩放变形。
            // 显隐由设置页「图形 → 显示帧率」控制，见 LoadComplete 里对 ShowFps 的同步。
            Add(new FPSCounter
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding(5),
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            defaultLogLevel = Logger.Level;
            musicPlayer.DebugMode.BindValueChanged(e => applyDiagnostics(e.NewValue), true);

            // 帧率计数器显隐：设置页「图形 → 显示帧率」写 MusicPlayer.ShowFps，
            // 这里同步给 osu! 的配置项（FPSCounter 自己读它控制显隐），与 FemboyTest 的做法一致。
            LocalConfig.SetValue(OsuSetting.ShowFpsDisplay, musicPlayer.ShowFps.Value);
            musicPlayer.ShowFps.BindValueChanged(e => LocalConfig.SetValue(OsuSetting.ShowFpsDisplay, e.NewValue));

            // 任何用 Error 级记录的失败都会变成一条通知：既进"通知"面板的历史，也立刻弹一个 toast。
            NotificationCenter.Hook();
            NotificationCenter.Posted += showErrorToast;

            // 隐藏系统光标，只保留 osu! 自己画的那一只（GlobalCursorDisplay 由 OsuGameBase 提供）。
            // 用 |= 而不是 =：CursorState 是标志位枚举，避免覆盖宿主已设的其它状态。
            if (Window is not null)
                Window.CursorState |= CursorState.Hidden;

            // 训练/评测模式：BASS 此时已由音频管理器初始化好，跑完直接把进程退掉。
            if (commentProbe is not null)
            {
                _ = runCommentsProbeAsync();
                return;
            }

            if (bpmDetectFile is not null)
            {
                _ = runDetectBpmAsync();
                return;
            }

            if (bpmEvalDirectory is not null)
            {
                _ = runBpmEvalAsync();
                return;
            }

            if (trainingDirectory is not null)
            {
                if (kiaiExportDirectory is not null)
                    _ = runKiaiExportAsync();
                else
                    _ = runTrainingAsync();

                return;
            }

            screenStack.Push(new MainScreen());
        }

        private async Task runTrainingAsync()
        {
            try
            {
                string report = await BeatmapTraining.RunAsync(trainingDirectory!, trainingReportPath!, trainingOnnx).ConfigureAwait(false);
                Console.WriteLine(report);
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"训练失败：{e}");
                Console.Error.Flush();
            }
            finally
            {
                Schedule(() => Host.Exit());
            }
        }

        /// <summary>通知自身如果再记 Error 日志就会无限递归（toast → 日志 → toast），用这个挡住。</summary>
        private bool postingErrorToast;

        private void showErrorToast(NotificationCenter.Entry entry)
        {
            // 事件可能来自任意线程，必须回到更新线程再碰 Drawable。
            Schedule(() =>
            {
                if (postingErrorToast)
                    return;

                postingErrorToast = true;

                try
                {
                    notifications.Post(new SimpleErrorNotification
                    {
                        Text = $"[{entry.Source}] {entry.Message}",
                    });
                }
                finally
                {
                    postingErrorToast = false;
                }
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            NotificationCenter.Posted -= showErrorToast;
            base.Dispose(isDisposing);
        }

        /// <summary>诊断快照间隔（毫秒）。</summary>
        private const double diagnostics_snapshot_interval_ms = 2000;

        /// <summary>开启调试模式前的日志级别，关闭时恢复。</summary>
        private LogLevel defaultLogLevel = LogLevel.Verbose;

        private ScheduledDelegate? diagnosticsSnapshot;

        /// <summary>当前是否已应用诊断模式，仅用于避免重复写日志。</summary>
        private bool diagnosticsEnabled;

        /// <summary>
        /// 超级诊断模式（设置页「调试模式」开关，或启动参数 <c>--diagnostics</c>）：
        ///   · <see cref="Logger.Level"/> 调到最详细的 <see cref="LogLevel.Debug"/>（全量日志）；
        ///   · <c>Host.PerformanceLogging</c> 打开各线程的性能分析（帧耗时 / GC 暂停），
        ///     同时打开 <c>DebugUtils.LogPerformanceIssues</c>（性能异常时自动写日志）；
        ///   · <see cref="FrameStatistics"/> = Full，显示 osu! 原生的性能叠加层（Ctrl+F11 同款），
        ///     直接看到 input / update / audio / draw 四个线程各自的帧率与帧时间曲线；
        ///   · 每 2 秒把全局统计（含 Bass CPU%）与各线程帧率、GC、内存写进 <c>performance.log</c>，
        ///     复盘时能把日志对到具体时间点。
        /// </summary>
        private void applyDiagnostics(bool enabled)
        {
            if (enabled)
            {
                Logger.Level = LogLevel.Debug;
                Host.PerformanceLogging.Value = true;
                FrameStatistics.Value = FrameStatisticsMode.Full;

                diagnosticsSnapshot ??= Scheduler.AddDelayed(logDiagnosticsSnapshot, diagnostics_snapshot_interval_ms, true);
            }
            else
            {
                Logger.Level = defaultLogLevel;
                Host.PerformanceLogging.Value = false;
                FrameStatistics.Value = FrameStatisticsMode.None;

                diagnosticsSnapshot?.Cancel();
                diagnosticsSnapshot = null;
            }

            if (enabled != diagnosticsEnabled)
            {
                diagnosticsEnabled = enabled;

                Logger.Log($"[诊断] 调试模式已{(enabled ? "开启" : "关闭")}：全量日志 + 性能分析 + 性能叠加层"
                           + (enabled ? $" + 每 {diagnostics_snapshot_interval_ms / 1000:0}s 线程/GC 快照" : string.Empty),
                    LoggingTarget.Performance);
            }
        }

        private void logDiagnosticsSnapshot()
        {
            // 全局统计（Bass CPU% 等）由框架维护，直接落到 performance.log。
            GlobalStatistics.OutputToLog();

            foreach (GameThread thread in Host.Threads)
            {
                Logger.Log($"[诊断] {thread.Name,-6} 线程：{thread.Clock.FramesPerSecond,6:0} fps／帧间隔 {thread.Clock.ElapsedFrameTime,6:0.00} ms",
                    LoggingTarget.Performance);
            }

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();

            Logger.Log($"[诊断] 工作集 {process.WorkingSet64 / 1024.0 / 1024.0:0} MB／"
                       + $"托管堆 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:0} MB／"
                       + $"GC Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}",
                LoggingTarget.Performance);
        }

        private async Task runCommentsProbeAsync()
        {
            try
            {
                string hash = commentProbe![0];
                long mixSongId = long.Parse(commentProbe[1], System.Globalization.CultureInfo.InvariantCulture);
                string name = commentProbe.Length > 2 ? commentProbe[2] : string.Empty;

                var song = new SongInfo(hash, name, string.Empty, string.Empty, 0, mixSongId);
                (int total, List<SongComment> items) =
                    await musicPlayer.FetchCommentsAsync(song, 1).ConfigureAwait(false);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# 评论诊断：hash={hash} mixsongid={mixSongId}");
                sb.AppendLine($"总数 {total}，本页 {items.Count} 条");

                foreach (SongComment c in items)
                {
                    sb.AppendLine($"- {c.UserName}（{c.MetaText}）♥{c.LikeCount}");
                    sb.AppendLine($"  {c.Content}");

                    if (c.IsReply)
                        sb.AppendLine($"  回复 @{c.ReplyToUser}：{c.ReplyToContent}");
                }

                Console.WriteLine(sb.ToString());
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"评论诊断失败：{e}");
                Console.Error.Flush();
            }
            finally
            {
                Schedule(() => Host.Exit());
            }
        }

        private async Task runDetectBpmAsync()
        {
            try
            {
                string report = await BeatmapTraining.DetectBpmAsync(bpmDetectFile!).ConfigureAwait(false);
                Console.WriteLine(report);
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"单曲 BPM 推理失败：{e}");
                Console.Error.Flush();
            }
            finally
            {
                Schedule(() => Host.Exit());
            }
        }

        private async Task runBpmEvalAsync()
        {
            try
            {
                string report = await BeatmapTraining.EvaluateBpmAsync(
                    bpmEvalDirectory!, bpmLabelDirectory,
                    Path.Combine(Environment.CurrentDirectory, "bpm-eval-report.txt")).ConfigureAwait(false);
                Console.WriteLine(report);
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"BPM 评测失败：{e}");
                Console.Error.Flush();
            }
            finally
            {
                Schedule(() => Host.Exit());
            }
        }

        private async Task runKiaiExportAsync()
        {
            try
            {
                string summary = await BeatmapTraining.ExportKiaiDataAsync(trainingDirectory!, kiaiExportDirectory!).ConfigureAwait(false);
                Console.WriteLine(summary);
                Console.Out.Flush();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"kiai 特征导出失败：{e}");
                Console.Error.Flush();
            }
            finally
            {
                Schedule(() => Host.Exit());
            }
        }
    }
}
