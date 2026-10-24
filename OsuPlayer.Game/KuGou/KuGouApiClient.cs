using System.Globalization;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OsuPlayer.Game.KuGou.Signing;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OsuPlayer.Game.KuGou
{
    internal enum KuGouSignKind
    {
        /// <summary>Open endpoint: no signature, browser identity (mobilecdn / lyrics hosts).</summary>
        None,

        /// <summary>Signed gateway request with lowercase MD5 digest.</summary>
        Android,

        /// <summary>Signed login-host request with UPPERCASE MD5 digest.</summary>
        Web
    }

    /// <summary>
    /// Low level request description. Scalar <see cref="Query"/> values are formatted the same way both
    /// for the signature and on the wire; <see cref="Body"/> is serialized once and the exact same JSON
    /// string is both signed and sent.
    /// </summary>
    internal sealed record KuGouApiRequest(
        HttpMethod Method,
        string Path,
        string BaseUrl = KuGouConstants.gateway_base_url,
        IDictionary<string, object?>? Query = null,
        object? Body = null,
        KuGouSignKind Sign = KuGouSignKind.None,
        bool InjectDefaults = false,
        bool MillisecondTime = false,
        bool IncludeUuid = true,
        string? Router = null,
        // 逐个接口要覆盖的请求头（登录接口用专属的 User-Agent 与 support-calm，标准头不够用）。
        IReadOnlyDictionary<string, string>? Headers = null);

    /// <summary>
    /// HTTP transport for the three KuGou request flavours verified live (2026-10):
    /// open CDN endpoints, MD5-signed gateway requests and uppercase-signed login requests.
    /// Business-level status fields differ per endpoint, so responses are returned as raw JSON;
    /// only transport / parse failures throw.
    /// </summary>
    public sealed class KuGouApiClient : IDisposable
    {
        /// <summary>
        /// Overall wall-clock budget for one request attempt (the framework default of 100s is far too long for a UI action).
        ///
        /// 30 秒而不是 20 秒：酷狗风控（20028）触发后会把该账号的请求拖慢到 17–21 秒（实测），
        /// 20 秒会把"被限速"表现成"网络异常"，让整条链路看起来全线崩掉。
        /// </summary>
        private static readonly TimeSpan request_timeout = TimeSpan.FromSeconds(30);

        /// <summary>Extra attempts for idempotent GETs after transport failures / gateway throttling.</summary>
        private const int max_get_attempts = 3;

        private static readonly TimeSpan[] retry_delays =
        [
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1500),
        ];

        private readonly HttpClient http;
        private readonly KuGouSession session;

        /// <summary>
        /// 最近一次响应里的 <c>ssa-code</c> 头。网关要求人机验证时（errcode 20028）它携带验证事件 id，
        /// 后续 /verifyservice/v3/get_verify_info 要用它换验证配置。
        /// </summary>
        public string? LastSsaCode { get; private set; }

        public KuGouApiClient(KuGouSession session, HttpMessageHandler? handler = null)
        {
            this.session = session;

            if (handler is null)
            {
                handler = new SocketsHttpHandler
                {
                    // Recycle pooled connections periodically so throttled/half-dead sockets don't linger.
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                    MaxConnectionsPerServer = 16,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                };
            }

            http = new HttpClient(handler) { Timeout = request_timeout };
        }

        internal async Task<JObject> SendAsync(KuGouApiRequest request, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            // 带上客户端身份，日志里能直接确认请求是以概念版还是标准版发出的。
            string endpoint = request.Sign == KuGouSignKind.None
                ? $"{request.Method} {request.Path} [None]"
                : $"{request.Method} {request.Path} [{request.Sign}/{KuGouConstants.DisplayName(session.Platform)}]";
            string bodyJson = request.Body is null ? string.Empty : JsonConvert.SerializeObject(request.Body);

            AppLog.Net.Add($"请求 {endpoint}");

            // Only idempotent GETs are retried; a POST may have been processed server-side even if the response was lost.
            int attempts = request.Method == HttpMethod.Get ? max_get_attempts : 1;
            string responseText = string.Empty;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(retry_delays[attempt - 1], cancellationToken).ConfigureAwait(false);
                    AppLog.Net.Add($"重试 {endpoint}（第 {attempt + 1}/{attempts} 次）");
                }

                // Rebuild per attempt so the signed clienttime never goes stale during backoff.
                var parameters = new Dictionary<string, object?>();

                if (request.InjectDefaults)
                    injectDefaultParameters(parameters, request.MillisecondTime, request.IncludeUuid);

                if (request.Query is not null)
                {
                    foreach (var pair in request.Query)
                        parameters[pair.Key] = pair.Value;
                }

                if (request.Sign == KuGouSignKind.Android)
                    parameters["signature"] = KuGouSigner.SignatureAndroid(parameters, bodyJson, session.Platform);
                else if (request.Sign == KuGouSignKind.Web)
                    parameters["signature"] = KuGouSigner.SignatureWeb(parameters, bodyJson);

                string url = $"{request.BaseUrl.TrimEnd('/')}{request.Path}";
                if (parameters.Count > 0)
                    url += $"?{buildQueryString(parameters)}";

                using var httpRequest = new HttpRequestMessage(request.Method, url);

                // Identity headers mirror what each verified channel actually requires.
                if (request.Sign == KuGouSignKind.Android)
                {
                    httpRequest.Headers.TryAddWithoutValidation("User-Agent", KuGouConstants.user_agent);
                    httpRequest.Headers.TryAddWithoutValidation("dfid", session.Dfid);
                    httpRequest.Headers.TryAddWithoutValidation("mid", session.Mid);
                    httpRequest.Headers.TryAddWithoutValidation("clienttime", formatScalar(parameters["clienttime"]));
                    httpRequest.Headers.TryAddWithoutValidation("kg-rc", "1");
                    httpRequest.Headers.TryAddWithoutValidation("kg-thash", "5d816a0");
                    httpRequest.Headers.TryAddWithoutValidation("kg-rec", "1");
                    httpRequest.Headers.TryAddWithoutValidation("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
                    // 平台/设备 cookie 与两个参考客户端一致（KUGOU_API_* + 登录凭证）。kugouvip 与
                    // tracker 的授权接口会读取这些 cookie 判定平台与设备身份，只发查询参数会被判 "params invalid"。
                    httpRequest.Headers.TryAddWithoutValidation("Cookie", buildCookie());
                }
                else
                {
                    httpRequest.Headers.TryAddWithoutValidation("User-Agent", KuGouConstants.browser_user_agent);
                }

                if (!string.IsNullOrEmpty(request.Router))
                    httpRequest.Headers.TryAddWithoutValidation("x-router", request.Router);

                // 接口级覆盖放在最后：登录接口要换掉 User-Agent（其中的平台字段会被服务端读走）。
                if (request.Headers is not null)
                {
                    foreach (var header in request.Headers)
                    {
                        httpRequest.Headers.Remove(header.Key);
                        httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                if (request.Method == HttpMethod.Post)
                    httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                if (Environment.GetEnvironmentVariable("KUGOU_DEBUG_RAW") == "1")
                {
                    // Raw wire format (incl. signature/token) is opt-in only; normal diagnostics
                    // never log query strings.
                    Console.Error.WriteLine($"[KuGou] {request.Method} {url}");
                    if (!string.IsNullOrEmpty(bodyJson))
                        Console.Error.WriteLine($"[KuGou] body={bodyJson}");
                }

                bool retryable = false;

                try
                {
                    using var httpResponse = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
                    responseText = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (httpResponse.Headers.TryGetValues("ssa-code", out var ssaValues))
                        LastSsaCode = ssaValues.FirstOrDefault();

                    if (Environment.GetEnvironmentVariable("KUGOU_DEBUG_RAW") == "1")
                    {
                        // 排障用：把原始响应正文（≤1200 字符）打到 stderr，正常日志不会记录正文。
                        Console.Error.WriteLine($"[KuGou] resp {(int)httpResponse.StatusCode} {request.Method} {request.Path} {Truncate(responseText, 1200)}");
                        Console.Error.Flush();
                    }

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        // 5xx/429 from a throttled gateway are worth one more try; 4xx errors are definitive.
                        retryable = (int)httpResponse.StatusCode is >= 500 or 429;
                        AppLog.Net.Add($"HTTP 失败 {endpoint} → {(int)httpResponse.StatusCode}，{responseText.Length}B，{sw.ElapsedMilliseconds}ms{(retryable && attempt < attempts - 1 ? "，将重试" : string.Empty)}：{Truncate(responseText, 300)}",
                            LogLevel.Important);

                        if (!retryable || attempt == attempts - 1)
                            throw new KuGouApiException($"HTTP {(int)httpResponse.StatusCode}: {Truncate(responseText, 800)}");
                    }
                    else
                    {
                        break;
                    }
                }
                catch (KuGouApiException) when (retryable && attempt < attempts - 1)
                {
                    // fall through to the delay and next attempt
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < attempts - 1)
                {
                    // Our own HttpClient timeout (the request never cancelled by the caller): safe to retry.
                    AppLog.Net.Add($"请求超时 {endpoint}，{sw.ElapsedMilliseconds}ms，将重试", LogLevel.Important);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (attempt < attempts - 1)
                        AppLog.Net.Add($"网络异常 {endpoint}，{sw.ElapsedMilliseconds}ms，将重试：{e.GetType().Name}: {e.Message}", LogLevel.Important);
                    else
                    {
                        AppLog.Net.Add($"网络异常 {endpoint}，{sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                        throw;
                    }
                }
            }

            AppLog.Net.Add($"响应 {endpoint} → 200，{responseText.Length}B，{sw.ElapsedMilliseconds}ms");

            try
            {
                if (JToken.Parse(responseText) is JObject body)
                    return body;
                AppLog.Api.Add($"响应结构异常 {endpoint}：{Truncate(responseText, 300)}", LogLevel.Important);
                throw new KuGouApiException($"Unexpected response shape: {Truncate(responseText, 800)}");
            }
            catch (JsonReaderException)
            {
                AppLog.Api.Add($"非 JSON 响应 {endpoint}：{Truncate(responseText, 300)}", LogLevel.Important);
                throw new KuGouApiException($"Non-JSON response: {Truncate(responseText, 800)}");
            }
        }

        /// <summary>
        /// Device/login defaults. token and userid are always sent (empty/"0" when logged out),
        /// matching the current working clients.
        /// </summary>
        private void injectDefaultParameters(IDictionary<string, object?> parameters, bool millisecondTime, bool includeUuid)
        {
            long clientTime = millisecondTime
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            parameters["dfid"] = session.Dfid;
            parameters["mid"] = session.Mid;
            if (includeUuid)
                parameters["uuid"] = session.Uuid;
            parameters["appid"] = KuGouConstants.AppId(session.Platform);
            parameters["clientver"] = KuGouConstants.ClientVer(session.Platform);
            parameters["clienttime"] = clientTime;
            parameters["token"] = session.Token;
            parameters["userid"] = session.UserId == 0 ? "0" : session.UserId;
        }

        /// <summary>
        /// Cookie bag sent with every signed request, matching KuGouMusicApi server.js / KGLiteVIP:
        /// platform + device identity + login credentials. Several endpoints (kugouvip, tracker 授权)
        /// read the platform/device identity from here rather than from the query string.
        /// </summary>
        private string buildCookie()
        {
            var parts = new List<string>
            {
                $"KUGOU_API_PLATFORM={(session.Platform == KuGouPlatform.Lite ? "lite" : string.Empty)}",
                $"KUGOU_API_MID={session.Mid}",
                $"KUGOU_API_GUID={session.Device.Guid}",
                $"KUGOU_API_DEV={session.Device.Dev}",
                "KUGOU_API_MAC=02:00:00:00:00:00",
            };

            if (!string.IsNullOrEmpty(session.Token))
                parts.Add($"token={session.Token}");

            if (session.UserId != 0)
                parts.Add($"userid={session.UserId}");

            return string.Join("; ", parts);
        }

        private static string buildQueryString(IReadOnlyDictionary<string, object?> parameters)
            => string.Join('&', parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(formatScalar(p.Value))}"));

        private static string formatScalar(object? value) => value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonConvert.SerializeObject(value) // nested objects/arrays are JSON-encoded into the signature
        };

        private static string Truncate(string value, int max = 300) => value.Length <= max ? value : value[..max];

        public void Dispose() => http.Dispose();
    }
}
