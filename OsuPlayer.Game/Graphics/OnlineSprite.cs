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
            Texture? texture;
            try
            {
                texture = await getTextureAsync(store, url).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppLog.Net.Add($"封面/图片加载失败：{AppLog.SafeUrl(url)}：{e.Message}", LogLevel.Error, e);
                return;
            }

            AppLog.Net.Debug($"封面/图片加载完成：{AppLog.SafeUrl(url)}，{(texture is null ? "空纹理" : $"{texture.Width}x{texture.Height}")}");

            Schedule(() =>
            {
                if (own == serial && currentUrl == url)
                    Texture = texture;
                else
                    AppLog.Net.Debug("封面到达但已过期，丢弃");
            });
        }
    }
}
