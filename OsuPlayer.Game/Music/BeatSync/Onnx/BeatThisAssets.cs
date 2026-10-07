using System.Net.Http;
using osu.Framework.Logging;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// Beat This! 的 ONNX 资产（模型 + mel 滤波器组）在磁盘上的位置、就绪判断与按需下载。
    ///
    /// 模型 120MB，不适合打进安装包，所以走「用到才下载」：设置页里点一下下载，落到
    /// <c>%APPDATA%\OsuPlayer\models\beat-this</c>（与 osu!framework 的 host storage 同一层）。
    ///
    /// 前端参数全部来自模型自带的 config.json。这些值与训练时严格对应，改动任何一项都会让
    /// 模型输出失真，而且症状是「跑得动但结果离谱」，极难排查 —— 所以集中放在这里，只此一处。
    /// </summary>
    public static class BeatThisAssets
    {
        // ---- 前端参数（= config.json）----
        public const int SampleRate = 22050;
        public const int NFft = 1024;
        public const int HopLength = 441;
        public const int MelBins = 128;
        public const double LogMultiplier = 1000;

        // ---- 推理 / 后处理参数 ----
        public const double Fps = 50;
        public const int ChunkSize = 513;
        public const int BorderSize = 6;
        public const int PeakKernel = 7;        // ±3 帧的局部极大
        public const int DeduplicateWidth = 1;  // 相邻 ≤1 帧的峰合并

        private const string base_url = "https://huggingface.co/musetric/beat-this-onnx/resolve/main/";

        private const string model_file = "beat_this.onnx";
        private const string filterbank_file = "mel-filterbank.bin";

        /// <summary>mel 滤波器组的元素个数：一维谱（513 个频点）× 128 个 mel。</summary>
        public const int FilterbankRows = NFft / 2 + 1;
        public const int FilterbankFloats = FilterbankRows * MelBins;

        /// <summary>模型文件字节数（与模型卡上的 SHA256 一起构成版本指纹）。</summary>
        private const long model_bytes = 120259561;

        private static readonly HttpClient http = createHttpClient();

        public static string Directory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuPlayer", "models", "beat-this");

        public static string ModelPath => Path.Combine(Directory, model_file);
        public static string FilterbankPath => Path.Combine(Directory, filterbank_file);

        /// <summary>模型与滤波器组都在、且大小正确。设置页据此显示「未下载 / 就绪」。</summary>
        public static bool IsReady => hasSize(ModelPath, model_bytes) && hasSize(FilterbankPath, FilterbankFloats * 4);

        private static bool hasSize(string path, long expected)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == expected;
        }

        /// <summary>
        /// 下载缺失的资产。<paramref name="progress"/> 收到 0..1 的整体进度。
        /// 已存在且大小正确的文件会跳过，因此中断后重试不会从头再来。
        /// </summary>
        public static async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        {
            System.IO.Directory.CreateDirectory(Directory);

            (string file, long size)[] wanted =
            [
                (model_file, model_bytes),
                (filterbank_file, FilterbankFloats * 4),
            ];

            long total = wanted.Sum(w => w.size);
            long done = 0;

            foreach ((string file, long size) in wanted)
            {
                ct.ThrowIfCancellationRequested();

                string destination = Path.Combine(Directory, file);

                if (hasSize(destination, size))
                {
                    done += size;
                    progress?.Report((double)done / total);
                    continue;
                }

                AppLog.Beat.Add($"下载 Beat This! 资产 {file}（{size / 1024 / 1024.0:0.#}MB）…", LogLevel.Important);

                // 先写临时文件再替换：中途失败不会留下一个大小不对、看起来「已就绪」的半截文件。
                string temp = destination + ".part";

                using (var response = await http
                           .GetAsync(base_url + file, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using (var target = File.Create(temp))
                        await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                File.Move(temp, destination, overwrite: true);

                if (!hasSize(destination, size))
                    throw new IOException($"{file} 下载后大小不符（期望 {size} 字节）。");

                done += size;
                progress?.Report((double)done / total);
            }

            AppLog.Beat.Add("Beat This! 资产已就绪。", LogLevel.Important);
        }

        /// <summary>读取 mel 滤波器组（513 × 128，行主序 float32）。</summary>
        public static float[] LoadFilterbank()
        {
            byte[] raw = File.ReadAllBytes(FilterbankPath);

            if (raw.Length != FilterbankFloats * 4)
                throw new InvalidDataException($"mel-filterbank.bin 大小异常：{raw.Length} 字节。");

            var weights = new float[FilterbankFloats];
            Buffer.BlockCopy(raw, 0, weights, 0, raw.Length);
            return weights;
        }

        private static HttpClient createHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            return client;
        }
    }
}
