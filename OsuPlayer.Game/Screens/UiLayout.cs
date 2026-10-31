using osuTK;

namespace OsuPlayer.Game.Screens
{
    /// <summary>
    /// Pixel layout constants of the 1366x768 design draft.
    /// </summary>
    public static class UiLayout
    {
        public const float TopBarHeight = 58;

        /// <summary>Centre of the main disc.</summary>
        public static readonly Vector2 DiscCentre = new(425, 438);

        public const float DiscDiameter = 560;
        public const float DiscRadius = DiscDiameter / 2;

        /// <summary>
        /// 圆饼实际绘制的直径：比设计稿小 15%。悬停时圆饼会放大到 1.1 倍，按原尺寸会盖住
        /// <see cref="DiscRadius"/> 处的歌词内圈；缩到 85% 后最大半径 (×1.1) 仍在歌词之内。
        /// </summary>
        public const float DiscDrawDiameter = DiscDiameter * 0.85f;

        /// <summary>
        /// 歌名 / 歌手 / 状态行的纵向位置。横向不写死：这三行锚在窗口顶边中线上
        /// （<c>Anchor.TopCentre</c>），随窗口宽度保持居中。
        /// </summary>
        public const float TitleY = 92;
        public const float ArtistY = 146;
    }
}
