using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Threading;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Top navigation strip of the design draft:
    /// 设置 / 本地 / 推荐 / 搜索 / 私人FM / 喜欢 / 播放历史 / 歌单 on the left,
    /// 正在播放 / 账号（头像） / live clock / 通知 on the right.
    /// 导航与功能项都是图标按钮，鼠标悬停时由 osu! 的 tooltip 浮出中文名称。
    /// </summary>
    public partial class TopBar : Container
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notifications { get; set; } = null!;

        public Action<PanelKind>? OnNavigate;

        private AccountAvatarButton accountButton = null!;
        private UiIconButton notificationButton = null!;
        private TopBarClock clock = null!;

        /// <summary>当前账号头像地址；已登录时由账号资料接口异步取回，避免每次登录状态变化都重复请求。</summary>
        private string? accountAvatarUrl;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = UiLayout.TopBarHeight;

            UiIconButton nav(IconUsage icon, string tooltip, PanelKind kind)
                => new(icon, () => OnNavigate?.Invoke(kind), 22, tooltip);

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
                    Position = new Vector2(12, 0),
                    Spacing = Vector2.Zero,
                    AutoSizeAxes = Axes.Both,
                    Children = new[]
                    {
                        nav(FontAwesome.Solid.Cog, "设置", PanelKind.Settings),
                        nav(FontAwesome.Solid.FolderOpen, "本地音乐", PanelKind.Local),
                        nav(FontAwesome.Solid.Compass, "推荐", PanelKind.Recommend),
                        nav(FontAwesome.Solid.Search, "搜索", PanelKind.Search),
                        nav(FontAwesome.Solid.BroadcastTower, "私人 FM", PanelKind.PersonalFm),
                        nav(FontAwesome.Solid.Heart, "我的喜欢", PanelKind.Liked),
                        nav(FontAwesome.Solid.History, "播放历史", PanelKind.History),
                        nav(FontAwesome.Solid.ListUl, "歌单广场", PanelKind.Playlists),
                    },
                },
                new FillFlowContainer
                {
                    Direction = FillDirection.Horizontal,
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-12, 0),
                    Spacing = Vector2.Zero,
                    AutoSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        nav(FontAwesome.Solid.Music, "正在播放", PanelKind.Queue),
                        accountButton = new AccountAvatarButton(player.OnlineTextures,
                            () => OnNavigate?.Invoke(PanelKind.Account)),
                        clock = new TopBarClock
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            // 与图标按钮同高（22 图标 + 9 上下留白），保持悬停底板一致；
                            // 官方 ToolbarClock 也是在固定高度工具栏里用 RelativeSizeAxes.Y。
                            RelativeSizeAxes = Axes.None,
                            Height = 40,
                        },
                        notificationButton = new UiIconButton(FontAwesome.Solid.Bell,
                            () => (notifications as NotificationOverlay)?.ToggleVisibility(), 22, "通知"),
                    },
                },
            };

            player.LoggedIn.BindValueChanged(_ => updateAccount(), true);
        }

        /// <summary>
        /// 官方的未读数镜像。INotificationOverlay.UnreadCount 只暴露 IBindable&lt;int&gt;，
        /// 直接订阅无法解绑，因此用一个本地 Bindable 绑定过去。
        /// </summary>
        private readonly IBindable<int> unreadMirror = new Bindable<int>();

        protected override void LoadComplete()
        {
            base.LoadComplete();

            unreadMirror.BindTo(notifications.UnreadCount);
            unreadMirror.BindValueChanged(e => updateNotificationBadge(e.NewValue), true);
        }

        private void updateNotificationBadge(int unread)
        {
            // 官方抽屉在收起时会把所有通知标记为已读，角标随之清零，与 osu! 本体一致。
            notificationButton.BadgeText = unread > 0 ? unread.ToString() : string.Empty;
            notificationButton.TooltipText = unread > 0 ? $"通知（{unread} 条未读）" : "通知";
        }

        private void updateAccount()
        {
            if (!player.LoggedIn.Value)
            {
                accountAvatarUrl = null;
                accountButton.SetAvatar(null);
                accountButton.TooltipText = "账号（未登录）";
                return;
            }

            accountButton.TooltipText = $"账号 {player.LoginUserId}";

            if (!string.IsNullOrEmpty(accountAvatarUrl))
            {
                accountButton.SetAvatar(accountAvatarUrl);
                return;
            }

            // 先挂 guest 头像占位，资料回来后再换成账号头像。
            accountButton.SetAvatar(null);
            _ = loadAvatarAsync();
        }

        private async Task loadAvatarAsync()
        {
            MusicPlayer.AccountProfile? profile = await player.GetProfileAsync();

            Schedule(() =>
            {
                if (!player.LoggedIn.Value)
                    return;

                accountAvatarUrl = profile?.AvatarUrl;
                accountButton.SetAvatar(accountAvatarUrl);
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            unreadMirror.UnbindAll();
            base.Dispose(isDisposing);
        }

        /// <summary>
        /// 顶栏账号按钮：登录后显示账号头像，未登录显示 osu! 官方 guest 头像；
        /// 头像还没加载出来（或加载失败）时露出用户图标占位。悬停显示账号说明。
        /// </summary>
        private partial class AccountAvatarButton : OsuAnimatedButton
        {
            /// <summary>osu! 官方的 guest 头像（未登录时使用）。</summary>
            private const string guest_avatar_url = "https://osu.ppy.sh/images/layout/avatar-guest.png";

            private const float avatar_size = 30;

            private readonly OnlineSprite avatar;

            public AccountAvatarButton(TextureStore textures, Action action)
            {
                Action = action;
                AutoSizeAxes = Axes.Both;

                // 与 UiIconButton 一样在横向 FillFlow 里对齐中线。
                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;

                Add(new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(avatar_size),
                    // 与图标按钮一致的悬停/点击热区。
                    Margin = new MarginPadding { Horizontal = 9, Vertical = 9 },
                    Children = new Drawable[]
                    {
                        // 头像未就绪时的占位图标（在圆形头像下层，图片一到位就被盖住）。
                        new SpriteIcon
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new Vector2(avatar_size * 0.65f),
                            Icon = FontAwesome.Solid.User,
                            Colour = new Color4(150, 150, 160, 255),
                        },
                        new CircularContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            Child = avatar = new OnlineSprite(textures)
                            {
                                RelativeSizeAxes = Axes.Both,
                            },
                        },
                    },
                });

                SetAvatar(null);
            }

            /// <summary>设置头像地址；null / 空串回落到 guest 头像。</summary>
            public void SetAvatar(string? url)
                => avatar.SetUrl(string.IsNullOrEmpty(url) ? guest_avatar_url : url);

            [BackgroundDependencyLoader]
            private void load()
            {
                // 与 UiTextButton / UiIconButton 一致：去掉常驻阴影底板，只保留官方悬停高亮。
                Content.CornerRadius = 6;
                Content.EdgeEffect = default;
            }
        }
    }
}
