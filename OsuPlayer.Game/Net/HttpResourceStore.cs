using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using OsuPlayer.Game.KuGou;
using OsuPlayer.Game.Logging;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace OsuPlayer.Game.Net
{
    /// <summary>
    /// Resource store that treats resource names as URLs:
    /// http(s) names are streamed straight from the network (framework wraps the forward-only
    /// stream in an AsyncBufferStream for BASS, and ImageSharp reads it once for textures),
    /// and data: URIs are decoded from their base64 payload (used for the login QR image).
    ///
    /// <para><b>为什么要把超时/并发/失败缓存做在这里</b>：这个 store 同时服务音频流和封面纹理。
    /// 之前用的是默认 <see cref="HttpClient"/>（100 秒超时、不限制并发），一旦封面 CDN 变慢，
    /// 几十上百个封面请求会一起挂起几十秒，把连接和线程池占满，音频/接口请求跟着排队，
    /// 表现就是"连接几乎瘫痪"；每个超时又会以 Error 级日志弹一条通知，通知面板被刷屏。
    /// 现在：图片单独限并发、按请求类型设置较短超时、失败后短时间内不再重试，
    /// 并且图片失败只记 Important（不进通知中心）。</para>
    ///
    /// <para><b>关键：同步路径必须是真同步 I/O，不能用 async-over-sync。</b>
    /// 框架的 <c>TextureStore.GetAsync</c> 是 <c>Task.Run(() => Get())</c>，也就是在线程池线程上
    /// 调到这里；如果这里再 <c>GetAwaiter().GetResult()</c> 等一个异步 HTTP，续体同样要回线程池排队。
    /// 几十个封面一起进来就会把线程池占满：续体排不上队，连 <c>CancelAfter</c> 的取消回调都跑不了，
    /// 实测 10 秒超时会拖到 66 秒才抛，所有后续请求（包括酷狗接口）一起"瘫痪"。
    /// 所以 <see cref="GetStream"/> / <see cref="Get"/> 走 <see cref="HttpClient.Send(HttpRequestMessage, HttpCompletionOption, CancellationToken)"/>
    /// 的同步路径（阻塞在调用线程的 socket 读上，不占用额外的线程池调度），
    /// 与框架自带 <c>OnlineStore</c> 的做法一致；只有真正异步的调用方用 <see cref="GetAsync"/>。</para>
    /// </summary>
    public sealed class HttpResourceStore : IResourceStore<byte[]>
    {
        /// <summary>封面/图片的超时（短：失败就快速放弃，不拖累其它请求）。</summary>
        private static readonly TimeSpan image_timeout = TimeSpan.FromSeconds(10);

        /// <summary>音频流的超时（整首下载，留足时间）。</summary>
        private static readonly TimeSpan audio_timeout = TimeSpan.FromSeconds(60);

        /// <summary>封面下载失败后的冷却时间：短一些，让列表里的封面能靠重试补回来。</summary>
        private static readonly TimeSpan image_failure_retry_delay = TimeSpan.FromSeconds(10);

        /// <summary>音频下载失败后的冷却时间（整首下载，失败一次就先别再试）。</summary>
        private static readonly TimeSpan audio_failure_retry_delay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// 同时进行的封面下载上限。框架的文本/封面加载走同步 <see cref="GetStream"/>，
        /// 每个请求都会占住一个线程池线程；列表里几十个封面一起进来会把线程池吃光，
        /// 连音频/接口请求都排不上队。
        /// </summary>
        private const int image_concurrency = 6;

        /// <summary>等一个封面并发槽位的最长时间；超时按缺失处理，由 OnlineSprite 稍后重试。</summary>
        private static readonly TimeSpan image_slot_wait = TimeSpan.FromSeconds(3);

        private readonly SemaphoreSlim image_slots = new(image_concurrency, image_concurrency);

        private readonly HttpClient http;

        private readonly ConcurrentDictionary<string, DateTime> recent_failures =
            new(StringComparer.OrdinalIgnoreCase);

        public HttpResourceStore(HttpClient? client = null)
        {
            if (client is not null)
            {
                http = client;
            }
            else
            {
                var handler = new SocketsHttpHandler
                {
                    // 定时回收连接：系统睡眠/换网后池里的半死 socket 会被直接丢弃，而不是一直复用它。
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                    MaxConnectionsPerServer = 12,
                    ConnectTimeout = TimeSpan.FromSeconds(6),
                    AutomaticDecompression = DecompressionMethods.All,
                };

                // 超时由每次请求自己的 CancellationToken 控制（图片/音频不同）。
                http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            }

            if (!http.DefaultRequestHeaders.Contains("User-Agent"))
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KuGouConstants.browser_user_agent);
        }

        public byte[] Get(string name) => downloadBytesSync(name);

        public async Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            return await downloadAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public Stream GetStream(string name)
        {
            bool image = isImage(name);

            // 刚失败过：直接按缺失处理，不写日志、不再发请求。
            if (isRecentlyFailed(name))
                return null!;

            // 超过并发上限时排队等一个槽位（最多等 3 秒），而不是直接放弃 ——
            // 之前直接返回 null，列表一次滚出几十个封面时大部分会被永久丢掉，
            // 表现就是"很多歌的封面一直加载不出来"。
            bool acquired = false;

            if (image)
            {
                acquired = image_slots.Wait(image_slot_wait);

                if (!acquired)
                    return null!;
            }

            // TrackBass wraps the stream in an AsyncBufferStream that requires a seekable stream
            // with a known Length; a raw forward-only HttpBaseStream throws NotSupportedException.
            // Buffer fully (framework calls this on thread-pool threads).
            try
            {
                return new MemoryStream(downloadBytesSync(name));
            }
            catch (Exception e)
            {
                // 框架把 null 视为资源缺失；这里把原因写进日志（图片失败只记 Important，不弹通知）。
                string kind = image ? "封面" : "音频流";

                AppLog.Net.Add($"{kind}获取失败，按资源缺失处理：{AppLog.SafeUrl(name)}：{e.Message}",
                    image ? LogLevel.Important : LogLevel.Error, e);

                return null!;
            }
            finally
            {
                if (acquired)
                    image_slots.Release();
            }
        }

        /// <summary>
        /// 同步下载，供 <see cref="GetStream"/> / <see cref="Get"/> 使用。
        ///
        /// <para>这里必须走 <see cref="HttpClient.Send(HttpRequestMessage, HttpCompletionOption, CancellationToken)"/>
        /// 的真同步路径（阻塞在调用线程的 socket 读上），而不是 <c>await</c> + <c>GetAwaiter().GetResult()</c>：
        /// 后者虽然也能跑通，但会把"等待异步续体"这件事压回线程池。框架用 <c>Task.Run(() => Get())</c>
        /// 调进来时，每个封面已经占了一个线程池线程，续体再排不上队就会连锁卡死，
        /// 连 <c>CancelAfter</c> 的定时器回调都执行不了（实测 10s 超时拖到 66s 才抛）。</para>
        /// </summary>
        private byte[] downloadBytesSync(string name)
        {
            if (name.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] inline = Convert.FromBase64String(extractDataPayload(name));
                AppLog.Net.Debug($"资源命中内联 data:，{inline.Length}B");
                return inline;
            }

            bool image = isImage(name);

            // 刚失败过的资源直接快速失败：不占用连接，也不再刷通知。
            if (isRecentlyFailed(name))
                throw new HttpRequestException("该资源刚刚下载失败，短时间内不再重试");

            var sw = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(image ? image_timeout : audio_timeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, name);
                using HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                response.EnsureSuccessStatusCode();

                using Stream content = response.Content.ReadAsStream(timeout.Token);
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);

                byte[] bytes = buffer.ToArray();

                recent_failures.TryRemove(name, out _);
                AppLog.Net.Add($"资源下载完成：{AppLog.SafeUrl(name)}，{bytes.Length / 1024}KB，{sw.ElapsedMilliseconds}ms");

                return bytes;
            }
            catch (Exception e)
            {
                recent_failures[name] = DateTime.UtcNow;

                string kind = image ? "封面" : "资源";
                TimeSpan limit = image ? image_timeout : audio_timeout;
                bool timedOut = e is OperationCanceledException && timeout.IsCancellationRequested;

                AppLog.Net.Add($"{kind}下载{(timedOut ? "超时" : "失败")}：{AppLog.SafeUrl(name)}，"
                               + $"{sw.ElapsedMilliseconds}ms（上限 {limit.TotalSeconds:0}s）：{e.Message}",
                    image ? LogLevel.Important : LogLevel.Error, e);

                throw;
            }
        }

        private async Task<byte[]> downloadAsync(string name, CancellationToken cancellationToken)
        {
            if (name.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] inline = Convert.FromBase64String(extractDataPayload(name));
                AppLog.Net.Debug($"资源命中内联 data:，{inline.Length}B");
                return inline;
            }

            bool image = isImage(name);

            // 刚失败过的资源直接快速失败：不占用连接，也不再刷通知。
            if (isRecentlyFailed(name))
                throw new HttpRequestException("该资源刚刚下载失败，短时间内不再重试");

            var sw = Stopwatch.StartNew();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(image ? image_timeout : audio_timeout);

                byte[] bytes = await http.GetByteArrayAsync(name, timeout.Token).ConfigureAwait(false);

                recent_failures.TryRemove(name, out _);
                AppLog.Net.Add($"资源下载完成：{AppLog.SafeUrl(name)}，{bytes.Length / 1024}KB，{sw.ElapsedMilliseconds}ms");

                return bytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消（切歌等），不算资源失败。
                throw;
            }
            catch (Exception e)
            {
                recent_failures[name] = DateTime.UtcNow;

                string kind = image ? "封面" : "资源";
                TimeSpan limit = image ? image_timeout : audio_timeout;

                AppLog.Net.Add($"{kind}下载失败：{AppLog.SafeUrl(name)}，"
                               + $"{sw.ElapsedMilliseconds}ms（上限 {limit.TotalSeconds:0}s）：{e.Message}",
                    image ? LogLevel.Important : LogLevel.Error, e);

                throw;
            }
        }

        public IEnumerable<string> GetAvailableResources() => Enumerable.Empty<string>();

        private bool isRecentlyFailed(string name)
        {
            if (!recent_failures.TryGetValue(name, out DateTime failedAt))
                return false;

            TimeSpan delay = isImage(name) ? image_failure_retry_delay : audio_failure_retry_delay;
            return DateTime.UtcNow - failedAt < delay;
        }

        private static bool isImage(string name)
        {
            return name.Contains("imge.kugou.com", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static string extractDataPayload(string dataUri)
        {
            int comma = dataUri.IndexOf(',');
            return comma >= 0 ? dataUri[(comma + 1)..] : dataUri;
        }

        public void Dispose()
        {
            image_slots.Dispose();
            http.Dispose();
        }
    }
}