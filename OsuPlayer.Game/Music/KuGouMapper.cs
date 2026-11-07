using Newtonsoft.Json.Linq;
using OsuPlayer.Game.KuGou;

namespace OsuPlayer.Game.Music
{
    public sealed record PlaylistInfo(string CollectionId, string Name, string? CoverUrl, int SongCount);

    /// <summary>账号下的一个自建歌单（含默认的「我喜欢」）。listid 是曲目接口与加删歌接口要用的 id。</summary>
    public sealed record UserPlaylistInfo(long ListId, string Name, string GlobalCollectionId, bool IsDefault);

    /// <summary>
    /// Maps the slightly different shapes of each KuGou endpoint into the player's own models.
    /// Field names were verified against live responses on 2026-10; fallbacks cover FM/playlist variants.
    /// </summary>
    public static class KuGouMapper
    {
        public static IReadOnlyList<SongInfo> SearchResults(JObject json)
        {
            var list = (JArray?)json["data"]?["info"];
            return list?.Select(s => new SongInfo(
                str(s, "hash"),
                str(s, "songname"),
                str(s, "singername"),
                str(s, "album_name"),
                num(s, "duration") * 1000,
                num(s, "album_audio_id"),
                // 搜索接口不直接给封面，封面地址在 trans_param.union_cover（含 {size} 占位符）。
                cover(s["trans_param"], "union_cover")
                    ?? cover(s, "union_cover")
                    ?? cover(s, "album_cover")
                    ?? cover(s, "cover"))
            {
                Fee = fee(s),
            }).Where(x => !string.IsNullOrEmpty(x.Hash))
                .ToList() ?? [];
        }

        public static IReadOnlyList<SongInfo> HomeRecommend(JObject json)
        {
            var items = (JArray?)json["data"]?["items"];
            return items?
                .Select(i => i["song_info"])
                .Where(s => s is not null)
                .Select(s => new SongInfo(
                    str(s!, "hash_128"),
                    str(s!, "songname"),
                    str(s!, "author_name"),
                    str(s!, "album_name"),
                    pickMs(s!, "timelength_128", "duration"),
                    num(s!, "album_audio_id"),
                    cover(s, "album_cover"))
                {
                    Fee = fee(s!),
                })
                .Where(x => !string.IsNullOrEmpty(x.Hash))
                .ToList() ?? [];
        }

        public static IReadOnlyList<SongInfo> PersonalFm(JObject json)
        {
            var list = (JArray?)json["data"]?["song_list"];
            return list?.Select(s =>
            {
                string artist = string.Join(",", ((JArray?)s["singerinfo"])?.Select(a => a["name"]?.ToString()) ?? []);
                if (string.IsNullOrEmpty(artist))
                    artist = str(s, "singername") ?? str(s, "author_name");

                string? hash = s["hash_128"]?.ToString() ?? s["hash"]?.ToString();
                return new SongInfo(
                    hash ?? string.Empty,
                    str(s, "songname"),
                    artist,
                    str(s, "album_name") ?? str(s, "albumname"),
                    pickMs(s, "timelength_128", "duration"),
                    num(s, "album_audio_id"),
                    cover(s, "album_cover") ?? cover(s, "image") ?? cover(s, "cover"))
                {
                    Fee = fee(s),
                };
            }).Where(x => !string.IsNullOrEmpty(x.Hash)).ToList() ?? [];
        }

        public static IReadOnlyList<PlaylistInfo> Playlists(JObject json)
        {
            var list = (JArray?)json["data"]?["special_list"];
            return list?.Select(p => new PlaylistInfo(
                str(p, "global_collection_id"),
                str(p, "specialname"),
                cover(p, "imgurl") ?? cover(p, "img"),
                (int)num(p, "songcount")))
                .Where(x => !string.IsNullOrEmpty(x.CollectionId)).ToList() ?? [];
        }

        public static IReadOnlyList<SongInfo> PlaylistTracks(JObject json)
        {
            var list = (JArray?)json["data"]?["songs"];
            return list?.Select(s => new SongInfo(
                str(s, "hash"),
                str(s, "name"),
                string.Join(",", ((JArray?)s["singerinfo"])?.Select(a => a["name"]?.ToString()) ?? []),
                s["albuminfo"]?["name"]?.ToString() ?? str(s, "remark"),
                // Verified live (2026-10): this endpoint reports "timelen" in milliseconds.
                pickMs(s, "timelen", "duration", "timelength"),
                num(s, "album_audio_id") != 0 ? num(s, "album_audio_id") : num(s, "audio_id"),
                cover(s, "cover"))
            {
                Fee = fee(s),
            })
                .Where(x => !string.IsNullOrEmpty(x.Hash)).ToList() ?? [];
        }

        /// <summary>
        /// 账号自建歌单列表（含默认「我喜欢」）。POST /v7/get_all_list → data.info[]。
        /// </summary>
        public static IReadOnlyList<UserPlaylistInfo> UserPlaylists(JObject json)
        {
            var list = (JArray?)json["data"]?["info"] ?? (JArray?)json["data"]?["list"];

            return list?.Select(p =>
            {
                long listId = num(p, "listid");
                if (listId == 0)
                    listId = num(p, "specialid");

                string name = str(p, "name");
                if (string.IsNullOrEmpty(name))
                    name = str(p, "specialname");

                return new UserPlaylistInfo(
                    listId,
                    name,
                    p["global_collection_id"]?.ToString() ?? p["gid"]?.ToString() ?? string.Empty,
                    num(p, "is_def") is 1 or 2 || num(p, "is_default") == 1);
            }).Where(x => x.ListId != 0).ToList() ?? [];
        }

        /// <summary>
        /// 账号歌单内的曲目。POST /v4/get_list_all_file_v3 → data.info[]（旧版为 data.songs[]）。
        /// 与公开歌单不同，这里的曲目信息可能整块嵌在 audio_info 里；同时保留 fileid 供删歌使用。
        /// </summary>
        public static IReadOnlyList<SongInfo> UserPlaylistTracks(JObject json)
        {
            var list = (JArray?)json["data"]?["info"]
                       ?? (JArray?)json["data"]?["songs"]
                       ?? (JArray?)json["data"]?["list"]
                       ?? (JArray?)json["data"];

            return list?.Select(s =>
            {
                JToken audio = s["audio_info"] ?? s;

                long albumAudioId = num(s, "album_audio_id");
                if (albumAudioId == 0) albumAudioId = num(s, "mixsongid");
                if (albumAudioId == 0) albumAudioId = num(audio, "album_audio_id");
                if (albumAudioId == 0) albumAudioId = num(audio, "mixsongid");

                long fileId = num(s, "fileid");
                if (fileId == 0) fileId = num(s, "file_id");
                if (fileId == 0) fileId = num(s, "audio_id");
                if (fileId == 0) fileId = num(audio, "audio_id");

                long albumId = num(s, "album_id");
                if (albumId == 0) albumId = num(audio, "album_id");

                string hash = str(s, "hash");
                if (string.IsNullOrEmpty(hash)) hash = str(audio, "hash_128");
                if (string.IsNullOrEmpty(hash)) hash = str(audio, "hash");

                string name = str(s, "songname");
                if (string.IsNullOrEmpty(name)) name = str(s, "name");
                if (string.IsNullOrEmpty(name)) name = str(s, "audio_name");
                if (string.IsNullOrEmpty(name)) name = str(audio, "songname");
                if (string.IsNullOrEmpty(name)) name = str(audio, "name");

                // audio_name / name 常是文件名（含扩展名）；用它兜底时会把 ".mp3" 带进歌名。
                name = stripAudioExtension(name);

                string artist = str(s, "singername");
                if (string.IsNullOrEmpty(artist)) artist = str(s, "author_name");
                if (string.IsNullOrEmpty(artist)) artist = str(audio, "author_name");

                string album = s["albuminfo"]?["name"]?.ToString()
                               ?? s["album_info"]?["name"]?.ToString()
                               ?? str(s, "album_name");
                if (string.IsNullOrEmpty(album)) album = str(audio, "album_name");

                return new SongInfo(
                    hash,
                    name,
                    artist,
                    album,
                    pickMs(s, "timelen", "duration", "timelength"),
                    albumAudioId,
                    cover(s, "cover") ?? cover(s, "imgurl"))
                {
                    Fee = fee(s),
                    FileId = fileId,
                    AlbumId = albumId,
                };
            }).Where(x => !string.IsNullOrEmpty(x.Hash)).ToList() ?? [];
        }

        private static readonly string[] audio_extensions =
        [
            ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".ape", ".dsf", ".dff",
        ];

        /// <summary>去掉文件名式的后缀（酷狗的 audio_name 字段常是「歌名.mp3」）。</summary>
        private static string stripAudioExtension(string name)
        {
            foreach (string extension in audio_extensions)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return name[..^extension.Length].Trim();
            }

            return name;
        }

        /// <summary>
        /// Reads KuGou's per-song fee fields. The documented bit layout of <c>privilege</c> is
        /// (MSB→LSB) 下载付费 / 下载禁止 / 播放付费 / 播放禁止, so bit1 set means playback is charged;
        /// <c>feetype</c> 2 and <c>pay_type</c> 2/4 mark purchase-only tracks (数字专辑/单曲购买).
        /// Some feeds instead use the open-platform naming (privilege_type: 2 = 会员歌曲).
        /// </summary>
        private static SongFee fee(JToken? token)
        {
            long feeType = num(token, "feetype");
            long payType = num(token, "pay_type");

            if (payType == 0 && token?["download"] is JArray downloads && downloads.Count > 0)
                payType = num(downloads[0], "pay_type");

            if (feeType == 2 || payType is 2 or 4)
                return SongFee.Paid;

            long privilegeType = num(token, "privilege_type");

            if (privilegeType == 0)
                privilegeType = num(token, "privilegeType");

            if (privilegeType == 2)
                return SongFee.Vip;

            long privilege = num(token, "privilege");

            if (privilege == 0)
                return SongFee.Unknown;

            return (privilege & 2) != 0 ? SongFee.Vip : SongFee.Unknown;
        }

        private static long pickMs(JToken token, params string[] fields)
        {
            foreach (string f in fields)
            {
                long v = num(token, f);
                if (v > 0)
                    return v > 100_000 ? v : v * 1000; // endpoints are inconsistent: some give ms, some seconds.
            }
            return 0;
        }

        private static string str(JToken? token, string field)
            => token?[field]?.ToString() ?? string.Empty;

        private static long num(JToken? token, string field)
        {
            JToken? value = token?[field];

            // KuGou mixes numeric and string-encoded numbers across endpoints and client versions.
            return value?.Type switch
            {
                JTokenType.Integer => value.Value<long>(),
                JTokenType.Float => (long)value.Value<double>(),
                JTokenType.String when long.TryParse(value.ToString(), out long parsed) => parsed,
                _ => 0,
            };
        }

        private static string? cover(JToken? token, string field)
        {
            string? raw = token?[field]?.ToString();
            return string.IsNullOrEmpty(raw) ? null : raw.Replace("{size}", "150");
        }
    }
}
