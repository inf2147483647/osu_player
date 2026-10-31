using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Logging;
using osu.Framework.Logging;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Bottom area, from top to bottom:
    ///  - transport controls, horizontally centred on the window: 循环 上一首 暂停 下一首 音量
    ///  - one full-width row at the very bottom: 进度条（当前时间 + 拖动条 + 总时长）在左，
    ///    在线搜索歌词 / 喜欢 / 评论 / 倍数 / 音质 / 播放队列 六个图标在右
    /// </summary>
    public partial class BottomBar : Container
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        /// <summary>「音质 / 倍数」的下拉浮层，由 <see cref="Screens.MainScreen"/> 缓存。</summary>
        [Resolved]
        private PlaybackOptionsPopup optionsPopup { get; set; } = null!;

        public Action<PanelKind>? OnNavigate;

        private UiIconButton repeatButton = null!;
        private UiIconButton playButton = null!;
        private UiIconButton likeButton = null!;
        private UiIconButton tempoButton = null!;
        private UiIconButton qualityButton = null!;
        private UiIconButton volumeButton = null!;
        private SpriteText currentTimeText = null!;
        private SpriteText lengthTimeText = null!;
        private SeekSliderBar progress = null!;
        private CommittingRoundedSliderBar<float> volumeSlider = null!;

        /// <summary>最底那一行：进度条 + 右侧工具按钮。</summary>
        private Container bottomRow = null!;

        /// <summary>底行右侧的六个功能图标（自动尺寸，用来给进度条留出宽度）。</summary>
        private FillFlowContainer toolsRow = null!;

        /// <summary>进度条左端与窗口左边距之间的距离（时间文字 + 空隙）。</summary>
        private const float side_margin = 28;

        /// <summary>时间文字与拖动条之间、拖动条与总时长之间的空隙。</summary>
        private const float time_gap = 12;

        /// <summary>总时长与右侧工具按钮之间留的空隙，让进度条和按钮区分成两个区域。</summary>
        private const float tools_gap = 44;

        /// <summary>「00:00」还没量出宽度时用的兜底值（字体 26、格式固定）。</summary>
        private const float fallback_time_width = 92;

        /// <summary>六个工具按钮还没量出宽度时用的兜底值：图标 20 + 两侧 9 留白 ×6，加 5 个 20 间距。</summary>
        private const float fallback_tools_width = 6 * 38 + 5 * 20;

        /// <summary>进度条的取值范围（只由进度条写入，避免与播放循环抢同一个 bindable）。</summary>
        private readonly BindableNumber<double> seekRange = new BindableNumber<double>(0);

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                // Transport controls（图标版：循环 / 上一首 / 播放暂停 / 下一首 / 音量 + 滑块）。
                // 整行以窗口中线对称居中；往下挪一点，让整排图标落在圆饼描边之下。
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    Position = new Vector2(0, -56),
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(26, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        repeatButton = new UiIconButton(FontAwesome.Solid.Redo, () => player.CycleRepeat(), 20, "列表循环"),
                        new UiIconButton(FontAwesome.Solid.StepBackward, () => _ = player.PreviousAsync(), 22, "上一首"),
                        playButton = new UiIconButton(FontAwesome.Solid.Pause, () => player.TogglePlay(), 28, "暂停"),
                        new UiIconButton(FontAwesome.Solid.StepForward, () => _ = player.NextAsync(), 22, "下一首"),
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(10, 0),
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Margin = new MarginPadding { Left = 8 },
                            Children = new Drawable[]
                            {
                                volumeButton = new UiIconButton(FontAwesome.Solid.VolumeUp, null, 22, "音量"),
                                volumeSlider = new CommittingRoundedSliderBar<float>
                                {
                                    Width = 110,
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    OnDragEndCommitted = v => AppLog.Ui.Add($"音量调整为 {v * 100:0}%", LogLevel.Important),
                                },
                            },
                        },
                    },
                },

                // 最底一行：左边进度条（当前时间 + 拖动条 + 总时长），右边六个功能按钮。
                // 整行铺满窗口宽度，进度条宽度由 layoutBottomRow() 按窗口宽度算出来。
                bottomRow = new Container
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    RelativeSizeAxes = Axes.X,
                    Height = 44,
                    Position = new Vector2(0, -10),
                    Children = new Drawable[]
                    {
                        currentTimeText = makeTimeText(new Vector2(side_margin, 0)),

                        progress = new SeekSliderBar
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            OnScrub = ms => player.Scrub(ms),
                            OnSeek = ms => player.Seek(ms),
                        },

                        lengthTimeText = makeTimeText(Vector2.Zero),

                        toolsRow = new FillFlowContainer
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Position = new Vector2(-side_margin, 0),
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(20, 0),
                            AutoSizeAxes = Axes.Both,
                            Children = new[]
                            {
                                new UiIconButton(FontAwesome.Solid.Search, () => OnNavigate?.Invoke(PanelKind.LyricsSearch), 20, "在线搜索歌词"),
                                likeButton = new UiIconButton(FontAwesome.Regular.Heart, () => _ = player.ToggleLikeAsync(), 20, "喜欢"),
                                new UiIconButton(FontAwesome.Regular.Comment, () => OnNavigate?.Invoke(PanelKind.Comments), 20, "评论"),
                                tempoButton = new UiIconButton(FontAwesome.Solid.TachometerAlt, showTempoOptions, 20, "倍数 1.00"),
                                qualityButton = new UiIconButton(FontAwesome.Solid.Headphones, showQualityOptions, 20, "音质 标准"),
                                new UiIconButton(FontAwesome.Solid.ListUl, () => OnNavigate?.Invoke(PanelKind.Queue), 20, "播放队列"),
                            },
                        },
                    },
                },
            };

            // 进度条需要一个 BindableNumber 来定义取值范围，但**不能**直接绑 CurrentTimeMs：
            // 播放循环每帧都会写回播放位置，双向绑定下会和正在拖动的用户抢同一个 bindable，
            // 拖动就会来回跳（卡顿）。这里给它一个只由滑块自己写入的范围 bindable，
            // 播放进度改由 SeekSliderBar.Update 单向拉取。
            player.CurrentTimeMs.MinValue = 0;
            seekRange.MinValue = 0;
            seekRange.MaxValue = 1;

            player.LengthMs.BindValueChanged(e =>
            {
                double max = Math.Max(1, e.NewValue);
                player.CurrentTimeMs.MaxValue = max;
                seekRange.MaxValue = max;
            }, true);

            progress.Current = seekRange;
            progress.Source = player.CurrentTimeMs;

            player.Volume.MinValue = 0;
            player.Volume.MaxValue = 1;
            volumeSlider.Current = player.Volume;

            player.IsPlaying.BindValueChanged(_ => updatePlayLabel(), true);
            player.Repeat.BindValueChanged(_ => updateRepeatLabel(), true);
            player.Tempo.BindValueChanged(_ => updateTempoLabel(), true);
            player.Quality.BindValueChanged(_ => updateQualityLabel(), true);
            player.Volume.BindValueChanged(_ => updateVolumeLabel(), true);
            player.CurrentSong.BindValueChanged(_ => Schedule(updateLikeState), true);
            player.Liked.BindCollectionChanged((_, _) => Schedule(updateLikeState));
        }

        /// <summary>
        /// Top edge (in design-draft pixels) of the transport row.
        /// Everything above it is empty space that must stay hover-through: this container is
        /// full-screen and sits above the disc, so without this the disc could never be hovered.
        /// </summary>
        private const float controls_top = 768 - 108;

        /// <summary>
        /// Only the bottom control band takes positional input; the empty upper area lets hover and
        /// clicks reach the main disc / lyric fan underneath.
        /// </summary>
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => base.ReceivePositionalInputAt(screenSpacePos) && ToLocalSpace(screenSpacePos).Y >= controls_top;

        /// <summary>底行的时间文字：纵向居中于底行、从该位置开始向右排。</summary>
        private SpriteText makeTimeText(Vector2 position) => new()
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Position = position,
            Font = OsuFont.GetFont(size: 26),
            Colour = Color4.White,
            Text = "00:00",
        };

        /// <summary>
        /// 底行横向排布：当前时间贴左，总时长跟在拖动条后面，六个工具图标贴右；
        /// 拖动条占满中间剩下的宽度。窗口缩放后每帧重算，赋值不变时是空操作。
        /// </summary>
        private void layoutBottomRow()
        {
            float width = bottomRow.DrawWidth;
            if (width <= 0)
                return;

            // 时间文字与工具按钮都是自动尺寸，首帧还没量出宽度时用兜底值，下一帧就是实测值。
            float timeWidth = Math.Max(currentTimeText.DrawWidth, lengthTimeText.DrawWidth);
            if (timeWidth <= 0)
                timeWidth = fallback_time_width;

            float toolsWidth = toolsRow.DrawWidth > 0 ? toolsRow.DrawWidth : fallback_tools_width;

            float progressLeft = side_margin + timeWidth + time_gap;
            float progressRight = width - side_margin - toolsWidth - tools_gap - timeWidth - time_gap;

            progress.X = progressLeft;
            progress.Width = Math.Max(120, progressRight - progressLeft);
            lengthTimeText.X = progressLeft + progress.Width + time_gap;
        }

        protected override void Update()
        {
            base.Update();

            currentTimeText.Text = formatTime(player.CurrentTimeMs.Value);
            lengthTimeText.Text = formatTime(player.LengthMs.Value);

            layoutBottomRow();
        }

        private void updatePlayLabel()
        {
            playButton.Icon = player.IsPlaying.Value ? FontAwesome.Solid.Pause : FontAwesome.Solid.Play;
            playButton.TooltipText = player.IsPlaying.Value ? "暂停" : "播放";
        }

        /// <summary>
        /// 循环方式只换图标：列表循环与单曲循环共用同一个环形 redo（osu! 自带的 FontAwesome5
        /// 字体里 RedoAlt 与 Redo 几乎不可区分），单曲循环靠右下角数字角标「1」区分；
        /// 不循环则是一根直线箭头。具体含义靠悬停说明补充。
        /// </summary>
        private void updateRepeatLabel()
        {
            switch (player.Repeat.Value)
            {
                case Music.RepeatMode.All:
                    repeatButton.Icon = FontAwesome.Solid.Redo;
                    repeatButton.BadgeText = string.Empty;
                    repeatButton.TooltipText = "列表循环";
                    break;

                case Music.RepeatMode.One:
                    repeatButton.Icon = FontAwesome.Solid.Redo;
                    repeatButton.BadgeText = "1";
                    repeatButton.TooltipText = "单曲循环";
                    break;

                default:
                    repeatButton.Icon = FontAwesome.Solid.LongArrowAltRight;
                    repeatButton.BadgeText = string.Empty;
                    repeatButton.TooltipText = "不循环";
                    break;
            }
        }

        private void updateTempoLabel() => tempoButton.TooltipText = $"倍数 {player.Tempo.Value:0.00}";

        private void updateQualityLabel() => qualityButton.TooltipText = $"音质 {qualityLabel(player.Quality.Value)}";

        /// <summary>音量图标：静音时换成喇叭关闭的样式，悬停说明带上当前百分比。</summary>
        private void updateVolumeLabel()
        {
            volumeButton.Icon = player.Volume.Value <= 0.001
                ? FontAwesome.Solid.VolumeMute
                : FontAwesome.Solid.VolumeUp;

            volumeButton.TooltipText = $"音量 {player.Volume.Value * 100:0}%";
        }

        /// <summary>音质的界面文案（下拉框里的选项与底栏按钮共用一套）。</summary>
        private static string qualityLabel(string quality)
        {
            foreach ((string value, string label) in MusicPlayer.QualityOptions)
            {
                if (value == quality)
                    return label;
            }

            return quality;
        }

        /// <summary>下拉卡片以按钮左上角为锚点弹出。</summary>
        private void showTempoOptions() => optionsPopup.ShowTempo(buttonAnchor(tempoButton));

        private void showQualityOptions() => optionsPopup.ShowQuality(buttonAnchor(qualityButton));

        private static Vector2 buttonAnchor(Drawable button) => button.ScreenSpaceDrawQuad.TopLeft;

        private void updateLikeState()
        {
            SongInfo? song = player.CurrentSong.Value;
            bool liked = song is not null && player.IsLiked(song);

            // 已喜欢用实心红心，未喜欢用空心红心（和文字版「♥ 喜欢」的区分方式一致）。
            likeButton.Icon = liked ? FontAwesome.Solid.Heart : FontAwesome.Regular.Heart;
            likeButton.TooltipText = liked ? "已喜欢（点击取消）" : "喜欢";
            likeButton.IconColour = liked ? new Color4(255, 102, 171, 255) : Color4.White;
        }

        private static string formatTime(double ms)
        {
            int totalSeconds = Math.Max(0, (int)(ms / 1000));
            return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        }
    }
}
