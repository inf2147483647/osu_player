using ManagedBass;
using OsuPlayer.Game.Logging;
using OsuPlayer.Resources;
using osu.Framework.Logging;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// 训练窗口的氛围音乐：嵌入 dll 的《OH MY PC》循环播放。
    ///
    /// 走 BASS 内存流（与 <see cref="BeatAnalyser"/> 解码同一个库，不经过 osu! 的 TrackStore），
    /// 训练命令行模式因此不需要文件路径，也不会和播放器的曲目队列抢音频资源。
    /// BASS 设备由框架的音频管理器初始化，训练入口可能抢在初始化之前，所以创建流时按
    /// <see cref="Errors.Init"/> 重试。
    /// </summary>
    public sealed class TrainingMusic : IDisposable
    {
        private const string resource_name = "OsuPlayer.Resources.Tracks.oh-my-pc.mp3";

        /// <summary>背景音乐音量：烘托氛围但别盖过风扇声。</summary>
        private const float volume = 0.6f;

        private readonly CancellationTokenSource stop = new();

        private volatile int handle;
        private volatile bool muted;
        private volatile bool disposed;

        /// <summary>异步开始循环播放；失败只写日志，不影响训练。</summary>
        public void Play()
        {
            byte[]? audio = readResource();

            if (audio is null)
                return;

            _ = Task.Run(() => start(audio));
        }

        /// <summary>静音 / 取消静音，返回切换后的状态。</summary>
        public bool ToggleMute()
        {
            muted = !muted;
            applyVolume();
            return muted;
        }

        private async Task start(byte[] audio)
        {
            int stream = 0;

            for (int attempt = 0; !stop.IsCancellationRequested; attempt++)
            {
                stream = Bass.CreateStream(audio, 0, audio.Length, BassFlags.Loop);

                if (stream != 0)
                    break;

                // 设备还没 Init 完成是正常竞态；其它错误重试也没用。
                if (Bass.LastError != Errors.Init || attempt >= 20)
                {
                    AppLog.Beat.Add($"氛围音乐播放失败：{Bass.LastError}", LogLevel.Important);
                    return;
                }

                try
                {
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (stream == 0)
                return;

            if (stop.IsCancellationRequested)
            {
                Bass.StreamFree(stream);
                return;
            }

            handle = stream;
            applyVolume();

            if (!play(stream))
                AppLog.Beat.Add($"氛围音乐播放失败：{Bass.LastError}", LogLevel.Important);
        }

        /// <summary>
        /// 起播。BASS 输出可能被框架停在暂停态（窗口不在前台时框架会停音频输出，
        /// 此时 ChannelPlay 返回 <see cref="Errors.Start"/>），显式 Start 后再试。
        /// </summary>
        private static bool play(int stream)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Bass.ChannelPlay(stream, attempt == 0))
                    return true;

                if (Bass.LastError != Errors.Start)
                    return false;

                Bass.Start();
            }

            return false;
        }

        private void applyVolume()
        {
            int current = handle;

            if (current == 0 || disposed)
                return;

            Bass.ChannelSetAttribute(current, ChannelAttribute.Volume, muted ? 0f : volume);
        }

        private static byte[]? readResource()
        {
            try
            {
                using Stream? stream = OsuPlayerResources.ResourceAssembly.GetManifestResourceStream(resource_name);

                if (stream is null)
                {
                    AppLog.Beat.Add($"氛围音乐缺失：{resource_name}", LogLevel.Important);
                    return null;
                }

                using var memory = new MemoryStream((int)stream.Length);
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (Exception e)
            {
                AppLog.Beat.Add($"氛围音乐读取失败：{e.Message}", LogLevel.Important, e);
                return null;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            stop.Cancel();

            int current = handle;
            handle = 0;

            if (current != 0)
                Bass.StreamFree(current);

            stop.Dispose();
        }
    }
}
