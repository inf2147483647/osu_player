using System.Globalization;
using System.Net;
using Newtonsoft.Json.Linq;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.NetEase
{
    /// <summary>网易云接口返回了非 JSON / 非 2xx / 缺少关键字段时抛出。</summary>
    public sealed class NetEaseApiException : Exception
    {
        public NetEaseApiException(string message, string? body = null)
            : base(message)
        {
            Body = body;
        }

        public string? Body { get; }
    }

    /// <summary>
    /// 网易云 weapi 传输层：签名、Cookie 合并、JSON 解析。业务字段解析放在 <see cref="NetEaseApi"/>。
    /// 走 <c>https://music.163.com</c> 的 weapi 通道（不依赖外部 NeteaseCloudMusicApi 服务）。
    /// </summary>
    public sealed class NetEaseApiClient : IDisposable
    {
        private const string base_url = "https://music.163.com";

        /// <summary>
        /// eapi（App 端加密通道）域名。扫码登录的 key / 轮询必须走这里：
        /// web 端的 weapi 在「手机确认」阶段会被服务端风控拒绝（返回 800/提示升级版本）。
        /// </summary>
        private const string eapi_base_url = "https://interfacepc.music.163.com";

        /// <summary>eapi 的 Cookie 头与请求体里的 <c>header</c> 是同一份字段，按 App 默认值构造。</summary>
        private const string eapi_osver = "Microsoft-Windows-10-Professional-build-19045-64bit";
        private const string eapi_appver = "3.1.17.204416";
        private const string eapi_channel = "netease";

        private const string user_agent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private readonly HttpClient http;

        public NetEaseApiClient(NetEaseSession session)
        {
            Session = session;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };

            http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", user_agent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.163.com");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        }

        public NetEaseSession Session { get; }

        /// <summary>发送一个 weapi POST；自动带上 CSRF 与 Cookie，并合并响应下发的 Cookie。</summary>
        public Task<JObject> PostAsync(string path, JObject data, CancellationToken cancellationToken = default)
        {
            data["csrf_token"] = Session.CsrfToken;

            (string parameters, string encSecKey) = NetEaseCrypto.Encrypt(data);

            return SendAsync(base_url + path, new Dictionary<string, string>
            {
                ["params"] = parameters,
                ["encSecKey"] = encSecKey,
            }, Session.CookieHeader, cancellationToken);
        }

        /// <summary>
        /// 发送一个 eapi POST（路径仍写 <c>/api/...</c>，内部换成 <c>/eapi/...</c>）。
        /// 请求体里会嵌入 App 形态的 <c>header</c> 字段，Cookie 头与之一致。
        /// </summary>
        public Task<JObject> PostEapiAsync(string path, JObject data, CancellationToken cancellationToken = default)
        {
            JObject header = BuildEapiHeader();

            data["header"] = header;
            data["e_r"] = false;

            string parameters = NetEaseCrypto.EapiEncrypt(path, data);

            return SendAsync(eapi_base_url + "/eapi/" + path[5..], new Dictionary<string, string>
            {
                ["params"] = parameters,
            }, EapiCookie(header), cancellationToken);
        }

        private JObject BuildEapiHeader()
        {
            var header = new JObject
            {
                ["osver"] = eapi_osver,
                ["deviceId"] = Session.DeviceId,
                ["os"] = "pc",
                ["appver"] = eapi_appver,
                ["versioncode"] = "140",
                ["mobilename"] = string.Empty,
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["resolution"] = "1920x1080",
                ["__csrf"] = Session.CsrfToken,
                ["channel"] = eapi_channel,
                ["requestId"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}_"
                                + Random.Shared.Next(0, 10000).ToString("D4", CultureInfo.InvariantCulture),
            };

            foreach (string name in new[] { "MUSIC_U", "MUSIC_A", "NMTID" })
            {
                if (Session.GetCookie(name) is { Length: > 0 } value)
                    header[name] = value;
            }

            return header;
        }

        private static string EapiCookie(JObject header)
            => string.Join("; ", header.Properties().Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value.ToString())}"));

        private async Task<JObject> SendAsync(string url, Dictionary<string, string> form, string? cookie,
            CancellationToken cancellationToken)
        {
            Exception? lastError = null;

            // 都是幂等接口（扫码轮询也允许重复），网络抖动时重试一次即可。
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Content = new FormUrlEncodedContent(form);

                    if (cookie is { Length: > 0 })
                        request.Headers.TryAddWithoutValidation("Cookie", cookie);

                    using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    MergeCookies(response);

                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                        throw new NetEaseApiException($"网易云接口 HTTP {(int)response.StatusCode}", body);

                    try
                    {
                        return JObject.Parse(body);
                    }
                    catch (Exception e)
                    {
                        throw new NetEaseApiException($"网易云接口返回了无法解析的内容：{e.Message}", body);
                    }
                }
                catch (Exception e) when ((e is HttpRequestException || e is TaskCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    lastError = e;

                    if (attempt < 2)
                        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new NetEaseApiException($"网易云接口连接失败：{lastError?.Message ?? "未知错误"}");
        }

        private void MergeCookies(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
                Session.MergeSetCookies(values);
        }

        public void Dispose() => http.Dispose();
    }
}
