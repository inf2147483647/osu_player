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
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        [Resolved]
        private IBeatSyncProvider beatSync { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notificationOverlay { get; set; } = null!;

        private SpriteText titleText = null!;
        private SpriteText artistText = null!;
        private SpriteText statusText = null!;
        private SidePanel sidePanel = null!;
        private TimingEditorPanel timingEditor = null!;

        private MenuSideFlashes sideFlashes = null!;
        private KiaiFountains kiaiFountains = null!;

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
                // Background.
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(15, 15, 23, 255),
                },

                // osu! beat-synced effects (behind the content).
                sideFlashes = new MenuSideFlashes(),
                kiaiFountains = new KiaiFountains(),

                // Main record disc.
                new Container
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Position = UiLayout.DiscCentre,
                    Size = new Vector2(UiLayout.DiscDrawDiameter),
                    Child = new NowPlayingDisc(),
                },

                // Radial lyrics fan.
                new RadialLyrics(),

                // Title / artist block above-right of the disc. Wrapped in copy targets so a
                // right-click or a touch long-press offers 「复制歌名」/「复制歌手」.
                new CopyContextTarget
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(UiLayout.TitleCentreX, UiLayout.TitleY),
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
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(UiLayout.TitleCentreX, UiLayout.ArtistY),
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
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(UiLayout.TitleCentreX, UiLayout.ArtistY + 38),
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
            AddInternal(overlayBridge);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            player.CurrentSong.BindValueChanged(onSongChanged, true);
            ((MusicBeatSyncProvider)beatSync).AnalysisStatus.BindValueChanged(onStatusChanged, true);
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
            if (e.Key != Key.Space || e.Repeat)
                return base.OnKeyDown(e);

            if (GetContainingInputManager() is InputManager input && input.FocusedDrawable is TextBox)
                return base.OnKeyDown(e);

            player.TogglePlay();
            return true;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }
    }
}
