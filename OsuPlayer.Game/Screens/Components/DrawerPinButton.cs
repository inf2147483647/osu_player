using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 抽屉左上角 / 右上角的大头针开关：点亮后面板固定，点击抽屉外的部分不会关闭；
    /// 熄灭时点击外部关闭面板。状态由按钮自身保存，宿主（SidePanel / TimingEditorPanel）
    /// 直接读 <see cref="Pinned"/> 判断即可。
    /// </summary>
    public partial class DrawerPinButton : OsuAnimatedButton
    {
        private static readonly Color4 idle_colour = new Color4(150, 150, 165, 255);
        private static readonly Color4 pinned_colour = new Color4(255, 102, 171, 255);

        private SpriteIcon icon = null!;

        /// <summary>默认点亮：抽屉打开后即固定，需手动取消后才会点外部关闭。</summary>
        private bool pinned = true;

        public bool Pinned
        {
            get => pinned;
            set
            {
                if (pinned == value)
                    return;

                pinned = value;
                updateVisuals();
            }
        }

        public DrawerPinButton()
        {
            AutoSizeAxes = Axes.Both;
            Anchor = Anchor.CentreLeft;
            Origin = Anchor.CentreLeft;

            Action = () => Pinned = !Pinned;

            Add(new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(28),
                Margin = new MarginPadding { Horizontal = 6, Vertical = 6 },
                Child = icon = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(17),
                    Icon = FontAwesome.Solid.Thumbtack,
                    Colour = idle_colour,
                },
            });

            updateVisuals();
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // 与顶栏图标按钮一致：去掉常驻阴影底板，只保留官方悬停高亮。
            Content.CornerRadius = 6;
            Content.EdgeEffect = default;
        }

        private void updateVisuals()
        {
            icon.FadeColour(pinned ? pinned_colour : idle_colour, 160, Easing.OutQuint);
            icon.RotateTo(pinned ? -45 : 0, 160, Easing.OutQuint);

            TooltipText = pinned ? "取消固定（点击面板外会关闭）" : "固定面板（点击面板外不关闭）";
        }
    }
}
