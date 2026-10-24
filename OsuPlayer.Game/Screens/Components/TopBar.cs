using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Threading;
using osu.Game.Graphics;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Top navigation strip of the design draft:
    /// 设置 / 推荐 / 搜索 / 私人FM / 喜欢 / 播放历史 / 歌单 on the left,
    /// 正在播放 / 账号 / live clock / 通知 on the right.
    /// </summary>
    public partial class TopBar : Container
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        public Action<PanelKind>? OnNavigate;

        private UiTextButton accountButton = null!;
        private UiTextButton notificationButton = null!;
        private SpriteText clockText = null!;
        private ScheduledDelegate? clockDelegate;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = UiLayout.TopBarHeight;

            UiTextButton nav(string text, PanelKind kind) => new(text, () => OnNavigate?.Invoke(kind), 22);

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(16, 16, 22, 245),
                },
                new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 2,
                    Colour = new Color4(60, 60, 80, 255),
                },
                new FillFlowContainer
                {
                    Direction = FillDirection.Horizontal,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new Vector2(24, 0),
                    Spacing = new Vector2(26, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new[]
                    {
                        nav("设置", PanelKind.Settings),
                        nav("本地", PanelKind.Local),
                        nav("推荐", PanelKind.Recommend),
                        nav("搜索", PanelKind.Search),
                        nav("私人FM", PanelKind.PersonalFm),
                        nav("喜欢", PanelKind.Liked),
                        nav("播放历史", PanelKind.History),
                        nav("歌单", PanelKind.Playlists),
                    },
                },
                new FillFlowContainer
                {
                    Direction = FillDirection.Horizontal,
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-24, 0),
                    Spacing = new Vector2(26, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new[]
                    {
                        new UiTextButton("正在播放", () => OnNavigate?.Invoke(PanelKind.Queue), 22),
                        accountButton = new UiTextButton("账号", () => OnNavigate?.Invoke(PanelKind.Account), 22),
                        CreateClock(),
                        notificationButton = new UiTextButton("通知", () => OnNavigate?.Invoke(PanelKind.Notifications), 22),
                    },
                },
            };

            player.LoggedIn.BindValueChanged(_ => updateAccountLabel(), true);
        }

        private Drawable CreateClock()
        {
            var container = new Container
            {
                AutoSizeAxes = Axes.Both,
                Child = clockText = new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = OsuFont.GetFont(size: 22),
                    Colour = Color4.White,
                },
            };

            return container;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            clockDelegate = Scheduler.AddDelayed(updateClock, 0, true);
            updateClock();

            NotificationCenter.Changed += onNotificationsChanged;
            onNotificationsChanged();
        }

        private void onNotificationsChanged()
        {
            // 通知可能来自任意线程，回更新线程再改 Drawable。
            Schedule(() =>
            {
                int unread = NotificationCenter.UnreadCount;
                notificationButton.Text = unread > 0 ? $"通知 {unread}" : "通知";
            });
        }

        private void updateClock()
            => clockText.Text = DateTime.Now.ToString("HH:mm");

        private void updateAccountLabel()
            => accountButton.Text = player.LoggedIn.Value ? $"账号 {player.LoginUserId}" : "账号";

        protected override void Dispose(bool isDisposing)
        {
            clockDelegate?.Cancel();
            NotificationCenter.Changed -= onNotificationsChanged;
            base.Dispose(isDisposing);
        }
    }
}
