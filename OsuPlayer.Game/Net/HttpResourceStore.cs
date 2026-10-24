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
    /// </summary>
    public sealed class HttpResourceStore : IResourceStore<byte[]>
    {
        /// <summary>封面/图片的超时（短：失败就快速放弃，不拖累其它请求）。</summary>
        private static readonly TimeSpan image_timeout = TimeSpan.FromSeconds(10);

        /// <summary>音频流的超时（整首下载，留足时间）。</summary>
        private static readonly TimeSpan audio_timeout = TimeSpan.FromSeconds(60);

        /// <summary>同一个 URL 失败后多久内不再重试（避免框架反复请求、通知刷屏）。</summary>
        private static readonly TimeSpan failure_retry_delay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// 同时进行的封面下载上限。框架的文本/封面加载走同步 <see cref="GetStream"/>，
        /// 每个请求都会占住一个线程池线程；列表里几十个封面一起进来会把线程池吃光，
        /// 连音频/接口请求都排不上队（"连接瘫痪"的主因之一）。超出的封面直接按缺失处理，
        /// 等滚动/重绘时再加载，保证播放链路永远有线程可用。
        /// </summary>
        private const int image_concurrency = 6;

        private int image_inflight;

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

        public byte[] Get(string name) => downloadBytes(name);

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

            // 同步加载会占线程：超过并发上限的封面直接放弃，保证音频/接口线程不被挤占。
            if (image && Interlocked.Increment(ref image_inflight) > image_concurrency)
            {
                Interlocked.Decrement(ref image_inflight);
                return null!;
            }

            // TrackBass wraps the stream in an AsyncBufferStream that requires a seekable stream
            // with a known Length; a raw forward-only HttpBaseStream throws NotSupportedException.
            // Buffer fully (framework calls this on thread-pool threads).
            try
            {
                return new MemoryStream(downloadBytes(name));
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
                if (image)
                    Interlocked.Decrement(ref image_inflight);
            }
        }

        private byte[] downloadBytes(string name)
            => downloadAsync(name, CancellationToken.None).GetAwaiter().GetResult();

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
            => recent_failures.TryGetValue(name, out DateTime failedAt)
               && DateTime.UtcNow - failedAt < failure_retry_delay;

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
            http.Dispose();
        }
    }
}