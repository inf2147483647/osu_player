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
    /// Bottom area of the design draft, from top to bottom:
    ///  - right-aligned tools: 在线搜索歌词 喜欢 评论 倍数 音质 播放队列
    ///  - transport controls (centred): 列表循环 上一首 暂停 下一首 音量
    ///  - progress: 00:00 ━━━━━━━ 00:00
    /// </summary>
    public partial class BottomBar : Container
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        /// <summary>「音质 / 倍数」的下拉浮层，由 <see cref="Screens.MainScreen"/> 缓存。</summary>
        [Resolved]
        private PlaybackOptionsPopup optionsPopup { get; set; } = null!;

        public Action<PanelKind>? OnNavigate;

        private UiTextButton repeatButton = null!;
        private UiTextButton playButton = null!;
        private UiTextButton likeButton = null!;
        private UiTextButton tempoButton = null!;
        private UiTextButton qualityButton = null!;
        private UiTextButton volumeLabel = null!;
        private SpriteText currentTimeText = null!;
        private SpriteText lengthTimeText = null!;
        private SeekSliderBar progress = null!;
        private CommittingRoundedSliderBar<float> volumeSlider = null!;

        /// <summary>进度条的取值范围（只由进度条写入，避免与播放循环抢同一个 bindable）。</summary>
        private readonly BindableNumber<double> seekRange = new BindableNumber<double>(0);

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                // Right-aligned tool row.
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    Position = new Vector2(-28, -112),
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(22, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new[]
                    {
                        new UiTextButton("在线搜索歌词", () => OnNavigate?.Invoke(PanelKind.LyricsSearch), 22),
                        likeButton = new UiTextButton("喜欢", () => _ = player.ToggleLikeAsync(), 22),
                        new UiTextButton("评论", () => OnNavigate?.Invoke(PanelKind.Comments), 22),
                        tempoButton = new UiTextButton("倍数 1.00", showTempoOptions, 22),
                        qualityButton = new UiTextButton("音质 标准", showQualityOptions, 22),
                        new UiTextButton("播放队列", () => OnNavigate?.Invoke(PanelKind.Queue), 22),
                    },
                },

                // Transport controls.
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    Position = new Vector2(322, -72),
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(34, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        repeatButton = new UiTextButton("列表循环", () => player.CycleRepeat(), 24),
                        new UiTextButton("上一首", () => _ = player.PreviousAsync(), 24),
                        playButton = new UiTextButton("暂停", () => player.TogglePlay(), 30),
                        new UiTextButton("下一首", () => _ = player.NextAsync(), 24),
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
                                volumeLabel = new UiTextButton("音量", null, 24),
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

                // Progress row.
                currentTimeText = makeTimeText(Anchor.BottomLeft, new Vector2(150, -24)),
                progress = new SeekSliderBar
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new Vector2(296, -24),
                    Width = 648,
                    OnScrub = ms => player.Scrub(ms),
                    OnSeek = ms => player.Seek(ms),
                },
                lengthTimeText = makeTimeText(Anchor.BottomLeft, new Vector2(972, -24)),
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
            player.CurrentSong.BindValueChanged(_ => Schedule(updateLikeState), true);
            player.Liked.BindCollectionChanged((_, _) => Schedule(updateLikeState));
        }

        /// <summary>
        /// Top edge (in design-draft pixels) of the lowest control row - 倍数/音质 那一排。
        /// Everything above it is empty space that must stay hover-through: this container is
        /// full-screen and sits above the disc, so without this the disc could never be hovered.
        /// </summary>
        private const float controls_top = 768 - 150;

        /// <summary>
        /// Only the bottom control band takes positional input; the empty upper area lets hover and
        /// clicks reach the main disc / lyric fan underneath.
        /// </summary>
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => base.ReceivePositionalInputAt(screenSpacePos) && ToLocalSpace(screenSpacePos).Y >= controls_top;

        private SpriteText makeTimeText(Anchor anchor, Vector2 position) => new()
        {
            Anchor = anchor,
            Origin = Anchor.Centre,
            Position = position,
            Font = OsuFont.GetFont(size: 26),
            Colour = Color4.White,
            Text = "00:00",
        };

        protected override void Update()
        {
            base.Update();

            currentTimeText.Text = formatTime(player.CurrentTimeMs.Value);
            lengthTimeText.Text = formatTime(player.LengthMs.Value);
        }

        private void updatePlayLabel() => playButton.Text = player.IsPlaying.Value ? "暂停" : "播放";

        private void updateRepeatLabel()
            => repeatButton.Text = player.Repeat.Value switch
            {
                Music.RepeatMode.All => "列表循环",
                Music.RepeatMode.One => "单曲循环",
                _ => "不循环",
            };

        private void updateTempoLabel() => tempoButton.Text = $"倍数 {player.Tempo.Value:0.00}";

        private void updateQualityLabel() => qualityButton.Text = $"音质 {qualityLabel(player.Quality.Value)}";

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

            likeButton.Text = liked ? "♥ 喜欢" : "喜欢";
            likeButton.LabelColour = liked ? new Color4(255, 102, 171, 255) : Color4.White;
        }

        private static string formatTime(double ms)
        {
            int totalSeconds = Math.Max(0, (int)(ms / 1000));
            return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        }
    }
}
