using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 底栏的纯图标按钮：图标居中、鼠标悬停浮出中文说明（osu! 的 tooltip 容器负责显示）。
    /// 与 <see cref="UiTextButton"/> 共用同一套 osu! 交互（悬停高亮、按下回弹、点击闪光与音效）。
    /// 支持右下角数字角标（如单曲循环的「1」）。
    /// </summary>
    public partial class UiIconButton : OsuAnimatedButton
    {
        /// <summary>图标宿主：固定方形，角标相对它定位（不参与按钮的自动尺寸）。</summary>
        private readonly Container iconHost;

        private readonly SpriteIcon icon;

        private readonly Container badge;

        private readonly SpriteText badgeText;

        /// <summary>图标本身（切换播放/暂停、循环方式、喜欢状态时直接换它）。</summary>
        public IconUsage Icon
        {
            get => icon.Icon;
            set => icon.Icon = value;
        }

        public float IconSize
        {
            get => icon.Size.X;
            set
            {
                icon.Size = new Vector2(value);
                iconHost.Size = new Vector2(value);
            }
        }

        private Color4 baseColour = Color4.White;

        public Color4 IconColour
        {
            get => baseColour;
            set
            {
                baseColour = value;
                icon.Colour = value;
            }
        }

        /// <summary>右下角的数字角标（如单曲循环的「1」）；空字符串隐藏。</summary>
        private string badgeValue = string.Empty;

        public string BadgeText
        {
            get => badgeValue;
            set
            {
                badgeValue = value;
                badgeText.Text = value;
                badge.Alpha = string.IsNullOrEmpty(value) ? 0 : 1;
            }
        }

        /// <param name="iconUsage">要显示的图标。</param>
        /// <param name="action">点击动作；为 null 时只作为状态指示（如音量图标）。</param>
        /// <param name="iconSize">图标边长（点击区域会比它四周各多 9 像素）。</param>
        /// <param name="tooltip">悬停时显示的中文说明。</param>
        public UiIconButton(IconUsage iconUsage, Action? action = null, float iconSize = 20, string tooltip = "")
        {
            Action = action;
            AutoSizeAxes = Axes.Both;

            // 在横向 FillFlow 里与其它不同高度的按钮共用一条中线，避免大小图标上下错位。
            Anchor = Anchor.CentreLeft;
            Origin = Anchor.CentreLeft;

            TooltipText = tooltip;

            Add(iconHost = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(iconSize),
                // 图标四周留白，既是视觉间距也是点击/悬停热区（纯图标太小不好命中）。
                Margin = new MarginPadding { Horizontal = 9, Vertical = 9 },
                Children = new Drawable[]
                {
                    icon = new SpriteIcon
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(iconSize),
                        Icon = iconUsage,
                        Colour = Color4.White,
                    },
                    // 角标圆点骑在图标右下角上；iconHost 是固定尺寸，角标略微出界不影响按钮自动尺寸。
                    badge = new Container
                    {
                        Anchor = Anchor.BottomRight,
                        Origin = Anchor.Centre,
                        Position = new Vector2(1, 1),
                        Alpha = 0,
                        AutoSizeAxes = Axes.Both,
                        Children = new Drawable[]
                        {
                            new Circle
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Size = new Vector2(14),
                                Colour = new Color4(255, 102, 171, 255),
                            },
                            badgeText = new SpriteText
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Font = OsuFont.Default.With(size: 10, weight: FontWeight.Bold),
                                Colour = Color4.White,
                                Text = string.Empty,
                            },
                        },
                    },
                },
            });
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // 与 UiTextButton 一致：去掉常驻的圆角阴影底板，只保留悬停/点击时的官方高亮。
            Content.CornerRadius = 6;
            Content.EdgeEffect = default;
            icon.Colour = baseColour;

            // ClickableContainer 的 Action setter 会把 Enabled 设成 action != null，而
            // OsuAnimatedButton 在禁用时会把内容压成 Gray9。这里没有 Action 的按钮（音量图标）
            // 只是状态指示，需要保持启用：白色图标 + 悬停高亮 + tooltip 都要正常。
            if (Action is null)
                Enabled.Value = true;
        }
    }
}
