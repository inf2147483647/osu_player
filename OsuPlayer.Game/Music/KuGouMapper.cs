using Newtonsoft.Json.Linq;
using OsuPlayer.Game.KuGou;

namespace OsuPlayer.Game.Music
{
    public sealed record PlaylistInfo(string CollectionId, string Name, string? CoverUrl, int SongCount);

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
                num(s, "album_audio_id"))
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
