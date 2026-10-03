using System.Globalization;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using OsuPlayer.Game.KuGou.Signing;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;

namespace OsuPlayer.Game.KuGou
{
    /// <summary>
    /// High-level KuGou API facade covering the six required capabilities:
    /// QR login, song search, play URLs, recommendations, playlists and lyrics.
    /// Every endpoint here was verified live against the current servers (2026-10);
    /// raw gateway JSON is returned and typed mapping is layered on when the UI needs it.
    /// </summary>
    public sealed class KuGouApi
    {
        private readonly KuGouApiClient client;
        private readonly KuGouSession session;

        public KuGouApi(KuGouSession? session = null, HttpMessageHandler? handler = null)
        {
            this.session = session ?? new KuGouSession();
            client = new KuGouApiClient(this.session, handler);
        }

        public KuGouSession Session => session;

        // -------- 音乐搜索 --------
        // Open CDN endpoint, no signature: mobilecdn.kugou.com/api/v3/search/song
        // Shape: { errcode:0, data:{ total, info:[{ hash, songname, singername, album_name, album_id, duration, audio_id, ... }] } }
        public Task<JObject> SearchSongsAsync(string keyword, int page = 1, int pageSize = 30, CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["keyword"] = keyword,
                ["page"] = page,
                ["pagesize"] = pageSize,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/api/v3/search/song",
                BaseUrl: KuGouConstants.mobilecdn_base_url,
                Query: query), ct);
        }

        // -------- 音乐播放 --------
        // gateway /v5/url via x-router tracker.kugou.com.
        // Success: { status:1, url:[...], fileSize, bitRate, extName }.
        // VIP-only songs anonymously return status:2 with hash_offset (60s preview) and no url field;
        // passing IsFreePart=1 asks for that 60-second trial clip instead of being refused outright.
        public Task<JObject> GetSongUrlAsync(string hash, string quality = "128", long albumAudioId = 0,
            bool freePart = false, CancellationToken ct = default)
        {
            KuGouPlatform platform = session.Platform;
            int appid = KuGouConstants.AppId(platform);
            string userId = session.UserId == 0 ? "0" : session.UserId.ToString();

            var query = new Dictionary<string, object?>
            {
                ["album_audio_id"] = albumAudioId,
                ["area_code"] = 1,
                ["hash"] = hash.ToLowerInvariant(),
                ["behavior"] = "play",
                // pid / page_id / ppage_id 按客户端身份取值（概念版 = 411 / 967177915 / lite 那组）。
                ["pid"] = KuGouConstants.Pid(platform),
                ["cmd"] = 26,
                ["pidversion"] = 3001,
                ["IsFreePart"] = freePart ? 1 : 0, // 非会员试听：返回 60 秒片段
                ["album_id"] = 0,
                ["ssa_flag"] = "is_fromtrack",
                ["version"] = 11430,
                ["page_id"] = KuGouConstants.PageId(platform),
                ["quality"] = quality, // 128 / 320 / flac / high / viper_tape / viper_atmos ...
                ["ppage_id"] = KuGouConstants.PPageId(platform),
                ["cdnBackup"] = 1,
                ["module"] = string.Empty,
                // 播放地址接口的路由优先级与 clientver 是路由专属取值（对齐
                // KuGouMusicApi/module/song_url.js：那里两平台都用 11430）。
                ["clientver"] = 11430,
                ["key"] = KuGouSigner.SignKey(hash.ToLowerInvariant(), session.Mid, session.UserId, appid, platform),
            };

            // 会员权益由网关根据 token/userid 判定，vipType/vipToken 不是必需参数。此前无条件下发
            // vipType=0、vipToken="" 等于主动向网关声明「我是非会员」，会员歌曲因此一律被降级成
            // 60 秒试听（status=2）。现在只在确实拿到过凭证时才带上，其余交给网关按账号判定。
            if (session.VipType != 0)
                query["vipType"] = session.VipType;

            if (!string.IsNullOrEmpty(session.VipToken))
                query["vipToken"] = session.VipToken;

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v5/url",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true,
                IncludeUuid: false, // the track route rejects/ignores uuid; verified without it
                Router: "trackercdn.kugou.com"), ct); // 与参考实现 song_url.js 的 x-router 一致
        }

        // -------- 概念版会员与免费听 --------
        // 酷狗概念版是独立的会员体系：账号里有没有权益要看 kugouvip 的「概念版产品」列表，
        // 而不是酷狗音乐那套 vip_type。两个平台的产品互不通用。

        /// <summary>授权串按账号缓存（参考实现把它存进 cookie 复用），换账号时失效。</summary>
        private string? playAuth;
        private long playAuthUserId = -1;

        /// <summary>
        /// 概念版播放授权串（KuGouMusicApi/module/user_verify.js）：GET /v1/user_verify?module_id=51。
        /// 服务端返回 <c>data.auth</c>，它是 <c>/v1/authorization</c> 换取 <c>auth/open_time</c> 的前置条件，
        /// 也是概念版「免费听」能拿到完整曲目的关键。按账号缓存，未登录时返回 null。
        /// </summary>
        public async Task<string?> GetPlayAuthAsync(CancellationToken ct = default)
        {
            if (!session.IsLoggedIn)
                return null;

            if (playAuthUserId != session.UserId)
            {
                playAuth = null;
                playAuthUserId = session.UserId;
            }

            if (!string.IsNullOrEmpty(playAuth))
                return playAuth;

            JObject response = await client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v1/user_verify",
                BaseUrl: KuGouConstants.tracker_auth_base_url,
                Query: new Dictionary<string, object?> { ["module_id"] = KuGouConstants.auth_module_id },
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true), ct).ConfigureAwait(false);

            // data 正常时是带 auth 的对象；被服务端拒绝时可能是标量（如 data:0），
            // 用 as 安全取子节点，避免在 JValue 上取子键抛异常把诊断日志吞掉。
            playAuth = (response["data"] as JObject)?["auth"]?.ToString();

            if (string.IsNullOrEmpty(playAuth))
            {
                AppLog.Net.Add($"获取播放授权串失败，响应={truncateJson(response)}", LogLevel.Important);
                return null;
            }

            AppLog.Net.Add("已获取概念版播放授权串", LogLevel.Important);
            return playAuth;
        }

        /// <summary>丢弃缓存的授权串（退出登录/换账号时调用）。</summary>
        public void ResetPlayAuth()
        {
            playAuth = null;
            playAuthUserId = -1;
        }

        /// <summary>日志里只留一小段响应正文，够定位又不至于把整包塞进日志。</summary>
        private static string truncateJson(JObject response)
        {
            string text = response.ToString(Newtonsoft.Json.Formatting.None);
            return text.Length <= 400 ? text : text[..400] + "…";
        }
        /// <summary>
        /// Query the account's 概念版 products (KuGouMusicApi/module/user_vip_detail.js).
        /// Shape: { status:1, data:{ busi_vip:[{ product_type, is_vip, vip_begin_time, vip_end_time }] } }
        /// where product_type is tvip (畅听会员) / svip (概念会员).
        /// </summary>
        public Task<JObject> GetUserVipDetailAsync(CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?> { ["busi_type"] = "concept" };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v1/get_union_vip",
                BaseUrl: KuGouConstants.vip_base_url,
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        /// <summary>
        /// 概念版「免费听」链路（module/song_url_auth_merge.js 的两步合成）：
        /// 1. song_auth：向授权服务换取 <c>auth</c> 与 <c>open_time</c>；
        /// 2. song_url_auth：带着这两者向网关换正式地址。
        /// 授权拿不到（未登录、非概念版权益）时返回 null，由调用方回退到 60 秒试听。
        /// </summary>
        public async Task<JObject?> TryGetAuthorizedUrlAsync(string hash, string quality = "128",
            long albumAudioId = 0, CancellationToken ct = default)
        {
            string lower = hash.ToLowerInvariant();
            KuGouPlatform platform = session.Platform;

            // 第一步：授权串。参考实现里它来自 /v1/user_verify 并存进 cookie 供后续复用；
            // 之前这里直接传空 authorization，服务端因此不会下发 auth/open_time，整条链必然拿不到地址。
            string? playAuth = await GetPlayAuthAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(playAuth))
            {
                AppLog.Net.Add($"概念版免费听：未取得授权串（/v1/user_verify 未返回 auth），跳过后两步",
                    LogLevel.Important);
                return null;
            }

            JObject authResponse = await client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v1/authorization",
                BaseUrl: KuGouConstants.tracker_auth_base_url,
                Query: new Dictionary<string, object?>
                {
                    ["authorization"] = playAuth,
                    ["module_id"] = KuGouConstants.auth_module_id,
                    ["album_audio_id"] = albumAudioId,
                    ["clientver"] = KuGouConstants.auth_clientver,
                    ["hash"] = lower,
                },
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true), ct).ConfigureAwait(false);

            if (authResponse["data"] is not JObject authData)
            {
                // 没有 data 对象时把服务端原话记下来，否则日志里只能看到“无响应”。
                AppLog.Net.Add($"概念版免费听：/v1/authorization 未返回 data，响应={truncateJson(authResponse)}",
                    LogLevel.Important);
                return null;
            }

            string auth = authData["auth"]?.ToString() ?? string.Empty;
            long openTime = authData["open_time"]?.Value<long>() ?? 0;

            if (string.IsNullOrEmpty(auth) && openTime == 0)
            {
                AppLog.Net.Add($"概念版免费听：授权串为空，响应={truncateJson(authResponse)}", LogLevel.Important);
                return null;
            }

            var query = new Dictionary<string, object?>
            {
                ["album_audio_id"] = albumAudioId,
                ["area_code"] = 1,
                ["module"] = string.Empty,
                ["hash"] = lower,
                ["need_m"] = 0,
                ["ssa_flag"] = "is_fromtrack",
                ["version"] = 11430,
                ["open_time"] = openTime,
                ["ptype"] = 0,
                ["need_ogg"] = 1,
                ["page_id"] = KuGouConstants.PageId(platform),
                ["auth"] = auth,
                ["mtype"] = 0,
                ["quality"] = quality,
                ["behavior"] = "play",
                ["pid"] = KuGouConstants.Pid(platform),
                ["module_id"] = KuGouConstants.auth_module_id,
                ["cmd"] = 26,
                ["ppage_id"] = KuGouConstants.PPageId(platform),
                ["clientver"] = KuGouConstants.auth_clientver,
                ["pidversion"] = 3001,
                ["cdnBackup"] = 1,
                ["key"] = KuGouSigner.SignKey(lower, session.Mid, session.UserId, KuGouConstants.AppId(platform), platform),
            };

            return await client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/tracker/v5/url",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true,
                IncludeUuid: false), ct).ConfigureAwait(false);
        }

        // -------- 曲目音质与备用音源 --------
        /// <summary>
        /// 概念版权限/音质查询（module/privilege_lite.js）：POST media.store.kugou.com。
        /// <c>data[0].relate_goods[]</c> 给出该曲目的其它可用版本（不同 hash/音质），参考实现就是逐个
        /// 尝试这些 hash 来拿地址（EchoMusic <c>ensureTrackRelateGoods</c>）。
        /// </summary>
        public Task<JObject> GetSongPrivilegeLiteAsync(string hash, long albumId = 0, CancellationToken ct = default)
        {
            var resource = new List<object>
            {
                new
                {
                    type = "audio",
                    page_id = 0,
                    hash = hash.ToLowerInvariant(),
                    album_id = albumId == 0 ? string.Empty : albumId.ToString(),
                },
            };

            var body = new
            {
                appid = KuGouConstants.AppId(session.Platform),
                area_code = 1,
                behavior = "play",
                clientver = KuGouConstants.ClientVer(session.Platform),
                need_hash_offset = 1,
                relate = 1,
                support_verify = 1,
                resource,
                qualities = new[]
                {
                    "128", "320", "flac", "high", "viper_atmos", "viper_tape", "viper_clear", "super", "multitrack",
                },
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v2/get_res_privilege/lite",
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true,
                Router: "media.store.kugou.com"), ct);
        }

        // -------- 账号资料 --------
        /// <summary>
        /// 账号资料（module/user_info.js）：POST relation.user.kugou.com/v1/get_my_userinfo。
        /// <c>p</c> 是裸 RSA 加密的 {clienttime, token}，<c>key</c> 是 signParamsKey(clienttime)。
        /// </summary>
        public Task<JObject> GetMyUserInfoAsync(CancellationToken ct = default)
        {
            long clientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string p = KuGouVerificationCrypto
                .RawRsaEncryptHex($"{{\"clienttime\":{clientTime},\"token\":\"{session.Token}\"}}", session.Platform)
                .ToUpperInvariant();

            var body = new
            {
                p,
                appid = KuGouConstants.AppId(session.Platform),
                mid = session.Mid,
                clientver = KuGouConstants.ClientVer(session.Platform),
                source = 0,
                clienttime = clientTime,
                uuid = "-",
                userid = session.UserId,
                key = KuGouSigner.SignParamsKey(clientTime.ToString(CultureInfo.InvariantCulture), session.Platform),
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v1/get_my_userinfo",
                BaseUrl: KuGouConstants.relation_base_url,
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        // -------- 概念版会员领取与升级 --------
        // 概念版权益按天领取（KGLiteVIP / KuGouMusicApi 的 youth 系列接口）。
        /// <summary>
        /// 领取当日「畅听会员」（module/youth_day_vip.js）：POST /youth/v1/recharge/receive_vip_listen_song。
        /// 成功 status=1；今日已领取时返回非 0 的 error_code 或 msg 含「已领取」。
        /// </summary>
        public Task<JObject> ClaimDailyListenVipAsync(string receiveDay, CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["source_id"] = 90139,
                ["receive_day"] = receiveDay,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/youth/v1/recharge/receive_vip_listen_song",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        /// <summary>
        /// 升级为「概念会员」（module/youth_day_vip_upgrade.js）：POST /youth/v1/listen_song/upgrade_vip_reward。
        /// 成功 status=1；error_code=297002 表示今日已升级。
        /// </summary>
        public Task<JObject> UpgradeConceptVipAsync(CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["kugouid"] = session.UserId,
                ["ad_type"] = 1,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/youth/v1/listen_song/upgrade_vip_reward",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        /// <summary>本月畅听会员领取记录（module/youth_month_vip_record.js），用于判断今天是否已领。</summary>
        public Task<JObject> GetMonthVipRecordAsync(CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?> { ["latest_limit"] = 100 };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/youth/v1/activity/get_month_vip_record",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        // -------- 安全验证（SSA 人机校验） --------
        // 网关对播放请求返回 errcode 20028 时，响应头 ssa-code 即为验证事件 id。
        // 流程：get_verify_info 取验证配置 → 浏览器完成腾讯验证码 → verify_user_info 提交。

        /// <summary>最近一次响应携带的 ssa-code（验证事件 id）。</summary>
        public string? LastSsaCode => client.LastSsaCode;

        /// <summary>
        /// 取验证配置（module/get_verify_info.js）。
        /// Shape: { data:{ v_type, v_type_list, txappid, sessionid, business, url }, status:1 }
        /// </summary>
        public Task<JObject> GetVerifyInfoAsync(string eventId, CancellationToken ct = default)
        {
            var body = new
            {
                eventid = eventId,
                userid = session.UserId,
                platid = 2,
                rtype = 1,
                wasm = 1,
                i = string.Empty,
                sid = string.Empty,
                edt = string.Empty,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/verifyservice/v3/get_verify_info",
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true), ct);
        }

        /// <summary>
        /// 提交人机验证（module/sidedt.js + verify_user_info.js 的合并实现）：用服务端模拟的行为指纹
        /// <c>sid</c>/<c>edt</c> 与验证码一起上报。
        /// </summary>
        public Task<JObject> SubmitVerificationAsync(string eventId, int verifyType, string verifyCode,
            CancellationToken ct = default)
        {
            (string sid, string edt) = KuGouVerificationCrypto.GenerateSimulate(session.Mid, session.UserId, session.Dfid);
            KuGouVerificationCrypto.AesEncrypted encrypted = KuGouVerificationCrypto.AesEncryptEmptyObject();
            string pk = KuGouVerificationCrypto.RawRsaEncryptHex($"{{\"key\":\"{encrypted.Key}\"}}", session.Platform);

            var body = new
            {
                eventid = eventId,
                userid = session.UserId,
                platid = 2,
                v_type = verifyType,
                wasm = 1,
                i = string.Empty,
                sid,
                edt,
                verifycode = verifyCode,
                pk,
                @params = encrypted.Hex,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v4/verify_user_info",
                BaseUrl: KuGouConstants.verifyservice_base_url,
                Query: new Dictionary<string, object?> { ["clientver"] = KuGouConstants.verify_clientver },
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                MillisecondTime: true), ct);
        }

        // -------- 歌词 --------
        // Step 1: resolve lyric candidates by song hash. Shape: { errcode:200, candidates:[{ id, accesskey, duration, ... }] }
        public Task<JObject> SearchLyricsAsync(string hash, long durationMs = 0, CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["ver"] = 1,
                ["man"] = "yes",
                ["client"] = "pc",
                ["keyword"] = string.Empty,
                ["duration"] = durationMs,
                ["hash"] = hash,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/search",
                BaseUrl: KuGouConstants.lyrics_base_url,
                Query: query), ct);
        }

        // Step 2: download lyric content. fmt=lrc -> plain text base64; fmt=krc -> encrypted payload (use KrcDecoder).
        // Shape: { status:200, content:"<base64>", contenttype:1 }
        public Task<JObject> DownloadLyricAsync(string id, string accessKey, string fmt = "lrc", CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["ver"] = 1,
                ["client"] = "pc",
                ["fmt"] = fmt,
                ["charset"] = "utf8",
                ["id"] = id,
                ["accesskey"] = accessKey,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/download",
                BaseUrl: KuGouConstants.lyrics_base_url,
                Query: query), ct);
        }

        /// <summary>
        /// One-call lyric text fetch: resolve candidates, download the first one, base64-decode (and
        /// inflate KRC when <paramref name="fmt"/> is "krc"). Returns null when no lyric exists.
        /// </summary>
        public async Task<string?> GetLyricTextAsync(string hash, long durationMs = 0, string fmt = "lrc",
            CancellationToken ct = default)
        {
            JObject search = await SearchLyricsAsync(hash, durationMs, ct).ConfigureAwait(false);
            JToken? candidate = search["candidates"]?.FirstOrDefault();
            string? id = candidate?["id"]?.ToString();
            string? accessKey = candidate?["accesskey"]?.ToString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey))
                return null;

            JObject download = await DownloadLyricAsync(id, accessKey, fmt, ct).ConfigureAwait(false);
            string? content = download["content"]?.ToString();
            if (string.IsNullOrEmpty(content))
                return null;

            byte[] bytes = Convert.FromBase64String(content);
            return fmt == "krc" ? KrcDecoder.Decode(bytes) : System.Text.Encoding.UTF8.GetString(bytes);
        }

        // -------- 获取推荐音乐 --------
        // Anonymous home "刷歌" feed. Only answers over plain HTTP gateway.
        // Shape: { errcode:0, data:{ items:[{ item_type:"song", song_info:{ songname, author_name, hash_128, album_name, ... } }] } }
        public Task<JObject> GetHomeRecommendAsync(int pageSize = 10, CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["module_key"] = "home_discover_rec",
                ["module_id"] = 1,
                ["area_code"] = 1,
                ["platform"] = "android",
            };

            var body = new
            {
                support = "only_song",
                userid = session.UserId,
                recall_type = "song",
                today_play_num = 0,
                pagesize = pageSize,
                go_ky_extra = new[]
                {
                    new { key = "network", val = "2" },
                    new { key = "play_mode", val = "2" },
                    new { key = "no_mv_ret", val = "1" },
                },
            };

            // Server rejects clientver < 20489 content-wise; 20809 is the reference value.
            query["clientver"] = 20809;

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/homediscoverrec/v1/client/home_discover_rec",
                BaseUrl: KuGouConstants.gateway_http_base_url,
                Query: query,
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        // 每日推荐歌曲 (login required): POST /everyday_song_recommend
        public Task<JObject> GetDailyRecommendAsync(CancellationToken ct = default)
        {
            var body = new
            {
                platform = "android",
                userid = session.UserId,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/everyday_song_recommend",
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                Router: "everydayrec.service.kugou.com"), ct);
        }

        // 私人 FM / 猜你喜欢: POST /v2/personal_recommend (works anonymously, personalised after login)
        public Task<JObject> GetPersonalFmAsync(string? hash = null, long? songId = null, long? playtime = null,
            CancellationToken ct = default)
        {
            long clienttime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var bodyMap = new Dictionary<string, object?>
            {
                ["appid"] = KuGouConstants.AppId(session.Platform),
                ["clienttime"] = clienttime,
                ["mid"] = session.Mid,
                ["action"] = "play",
                ["recommend_source_locked"] = 0,
                ["song_pool_id"] = 0,
                ["callerid"] = 0,
                ["m_type"] = 1,
                ["platform"] = "ios",
                ["area_code"] = 1,
                ["remain_songcnt"] = 0,
                ["clientver"] = KuGouConstants.ClientVer(session.Platform),
                ["is_overplay"] = 0,
                ["mode"] = "normal",
                ["fakem"] = "ca981cfc583a4c37f28d2d49000013c16a0a",
                ["key"] = KuGouSigner.SignParamsKey(clienttime.ToString(), session.Platform),
            };

            if (session.UserId != 0)
            {
                bodyMap["userid"] = session.UserId;
                bodyMap["kguid"] = session.UserId;
            }
            if (!string.IsNullOrEmpty(session.Token))
                bodyMap["token"] = session.Token;
            if (session.VipType != 0)
                bodyMap["vip_type"] = session.VipType;
            if (hash is not null) bodyMap["hash"] = hash;
            if (songId is not null) bodyMap["songid"] = songId;
            if (playtime is not null) bodyMap["playtime"] = playtime;

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v2/personal_recommend",
                Body: bodyMap,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                Router: "persnfm.service.kugou.com"), ct);
        }

        // -------- 歌单 --------
        /// <summary>
        /// Resolve a public playlist id to the global_collection_id used by the detail/tracks APIs.
        /// POST t.kugou.com/v1/songlist/batch_decode. Already-collection ids can be used directly.
        /// </summary>
        public async Task<string?> ResolveCollectionIdAsync(string playlistId, CancellationToken ct = default)
        {
            if (playlistId.StartsWith("collection_", StringComparison.Ordinal))
                return playlistId;

            var body = new
            {
                data = new[] { new { id = playlistId, id_type = "1" } },
                ret_info = 1,
            };

            JObject result = await client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v1/songlist/batch_decode",
                BaseUrl: KuGouConstants.songlist_base_url,
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct).ConfigureAwait(false);

            return result["data"]?["list"]?.FirstOrDefault()?["global_collection_id"]?.ToString();
        }

        // 歌单广场: POST gateway /v2/special_recommend via x-router specialrec.service.kugou.com
        // categoryid 0 = 推荐, 11292 = HI-RES. Shape: data.special_list[].global_collection_id / specialname / imgurl.
        public Task<JObject> GetRecommendedPlaylistsAsync(int categoryId = 0, int page = 1, int pageSize = 30,
            CancellationToken ct = default)
        {
            long clienttime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var body = new Dictionary<string, object?>
            {
                ["appid"] = KuGouConstants.AppId(session.Platform),
                ["mid"] = session.Mid,
                ["clientver"] = KuGouConstants.ClientVer(session.Platform),
                ["platform"] = "android",
                // The reference sends clienttime as a STRING inside the body; the signature covers the body verbatim.
                ["clienttime"] = clienttime.ToString(),
                ["userid"] = session.UserId,
                ["module_id"] = 1,
                ["page"] = page,
                ["pagesize"] = pageSize,
                ["key"] = KuGouSigner.SignParamsKey(clienttime.ToString(), session.Platform),
                ["special_recommend"] = new
                {
                    withtag = 1,
                    withsong = 1,
                    sort = 1,
                    ugc = 1,
                    is_selected = 0,
                    withrecommend = 1,
                    area_code = 1,
                    categoryid = categoryId,
                },
                ["req_multi"] = 1,
                ["retrun_min"] = 5, // misspelling is part of the live API contract
                ["return_special_falg"] = 1,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v2/special_recommend",
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                Router: "specialrec.service.kugou.com"), ct);
        }

        // Playlist title: POST gateway /v3/get_list_info via x-router pubsongs.kugou.com
        public Task<JObject> GetPlaylistDetailAsync(string collectionId, CancellationToken ct = default)
        {
            var body = new
            {
                data = new[] { new { global_collection_id = collectionId } },
                userid = session.UserId == 0 ? "0" : session.UserId.ToString(),
                token = session.Token,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Post,
                "/v3/get_list_info",
                Body: body,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true,
                Router: "pubsongs.kugou.com"), ct);
        }

        // Playlist tracks (paged): GET gateway /pubsongs/v2/get_other_list_file_nofilt
        // Shape: { error_code:0, data:{ count, songs:[{ hash, name, cover, singerinfo:[{name}], albuminfo:{name} }] } }
        public Task<JObject> GetPlaylistTracksAsync(string collectionId, int page = 1, int pageSize = 100,
            CancellationToken ct = default)
        {            var query = new Dictionary<string, object?>
            {
                ["global_collection_id"] = collectionId,
                ["pagesize"] = pageSize,
                ["plat"] = 1,
                ["type"] = 1,
                ["mode"] = 1,
                ["personal_switch"] = 1,
                ["extend_fields"] = "abtags,hot_cmt,popularization",
                ["area_code"] = 1,
                ["begin_idx"] = (page - 1) * pageSize,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/pubsongs/v2/get_other_list_file_nofilt",
                Query: query,
                Sign: KuGouSignKind.Android,
                InjectDefaults: true), ct);
        }

        // -------- 登录（扫码） --------
        /// <summary>
        /// Create a QR login session. Shape: data.qrcode = key, data.qrcode_img = data:image/png;base64,...
        /// Poll with <see cref="CheckQrAsync"/>; status: 0 expired, 1 waiting, 2 scanned/awaiting confirm, 4 authorized.
        /// </summary>
        public Task<JObject> CreateQrKeyAsync(CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                // 该接口的 appid 是「登录产品号」而非内容 appid，概念版与标准版都是 1001
                // （web 为 1014），对齐 KuGouMusicApi/module/login_qr_key.js。平台身份只体现在
                // qrcode_txt 指向的登录页 appid 上。
                ["appid"] = 1001,
                ["clientver"] = KuGouConstants.ClientVer(session.Platform),
                ["clienttime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["mid"] = session.Mid,
                ["uuid"] = session.Uuid,
                ["dfid"] = session.Dfid,
                ["type"] = 1,
                ["plat"] = 4,
                ["qrcode_txt"] = $"https://h5.kugou.com/apps/loginQRCode/html/index.html?appid={KuGouConstants.AppId(session.Platform)}&",
                ["srcappid"] = KuGouConstants.srcappid,
            };

            return client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v2/qrcode",
                BaseUrl: KuGouConstants.login_base_url,
                Query: query,
                Sign: KuGouSignKind.Web,
                MillisecondTime: true), ct);
        }

        /// <summary>The URL the KuGou mobile app opens when scanning the QR code for the given key.</summary>
        public static string QrLoginUrl(string key)
            => $"https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode={key}";

        /// <summary>
        /// Poll QR login state. On status 4 the session is updated with token/userid automatically.
        /// </summary>
        public async Task<JObject> CheckQrAsync(string key, CancellationToken ct = default)
        {
            var query = new Dictionary<string, object?>
            {
                ["appid"] = KuGouConstants.AppId(session.Platform),
                ["clientver"] = KuGouConstants.ClientVer(session.Platform),
                ["clienttime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["mid"] = session.Mid,
                ["uuid"] = session.Uuid,
                ["dfid"] = session.Dfid,
                ["plat"] = 4,
                ["qrcode"] = key,
                ["srcappid"] = KuGouConstants.srcappid,
                // 登录服务器用它把签发的 token 绑到本设备；缺省时 token 可能一拿到就"token invalid"。
                ["dev"] = session.Device.Dev,
            };

            JObject result = await client.SendAsync(new KuGouApiRequest(
                HttpMethod.Get,
                "/v2/get_userinfo_qrcode",
                BaseUrl: KuGouConstants.login_base_url,
                Query: query,
                Sign: KuGouSignKind.Web,
                MillisecondTime: true), ct).ConfigureAwait(false);

            if (result["data"] is JObject data && data["status"]?.Value<long>() == 4)
            {
                string? token = data["token"]?.ToString();
                long userId = data["userid"]?.Value<long>() ?? 0;
                int vipType = data["vip_type"]?.Value<int>() ?? 0;
                string vipToken = data["vip_token"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(token))
                    session.SetLogin(token, userId, vipType, vipToken);
            }

            return result;
        }
    }
}
