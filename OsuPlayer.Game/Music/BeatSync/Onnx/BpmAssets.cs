using Newtonsoft.Json.Linq;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// 全局 BPM 模型（Ballroom 698 首真值训练、sklearn 导出 ONNX）在磁盘上的位置与后处理参数。
    /// 与 <see cref="KiaiAssets"/> 同一套查找规则：%APPDATA% 优先，其次随程序发布。
    /// </summary>
    internal static class BpmAssets
    {
        public const string ModelFile = "bpm.onnx";
        public const string ConfigFile = "config.json";

        public static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuPlayer", "models", "bpm");

        public static string AppLocalDirectory => Path.Combine(AppContext.BaseDirectory, "models", "bpm");

        public static string? ModelPath =>
            File.Exists(Path.Combine(AppDataDirectory, ModelFile))
                ? Path.Combine(AppDataDirectory, ModelFile)
                : File.Exists(Path.Combine(AppLocalDirectory, ModelFile))
                    ? Path.Combine(AppLocalDirectory, ModelFile)
                    : null;

        public static bool IsReady => ModelPath is not null
                                      && File.Exists(Path.Combine(Path.GetDirectoryName(ModelPath)!, ConfigFile));

        public static BpmConfig LoadConfig()
        {
            var config = new BpmConfig();

            try
            {
                string? modelPath = ModelPath;

                if (modelPath is null)
                    return config;

                string configPath = Path.Combine(Path.GetDirectoryName(modelPath)!, ConfigFile);

                if (!File.Exists(configPath))
                    return config;

                JObject root = JObject.Parse(File.ReadAllText(configPath));

                config.FftN = root.Value<int?>("fft_n") ?? config.FftN;
                config.Hop = root.Value<int?>("hop") ?? config.Hop;
                config.OverrideMargin = root.Value<double?>("override_margin") ?? config.OverrideMargin;
                config.DoublingProbabilityMin = root.Value<double?>("doubling_probability_min") ?? config.DoublingProbabilityMin;
                config.DoublingGapMax = root.Value<double?>("doubling_gap_max") ?? config.DoublingGapMax;
                config.Model = root.Value<string>("model") ?? config.Model;
            }
            catch
            {
                // 配置坏了不该让整条分析挂掉，用默认参数即可。
            }

            return config;
        }
    }

    /// <summary>BPM 模型的后处理参数（训练脚本按 OOF 结果写入 config.json）。</summary>
    internal sealed class BpmConfig
    {
        public int FftN = BpmFeatureBuilder.FftN;
        public int Hop = BpmFeatureBuilder.Hop;

        /// <summary>保守覆盖阈值：模型概率相对「显著度最强候选」的领先不足此值时，保留后者。</summary>
        public double OverrideMargin = 0.1;

        /// <summary>
        /// 倍频抑制：首选 BPM 恰好是另一候选的 2×/4× 时，若低候选概率不低于此值，允许降到低层级。
        /// 用户实际使用中只出现「偏快一倍」，没有「偏慢一半」，所以这里只向下修正。
        /// </summary>
        public double DoublingProbabilityMin = 0.10;

        /// <summary>倍频抑制：首选与低候选的概率差不超过此值才降级；差距太大说明模型很有把握。</summary>
        public double DoublingGapMax = 0.16;

        public string Model = "unknown";

        /// <summary>特征管线与模型必须同口径，否则拒绝使用模型而不是给出错误结果。</summary>
        public bool MatchesBuilder => FftN == BpmFeatureBuilder.FftN && Hop == BpmFeatureBuilder.Hop;
    }
}
