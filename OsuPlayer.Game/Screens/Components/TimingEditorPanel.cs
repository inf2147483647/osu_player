using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Beatmaps.Timing;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osu.Game.Screens.Edit.Timing;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using OsuPlayer.Game.Screens.Components.Timing;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 右侧分屏的 timing 编辑器，结构与控件对齐 osu! 编辑器的 Timing 页
    /// （<c>TimingScreen</c> + <c>ControlPointSettings</c>）：
    ///
    ///  1. 控制点列表（对应 osu! 的 <c>ControlPointList</c>）
    ///  2. <b>Time</b> 文本框 + <b>Use current time</b>（对应 <c>GroupSection</c>）
    ///  3. <b>Timing</b> 卡片（对应 <c>TimingSection</c>）：节拍器（osu! 的
    ///     <see cref="MetronomeDisplay"/>）、Offset、BPM、Tap the beat + 播放/停止键、Time Signature
    ///  4. <b>Effect</b> 卡片（对应 <c>EffectSection</c>）：Kiai Time
    ///
    /// 未移植的部分及原因见 README 注释：波形对比需要额外解码整首音频；「Adjust existing objects」
    /// 与「Scroll Speed」在本播放器没有对应概念（osu! 对不支持滚动速度的 ruleset 也会隐藏后者）。
    ///
    /// 每个输入一敲下就生效（"输入时立刻生效"）。它以固定宽度停靠在窗口右侧，与左侧主内容构成分屏，
    /// 且只拦截自身矩形内的输入。
    /// </summary>
    public partial class TimingEditorPanel : VisibilityContainer
    {
        /// <summary>停靠宽度；MainScreen 用它来决定主内容的布局。</summary>
        public const float DockWidth = 440;

        private const float header_bar_height = 44;
        private const float section_header_height = 50;

        /// <summary>固定面板高度（设计稿 768 减去顶栏）；内容超出时由滚动视口承接。</summary>
        private const float panel_height = 768 - UiLayout.TopBarHeight;

        private static readonly Color4 selected_colour = new(255, 102, 171, 110);
        private static readonly Color4 kiai_colour = new(255, 102, 171, 255);

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        private readonly FillFlowContainer<TimingRow> rowFlow;

        private readonly FormTextBox timeBox;
        private readonly RoundedButton useCurrentTimeButton;
        private readonly OsuPlayerButton deleteButton;
        private readonly OsuCheckbox? timingCardCheckbox;
        private readonly TimingAdjustmentControl offsetControl;
        private readonly TimingAdjustmentControl bpmControl;
        private readonly TapTimingButton tapButton;
        private readonly LabelledTimeSignature timeSignature;
        private readonly FormCheckBox kiaiCheck;
        private readonly MetronomeDisplay metronome;

        private readonly SpriteText subtitle;
        private readonly Container headerBar;

        private List<BeatTimingLine> lines = [];
        private int selectedIndex;

        /// <summary>Guards the change handlers while the editor is being populated from a row.</summary>
        private bool rebinding;

        /// <summary>Hash of the song the rows were built from; edits after a switch are refused.</summary>
        private string openedForHash = string.Empty;

        public TimingEditorPanel()
        {
            // 右侧分屏：贴齐顶栏下方、固定在窗口右边，因此没有圆角/阴影，也不可拖动。
            Anchor = Anchor.TopRight;
            Origin = Anchor.TopRight;
            X = DockWidth;
            Y = UiLayout.TopBarHeight;
            Width = DockWidth;
            Height = panel_height;
            Masking = true;

            // osu! 编辑器的基准增量：Offset 每次 ±1ms，BPM 每次 ±0.1。
            offsetControl = new TimingAdjustmentControl(1)
            {
                Caption = "Offset",
                Format = "0.###",
            };
            offsetControl.ValueChanged += _ => applyOffset();

            bpmControl = new TimingAdjustmentControl(0.1)
            {
                Caption = "BPM",
            };
            bpmControl.ValueChanged += _ => applyBpm();

            tapButton = new TapTimingButton
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            };
            tapButton.TimingChanged += result =>
            {
                AppLog.Ui.Add($"tap the beat：首拍 {result.FirstBeatMs:0.#}ms"
                              + (result.HasBpm ? $"，BPM {result.Bpm:0.##}" : "（第一拍）")
                              + $"；可编辑={editing}，Offset 禁用={offsetControl.Current.Disabled}，"
                              + $"BPM 禁用={bpmControl.Current.Disabled}，选中行={selectedIndex}/{lines.Count}",
                    LogLevel.Important);

                // 第一次敲击把首拍定到敲下去的位置；之后每次敲击既刷新 BPM，也把首拍修正到对齐后的拍点。
                // 效果线没有 BPM，此时控件是禁用态，赋值会抛异常。
                if (!offsetControl.Current.Disabled)
                    offsetControl.Current.Value = Math.Round(result.FirstBeatMs, 1);

                if (result.HasBpm && !bpmControl.Current.Disabled)
                    bpmControl.Current.Value = result.Bpm;
            };

            timeSignature = new LabelledTimeSignature
            {
                Label = "Time Signature",
            };
            timeSignature.Current.BindValueChanged(_ => commitTimeSignature());

            kiaiCheck = new FormCheckBox
            {
                Caption = "Kiai Time",
                RelativeSizeAxes = Axes.X,
            };
            kiaiCheck.Current.BindValueChanged(_ => commitKiai());

            var timingCard = new SectionCard("Timing", true);
            timingCardCheckbox = timingCard.Checkbox;
            timingCardCheckbox!.Current.BindValueChanged(_ => commitUninherited());

            var effectCard = new SectionCard("Effect", false);

            timeBox = new FormTextBox
            {
                Caption = "Time",
                RelativeSizeAxes = Axes.X,
                SelectAllOnFocus = true,
            };
            timeBox.OnCommit += (_, isNew) =>
            {
                if (isNew && double.TryParse(timeBox.Current.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double time))
                    changeSelectedTime(time);
                else
                    rebindFields();
            };

            useCurrentTimeButton = new RoundedButton
            {
                Text = "Use current time",
                // 只能用 X 相对：RoundedButton 自带 Height=40（绝对），一旦把 Y 也设成相对，
                // 40 会被当成「父容器高度的 40 倍」，按钮会被撑到几千像素高（文字被挤到屏幕外）。
                RelativeSizeAxes = Axes.X,
                Action = () =>
                {
                    AppLog.Ui.Add("timing 编辑：使用当前播放位置作为时间");
                    changeSelectedTime(player.CurrentTimeMs.Value);
                },
            };

            // osu! ControlPointList 底部的红色「-」：删除当前选中的控制点。
            deleteButton = new OsuPlayerButton
            {
                Width = 40,
                Height = 40,
                Text = "−",
                BackgroundColour = new Color4(202, 32, 32, 255),
                Action = deleteSelected,
            };

            var stopButton = new InlineIconButton(FontAwesome.Solid.Stop, Anchor.TopLeft)
            {
                Action = () =>
                {
                    if (lines.Count == 0)
                        return;

                    AppLog.Ui.Add("timing 编辑：停止并回到选中时间");
                    player.StopAt(lines[selectedIndex].TimeMs);
                },
            };

            var playButton = new InlineIconButton(FontAwesome.Solid.Play, Anchor.BottomLeft)
            {
                Action = () =>
                {
                    if (lines.Count == 0)
                        return;

                    AppLog.Ui.Add("timing 编辑：从选中时间开始播放");
                    player.PlayFrom(lines[selectedIndex].TimeMs);
                },
            };

            metronome = new MetronomeDisplay
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            };

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(20, 20, 28, 250),
                },
                // 与主内容之间的分界线，强调这是分屏而不是浮窗。
                new Box
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                    Width = 1,
                    RelativeSizeAxes = Axes.Y,
                    Colour = new Color4(255, 255, 255, 0.10f),
                },
                // 面板高度固定，内容超出时此前会被 Masking 直接裁掉且无法滚动；滚轮还会穿透到主界面
                // 去滚动歌词。整块内容改放进滚动视口，顶栏固定在视口之上、不随内容滚动。
                new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Y = header_bar_height,
                    Height = panel_height - header_bar_height,
                    ScrollbarOverlapsContent = false,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding { Bottom = 16 },
                        Children = new Drawable[]
                        {
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 12),
                            Padding = new MarginPadding { Horizontal = 12, Top = 12 },
                            Children = new Drawable[]
                            {
                                subtitle = new SpriteText
                                {
                                    Font = OsuFont.GetFont(size: 16),
                                    Colour = Color4.LightGray,
                                    Text = string.Empty,
                                },
                                sectionHeader("控制点"),
                                new OsuScrollContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    Height = 132,
                                    ScrollbarOverlapsContent = false,
                                    Child = rowFlow = new FillFlowContainer<TimingRow>
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new Vector2(0, 2),
                                    },
                                },

                                // GroupSection：Time 文本框，以及「删除 + Use current time」一行
                                // （删除按钮对应 osu! ControlPointList 的红色「-」）。
                                timeBox,
                                new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    Height = 40,
                                    Children = new Drawable[]
                                    {
                                        deleteButton,
                                        new Container
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Padding = new MarginPadding { Left = 48 },
                                            Child = useCurrentTimeButton,
                                        },
                                    },
                                },

                                // TimingSection：节拍器 → Offset → BPM → Tap/播放停止 → Time Signature。
                                timingCard,
                                offsetControl,
                                new OsuTextFlowContainer(t => t.Font = t.Font.With(size: 13))
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                }.With(t => t.AddText("增大 offset 以抵消节拍器提前，减小 offset 以抵消节拍器滞后",
                                    c => c.Colour = Color4.Gray)),
                                bpmControl,
                                new GridContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    ColumnDimensions = new[]
                                    {
                                        new Dimension(GridSizeMode.Relative, 0.70f),
                                        new Dimension(GridSizeMode.Relative, 0.30f),
                                    },
                                    RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                                    Content = new[]
                                    {
                                        new Drawable[]
                                        {
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                Height = TapTimingButton.SIZE,
                                                Child = tapButton,
                                            },
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                Padding = new MarginPadding { Vertical = 8 },
                                                Child = new Container
                                                {
                                                    Anchor = Anchor.Centre,
                                                    Origin = Anchor.Centre,
                                                    RelativeSizeAxes = Axes.Both,
                                                    Width = 0.92f,
                                                    Masking = true,
                                                    CornerRadius = 12,
                                                    Children = new Drawable[] { stopButton, playButton },
                                                },
                                            },
                                        },
                                    },
                                },
                                timeSignature,

                                // EffectSection：Kiai Time（我们的模型没有独立的效果点，所以卡片不带勾选框）。
                                effectCard,
                                kiaiCheck,

                                new OsuPlayerButton
                                {
                                    Width = 240,
                                    Height = 40,
                                    Text = "在当前位置新增 Timing",
                                    Action = addTiming,
                                },
                                new OsuTextFlowContainer(t => t.Font = t.Font.With(size: 14))
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                }.With(t => t.AddText("红线 = 变速，绿线 = 效果；输入/敲击立即生效", c => c.Colour = Color4.Gray)),
                            },
                        },
                    },
                },
                },
                // 顶栏最后加入并绘制在最上层，固定不随内容滚动。
                headerBar = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = header_bar_height,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = new Color4(28, 28, 38, 255),
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            X = 16,
                            Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold),
                            Text = "Timing 编辑",
                        },
                        new UiTextButton("关闭", Hide, 18)
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Margin = new MarginPadding { Right = 10 },
                        },
                    },
                },
            };

            // 节拍器放在 Timing 卡片的内容里（osu! 里它就在 TapTimingControl 的第一行，自带尺寸）。
            timingCard.Flow.Add(metronome);

            // osu! 的 MetronomeDisplay 自带 click 样本，但默认静音（EnableClicking=false）。
            // 这里跟随分屏可见性开声：隐藏时它仍在更新，若不关掉会在面板外一直听到节拍声。
            State.BindValueChanged(v => metronome.EnableClicking = v.NewValue == Visibility.Visible, true);
        }

        /// <summary>Small pink caption with a hairline divider, used to break the editor into sections.</summary>
        private static Drawable sectionHeader(string text) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Font = OsuFont.GetFont(size: 15, weight: FontWeight.SemiBold),
                    Colour = new Color4(255, 102, 171, 235),
                    Text = text,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Colour = new Color4(255, 255, 255, 0.12f),
                },
            },
        };

        /// <summary>Opens the panel on the given timeline, rebuilding the rows for it.</summary>
        public void Open(BeatmapTiming timing)
        {
            lines = timing.Lines.OrderBy(l => l.TimeMs).ToList();
            selectedIndex = 0;
            openedForHash = player.CurrentSong.Value?.Hash ?? string.Empty;

            subtitle.Text = player.CurrentSong.Value?.DisplayText ?? string.Empty;

            rebuildRows();
            rebindFields();

            this.Show();
        }

        protected override void PopIn()
        {
            // 从右侧滑入（分屏展开），不遮罩屏幕也不拦截面板以外的输入。
            this.FadeIn(160, Easing.OutQuint);
            this.MoveToX(0, 260, Easing.OutQuint);
        }

        protected override void PopOut()
        {
            this.FadeOut(160, Easing.OutQuint);
            this.MoveToX(DockWidth, 220, Easing.OutQuint);
        }

        /// <summary>Hidden panels must be completely transparent to input.</summary>
        public override bool PropagatePositionalInputSubTree => State.Value == Visibility.Visible;

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => State.Value == Visibility.Visible && base.ReceivePositionalInputAt(screenSpacePos);

        /// <summary>
        /// 面板内的滚轮一律吞掉：内容由上面的滚动视口处理，落在顶栏等空白处的滚轮也不能穿透到
        /// 主界面去滚动歌词。
        /// </summary>
        protected override bool OnScroll(ScrollEvent e) => State.Value == Visibility.Visible;

        /// <summary>
        /// 面板可见时吞掉未被控件处理的鼠标按下/点击。否则点在卡片空白处（以及 tap-the-beat 圆环
        /// 外圈）会继续下发到主界面，命中歌词扇面触发跳转。
        /// </summary>
        protected override bool OnMouseDown(MouseDownEvent e) => State.Value == Visibility.Visible;

        protected override bool OnClick(ClickEvent e) => State.Value == Visibility.Visible;

        // -------- 行列表 --------

        private void rebuildRows()
        {
            rowFlow.Clear();

            for (int i = 0; i < lines.Count; i++)
            {
                int index = i;
                rowFlow.Add(new TimingRow(lines[i]) { Action = () => select(index) });
            }

            updateRowSelection();
        }

        private void updateRowSelection()
        {
            for (int i = 0; i < rowFlow.Count; i++)
                rowFlow[i].Selected = i == selectedIndex;

            updateDeleteButton();
        }

        private void select(int index)
        {
            if (index == selectedIndex)
                return;

            selectedIndex = index;
            AppLog.Ui.Add($"timing 编辑：选中第 {index + 1} 个时间点 {formatTime(lines[index].TimeMs)}");
            updateRowSelection();
            rebindFields();
        }

        /// <summary>Writes the selected line's values into every control without re-triggering the editors.</summary>
        private void rebindFields()
        {
            if (lines.Count == 0)
                return;

            selectedIndex = Math.Clamp(selectedIndex, 0, lines.Count - 1);
            BeatTimingLine line = lines[selectedIndex];

            rebinding = true;

            offsetControl.Current.Value = line.TimeMs;
            timeBox.Current.Value = $"{line.TimeMs:n0}";

            // 禁用中的 bindable 不允许赋值（会抛 InvalidOperationException），
            // 所以先解开禁用、写入数值，再按行类型恢复禁用态。
            bpmControl.Current.Disabled = false;
            bpmControl.Current.Value = BeatmapTiming.BpmOf(line);
            bpmControl.Current.Disabled = !line.Uninherited;

            timeSignature.Current.Value = new TimeSignature(Math.Clamp(line.Meter, 1, 32));
            kiaiCheck.Current.Value = line.Kiai;

            // 第一根线是整首歌的基准，不允许取消（对应 osu! 对首个控制点的处理）。
            timingCardCheckbox!.Current.Disabled = false;
            timingCardCheckbox.Current.Value = line.Uninherited;
            timingCardCheckbox.Current.Disabled = selectedIndex == 0;

            rebinding = false;
        }

        // -------- 编辑 --------

        private bool editing => !rebinding && selectedIndex >= 0 && selectedIndex < lines.Count;

        /// <summary>Offset edit: moves the selected control point, reshuffling the list order.</summary>
        private void applyOffset()
        {
            if (!editing)
                return;

            changeSelectedTime(offsetControl.Current.Value);
        }

        /// <summary>Moves the selected control point to <paramref name="time"/> (Offset / Time box / Use current time).</summary>
        private void changeSelectedTime(double time)
        {
            if (!editing)
                return;

            lines[selectedIndex] = lines[selectedIndex] with { TimeMs = time };

            BeatTimingLine edited = lines[selectedIndex];

            sortAndReselect(edited);
            publish();
            rebuildRows();
            rebindFields();
        }

        /// <summary>BPM edit: only tempo lines carry a beat length.</summary>
        private void applyBpm()
        {
            if (!editing)
                return;

            BeatTimingLine line = lines[selectedIndex];

            if (!line.Uninherited)
                return;

            double bpm = bpmControl.Current.Value;

            if (bpm <= 0)
                return;

            lines[selectedIndex] = line with { BeatLengthMs = 60000.0 / bpm };

            publish();
            rebuildRows();
        }

        private void commitTimeSignature()
        {
            if (!editing)
                return;

            lines[selectedIndex] = lines[selectedIndex] with { Meter = Math.Clamp(timeSignature.Current.Value.Numerator, 1, 32) };

            publish();
            rebuildRows();
        }

        private void commitKiai()
        {
            if (!editing)
                return;

            lines[selectedIndex] = lines[selectedIndex] with { Kiai = kiaiCheck.Current.Value };

            publish();
            rebuildRows();
        }

        /// <summary>
        /// Timing 卡片勾选框：取消后该行变成效果线（绿线，只带 Kiai / 拍号而不改 BPM），
        /// 对应 osu! 里从控制点组中移除 TimingControlPoint。
        /// </summary>
        private void commitUninherited()
        {
            if (!editing)
                return;

            bool uninherited = timingCardCheckbox!.Current.Value;

            if (!uninherited && selectedIndex == 0)
                return;

            lines[selectedIndex] = lines[selectedIndex] with { Uninherited = uninherited };

            publish();
            rebuildRows();
            rebindFields();
        }

        /// <summary>Adds a tempo point at the current playback position, inheriting the active BPM/kiai.</summary>
        private void addTiming()
        {
            if (!isStillCurrent())
                return;

            double time = Math.Round(player.CurrentTimeMs.Value);

            // 以播放位置之前最近的红线为模板，保证新增点不改变原本的节拍网格。
            BeatTimingLine? reference = lines.Where(l => l.Uninherited && l.TimeMs <= time).LastOrDefault()
                                      ?? lines.FirstOrDefault(l => l.Uninherited);

            bool kiai = lines.Where(l => l.TimeMs <= time).Select(l => (bool?)l.Kiai).LastOrDefault() ?? false;

            lines.Add(new BeatTimingLine(time, reference?.BeatLengthMs ?? 500, reference?.Meter ?? 4, true, kiai));

            BeatTimingLine added = lines[^1];

            sortAndReselect(added);
            publish();
            rebuildRows();
            rebindFields();
        }

        private void sortAndReselect(BeatTimingLine target)
        {
            lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            selectedIndex = Math.Max(0, lines.IndexOf(target));
        }

        /// <summary>
        /// 删除当前选中的控制点（对应 osu! ControlPointList 的红色「-」）。
        /// 至少要留下一个控制点、且至少留一条红线（tempo），否则节拍网格就没有基准了。
        /// </summary>
        private void deleteSelected()
        {
            if (!editing || lines.Count == 0)
                return;

            BeatTimingLine target = lines[selectedIndex];

            if (lines.Count <= 1)
            {
                AppLog.Ui.Add("timing 编辑：至少要保留一个控制点，跳过删除");
                return;
            }

            if (target.Uninherited && lines.Count(l => l.Uninherited) <= 1)
            {
                AppLog.Ui.Add("timing 编辑：至少要保留一条红线（tempo），跳过删除");
                return;
            }

            lines.RemoveAt(selectedIndex);
            selectedIndex = Math.Clamp(selectedIndex, 0, lines.Count - 1);

            AppLog.Ui.Add($"timing 编辑：删除时间点 {formatTime(target.TimeMs)}"
                          + $"（{(target.Uninherited ? "变速" : "效果")}），剩余 {lines.Count} 个", LogLevel.Important);

            publish();
            rebuildRows();
            rebindFields();
        }

        /// <summary>删除按钮只在「删了以后仍至少剩一个控制点且仍有红线」时可用。</summary>
        private void updateDeleteButton()
            => deleteButton.Enabled.Value = editing
                                            && lines.Count > 1
                                            && !(lines[selectedIndex].Uninherited && lines.Count(l => l.Uninherited) <= 1);

        /// <summary>Publishes the edited timeline; refuses edits once the song has moved on.</summary>
        private void publish()
        {
            if (!isStillCurrent())
                return;

            player.ApplyTiming(new BeatmapTiming(lines.ToList()));
        }

        private bool isStillCurrent()
        {
            if (player.CurrentSong.Value?.Hash == openedForHash)
                return true;

            AppLog.Ui.Add("曲目已切换，关闭 timing 编辑面板");
            Hide();
            return false;
        }

        private static string formatTime(double ms)
        {
            bool negative = ms < 0;
            var span = TimeSpan.FromMilliseconds(Math.Abs(ms));

            return $"{(negative ? "-" : string.Empty)}{(int)span.TotalMinutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
        }

        /// <summary>
        /// 移植 osu! 编辑器 Timing 页的 <c>Section&lt;T&gt;</c>：圆角卡片 + 50px 标题行 + 内容区。
        /// <see cref="Checkbox"/> 非空时标题行是一枚勾选框（与 osu! 一致，勾选框自带标签文字）。
        /// </summary>
        private partial class SectionCard : CompositeDrawable
        {
            public readonly OsuCheckbox? Checkbox;

            public readonly FillFlowContainer Flow;

            public SectionCard(string title, bool withCheckbox)
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
                Masking = true;
                CornerRadius = 5;

                Checkbox = withCheckbox ? new OsuCheckbox() : null;

                var headerChildren = new List<Drawable>();

                if (Checkbox is not null)
                {
                    Checkbox.LabelText = title;
                    Checkbox.Anchor = Anchor.CentreLeft;
                    Checkbox.Origin = Anchor.CentreLeft;
                    headerChildren.Add(Checkbox);
                }
                else
                {
                    headerChildren.Add(new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Font = OsuFont.GetFont(size: 16, weight: FontWeight.SemiBold),
                        Text = title,
                    });
                }

                InternalChildren = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(28, 28, 38, 255),
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = section_header_height,
                        Padding = new MarginPadding { Horizontal = 10 },
                        Children = headerChildren.ToArray(),
                    },
                    new Container
                    {
                        Y = section_header_height,
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Child = Flow = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 10),
                            Padding = new MarginPadding { Horizontal = 10, Bottom = 10 },
                        },
                    },
                };
            }
        }

        /// <summary>osu! TapTimingControl 里的 InlineButton：整块底色 + 居中图标。</summary>
        private partial class InlineIconButton : OsuButton
        {
            private readonly IconUsage icon;
            private readonly Anchor anchor;

            private SpriteIcon spriteIcon = null!;

            [Resolved]
            private OverlayColourProvider colours { get; set; } = null!;

            public InlineIconButton(IconUsage icon, Anchor anchor)
            {
                this.icon = icon;
                this.anchor = anchor;

                RelativeSizeAxes = Axes.Both;
                Height = 0.49f;
                Anchor = anchor;
                Origin = anchor;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                Content.CornerRadius = 0;
                Content.Masking = false;

                BackgroundColour = colours.Background2;

                Add(new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(15),
                    Child = spriteIcon = new SpriteIcon
                    {
                        Icon = icon,
                        Size = new Vector2(22),
                        Anchor = anchor,
                        Origin = anchor,
                        Colour = colours.Background1,
                    },
                });
            }

            protected override bool OnHover(HoverEvent e)
            {
                spriteIcon.FadeColour(colours.Content2, 200, Easing.OutQuint);
                return base.OnHover(e);
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                spriteIcon.FadeColour(colours.Background1, 200, Easing.OutQuint);
                base.OnHoverLost(e);
            }
        }

        /// <summary>One control point row: time, BPM (tempo lines only), kind and the kiai flag.</summary>
        private partial class TimingRow : OsuClickableContainer
        {
            private readonly Box background;

            public bool Selected
            {
                set => background.FadeTo(value ? 1 : IsHovered ? 0.3f : 0, 120);
            }

            public TimingRow(BeatTimingLine line)
            {
                RelativeSizeAxes = Axes.X;
                Height = 28;

                Children = new Drawable[]
                {
                    background = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = selected_colour,
                        Alpha = 0,
                    },
                    makeText(8, formatTime(line.TimeMs), Color4.White),
                    makeText(170, line.Uninherited ? BeatmapTiming.BpmOf(line).ToString("0.##") : "—", Color4.White),
                    makeText(280, line.Uninherited ? "变速" : "效果", Color4.Gray),
                    makeText(360, line.Kiai ? "KIAI" : string.Empty, kiai_colour),
                };
            }

            private static SpriteText makeText(float x, string text, Color4 colour) => new()
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                X = x,
                Font = OsuFont.GetFont(size: 14),
                Colour = colour,
                Text = text,
            };
        }
    }
}
