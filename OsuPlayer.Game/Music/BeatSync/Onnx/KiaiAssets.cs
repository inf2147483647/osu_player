using Newtonsoft.Json.Linq;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// kiai 帧分类器（用 302 张真实谱面的真值在 Python 端训练，sklearn MLP 导出 ONNX）
    /// 在磁盘上的位置与后处理参数。
    ///
    /// 与 120MB 的 Beat This! 不同，这个模型只有几十~几百 KB，优先随程序发布
    /// （<c>&lt;程序目录&gt;/models/kiai</c>），同时允许用 <c>%APPDATA%\OsuPlayer\models\kiai</c>
    /// 里重新训练的版本覆盖，迭代模型不用重新编译。
    /// </summary>
    internal static class KiaiAssets
    {
        public const string ModelFile = "kiai.onnx";
        public const string ConfigFile = "config.json";

        public static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuPlayer", "models", "kiai");

        public static string AppLocalDirectory => Path.Combine(AppContext.BaseDirectory, "models", "kiai");

        /// <summary>模型不存在返回 null。APPDATA 优先（方便重新训练后立刻生效），否则用随程序发布的。</summary>
        public static string? ModelPath =>
            File.Exists(Path.Combine(AppDataDirectory, ModelFile))
                ? Path.Combine(AppDataDirectory, ModelFile)
                : File.Exists(Path.Combine(AppLocalDirectory, ModelFile))
                    ? Path.Combine(AppLocalDirectory, ModelFile)
                    : null;

        /// <summary>模型与参数都在才算就绪。</summary>
        public static bool IsReady => ModelPath is not null
                                      && File.Exists(Path.Combine(Path.GetDirectoryName(ModelPath)!, ConfigFile));

        /// <summary>读取后处理参数；缺字段用默认值，解析失败也用默认值。</summary>
        public static KiaiConfig LoadConfig()
        {
            var config = new KiaiConfig();

            try
            {
                string? modelPath = ModelPath;

                if (modelPath is null)
                    return config;

                string configPath = Path.Combine(Path.GetDirectoryName(modelPath)!, ConfigFile);

                if (!File.Exists(configPath))
                    return config;

                JObject root = JObject.Parse(File.ReadAllText(configPath));

                config.WindowRadius = root.Value<int?>("window_radius") ?? config.WindowRadius;
                config.RawDim = root.Value<int?>("raw_dim") ?? config.RawDim;
                config.Threshold = root.Value<double?>("threshold") ?? config.Threshold;
                config.MinCells = root.Value<int?>("min_cells") ?? config.MinCells;
                config.MergeGapCells = root.Value<int?>("merge_gap_cells") ?? config.MergeGapCells;
                config.PaddingCells = root.Value<int?>("padding_cells") ?? config.PaddingCells;
                config.SnapBoundaries = root.Value<bool?>("snap_boundaries") ?? config.SnapBoundaries;
            }
            catch
            {
                // 参数坏了不该让整条分析挂掉，用默认阈值即可。
            }

            return config;
        }
    }

    /// <summary>kiai 模型的后处理参数（由训练脚本按 OOF 结果选定后写入 config.json）。</summary>
    internal sealed class KiaiConfig
    {
        public int WindowRadius = KiaiFeatureBuilder.WindowRadius;
        public int RawDim = KiaiFeatureBuilder.RawDim;
        public double Threshold = 0.5;
        public int MinCells = 10;        // 5s，与启发式一致
        public int MergeGapCells = 6;    // 3s
        public int PaddingCells = 2;     // 1s
        public bool SnapBoundaries = true;
    }
}
