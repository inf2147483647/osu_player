using System.Globalization;
using Newtonsoft.Json.Linq;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.NetEase
{
    /// <summary>
    /// 把网易云各种接口的歌曲结构归一成 <see cref="SongInfo"/>。
    /// 网易云至少有两套字段名：详情/搜索用 <c>ar / al / dt</c>，推荐/新歌用 <c>artists / album / duration</c>，
    /// 这里统一兼容，缺失字段一律回落到 0 / 空串。
    /// </summary>
    public static class NetEaseMapper
    {
        public static IReadOnlyList<SongInfo> SearchResults(JObject json)
            => Songs(json["result"]?["songs"]);

        public static IReadOnlyList<SongInfo> DailySongs(JObject json)
            => Songs(json["data"]?["dailySongs"]);

        /// <summary>推荐新歌：<c>result[]</c> 里每项是 <c>{ song: {...} }</c>。</summary>
        public static IReadOnlyList<SongInfo> Newsong(JObject json)
        {
            var songs = new List<SongInfo>();

            foreach (JToken item in json["result"] as JArray ?? new JArray())
            {
                if (Song(item["song"] ?? item) is { } song)
                    songs.Add(song);
            }

            return songs;
        }

        public static IReadOnlyList<SongInfo> FmSongs(JObject json)
            => Songs(json["data"]);

        public static IReadOnlyList<SongInfo> PlaylistTracks(JObject json)
            => Songs(json["playlist"]?["tracks"]);

        public static IReadOnlyList<PlaylistInfo> RecommendedPlaylists(JObject json)
        {
            var playlists = new List<PlaylistInfo>();

            foreach (JToken item in json["result"] as JArray ?? new JArray())
            {
                string id = item["id"]?.ToString() ?? string.Empty;

                if (id.Length == 0)
                    continue;

                playlists.Add(new PlaylistInfo(
                    id,
                    item["name"]?.ToString() ?? "未命名歌单",
                    cover(item["picUrl"]?.ToString() ?? item["coverImgUrl"]?.ToString()),
                    (int)num(item, "trackCount", "songCount", "playCount")));
            }

            return playlists;
        }

        public static IReadOnlyList<SongInfo> Songs(JToken? token)
        {
            var songs = new List<SongInfo>();

            foreach (JToken item in token as JArray ?? new JArray())
            {
                if (Song(item) is { } song)
                    songs.Add(song);
            }

            return songs;
        }

        public static SongInfo? Song(JToken? token)
        {
            if (token is not JObject song)
                return null;

            long id = num(song, "id");

            if (id <= 0)
                return null;

            JToken? album = song["al"] ?? song["album"];

            string artist = artists(song["ar"] ?? song["artists"] ?? song["artist"]);
            long duration = num(song, "dt", "duration", "durationMs");

            return new SongInfo(
                id.ToString(CultureInfo.InvariantCulture),
                song["name"]?.ToString() ?? song["songName"]?.ToString() ?? "未知曲目",
                artist.Length > 0 ? artist : "未知歌手",
                album?["name"]?.ToString() ?? string.Empty,
                duration,
                id,
                cover(album?["picUrl"]?.ToString() ?? song["picUrl"]?.ToString()))
            {
                Fee = fee(song),
                AlbumId = num(album, "id"),
                Platform = MusicPlatform.NetEase,
            };
        }

        public static (int Total, IReadOnlyList<SongComment> Items) Comments(JObject json)
        {
            int total = json["total"]?.Value<int>() ?? 0;
            var comments = new List<SongComment>();

            foreach (JToken token in json["comments"] as JArray ?? new JArray())
            {
                if (token is not JObject item)
                    continue;

                string? content = item["content"]?.ToString();

                if (string.IsNullOrWhiteSpace(content))
                    continue;

                JToken? reply = item["beReplied"] is JArray { Count: > 0 } replies ? replies[0] : null;

                comments.Add(new SongComment(
                    item["commentId"]?.Value<long>() ?? item["id"]?.Value<long>() ?? 0,
                    item["user"]?["nickname"]?.ToString() ?? "匿名用户",
                    content,
                    item["likedCount"]?.Value<int>() ?? 0,
                    time(item["time"]?.Value<long>() ?? 0),
                    item["user"]?["avatarUrl"]?.ToString(),
                    item["ipLocation"]?["location"]?.ToString(),
                    reply?["user"]?["nickname"]?.ToString(),
                    reply?["content"]?.ToString()));
            }

            return (total, comments);
        }

        /// <summary>搜索结果不直接给封面地址，用详情接口补（按歌曲 id 对应，保持原有顺序）。</summary>
        public static IReadOnlyList<SongInfo> MergeDetails(IReadOnlyList<SongInfo> originals, IReadOnlyList<SongInfo> details)
        {
            if (originals.Count == 0 || details.Count == 0)
                return originals;

            var byId = details.ToDictionary(s => s.Hash, s => s, StringComparer.Ordinal);

            return originals.Select(original =>
            {
                if (!byId.TryGetValue(original.Hash, out SongInfo? detail))
                    return original;

                return original with
                {
                    CoverUrl = detail.CoverUrl ?? original.CoverUrl,
                    Album = string.IsNullOrEmpty(original.Album) ? detail.Album : original.Album,
                    Artist = original.Artist == "未知歌手" ? detail.Artist : original.Artist,
                    DurationMs = original.DurationMs > 0 ? original.DurationMs : detail.DurationMs,
                    Fee = detail.Fee != SongFee.Unknown ? detail.Fee : original.Fee,
                    AlbumId = original.AlbumId != 0 ? original.AlbumId : detail.AlbumId,
                };
            }).ToList();
        }

        private static string artists(JToken? token)
        {
            switch (token)
            {
                case JArray array:
                    return string.Join("/", array.Select(a => a["name"]?.ToString())
                                                .Where(n => !string.IsNullOrWhiteSpace(n)));

                case JValue value:
                    return value.ToString();

                default:
                    return token?["name"]?.ToString() ?? string.Empty;
            }
        }

        /// <summary>网易云封面是 http 地址且常带 CDN 参数：统一升到 https，并请求小尺寸图省流量。</summary>
        private static string? cover(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[7..];

            return url.Contains('?', StringComparison.Ordinal) ? url : url + "?param=300y300";
        }

        /// <summary>fee：0 免费、1 会员、4 付费专辑、8 免费但仅低音质。未知时保留 Unknown。</summary>
        private static SongFee fee(JToken song)
        {
            int value = song["fee"]?.Value<int>() ?? -1;

            return value switch
            {
                0 or 8 => SongFee.Free,
                1 => SongFee.Vip,
                4 => SongFee.Paid,
                _ => SongFee.Unknown,
            };
        }

        private static string time(long milliseconds)
        {
            if (milliseconds <= 0)
                return string.Empty;

            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                                 .ToLocalTime()
                                 .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static long num(JToken? token, params string[] names)
        {
            if (token is null)
                return 0;

            foreach (string name in names)
            {
                JToken? value = token[name];

                if (value is null || value.Type == JTokenType.Null)
                    continue;

                if (value.Type == JTokenType.Integer)
                    return value.Value<long>();

                if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                    return parsed;

                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    return (long)number;
            }

            return 0;
        }
    }
}
