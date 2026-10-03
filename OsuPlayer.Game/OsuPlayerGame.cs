using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Overlays;
using osu.Game.Screens.Edit;
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
        /// </summary>
        private readonly string? trainingDirectory;

        private readonly string? trainingReportPath;

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
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // 隐藏系统光标，只保留 osu! 自己画的那一只（GlobalCursorDisplay 由 OsuGameBase 提供）。
            // 用 |= 而不是 =：CursorState 是标志位枚举，避免覆盖宿主已设的其它状态。
            if (Window is not null)
                Window.CursorState |= CursorState.Hidden;

            // 训练模式：BASS 此时已由音频管理器初始化好，跑完评测直接把进程退掉。
            if (trainingDirectory is not null)
            {
                _ = runTrainingAsync();
                return;
            }

            screenStack.Push(new MainScreen());
        }

        private async Task runTrainingAsync()
        {
            try
            {
                string report = await BeatmapTraining.RunAsync(trainingDirectory!, trainingReportPath!).ConfigureAwait(false);
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
    }
}
