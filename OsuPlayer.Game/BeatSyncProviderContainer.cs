using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using OsuPlayer.Game.Music.BeatSync;

namespace OsuPlayer.Game
{
    /// <summary>
    /// Re-caches the music player's auto-analysed <see cref="IBeatSyncProvider"/> for its
    /// subtree, shadowing the beatmap-backed one <c>OsuGameBase</c> caches on itself
    /// (the interface carries a [Cached] marker, so it cannot be re-cached at the same
    /// level — only a nested container may override it).
    /// </summary>
    public partial class BeatSyncProviderContainer : Container
    {
        [Cached(typeof(IBeatSyncProvider))]
        private readonly MusicBeatSyncProvider beatSyncProvider;

        public BeatSyncProviderContainer(MusicBeatSyncProvider beatSyncProvider, Drawable? child = null)
        {
            this.beatSyncProvider = beatSyncProvider;

            RelativeSizeAxes = Axes.Both;

            AddInternal(beatSyncProvider);
            if (child is not null)
                AddInternal(child);
        }
    }
}
