using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Framework.Input.Bindings;
using osu.Framework.Screens;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osu.Framework.Logging;
using OsuPlayer.Game.Graphics.Effects;
using OsuPlayer.Game.Graphics.NowPlaying;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using OsuPlayer.Game.Music.BeatSync;
using OsuPlayer.Game.Screens.Components;

namespace OsuPlayer.Game.Screens
{
    /// <summary>
    /// The main player screen, assembled exactly from the 1366x768 design draft:
    /// top navigation, rotating record disc with radial lyrics, bottom transport,
    /// plus osu!'s side flashes and kiai star fountains.
    /// </summary>
    public partial class MainScreen : Screen, IKeyBindingHandler<GlobalAction>
    {
        /// <summary>方向键步进 / 步退的时间长度（毫秒）。</summary>
        private const double seek_step_ms = 5000;

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        [Resolved]
        private IBeatSyncProvider beatSync { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notificationOverlay { get; set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private SpriteText titleText = null!;
        private SpriteText artistText = null!;
        private SpriteText statusText = null!;
        private SidePanel sidePanel = null!;
        private TimingEditorPanel timingEditor = null!;

        /// <summary>
        /// 抽屉打开且未固定时铺在内容之上的透明点击层：点它（= 抽屉外）就收起抽屉。
        /// 放在顶栏/底栏之下，所以打开抽屉时顶栏导航与播放控制仍然可用。
        /// </summary>
        private OsuClickableContainer outsideClickCatcher = null!;

        private MenuSideFlashes sideFlashes = null!;
        private KiaiFountains kiaiFountains = null!;
        private RadialLyrics radialLyrics = null!;

        /// <summary>圆饼外层容器（只负责定位与尺寸，不参与旋转）。</summary>
        private Container discHolder = null!;

        /// <summary>主播放器圆饼：歌词驱动的旋转只作用于饼内封面。</summary>
        private NowPlayingDisc disc = null!;

        private CoverImageBackground background = null!;

        /// <summary>
        /// 把这两个特效真正播放的动画转发给透明全屏特效子进程（播放器最小化时铺在桌面上）。
        /// 见 <see cref="ExternalOverlayBridge"/>。
        /// </summary>
        private ExternalOverlayBridge overlayBridge = null!;

        /// <summary>
        /// 文本的「右键 / 触摸长按 → 复制」菜单。缓存在这里，树上任意位置的
        /// <see cref="CopyContextTarget"/>（歌名、歌手、歌词行）都能解析到同一份。
        /// </summary>
        [Cached]
        private readonly CopyContextMenu copyMenu = new CopyContextMenu();

        /// <summary>
        /// 底栏「音质 / 倍数」的下拉浮层。缓存在这里，底栏按 <see cref="PlaybackOptionsPopup"/> 解析它，
        /// 弹出的卡片因此位于内容树最上层（底栏自己那条控制带之外也能收到点击）。
        /// </summary>
        [Cached]
        private readonly PlaybackOptionsPopup playbackOptions = new PlaybackOptionsPopup();

        [BackgroundDependencyLoader]
        private void load()
        {
            // 先建桥接（它要拿到下面两个控件的引用），再挂进树。
            overlayBridge = new ExternalOverlayBridge();

            InternalChildren = new Drawable[]
            {
                // Background — 当前歌曲封面（「填充」铺满 + 模糊），切歌 1s 淡入淡出。
                background = new CoverImageBackground(),

                // osu! beat-synced effects (behind the content).
                sideFlashes = new MenuSideFlashes(),
                kiaiFountains = new KiaiFountains(),

                // Main record disc.
                discHolder = new Container
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Position = UiLayout.DiscCentre,
                    Size = new Vector2(UiLayout.DiscDrawDiameter),
                    Child = disc = new NowPlayingDisc(),
                },

                // Radial lyrics fan.
                radialLyrics = new RadialLyrics(),

                // Title / artist block: 水平居中于窗口（设计稿里圆饼偏左，文字块是居中的）。
                // Wrapped in copy targets so a right-click or a touch long-press offers
                // 「复制歌名」/「复制歌手」.
                new CopyContextTarget
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(0, UiLayout.TitleY),
                    AutoSizeAxes = Axes.Both,
                    ItemText = "复制歌名",
                    ContentProvider = () => player.CurrentSong.Value?.Name,
                    Child = titleText = new SpriteText
                    {
                        Font = OsuFont.GetFont(size: 40, weight: FontWeight.Bold),
                        Colour = Color4.White,
                        Text = "歌曲名",
                    },
                },
                new CopyContextTarget
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(0, UiLayout.ArtistY),
                    AutoSizeAxes = Axes.Both,
                    ItemText = "复制歌手",
                    ContentProvider = () => player.CurrentSong.Value?.Artist,
                    Child = artistText = new SpriteText
                    {
                        Font = OsuFont.GetFont(size: 24),
                        Colour = new Color4(175, 175, 190, 255),
                        Text = "歌曲作者",
                    },
                },
                // 状态行（BPM / 首拍 / kiai）同时是 timing 编辑弹窗的入口。
                new OsuClickableContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(0, UiLayout.ArtistY + 38),
                    AutoSizeAxes = Axes.Both,
                    Action = openTimingEditor,
                    Child = statusText = new SpriteText
                    {
                        Font = OsuFont.GetFont(size: 14),
                        Colour = new Color4(110, 110, 128, 255),
                        Text = string.Empty,
                    },
                },

                // Bottom transport area.
                new BottomBar { OnNavigate = showPanel },

                // 点击抽屉外的部分 → 收起未固定的抽屉。
                // 用 Alpha 开关：Alpha 为 0 时 IsPresent=false，完全不参与命中测试，
                // 不会挡住 BPM 状态、圆饼等内容；需要时用一个不可见的极小 Alpha 激活。
                outsideClickCatcher = new OsuClickableContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Alpha = 0,
                    Action = closeUnpinnedDrawers,
                },

                // Bars above the content, slide-in panel above everything, right-docked timing split view on top.
                new TopBar { OnNavigate = showPanel },
                sidePanel = new SidePanel(),
                timingEditor = new TimingEditorPanel(),
                // 播放选项浮层在复制菜单之下：两者关闭时都不参与输入，打开时后者最上层。
                playbackOptions,
                copyMenu,
            };

            // 把特效动画接给桌面全屏特效子进程（本身无视觉，挂进树只为跟随屏幕生命周期）。
            overlayBridge.Attach(sideFlashes, kiaiFountains);

            // 歌词扇形滚过多少度，饼内封面就跟着转多少度（同一时长与缓动，所以两者同步）。
            // 只在「跟随歌词」模式下生效；「匀速」模式由 NowPlayingDisc 自己转，两者不叠加。
            radialLyrics.FanScrolled += angle =>
            {
                if (player.CoverRotation.Value != CoverRotationMode.FollowLyrics)
                    return;

                disc.RotateCoverTo(angle, RadialLyrics.ScrollDuration, RadialLyrics.ScrollEasing);
            };

            AddInternal(overlayBridge);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            player.CurrentSong.BindValueChanged(onSongChanged, true);
            ((MusicBeatSyncProvider)beatSync).AnalysisStatus.BindValueChanged(onStatusChanged, true);

            // 「星星喷泉」直接复用 osu! 的同名设置（默认开启）。Alpha = 0 时 KiaiFountains
            // 不再参与更新，也就不会产生喷泉；同一份设置对外部全屏特效子进程同样生效。
            config.GetBindable<bool>(OsuSetting.StarFountains)
                  .BindValueChanged(e => kiaiFountains.Alpha = e.NewValue ? 1 : 0, true);
        }

        protected override void Update()
        {
            base.Update();

            // 只有「抽屉开着且没固定」时才接管点击；固定的抽屉旁边内容照常可交互。
            bool shouldCatch = (sidePanel.IsOpen && !sidePanel.Pinned)
                               || (timingEditor.IsOpen && !timingEditor.Pinned);

            if (shouldCatch == catcherActive)
                return;

            catcherActive = shouldCatch;

            // ClickableContainer 不论 Enabled 都会吃掉命中它的点击，所以用 Alpha 控制
            // 是否参与命中测试：0 = 不在输入队列里（内容可正常点击），极小值 = 隐身但可命中。
            outsideClickCatcher.Alpha = shouldCatch ? 0.001f : 0f;
        }

        /// <summary>点击层当前是否处于激活（隐身但可命中）状态。</summary>
        private bool catcherActive;

        /// <summary>点击抽屉外时收起所有未固定的抽屉；固定住的保持不变。</summary>
        private void closeUnpinnedDrawers()
        {
            if (sidePanel.IsOpen && !sidePanel.Pinned)
                sidePanel.HidePanel();

            if (timingEditor.IsOpen && !timingEditor.Pinned)
                timingEditor.Hide();
        }

        private void onSongChanged(ValueChangedEvent<SongInfo?> e)
        {
            titleText.Text = e.NewValue?.Name ?? "歌曲名";
            artistText.Text = e.NewValue?.Artist ?? "歌曲作者";

            // timing 编辑本身也会更新 CurrentSong（同一首歌换了 Timing），hash 相同就只是编辑回写，
            // 不能据此收起分屏；只有真的换了歌（hash 不同）才收起。
            if (e.NewValue?.Hash == e.OldValue?.Hash)
                return;

            // 换歌（含自动续播）后上一首的时间轴不再适用：收起编辑分屏，等新曲的 timing 就绪再打开，
            // 否则面板里显示/可编辑的会是上一首的 BPM、首拍与 kiai。
            if (timingEditor.State.Value == Visibility.Visible)
            {
                AppLog.Ui.Add("曲目已切换，收起 timing 编辑分屏");
                timingEditor.Hide();
            }
        }

        private void onStatusChanged(ValueChangedEvent<string> e) => statusText.Text = e.NewValue;

        private void showPanel(PanelKind kind) => sidePanel.Show(kind);

        /// <summary>
        /// 在右侧展开 timing 分屏（不是浮窗），编辑当前驱动特效的时间轴：
        /// BPM、首拍延迟、kiai，以及新增 timing 点。
        /// </summary>
        private void openTimingEditor()
        {
            var provider = (MusicBeatSyncProvider)beatSync;

            if (player.CurrentSong.Value is not { } song || provider.CurrentTiming is not { Lines.Count: > 0 } timing)
            {
                AppLog.Ui.Add("暂无 timing 可编辑（未在播放或还没有节拍信息）");
                return;
            }

            // 切歌后 CurrentSong 先更新，而 provider 清空 CurrentTiming 要等到下一帧 beginAnalysis()，
            // 这段窗口里 CurrentTiming 仍属于上一首。不校验归属就会把上一首的 BPM/首拍/kiai
            // 当成新歌的数据展示，并且因为 openedForHash 取的是新歌 hash，编辑还会写回新歌。
            if (provider.TimingOwnerHash != song.Hash)
            {
                AppLog.Ui.Add($"时间轴不属于当前曲目（owner={provider.TimingOwnerHash ?? "-"}，"
                              + $"当前={song.Hash}），等新曲分析完成后再打开");
                return;
            }

            AppLog.Ui.Add("打开 Timing 编辑面板", LogLevel.Important);
            timingEditor.Open(timing);
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Repeat)
                return false;

            if (e.Action != GlobalAction.ToggleNotifications)
                return false;

            (notificationOverlay as NotificationOverlay)?.ToggleVisibility();
            return true;
        }

        /// <summary>
        /// 空格键 = 暂停 / 恢复当前曲目。
        ///
        /// <para>为什么不走 osu! 的全局动作：osu! 把 Space 和 Enter 一起绑给了
        /// <c>GlobalAction.Select</c>，用那个动作会让侧栏「输入歌名，回车搜索」的回车顺带切播放状态。
        /// 这里直接认空格，范围刚好。</para>
        ///
        /// <para>输入焦点在输入框里时（搜索歌词、timing 编辑等）不抢空格 ——
        /// osu!framework 的 <see cref="TextBox"/> 只在有焦点时才进入非位置输入队列，
        /// 所以必须显式判一次焦点，否则打字时空格会顺带切播放。</para>
        /// </summary>
        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Repeat)
                return base.OnKeyDown(e);

            if (GetContainingInputManager() is InputManager input && input.FocusedDrawable is TextBox)
                return base.OnKeyDown(e);

            if (e.Key == Key.Space)
            {
                player.TogglePlay();
                return true;
            }

            // 左 / 右方向键 = 步退 / 步进 5 秒，播放器任意界面状态都可用。
            if (e.Key is Key.Left or Key.Right)
            {
                if (player.CurrentSong.Value is null)
                    return base.OnKeyDown(e);

                double step = e.Key == Key.Left ? -seek_step_ms : seek_step_ms;
                player.Seek(player.CurrentTimeMs.Value + step);
                return true;
            }

            return base.OnKeyDown(e);
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }
    }
}
