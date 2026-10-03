using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Input.Bindings;
using osu.Framework.Screens;
using osuTK;
using osuTK.Graphics;
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

        /// <summary>
        /// 文本的「右键 / 触摸长按 → 复制」菜单。缓存在这里，树上任意位置的
        /// <see cref="CopyContextTarget"/>（歌名、歌手、歌词行）都能解析到同一份。
        /// </summary>
        [Cached]
        private readonly CopyContextMenu copyMenu = new CopyContextMenu();

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                // Background.
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(15, 15, 23, 255),
                },

                // osu! beat-synced effects (behind the content).
                new MenuSideFlashes(),
                new KiaiFountains(),

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
                // 复制菜单在最上层；关闭时它完全不参与输入，不影响下面的交互。
                copyMenu,
            };
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

            if (player.CurrentSong.Value is null || provider.CurrentTiming is not { Lines.Count: > 0 } timing)
            {
                AppLog.Ui.Add("暂无 timing 可编辑（未在播放或还没有节拍信息）");
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

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }
    }
}
