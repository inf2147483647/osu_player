using System.Collections.Concurrent;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Graphics
{
    /// <summary>
    /// Sprite whose texture is fetched from an online/URL-backed <see cref="TextureStore"/>.
    /// The last SetUrl call wins, so rapid list recycling never shows a stale texture.
    /// </summary>
    public partial class OnlineSprite : Sprite
    {
        /// <summary>
        /// Per-store in-flight URL lookups. TextureStore.GetAsync internally deduplicates concurrent
        /// lookups with WaitSafely(), which throws on thread pool threads; we therefore coalesce
        /// duplicate URL requests ourselves and let the store cache serve subsequent lookups.
        /// </summary>
        private static readonly ConcurrentDictionary<TextureStore, ConcurrentDictionary<string, Task<Texture?>>> inflight = new();

        private readonly TextureStore store;
        private string? currentUrl;
        private int serial;

        /// <summary>一张图最多尝试几次（含首次）。列表滚动时资源会被并发限流，需要靠重试补齐。</summary>
        private const int load_attempts = 4;

        /// <summary>第 n 次失败的退避基数，实际等待 = 基数 × n。</summary>
        private const int retry_base_delay_ms = 900;

        public OnlineSprite(TextureStore store)
        {
            this.store = store;
        }

        public void SetUrl(string? url)
        {
            int own = ++serial;
            currentUrl = url;
            Texture = null;

            if (string.IsNullOrEmpty(url))
                return;

            loadAsync(url, own);
        }

        private static Task<Texture?> getTextureAsync(TextureStore store, string url)
        {
            var storeRequests = inflight.GetOrAdd(store, _ => new ConcurrentDictionary<string, Task<Texture?>>());

            return storeRequests.GetOrAdd(url, key =>
            {
                var task = store.GetAsync(key, CancellationToken.None)
                                .ContinueWith(t => (Texture?)t.Result, TaskScheduler.Default);

                // Release once finished; subsequent lookups hit the TextureStore cache directly.
                task.ContinueWith(_ => storeRequests.TryRemove(new KeyValuePair<string, Task<Texture?>>(key, task)),
                    TaskScheduler.Default);

                return task;
            });
        }

        private async void loadAsync(string url, int own)
        {
            for (int attempt = 1; attempt <= load_attempts; attempt++)
            {
                // 已经被更新的 SetUrl 取代：立刻收手，别把旧图写回去。
                if (own != serial || currentUrl != url)
                    return;

                Texture? texture = null;

                try
                {
                    texture = await getTextureAsync(store, url).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    AppLog.Net.Add($"封面/图片加载失败（第 {attempt}/{load_attempts} 次）：{AppLog.SafeUrl(url)}：{e.Message}",
                        LogLevel.Important, e);
                }

                if (texture is not null)
                {
                    AppLog.Net.Debug($"封面/图片加载完成：{AppLog.SafeUrl(url)}，{texture.Width}x{texture.Height}");

                    Schedule(() =>
                    {
                        if (own == serial && currentUrl == url)
                            Texture = texture;
                        else
                            AppLog.Net.Debug("封面到达但已过期，丢弃");
                    });

                    return;
                }

                // 取不到纹理（并发限流 / 网络失败 / 失败冷却）时退避重试：
                // 列表一次滚出几十个封面，前几次被限流很正常，重试才能补齐。
                if (attempt < load_attempts)
                    await Task.Delay(retry_base_delay_ms * attempt).ConfigureAwait(false);
            }

            AppLog.Net.Add($"封面/图片最终加载失败（已尝试 {load_attempts} 次）：{AppLog.SafeUrl(url)}", LogLevel.Important);
        }
    }
}
