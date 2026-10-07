namespace OsuPlayer.Game.Music
{
    /// <summary>How KuGou gates a track: freely playable, member-only, or purchase-only.</summary>
    public enum SongFee
    {
        /// <summary>Not determined yet (no lookup has completed).</summary>
        Unknown,

        /// <summary>Playable without an account or purchase.</summary>
        Free,

        /// <summary>Requires a KuGou membership.</summary>
        Vip,

        /// <summary>Requires buying the track (数字专辑 / 单曲购买).</summary>
        Paid
    }

    /// <summary>A playable track, normalized from the different KuGou endpoint shapes (search / recommend / FM / playlist).</summary>
    public sealed record SongInfo(
        string Hash,
        string Name,
        string Artist,
        string Album,
        long DurationMs,
        long AlbumAudioId = 0,
        string? CoverUrl = null,
        string? LocalPath = null)
    {
        /// <summary>
        /// Fee state reported by the list payload itself (KuGou ships it with every song), so the
        /// VIP / 付费 badge is known the moment a list is rendered. Resolving the play URL later can
        /// still correct it, which is why the player keeps its own cache on top of this.
        /// </summary>
        public SongFee Fee { get; init; } = SongFee.Unknown;

        /// <summary>
        /// 谱面自带的时间轴（仅从 .osz/.olz 里的 .osu 解析得到）。有值时播放直接用作者写好的
        /// BPM / 首拍 / kiai，不再做音频节拍分析。
        /// </summary>
        public BeatmapTiming? Timing { get; init; }

        /// <summary>
        /// 酷狗歌单里的文件 ID（fileid / audio_id）。从歌单删歌时接口要求传它；缺失时回落到
        /// <see cref="AlbumAudioId"/>（参考实现同样是 song.fileId ?? song.mixSongId）。
        /// </summary>
        public long FileId { get; init; }

        /// <summary>专辑 ID。加入歌单的 payload 需要它，缺失时按 0 发送（参考实现同样容错）。</summary>
        public long AlbumId { get; init; }

        public string DisplayText => $"{Name} · {Artist}";

        /// <summary>True for tracks imported from disk (see <see cref="LocalPath"/>) rather than KuGou.</summary>
        public bool IsLocal => !string.IsNullOrEmpty(LocalPath);

        /// <summary>Duration exactly as the song lists render it to the right of the label, e.g. "3:28".</summary>
        public string DurationText => FormatDuration(DurationMs);

        /// <summary>Formats a duration the same way the song lists do, e.g. "3:28" (empty when unknown).</summary>
        public static string FormatDuration(double durationMs)
        {
            if (durationMs <= 0)
                return string.Empty;

            // Truncate to whole seconds first: rounding the minutes and the seconds independently
            // produced impossible values like "1:60" and inflated minutes ("4:37" shown as "5:37").
            int totalSeconds = (int)(durationMs / 1000);
            return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
        }
    }
}
