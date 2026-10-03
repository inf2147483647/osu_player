using System.Globalization;
using System.Text.RegularExpressions;

namespace OsuPlayer.Game.Music
{
    public readonly record struct LrcLine(double TimeMs, string Text);

    /// <summary>Minimal LRC parser: collects all [mm:ss.xx] timestamps on each line.</summary>
    public static partial class LrcParser
    {
        public static List<LrcLine> Parse(string content)
        {
            var result = new List<LrcLine>();
            if (string.IsNullOrEmpty(content))
                return result;

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                var matches = TimestampRegex().Matches(line);
                if (matches.Count == 0)
                    continue;

                string text = line[matches[^1].Index..].TrimStart();
                // Strip the trailing timestamp itself.
                text = line[(matches[^1].Index + matches[^1].Length)..].Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                foreach (Match m in matches)
                {
                    if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) &&
                        double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    {
                        result.Add(new LrcLine(minutes * 60_000 + seconds * 1000, text));
                    }
                }
            }

            result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return result;
        }

        [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
        private static partial Regex TimestampRegex();
    }
}
