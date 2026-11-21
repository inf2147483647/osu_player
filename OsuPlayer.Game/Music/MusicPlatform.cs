namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// 本播放器支持的音乐平台。账号体系彼此独立：登录其中一个平台不会影响另一个平台的登录态，
    /// 曲目也各自带着自己的平台标记，队列里可以混排，播放地址按曲目所属平台解析。
    /// </summary>
    public enum MusicPlatform
    {
        KuGou,
        NetEase
    }

    public static class MusicPlatformExtensions
    {
        public static string DisplayName(this MusicPlatform platform) => platform switch
        {
            MusicPlatform.NetEase => "网易云音乐",
            _ => "酷狗音乐",
        };

        /// <summary>界面上的短名称（按钮、状态行用）。</summary>
        public static string ShortName(this MusicPlatform platform) => platform switch
        {
            MusicPlatform.NetEase => "网易云",
            _ => "酷狗",
        };
    }
}
