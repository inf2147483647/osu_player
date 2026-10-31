using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Graphics.Effects
{
    /// <summary>
    /// 主界面背景：当前歌曲封面铺满整个窗口（保持纵横比、裁掉溢出部分 —— 即「填充」），
    /// 再做一层高斯模糊；切歌时新封面淡入，1.0s 完成。
    /// 最上面压一层半透明暗色，保证白色歌名与歌词仍然可读。
    /// </summary>
    public partial class CoverImageBackground : Container
    {
        /// <summary>切换动画时长（毫秒）。</summary>
        private const double transition_duration = 1000;

        /// <summary>模糊半径（高斯 sigma，单位像素）。</summary>
        private static readonly Vector2 blur_sigma = new(7);

        /// <summary>压暗层：模糊后的封面仍可能很亮，压暗保证前景文字可读。</summary>
        private static readonly Color4 scrim_colour = new Color4(6, 6, 10, 140);

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        /// <summary>封面层都放这里 —— 放在压暗层下面，新层叠上去也不会盖住压暗。</summary>
        private Container coverHost = null!;

        /// <summary>当前这一层的引用：切歌时用它判断新的淡入是否已经完成。</summary>
        private Drawable? coverLayer;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.Both;

            Children = new Drawable[]
            {
                // 兜底底色：没封面、或封面还没下载完时不会露出窗口背景。
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(15, 15, 23, 255),
                },
                coverHost = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = scrim_colour,
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            player.CurrentCover.BindValueChanged(e => showCover(e.NewValue), true);
        }

        private void showCover(string? coverUrl)
        {
            // 本地曲目 / 没有封面：保留当前背景不动。
            if (string.IsNullOrEmpty(coverUrl))
                return;

            Drawable layer = createCoverLayer(coverUrl);
            layer.Alpha = 0;
            coverHost.Add(layer);
            coverLayer = layer;

            // 新层在上面淡入（旧层保持原样），淡入完成后把下面所有旧层一次性清掉：
            // 这样交叉过程中整体亮度不会变暗，连续切歌也不会残留旧封面。
            layer.FadeIn(transition_duration, Easing.Out).Finally(_ =>
            {
                if (!ReferenceEquals(coverLayer, layer))
                    return;

                foreach (Drawable child in coverHost.Children.ToArray())
                {
                    if (!ReferenceEquals(child, layer))
                        coverHost.Remove(child, true);
                }
            });
        }

        /// <summary>一层封面：模糊缓冲里放一张「填充」模式的封面图。</summary>
        private Drawable createCoverLayer(string coverUrl)
        {
            var sprite = new OnlineSprite(player.OnlineTextures)
            {
                // 居中锚点：「填充」模式下超出窗口的部分要上下左右平均裁掉，
                // 否则默认左上对齐会把画面顶到窗口最上面。
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fill,
            };

            sprite.SetUrl(coverUrl);

            return new BufferedContainer
            {
                RelativeSizeAxes = Axes.Both,
                BlurSigma = blur_sigma,
                Children = new Drawable[]
                {
                    // 稍微放大：模糊会把靠近边缘的像素往外糊，放大后四边不会露出底色。
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Scale = new Vector2(1.06f),
                        Child = sprite,
                    },
                },
            };
        }
    }
}
