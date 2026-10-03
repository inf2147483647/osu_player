using System.Net.Http;
using OsuPlayer.Game.KuGou;
using OsuPlayer.Game.Logging;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OsuPlayer.Game.Net
{
    /// <summary>
    /// Resource store that treats resource names as URLs:
    /// http(s) names are streamed straight from the network (framework wraps the forward-only
    /// stream in an AsyncBufferStream for BASS, and ImageSharp reads it once for textures),
    /// and data: URIs are decoded from their base64 payload (used for the login QR image).
    /// </summary>
    public sealed class HttpResourceStore : IResourceStore<byte[]>
    {
        private readonly HttpClient http;

        public HttpResourceStore(HttpClient? client = null)
        {
            http = client ?? new HttpClient();
            if (!http.DefaultRequestHeaders.Contains("User-Agent"))
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KuGou.KuGouConstants.browser_user_agent);
        }

        public byte[] Get(string name) => downloadBytes(name);

        public async Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] inline = Convert.FromBase64String(extractDataPayload(name));
                AppLog.Net.Debug($"资源命中内联 data:，{inline.Length}B");
                return inline;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(name, cancellationToken).ConfigureAwait(false);
                AppLog.Net.Add($"资源下载完成：{AppLog.SafeUrl(name)}，{bytes.Length / 1024}KB，{sw.ElapsedMilliseconds}ms");
                return bytes;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Net.Add($"资源下载失败：{AppLog.SafeUrl(name)}，{sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                throw;
            }
        }

        public Stream GetStream(string name)
        {
            // TrackBass wraps the stream in an AsyncBufferStream that requires a seekable stream
            // with a known Length; a raw forward-only HttpBaseStream throws NotSupportedException.
            // Buffer fully (framework calls this on thread-pool threads).
            try
            {
                return new MemoryStream(downloadBytes(name));
            }
            catch (Exception e)
            {
                // The framework treats a null stream as "resource missing"; surface the cause.
                AppLog.Net.Add($"音频流获取失败，按资源缺失处理：{AppLog.SafeUrl(name)}：{e.Message}", LogLevel.Error, e);
                return null!;
            }
        }

        private byte[] downloadBytes(string name)
        {
            if (name.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(extractDataPayload(name));

            return http.GetByteArrayAsync(name).GetAwaiter().GetResult();
        }

        public IEnumerable<string> GetAvailableResources() => Enumerable.Empty<string>();

        private static string extractDataPayload(string dataUri)
        {
            int comma = dataUri.IndexOf(',');
            return comma >= 0 ? dataUri[(comma + 1)..] : dataUri;
        }

        public void Dispose() => http.Dispose();
    }
}
