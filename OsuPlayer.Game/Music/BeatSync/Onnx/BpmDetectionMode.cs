namespace OsuPlayer.Game.Music.BeatSync.Onnx
{
    /// <summary>
    /// BPM 检测模式。两种模式走完全不同的技术路线，可以在设置页切换：
    /// <list type="bullet">
    /// <item><see cref="Native"/>：纯 C# 频谱分析（多频带谱通量 + 梳状滤波 + Ellis DP），无外部依赖。</item>
    /// <item><see cref="Onnx"/>：跑 Beat This!（JKU，ISMIR 2024，MIT）的 ONNX 节拍模型，
    /// 作为与频谱通量完全独立的第二意见。需要额外下载 120MB 模型。</item>
    /// </list>
    /// </summary>
    public enum BpmDetectionMode
    {
        /// <summary>原生模式（默认）：离线、零依赖。</summary>
        Native,

        /// <summary>ONNX 模式：Beat This! 模型推理，精度更高但更慢且需要模型文件。</summary>
        Onnx,
    }
}
