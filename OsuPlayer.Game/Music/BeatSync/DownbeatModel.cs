using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// 下拍（小节原点）轻量模型：窗口 ±8 拍的 20 维逐拍特征（12 色度 + 7 对数频段 + 1 起音强度，
    /// 与 <see cref="BeatAnalyser.buildBeatDescriptors"/> / <see cref="BeatAnalyser.buildOnsetEnvelope"/>
    /// 严格同源）→ 64 ReLU → 每拍「是小节第一拍」的概率。
    ///
    /// 权重由 Python 训练管线（约 300 张 osu! 谱面真值网格 + 半拍负样本）导出为裸 float32
    /// （models/downbeat/weights.bin，头 4 字节 'DBM1' + inDim/hidDim 两个 int32 + W1/b1/W2/b2/mu/sd）。
    /// 全模型约 2.2 万参数，单曲几百拍 × 340 维矩阵乘在毫秒级 —— 不值得过 ONNX Runtime，直接算。
    /// 模型缺失或损坏时返回 null，调用方回退到结构线索，行为与旧版一致。
    /// </summary>
    internal static class DownbeatModel
    {
        private const string model_file = "weights.bin";
        private const int feature_dim = 20;

        private static readonly Lazy<Model?> loaded = new(load);

        private sealed record Model(float[] W1, float[] B1, float[] W2, float[] B2, float[] Mu, float[] Sd,
                                    int InDim, int HidDim);

        public static bool IsAvailable => loaded.Value is not null;

        private static string AppDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuPlayer", "models", "downbeat");

        private static string AppLocalDirectory => Path.Combine(AppContext.BaseDirectory, "models", "downbeat");

        private static Model? load()
        {
            string? path = File.Exists(Path.Combine(AppDataDirectory, model_file)) ? Path.Combine(AppDataDirectory, model_file)
                : File.Exists(Path.Combine(AppLocalDirectory, model_file)) ? Path.Combine(AppLocalDirectory, model_file)
                : null;

            if (path is null)
                return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                if (bytes.Length < 12 || bytes[0] != 'D' || bytes[1] != 'B' || bytes[2] != 'M' || bytes[3] != '1')
                    return null;

                int inDim = BitConverter.ToInt32(bytes, 4);
                int hidDim = BitConverter.ToInt32(bytes, 8);
                int offset = 12;
                int need = inDim * hidDim + hidDim + hidDim * 2 + 2 + inDim * 2;

                if (inDim <= 0 || hidDim <= 0 || bytes.Length < offset + need * 4)
                    return null;

                float[] read(int count)
                {
                    var result = new float[count];
                    Buffer.BlockCopy(bytes, offset, result, 0, count * 4);
                    offset += count * 4;
                    return result;
                }

                var w1 = read(inDim * hidDim);
                var b1 = read(hidDim);
                var w2 = read(hidDim * 2);
                var b2 = read(2);
                var mu = read(inDim);
                var sd = read(inDim);

                if (sd.Any(v => v <= 1e-6f))
                    return null;

                return new Model(w1, b1, w2, b2, mu, sd, inDim, hidDim);
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"下拍模型加载失败：{e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 对逐拍特征（每拍 <see cref="feature_dim"/> 维，第 m 项 = 网格第 m 拍）预测每拍的
        /// downbeat 概率；返回数组与输入等长，或模型不可用时 null。
        /// </summary>
        public static double[]? PredictDownbeat(IReadOnlyList<double[]> features)
        {
            Model? model = loaded.Value;

            if (model is null || features.Count == 0)
                return null;

            int inDim = model.InDim;
            int hidDim = model.HidDim;
            var output = new double[features.Count];
            var hidden = new float[hidDim];

            for (int i = 0; i < features.Count; i++)
            {
                double[] f = features[i];

                if (f.Length != feature_dim)
                    return null;

                for (int h = 0; h < hidDim; h++)
                {
                    float sum = model.B1[h];
                    int wBase = h * inDim;

                    for (int k = 0; k < inDim; k++)
                        sum += (float)(f[k] - model.Mu[k]) / model.Sd[k] * model.W1[wBase + k];

                    if (sum > 0)
                        hidden[h] = sum;
                }

                float o0 = model.B2[0];
                float o1 = model.B2[1];

                for (int h = 0; h < hidDim; h++)
                {
                    o0 += hidden[h] * model.W2[h * 2];
                    o1 += hidden[h] * model.W2[h * 2 + 1];
                }

                Array.Clear(hidden);

                // 只要 downbeat 头：sigmoid(o1)。
                output[i] = 1.0 / (1.0 + Math.Exp(-Math.Clamp(o1, -30, 30)));
            }

            return output;
        }
    }
}
