using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ManagedBass;
using osu.Framework.Logging;
using osu.Framework.Platform;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// Turns files picked by the user into <see cref="SongInfo"/> entries for the local library.
    ///
    /// Supported input:
    ///  - <c>.osz</c> / <c>.olz</c> osu! beatmap archives (a zip): every <c>.osu</c> inside becomes one
    ///    track; its referenced audio file is extracted next to the library so BASS can decode it;
    ///  - plain audio files (mp3 / flac / wav / ogg / m4a / aac / wma / opus).
    ///
    /// Audio files are referenced in place (never copied); archive audio is extracted under
    /// <c>local/&lt;archive&gt;/</c> in the app's storage. A file BASS cannot open is skipped with a log
    /// line instead of being added as an unplayable entry.
    /// </summary>
    public sealed class LocalImporter
    {
        /// <summary>解析单个 .osu 文件得到的 timing；<see cref="SongId"/> 取自导出时写入的 Tags。</summary>
        public sealed record TimingFileImport(string FilePath, string? SongId, BeatmapTiming? Timing, string? Error);

        /// <summary>是否是单独的 .osu 文件（只含 timing，不是可播放曲目）。</summary>
        public static bool IsTimingFile(string path)
            => string.Equals(Path.GetExtension(path), ".osu", StringComparison.OrdinalIgnoreCase);

        private static readonly string[] audio_extensions =
        [
            ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus",
        ];

        private static readonly char[] invalid_name_chars = Path.GetInvalidFileNameChars();

        // osu! LegacyBeatmapDecoder：v4 及更早的谱面存在 24ms 的客户端级偏移；
        // TimingControlPoint.BeatLength 的可接受范围（超出会抛异常）。
        private const int early_version_timing_offset = 24;
        private const double min_beat_length = 6;
        private const double max_beat_length = 60000;

        private readonly Storage storage;

        public LocalImporter(Storage storage)
        {
            this.storage = storage;
        }

        /// <summary>Imports every given file, de-duplicated by target path within the batch.</summary>
        public IReadOnlyList<SongInfo> ImportAll(IEnumerable<string> paths)
        {
            var results = new List<SongInfo>();

            foreach (string path in paths)
            {
                foreach (SongInfo song in Import(path))
                {
                    if (song.LocalPath is not { } local || results.All(r => !string.Equals(r.LocalPath, local, StringComparison.OrdinalIgnoreCase)))
                        results.Add(song);
                }
            }

            return results;
        }

        /// <summary>Imports a single file; returns an empty list when the type is unsupported or unreadable.</summary>
        public IReadOnlyList<SongInfo> Import(string path)
        {
            try
            {
                if (File.Exists(path) is false)
                {
                    AppLog.Player.Add($"导入跳过（文件不存在）：{path}", LogLevel.Important);
                    return [];
                }

                string extension = Path.GetExtension(path).ToLowerInvariant();

                if (extension is ".osz" or ".olz")
                    return importArchive(path);

                if (audio_extensions.Contains(extension))
                {
                    SongInfo song = songFromAudio(path, Path.GetFileNameWithoutExtension(path), string.Empty, Path.GetFileNameWithoutExtension(path));

                    if (song.LocalPath is null)
                        return [];

                    AppLog.Player.Add($"导入本地音频：{path}");
                    return [song];
                }

                AppLog.Player.Add($"导入跳过（不支持的类型 {extension}）：{path}", LogLevel.Important);
                return [];
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"导入失败：{path}：{e.Message}", LogLevel.Error, e);
                return [];
            }
        }

        /// <summary>
        /// 解析单独导入的 .osu：读取 <c>[TimingPoints]</c> 与导出时写入的歌曲 id（<c>Tags:osuplayer=&lt;id&gt;</c>）。
        /// 不产生可播放曲目，调用方按 <see cref="TimingFileImport.SongId"/> 把时间轴套到对应歌曲上。
        /// </summary>
        public TimingFileImport ParseTimingFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new TimingFileImport(path, null, null, "文件不存在");

                string text = File.ReadAllText(path, Encoding.UTF8);
                string? songId = songIdFromTags(parseHeader(text).GetValueOrDefault("Tags"));
                BeatmapTiming? timing = parseTimingPoints(text, parseFormatVersion(text));

                return new TimingFileImport(path, songId, timing, timing is null ? "文件里没有 [TimingPoints]" : null);
            }
            catch (Exception e)
            {
                return new TimingFileImport(path, null, null, e.Message);
            }
        }

        /// <summary>从 <c>Tags</c> 里找出导出器写入的歌曲 id；没有则返回 null。</summary>
        private static string? songIdFromTags(string? tags)
        {
            if (string.IsNullOrWhiteSpace(tags))
                return null;

            foreach (string token in tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!token.StartsWith(OsuTimingExporter.SongIdTagPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string id = token[OsuTimingExporter.SongIdTagPrefix.Length..].Trim();
                return id.Length > 0 ? id : null;
            }

            return null;
        }

        private IReadOnlyList<SongInfo> importArchive(string path)
        {
            string setName = Path.GetFileNameWithoutExtension(path);
            var results = new List<SongInfo>();

            using ZipArchive archive = ZipFile.OpenRead(path);

            foreach (ZipArchiveEntry osuEntry in archive.Entries.Where(e => e.Name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                string text = readText(osuEntry);
                IReadOnlyDictionary<string, string> header = parseHeader(text);
                BeatmapTiming? timing = parseTimingPoints(text, parseFormatVersion(text));
                ZipArchiveEntry? audioEntry = findAudioEntry(archive, header.GetValueOrDefault("AudioFilename"));

                if (audioEntry is null)
                {
                    AppLog.Player.Add($"谱面缺少可用音频，跳过：{setName}/{osuEntry.Name}", LogLevel.Important);
                    continue;
                }

                string? audioPath = extract(audioEntry, $"local/{sanitise(setName)}/{sanitise(audioEntry.Name)}");

                if (audioPath is null)
                    continue;

                long duration = probeDurationMs(audioPath);

                if (duration < 0)
                {
                    AppLog.Player.Add($"音频编码不受支持，跳过：{setName}/{audioEntry.Name}", LogLevel.Important);
                    continue;
                }

                string title = pickPreferred(header, "Title", "TitleUnicode");
                string artist = pickPreferred(header, "Artist", "ArtistUnicode");

                results.Add(new SongInfo(
                    idFor(audioPath),
                    title.Length > 0 ? title : Path.GetFileNameWithoutExtension(audioEntry.Name),
                    artist,
                    setName,
                    duration,
                    CoverUrl: null,
                    LocalPath: audioPath)
                {
                    Timing = timing,
                });

                AppLog.Player.Add($"导入谱面曲目：{setName}/{osuEntry.Name} → {audioEntry.Name}{describeTiming(timing)}");
            }

            if (results.Count == 0)
                AppLog.Player.Add($"谱面中没有可导入的曲目：{path}", LogLevel.Important);

            return results;
        }

        /// <summary>Builds a local track from an audio file; returns a null <see cref="SongInfo.LocalPath"/> when unsupported.</summary>
        private SongInfo songFromAudio(string path, string title, string artist, string album)
        {
            long duration = probeDurationMs(path);

            if (duration < 0)
            {
                AppLog.Player.Add($"音频编码不受支持，跳过：{path}", LogLevel.Important);
                return new SongInfo(string.Empty, string.Empty, string.Empty, string.Empty, 0);
            }

            // 文件名常见的「歌手 - 歌名」形式：拆开比整串塞进歌名更有用。
            if (artist.Length == 0)
            {
                int separator = title.IndexOf(" - ", StringComparison.Ordinal);

                if (separator > 0)
                {
                    artist = title[..separator].Trim();
                    title = title[(separator + 3)..].Trim();
                }
            }

            return new SongInfo(idFor(path), title, artist, album, duration, CoverUrl: null, LocalPath: path);
        }

        private string? extract(ZipArchiveEntry entry, string targetName)
        {
            try
            {
                // 原子写入：解包过程中断只会留下临时文件，不会生成损坏的音频。
                using Stream output = storage.CreateFileSafely(targetName);
                using Stream input = entry.Open();
                input.CopyTo(output);

                return storage.GetFullPath(targetName);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"解包音频失败：{entry.FullName}：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        private static ZipArchiveEntry? findAudioEntry(ZipArchive archive, string? audioFilename)
        {
            if (!string.IsNullOrWhiteSpace(audioFilename))
            {
                string wanted = audioFilename.Replace('\\', '/').Trim();

                ZipArchiveEntry? exact = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, wanted, StringComparison.OrdinalIgnoreCase))
                                         ?? archive.Entries.FirstOrDefault(e => string.Equals(e.Name, Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase));

                if (exact is not null)
                    return exact;
            }

            return archive.Entries.FirstOrDefault(e => audio_extensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()));
        }

        private static string readText(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Reads the <c>Key:Value</c> header of a .osu file (everything before [HitObjects]) into a map.
        /// </summary>
        private static IReadOnlyDictionary<string, string> parseHeader(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = new StringReader(text);

            while (reader.ReadLine() is { } rawLine)
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith('['))
                {
                    if (line.StartsWith("[HitObjects]", StringComparison.OrdinalIgnoreCase))
                        break;

                    continue;
                }

                int colon = line.IndexOf(':');

                if (colon <= 0)
                    continue;

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();

                if (key.Length > 0 && value.Length > 0)
                    result.TryAdd(key, value);
            }

            return result;
        }

        /// <summary>
        /// Parses the <c>[TimingPoints]</c> section with osu!'s <c>LegacyBeatmapDecoder</c> semantics:
        /// <c>time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects</c>.
        /// A red line (uninherited) carries the beat length / meter; a green line only carries effects.
        /// Bit 0 of <c>effects</c> is the kiai flag, and maps of v4 and older get the +24ms offset osu!
        /// applies to their timing.
        /// </summary>
        private static BeatmapTiming? parseTimingPoints(string text, int formatVersion)
        {
            double offset = formatVersion < 5 ? early_version_timing_offset : 0;
            var lines = new List<BeatTimingLine>();
            bool inSection = false;

            using var reader = new StringReader(text);

            while (reader.ReadLine() is { } rawLine)
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith('['))
                {
                    inSection = line.StartsWith("[TimingPoints]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                    continue;

                string[] parts = line.Split(',');

                if (parts.Length < 2)
                    continue;

                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
                    || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength))
                    continue;

                int meter = parts.Length >= 3 && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMeter)
                    ? Math.Clamp(parsedMeter, 1, 32)
                    : 4;

                // 缺少 uninherited 列时按红线处理（osu! 的默认值）。
                bool uninherited = parts.Length < 7 || parts[6].Trim() != "0";

                bool kiai = parts.Length >= 8
                            && int.TryParse(parts[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int effects)
                            && (effects & 1) > 0;

                // osu! 会因非法拍长抛异常；这里只跳过该行，保证整张谱面仍然可用。
                if (uninherited && (double.IsNaN(beatLength) || beatLength is < min_beat_length or > max_beat_length))
                    continue;

                lines.Add(new BeatTimingLine(time + offset, beatLength, meter, uninherited, kiai));
            }

            return lines.Count > 0 ? new BeatmapTiming(lines) : null;
        }

        /// <summary>Reads the <c>osu file format vN</c> header (defaults to the latest known version).</summary>
        private static int parseFormatVersion(string text)
        {
            using var reader = new StringReader(text);
            string first = reader.ReadLine()?.Trim() ?? string.Empty;
            int marker = first.LastIndexOf('v');

            return marker >= 0 && int.TryParse(first[(marker + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
                ? version
                : 14;
        }

        /// <summary>Import log summary: BPM list, first beat, tempo-change count and kiai count.</summary>
        private static string describeTiming(BeatmapTiming? timing)
        {
            if (timing is null)
                return "（谱面无 TimingPoints）";

            List<BeatTimingLine> tempos = timing.TempoLines.ToList();

            string bpms = tempos.Count == 0
                ? "-"
                : string.Join("/", tempos.Take(4).Select(l => BeatmapTiming.BpmOf(l).ToString("0.##", CultureInfo.InvariantCulture)))
                  + (tempos.Count > 4 ? "/…" : string.Empty);

            return $"，BPM {bpms}，首拍 {timing.FirstBeatMs?.ToString("0.#", CultureInfo.InvariantCulture) ?? "-"}ms，变速 {tempos.Count} 处，kiai {timing.KiaiSections.Count} 段";
        }

        /// <summary>Prefers the native (non-ASCII) variant of a metadata field, e.g. TitleUnicode over Title.</summary>
        private static string pickPreferred(IReadOnlyDictionary<string, string> header, string asciiKey, string unicodeKey)
        {
            string ascii = header.GetValueOrDefault(asciiKey) ?? string.Empty;
            string unicode = header.GetValueOrDefault(unicodeKey) ?? string.Empty;

            return unicode.Any(c => c > 127) ? unicode : ascii.Length > 0 ? ascii : unicode;
        }

        /// <summary>Decoded length in milliseconds, or -1 when BASS cannot open the file.</summary>
        private static long probeDurationMs(string path)
        {
            int handle = Bass.CreateStream(path, 0, 0, BassFlags.Decode);

            if (handle == 0)
                return -1;

            try
            {
                long bytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
                double seconds = Bass.ChannelBytes2Seconds(handle, bytes);
                return seconds > 0 ? (long)Math.Round(seconds * 1000) : 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                Bass.StreamFree(handle);
            }
        }

        /// <summary>Stable identity for a local track (the same file always maps to the same value).</summary>
        private static string idFor(string path)
            => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).ToLowerInvariant();

        private static string sanitise(string name)
        {
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
                builder.Append(invalid_name_chars.Contains(c) ? '_' : c);

            return builder.ToString();
        }
    }
}
