using System.Globalization;
using System.Text;

namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// 把当前歌曲的时间轴写成 osu! 谱面编辑器「导出为 .osu（客串难度）」同款格式：
    /// 标准 v14 头 + Metadata / Difficulty / Events / TimingPoints，HitObjects 留空。
    /// 这样导出的文件可以直接拖进 osu! 的谱面文件夹，用编辑器继续做图或补齐其他难度。
    /// </summary>
    public static class OsuTimingExporter
    {
        /// <summary>
        /// 写进 <c>Tags</c> 的歌曲 id 前缀。再次导入 .osu 时据此把时间轴精确应用到同一首歌，
        /// 不依赖歌名/歌手的模糊匹配。
        /// </summary>
        public const string SongIdTagPrefix = "osuplayer=";

        /// <summary>生成建议文件名：`{歌手} - {歌名} [Timing].osu`。</summary>
        public static string SuggestedFileName(SongInfo song)
        {
            string name = sanitize($"{song.Artist} - {song.Name} [Timing]", Path.GetInvalidFileNameChars());

            if (name.Length > 150)
                name = name[..150].Trim();

            return name + ".osu";
        }

        /// <summary>
        /// 生成 .osu 文本。时间轴按时间排序；红线保持正 beatLength，绿线使用负值（SV）。
        /// </summary>
        public static string Build(SongInfo song, BeatmapTiming timing)
        {
            var lines = timing.Lines
                .OrderBy(line => line.TimeMs)
                .Where(line => line.Uninherited ? line.BeatLengthMs > 0 : true)
                .ToList();

            if (lines.All(line => !line.Uninherited))
                throw new InvalidOperationException("时间轴里没有红线（BPM 点），无法导出。");

            string title = clean(song.Name);
            string artist = clean(song.Artist);
            string album = clean(song.Album);
            string tags = string.IsNullOrEmpty(song.Hash) ? string.Empty : SongIdTagPrefix + song.Hash;
            string audio = song.IsLocal
                ? Path.GetFileName(song.LocalPath!)
                : sanitize(song.Name, Path.GetInvalidFileNameChars()) + ".mp3";

            var builder = new StringBuilder();

            builder.AppendLine("osu file format v14");
            builder.AppendLine();
            builder.AppendLine("[General]");
            builder.AppendLine($"AudioFilename: {audio}");
            builder.AppendLine("AudioLeadIn: 0");
            builder.AppendLine("PreviewTime: -1");
            builder.AppendLine("Countdown: 0");
            builder.AppendLine("SampleSet: Normal");
            builder.AppendLine("StackLeniency: 0.7");
            builder.AppendLine("Mode: 0");
            builder.AppendLine("LetterboxInBreaks: 0");
            builder.AppendLine("WidescreenStoryboard: 1");
            builder.AppendLine();
            builder.AppendLine("[Editor]");
            builder.AppendLine("DistanceSpacing: 1");
            builder.AppendLine("BeatDivisor: 4");
            builder.AppendLine("GridSize: 4");
            builder.AppendLine("TimelineZoom: 1");
            builder.AppendLine();
            builder.AppendLine("[Metadata]");
            builder.AppendLine($"Title:{title}");
            builder.AppendLine($"TitleUnicode:{title}");
            builder.AppendLine($"Artist:{artist}");
            builder.AppendLine($"ArtistUnicode:{artist}");
            builder.AppendLine("Creator:osu!player");
            builder.AppendLine("Version:Timing");
            builder.AppendLine($"Source:{album}");
            builder.AppendLine($"Tags:{clean(tags)}");
            builder.AppendLine("BeatmapID:0");
            builder.AppendLine("BeatmapSetID:-1");
            builder.AppendLine();
            builder.AppendLine("[Difficulty]");
            builder.AppendLine("HPDrainRate:5");
            builder.AppendLine("CircleSize:4");
            builder.AppendLine("OverallDifficulty:5");
            builder.AppendLine("ApproachRate:5");
            builder.AppendLine("SliderMultiplier:1.4");
            builder.AppendLine("SliderTickRate:1");
            builder.AppendLine();
            builder.AppendLine("[Events]");
            builder.AppendLine("//Background and Video events");
            builder.AppendLine("//Break Periods");
            builder.AppendLine("//Storyboard Layer 0 (Background)");
            builder.AppendLine("//Storyboard Layer 1 (Fail)");
            builder.AppendLine("//Storyboard Layer 2 (Pass)");
            builder.AppendLine("//Storyboard Layer 3 (Foreground)");
            builder.AppendLine("//Storyboard Sound Samples");
            builder.AppendLine();
            builder.AppendLine("[TimingPoints]");

            foreach (BeatTimingLine line in lines)
            {
                double beatLength = line.Uninherited
                    ? line.BeatLengthMs
                    : line.BeatLengthMs < 0 ? line.BeatLengthMs : -100;

                int meter = line.Meter > 0 ? line.Meter : 4;
                int effects = line.Kiai ? 1 : 0;

                builder.AppendLine($"{format(line.TimeMs)},{format(beatLength)},{meter},2,0,100,{(line.Uninherited ? 1 : 0)},{effects}");
            }

            builder.AppendLine();
            builder.AppendLine("[HitObjects]");

            return builder.ToString();
        }

        private static string format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>元数据值不能跨行，也会吃掉行首行尾空白。</summary>
        private static string clean(string value)
            => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string sanitize(string value, char[] invalid)
        {
            foreach (char c in invalid)
                value = value.Replace(c, '_');

            return value.Trim();
        }
    }
}
