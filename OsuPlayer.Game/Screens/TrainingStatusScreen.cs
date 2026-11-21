using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music.BeatSync;

namespace OsuPlayer.Game.Screens
{
    /// <summary>
    /// 命令行批量训练时的状态界面。
    ///
    /// 以前 <c>--train-beatmaps</c> / <c>--export-*-features</c> 只启动窗口不推 Screen，
    /// 整个训练过程是一块全黑窗口，既看不出进度也不知道还剩多久。这里把
    /// <see cref="TrainingProgress"/> 的快照画成进度卡，同时顺带展示机器状态
    /// （CPU 占用/内存/温度），方便判断线程数和散热是否拖慢了训练。
    /// </summary>
    public partial class TrainingStatusScreen : Screen
    {
        private const float card_width = 700;
        private const float card_height = 470;
        private const float bar_width = 600;
        private const double refresh_ms = 250;

        private readonly TrainingProgress progress;
        private readonly SystemMonitor monitor = new();

        /// <summary>训练期间循环播放的《OH MY PC》（嵌入 dll）。</summary>
        private readonly TrainingMusic music = new();

        private SpriteText titleText = null!;
        private Container progressFill = null!;
        private SpriteText countRow = null!;
        private SpriteText timeRow = null!;
        private SpriteText stageRow = null!;
        private SpriteText cpuRow = null!;
        private SpriteText threadsRow = null!;
        private SpriteText trainingThreadsRow = null!;
        private SpriteText usageRow = null!;
        private SpriteText memoryRow = null!;
        private SpriteText temperatureRow = null!;

        private Box muteBackground = null!;
        private SpriteText muteLabel = null!;

        private bool firstUpdate = true;

        public TrainingStatusScreen(TrainingProgress progress, string title = "正在训练 BPM 检测模型…",
                                    string doneTitle = "训练完成，正在退出…")
        {
            this.progress = progress;
            this.title = title;
            this.doneTitle = doneTitle;
            RelativeSizeAxes = Axes.Both;
        }

        private readonly string title;
        private readonly string doneTitle;

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = OsuColour.Gray(0.06f),
                    },
                    // 外框只比内层大 2px，充当 1px 圆角描边（osu!framework 的 Box 没有边框属性）。
                    new Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(card_width + 2, card_height + 2),
                        Masking = true,
                        CornerRadius = 20,
                        Child = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = OsuColour.Gray(0.26f),
                        },
                    },
                    new Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(card_width, card_height),
                        Masking = true,
                        CornerRadius = 18,
                        Children = new Drawable[]
                        {
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = OsuColour.Gray(0.13f),
                            },
                            titleText = new SpriteText
                            {
                                Anchor = Anchor.TopCentre,
                                Origin = Anchor.TopCentre,
                                Y = 36,
                                Font = OsuFont.GetFont(size: 30, weight: FontWeight.Bold),
                                Colour = Color4.White,
                                Text = title,
                            },
                            stageRow = text(92, 17, 150),
                            new Container
                            {
                                Anchor = Anchor.TopCentre,
                                Origin = Anchor.TopCentre,
                                Y = 124,
                                Size = new Vector2(bar_width, 12),
                                Masking = true,
                                CornerRadius = 6,
                                Children = new Drawable[]
                                {
                                    new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = OsuColour.Gray(0.24f),
                                    },
                                    progressFill = new Container
                                    {
                                        RelativeSizeAxes = Axes.Y,
                                        Width = 0,
                                        Masking = true,
                                        CornerRadius = 6,
                                        Child = new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Colour = new Color4(93, 189, 255, 255),
                                        },
                                    },
                                },
                            },
                            new FillFlowContainer
                            {
                                Anchor = Anchor.TopLeft,
                                Origin = Anchor.TopLeft,
                                Position = new Vector2(50, 160),
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 9),
                                Children = new Drawable[]
                                {
                                    countRow = text(),
                                    timeRow = text(),
                                    cpuRow = text(),
                                    threadsRow = text(),
                                    trainingThreadsRow = text(),
                                    usageRow = text(),
                                    memoryRow = text(),
                                    temperatureRow = text(),
                                },
                            },
                            new OsuClickableContainer
                            {
                                Anchor = Anchor.BottomRight,
                                Origin = Anchor.BottomRight,
                                Position = new Vector2(-30, -26),
                                Size = new Vector2(124, 38),
                                Masking = true,
                                CornerRadius = 19,
                                Action = toggleMute,
                                Children = new Drawable[]
                                {
                                    muteBackground = new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = OsuColour.Gray(0.22f),
                                    },
                                    muteLabel = new SpriteText
                                    {
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        Font = OsuFont.GetFont(size: 16),
                                        Colour = new Color4(220, 220, 230, 255),
                                        Text = "静音",
                                    },
                                },
                            },
                        },
                    },
                },
            };

            cpuRow.Text = $"CPU 型号：{monitor.CpuName}";
            threadsRow.Text = $"CPU 线程数：{monitor.LogicalProcessors}";

            music.Play();
            Scheduler.AddDelayed(update, refresh_ms, true);
        }

        /// <summary>「静音 / 取消静音」：只改音量，音乐一直循环（静音再取消时不用重新起播）。</summary>
        private void toggleMute()
        {
            bool muted = music.ToggleMute();

            AppLog.Beat.Add(muted ? "氛围音乐：已静音" : "氛围音乐：已取消静音", LogLevel.Verbose);

            muteLabel.Text = muted ? "取消静音" : "静音";
            muteLabel.Colour = muted ? OsuColour.Gray(0.1f) : new Color4(220, 220, 230, 255);
            muteBackground.Colour = muted ? new Color4(93, 189, 255, 255) : OsuColour.Gray(0.22f);
        }

        private static SpriteText text(float y = 0, float size = 18, float shade = 205) => new()
        {
            Anchor = Anchor.TopLeft,
            Origin = Anchor.TopLeft,
            Y = y,
            Font = OsuFont.GetFont(size: size),
            Colour = new Color4((byte)shade, (byte)shade, (byte)(shade + 10), 255),
            Text = string.Empty,
        };

        private void update()
        {
            TrainingProgress.Snapshot snapshot = progress.Read();
            monitor.Sample();

            if (snapshot.Failed)
                titleText.Text = "任务失败，正在退出…";
            else if (snapshot.Finished)
                titleText.Text = doneTitle;

            stageRow.Text = $"当前阶段：{snapshot.Stage}";
            countRow.Text = $"共 {snapshot.Total} 个　完成 {snapshot.Completed} 个"
                            + $"　跳过 {snapshot.Skipped} 个　剩余 {snapshot.Remaining} 个";

            string eta = snapshot.Finished
                ? "预计剩余 —"
                : snapshot.EtaSeconds is double seconds ? $"预计剩余 {seconds:0} 秒" : "预计剩余 计算中…";
            timeRow.Text = $"已用时 {snapshot.ElapsedSeconds:0.0} 秒　　{eta}";

            trainingThreadsRow.Text = $"使用线程数：{snapshot.Threads}";
            usageRow.Text = $"CPU 使用率：{monitor.CpuUsage * 100:0}%";
            memoryRow.Text = $"程序使用内存：{monitor.WorkingSetBytes / 1024.0 / 1024.0:0} MiB";

            if (monitor.Temperature is double temperature)
                temperatureRow.Text = monitor.TemperatureIsAcpiZone
                    ? $"CPU 温度：{temperature:0.0}°C（ACPI 热区）"
                    : $"CPU 温度：{temperature:0}°C";
            else
                temperatureRow.Text = monitor.TemperatureUnavailable
                    ? "CPU 温度：不可用（主板未暴露 WMI 温度）"
                    : "CPU 温度：读取中…";

            float ratio = snapshot.Total > 0 ? Math.Clamp((float)snapshot.Completed / snapshot.Total, 0, 1) : 0;

            if (firstUpdate)
            {
                progressFill.Width = bar_width * ratio;
                firstUpdate = false;
            }
            else
                progressFill.ResizeWidthTo(bar_width * ratio, 300, Easing.OutQuint);
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                music.Dispose();

            base.Dispose(isDisposing);
        }
    }
}
