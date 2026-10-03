using osu.Framework.IO.Stores;

namespace OsuPlayer.Game.Net
{
    /// <summary>
    /// Resource store for local media: the resource name IS the absolute file path on disk.
    /// BASS needs a seekable stream with a known length, so the file is read into memory first
    /// (same contract as <see cref="HttpResourceStore"/> uses for network audio).
    /// </summary>
    public sealed class LocalFileResourceStore : IResourceStore<byte[]>
    {
        public byte[] Get(string name) => File.ReadAllBytes(name);

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
            => File.ReadAllBytesAsync(name, cancellationToken);

        public Stream GetStream(string name) => new MemoryStream(File.ReadAllBytes(name));

        public IEnumerable<string> GetAvailableResources() => Enumerable.Empty<string>();

        public void Dispose()
        {
        }
    }
}
