namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// 一条歌曲评论（只读）。
    ///
    /// 字段取自酷狗 <c>comments/getCommentWithLike</c> 的 list 元素（已真机实测，见
    /// <c>KuGouApi.GetSongCommentsAsync</c>）。评论接口是只读通道：本应用不提供发表/点赞/回复，
    /// 也没有对应权限，所以这里只有展示所需的字段。
    /// </summary>
    public sealed record SongComment(
        long Id,
        string UserName,
        string Content,
        int LikeCount,
        string TimeText,
        string? AvatarUrl = null,
        string? Location = null,
        string? ReplyToUser = null,
        string? ReplyToContent = null)
    {
        /// <summary>是「回复某条评论」的子楼（接口会给出被回复者与原文）。</summary>
        public bool IsReply => !string.IsNullOrEmpty(ReplyToUser);

        /// <summary>「昵称 · 地区」右侧那行小字；地区缺失（如海外/隐藏）时只显示昵称。</summary>
        public string MetaText => string.IsNullOrWhiteSpace(Location) ? TimeText : $"{TimeText} · {Location}";
    }
}
