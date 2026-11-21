using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.NetEase
{
    public sealed record NetEasePlayUrl(string? Url, bool Preview, string Message);

    /// <summary>扫码轮询结果；<see cref="Status"/> 与酷狗一致：0 过期、1 等待、2 已扫待确认、4 成功。</summary>
    public sealed record NetEaseQrResult(int Status, string Message, bool LoggedIn);

    public sealed record NetEaseProfile(string Nickname, string? AvatarUrl, long UserId,
        string Gender, string Birthday, string MusicAge);

    /// <summary>
    /// 网易云音乐的业务接口封装（扫码登录 / 搜索 / 推荐 / 播放地址 / 歌词 / 评论 / 歌单）。
    /// 传输与签名由 <see cref="NetEaseApiClient"/> 负责；本类只做参数拼装与结果映射。
    /// 不提供下载：只解析在线播放地址。
    /// </summary>
    public sealed class NetEaseApi : IDisposable
    {
        /// <summary>二维码渲染服务：把 <c>music.163.com/login?codekey=</c> 画成图片。</summary>
        private const string qr_renderer = "https://api.qrserver.com/v1/create-qr-code/?size=300x300&margin=10&data=";

        private readonly NetEaseApiClient client;
        private readonly NetEaseSession session;

        public NetEaseApi()
        {
            session = new NetEaseSession();
            client = new NetEaseApiClient(session);
        }

        public NetEaseSession Session => session;

        // -------- 扫码登录 --------

        public async Task<(string Key, string ImageUrl)> CreateQrAsync(CancellationToken cancellationToken = default)
        {
            // 走 eapi（App 通道）：weapi 拿到的 key 在「手机确认」阶段会被风控拒绝。
            JObject response = await client.PostEapiAsync("/api/login/qrcode/unikey",
                new JObject { ["type"] = 3 }, cancellationToken).ConfigureAwait(false);

            string key = response["unikey"]?.ToString() ?? response["data"]?["unikey"]?.ToString() ?? string.Empty;

            if (key.Length == 0)
                throw new NetEaseApiException("网易云未返回二维码 key", response.ToString(Formatting.None));

            string loginUrl = "https://music.163.com/login?codekey=" + key;

            AppLog.NetEase.Add($"已创建网易云登录二维码（key={ShortKey(key)}）");

            return (key, qr_renderer + Uri.EscapeDataString(loginUrl));
        }

        public async Task<NetEaseQrResult> PollQrAsync(string key, CancellationToken cancellationToken = default)
        {
            // 确认阶段必须用 eapi，否则扫码后服务端会直接判定登录方式/版本不正确。
            JObject response = await client.PostEapiAsync("/api/login/qrcode/client/login",
                new JObject { ["key"] = key, ["type"] = 3 }, cancellationToken).ConfigureAwait(false);

            int code = Int(response["code"]);

            switch (code)
            {
                case 800:
                    return new NetEaseQrResult(0, "二维码已过期", false);

                case 801:
                    return new NetEaseQrResult(1, "等待扫码", false);

                case 802:
                    return new NetEaseQrResult(2, "已扫码，等待手机确认", false);

                case 803:
                    break;

                default:
                    string message = response["message"]?.ToString() ?? response["msg"]?.ToString() ?? $"未知状态 {code}";
                    return new NetEaseQrResult(0, message, false);
            }

            // 803 时 Set-Cookie 已由传输层合并进会话；再查一次账号资料补全昵称/头像/用户 ID。
            NetEaseProfile? profile = null;

            try
            {
                profile = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppLog.NetEase.Add($"扫码成功后读取资料失败：{e.Message}", LogLevel.Error, e);
            }

            long userId = profile?.UserId ?? Int64(response["account"]?["id"]) ?? session.UserId;
            string nickname = profile?.Nickname ?? response["nickname"]?.ToString() ?? string.Empty;
            string avatar = profile?.AvatarUrl ?? Https(response["avatarUrl"]?.ToString()) ?? string.Empty;

            if (!session.IsLoggedIn)
            {
                AppLog.NetEase.Add("扫码已确认，但响应里没有 MUSIC_U Cookie", LogLevel.Error);
                return new NetEaseQrResult(0, "登录已确认但未拿到凭证，请刷新二维码重试", false);
            }

            session.ApplyLogin(userId, nickname, avatar);
            AppLog.NetEase.Add($"网易云扫码登录成功：用户 ID={userId}，昵称={(nickname.Length == 0 ? "(未知)" : nickname)}", LogLevel.Important);

            return new NetEaseQrResult(4, "登录成功", true);
        }

        public void Logout()
        {
            session.Logout();
            AppLog.NetEase.Add("已退出网易云账号", LogLevel.Important);
        }

        // -------- 搜索 / 推荐 / FM --------

        public async Task<IReadOnlyList<SongInfo>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
        {
            JObject response = await client.PostAsync("/weapi/search/get", new JObject
            {
                ["s"] = keyword,
                ["type"] = 1,
                ["limit"] = 30,
                ["offset"] = 0,
                ["total"] = true,
            }, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SongInfo> songs = NetEaseMapper.SearchResults(response);

            if (songs.Count == 0)
                return songs;

            // 搜索接口不带封面，按 id 批量补一次详情（一次请求最多 30 首）。
            try
            {
                JObject detail = await client.PostAsync("/weapi/v3/song/detail", new JObject
                {
                    ["c"] = IdArray(songs),
                }, cancellationToken).ConfigureAwait(false);

                songs = NetEaseMapper.MergeDetails(songs, NetEaseMapper.Songs(detail["songs"]));
            }
            catch (Exception e)
            {
                AppLog.NetEase.Add($"补充搜索封面失败（不影响播放）：{e.Message}");
            }

            return songs;
        }

        /// <summary>每日推荐（登录后）或公开的新歌推荐（未登录）。</summary>
        public async Task<IReadOnlyList<SongInfo>> GetRecommendAsync(int count, CancellationToken cancellationToken = default)
        {
            if (session.IsLoggedIn)
            {
                try
                {
                    JObject daily = await client.PostAsync("/weapi/v1/discovery/recommend/songs", new JObject(),
                        cancellationToken).ConfigureAwait(false);

                    IReadOnlyList<SongInfo> songs = NetEaseMapper.DailySongs(daily);

                    if (songs.Count > 0)
                    {
                        AppLog.NetEase.Add($"每日推荐 {songs.Count} 首");
                        return songs;
                    }
                }
                catch (Exception e)
                {
                    AppLog.NetEase.Add($"每日推荐失败，回落到新歌推荐：{e.Message}");
                }
            }

            JObject response = await client.PostAsync("/weapi/personalized/newsong", new JObject
            {
                ["type"] = "recommend",
                ["limit"] = Math.Clamp(count, 1, 50),
                ["areaId"] = 0,
            }, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SongInfo> newsong = NetEaseMapper.Newsong(response);
            AppLog.NetEase.Add($"新歌推荐 {newsong.Count} 首");

            return newsong;
        }

        /// <summary>私人 FM（需要登录）；未登录或接口不可用时回落到推荐。</summary>
        public async Task<IReadOnlyList<SongInfo>> GetPersonalFmAsync(CancellationToken cancellationToken = default)
        {
            if (session.IsLoggedIn)
            {
                try
                {
                    JObject response = await client.PostAsync("/weapi/v1/radio/get", new JObject(),
                        cancellationToken).ConfigureAwait(false);

                    IReadOnlyList<SongInfo> songs = NetEaseMapper.FmSongs(response);

                    if (songs.Count > 0)
                    {
                        AppLog.NetEase.Add($"私人 FM {songs.Count} 首");
                        return songs;
                    }
                }
                catch (Exception e)
                {
                    AppLog.NetEase.Add($"私人 FM 失败，回落到推荐：{e.Message}");
                }
            }

            return await GetRecommendAsync(20, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(int limit = 30,
            CancellationToken cancellationToken = default)
        {
            JObject response = await client.PostAsync("/weapi/personalized/playlist", new JObject
            {
                ["limit"] = Math.Clamp(limit, 1, 100),
                ["offset"] = 0,
                ["total"] = true,
                ["n"] = 1000,
            }, cancellationToken).ConfigureAwait(false);

            return NetEaseMapper.RecommendedPlaylists(response);
        }

        public async Task<IReadOnlyList<SongInfo>> GetPlaylistTracksAsync(string id,
            CancellationToken cancellationToken = default)
        {
            JObject response = await client.PostAsync("/weapi/v6/playlist/detail", new JObject
            {
                ["id"] = id,
                ["n"] = 100000,
                ["s"] = 8,
            }, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SongInfo> tracks = NetEaseMapper.PlaylistTracks(response);

            // 详情接口的 playlist.tracks 只给前 10 首，完整顺序在 playlist.trackIds 里；
            // 按 trackIds 分批取歌曲详情，才能拿到整张歌单。
            List<string> ids = (response["playlist"]?["trackIds"] as JArray ?? new JArray())
                               .Select(token => token["id"]?.ToString())
                               .Where(value => !string.IsNullOrEmpty(value))
                               .Select(value => value!)
                               .Take(1000)
                               .ToList();

            if (ids.Count == 0)
                return tracks;

            var all = new List<SongInfo>();

            foreach (string[] chunk in ids.Chunk(200))
            {
                JObject detail = await client.PostAsync("/weapi/v3/song/detail", new JObject
                {
                    ["c"] = IdArray(chunk),
                }, cancellationToken).ConfigureAwait(false);

                all.AddRange(NetEaseMapper.Songs(detail["songs"]));
            }

            if (all.Count == 0)
                return tracks;

            var byId = all.ToDictionary(song => song.Hash, song => song, StringComparer.Ordinal);

            return ids.Where(byId.ContainsKey).Select(songId => byId[songId]).ToList();
        }

        /// <summary>账号「我喜欢的音乐」全部曲目；未登录或没有收藏时返回空列表。</summary>
        public async Task<IReadOnlyList<SongInfo>> GetLikedSongsAsync(CancellationToken cancellationToken = default)
        {
            if (!session.IsLoggedIn || session.UserId <= 0)
                return [];

            JObject response = await client.PostEapiAsync("/api/song/like/get", new JObject
            {
                ["uid"] = session.UserId,
            }, cancellationToken).ConfigureAwait(false);

            List<long> ids = (response["ids"] as JArray ?? new JArray())
                             .Select(token => long.TryParse(token.ToString(), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out long id) ? id : 0)
                             .Where(id => id > 0)
                             .Take(1000)
                             .ToList();

            if (ids.Count == 0)
                return [];

            var songs = new List<SongInfo>();

            foreach (long[] chunk in ids.Chunk(200))
            {
                var c = new JArray();

                foreach (long id in chunk)
                    c.Add(new JObject { ["id"] = id });

                JObject detail = await client.PostAsync("/weapi/v3/song/detail", new JObject
                {
                    ["c"] = c.ToString(Formatting.None),
                }, cancellationToken).ConfigureAwait(false);

                songs.AddRange(NetEaseMapper.Songs(detail["songs"]));
            }

            AppLog.NetEase.Add($"账号「我喜欢的音乐」共 {songs.Count} 首");

            return songs;
        }

        /// <summary>把一首歌写入 / 移出账号的「我喜欢的音乐」。</summary>
        public async Task SetSongLikedAsync(string hash, bool liked, CancellationToken cancellationToken = default)
        {
            if (!long.TryParse(hash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id <= 0)
                throw new NetEaseApiException("曲目 ID 无效");

            if (!session.IsLoggedIn)
                throw new NetEaseApiException("网易云未登录");

            JObject response = await client.PostEapiAsync("/api/song/like", new JObject
            {
                ["trackId"] = id,
                ["userid"] = session.UserId,
                ["like"] = liked,
            }, cancellationToken).ConfigureAwait(false);

            int code = Int(response["code"]);

            if (code != 200)
            {
                string message = response["message"]?.ToString() ?? response["msg"]?.ToString() ?? $"错误码 {code}";
                throw new NetEaseApiException($"网易云收藏失败：{message}", response.ToString(Formatting.None));
            }

            AppLog.NetEase.Add($"{(liked ? "收藏" : "取消收藏")}曲目 {id}");
        }

        // -------- 播放地址 / 歌词 / 评论 --------

        public async Task<NetEasePlayUrl> GetPlayUrlAsync(SongInfo song, string quality,
            CancellationToken cancellationToken = default)
        {
            if (!long.TryParse(song.Hash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id <= 0)
                return new NetEasePlayUrl(null, false, "曲目 ID 无效");

            JObject response = await client.PostAsync("/weapi/song/enhance/player/url", new JObject
            {
                ["ids"] = $"[{id}]",
                ["br"] = Bitrate(quality),
            }, cancellationToken).ConfigureAwait(false);

            if (response["data"] is not JArray { Count: > 0 } data || data[0] is not JObject first)
                return new NetEasePlayUrl(null, false, "接口未返回播放地址");

            string? url = first["url"]?.ToString();
            bool preview = first["freeTrialInfo"] is JObject;
            int code = Int(first["code"]);
            int fee = Int(first["fee"]);

            if (string.IsNullOrEmpty(url))
            {
                string message = code switch
                {
                    -110 when fee == 1 => "该曲目需要网易云音乐会员",
                    -110 => "该曲目暂无版权或已下架",
                    404 => "该曲目已下架",
                    _ when fee == 1 => "该曲目需要网易云音乐会员",
                    _ => "暂无可播放地址（可能因版权限制）",
                };

                AppLog.NetEase.Add($"播放地址为空（code={code}，fee={fee}）：{song.DisplayText}");
                return new NetEasePlayUrl(null, false, message);
            }

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[7..];

            return new NetEasePlayUrl(url, preview, preview ? "试听片段" : string.Empty);
        }

        public async Task<string?> GetLyricTextAsync(string hash, CancellationToken cancellationToken = default)
        {
            if (!long.TryParse(hash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id <= 0)
                return null;

            JObject response = await client.PostAsync("/weapi/song/lyric", new JObject
            {
                ["id"] = id,
                ["lv"] = -1,
                ["kv"] = -1,
                ["tv"] = -1,
            }, cancellationToken).ConfigureAwait(false);

            return response["lrc"]?["lyric"]?.ToString();
        }

        public async Task<(int Total, IReadOnlyList<SongComment> Items)> GetCommentsAsync(string hash, int page,
            int pageSize = 20, CancellationToken cancellationToken = default)
        {
            if (!long.TryParse(hash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id <= 0)
                return (0, []);

            JObject response = await client.PostAsync($"/weapi/v1/resource/comments/R_SO_4_{id}", new JObject
            {
                ["rid"] = id,
                ["limit"] = pageSize,
                ["offset"] = Math.Max(0, page - 1) * pageSize,
            }, cancellationToken).ConfigureAwait(false);

            return NetEaseMapper.Comments(response);
        }

        // -------- 账号资料 --------

        public async Task<NetEaseProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
        {
            if (!session.IsLoggedIn)
                return null;

            JObject response = await client.PostAsync("/weapi/w/nuser/account/get", new JObject(),
                cancellationToken).ConfigureAwait(false);

            JObject? profile = response["profile"] as JObject;
            JObject? account = response["account"] as JObject;

            if (profile is null && account is null)
                return null;

            long userId = account?["id"]?.Value<long>() ?? profile?["userId"]?.Value<long>() ?? session.UserId;

            return new NetEaseProfile(
                profile?["nickname"]?.ToString() ?? session.Nickname,
                Https(profile?["avatarUrl"]?.ToString()),
                userId,
                Gender(Int(profile?["gender"] ?? 0)),
                Birthday(profile?["birthday"]?.Value<long>() ?? 0),
                MusicAge(profile?["createTime"]?.Value<long>() ?? 0));
        }

        private static string Gender(int value) => value switch
        {
            1 => "男",
            2 => "女",
            _ => "未知",
        };

        private static string Birthday(long milliseconds)
        {
            if (milliseconds <= 0)
                return "未设置";

            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                                 .ToLocalTime()
                                 .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string MusicAge(long createTimeMilliseconds)
        {
            if (createTimeMilliseconds <= 0)
                return "未知";

            DateTime created = DateTimeOffset.FromUnixTimeMilliseconds(createTimeMilliseconds).LocalDateTime;
            int years = DateTime.Now.Year - created.Year;

            if (DateTime.Now.DayOfYear < created.DayOfYear)
                years--;

            return years <= 0 ? "不足 1 年" : $"{years} 年";
        }

        private static int Bitrate(string quality) => quality switch
        {
            "320" => 320000,
            "flac" or "high" or "super" => 999000,
            _ => 128000,
        };

        private static string IdArray(IReadOnlyList<SongInfo> songs)
            => IdArray(songs.Select(song => song.Hash));

        private static string IdArray(IEnumerable<string> hashes)
        {
            var ids = new JArray();

            foreach (string hash in hashes)
            {
                if (long.TryParse(hash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0)
                    ids.Add(new JObject { ["id"] = id });
            }

            return ids.ToString(Formatting.None);
        }

        private static int Int(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                return 0;

            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        private static long? Int64(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null)
                return null;

            return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value > 0
                ? value
                : null;
        }

        private static string ShortKey(string key) => key.Length <= 12 ? key : key[..12] + "…";

        private static string? Https(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;
        }

        public void Dispose() => client.Dispose();
    }
}
