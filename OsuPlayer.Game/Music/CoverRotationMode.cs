namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// 圆饼（封面）的旋转方式，由设置页「图形 → 封面旋转方式」控制。
    /// </summary>
    public enum CoverRotationMode
    {
        /// <summary>跟随歌词：歌词扇形滚过多少角度，圆饼就转多少角度。</summary>
        FollowLyrics,

        /// <summary>匀速：按固定周期匀速自转（不跟随歌词）。</summary>
        Constant,
    }
}
