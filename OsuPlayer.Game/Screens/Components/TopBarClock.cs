using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 顶栏时钟，逐行移植自 osu! 的 <c>osu.Game.Overlays.Toolbar.ToolbarClock</c>
    /// （ppy/osu, MIT）：同样的悬停/闪光底板、22px 指针盘 + 数字时间布局，
    /// 以及点击在 Analog → Full → DigitalWithRuntime → Digital 之间循环的四种显示模式。
    /// 唯一差别是没有把显示模式与 24 小时制写进配置，而是本地保存（默认值与 osu! 一致：
    /// Full，24 小时制按当前区域短时间格式里是否含 "tt" 推断）。
    /// </summary>
    public partial class TopBarClock : OsuClickableContainer
    {
        /// <summary>osu! 的 <c>ToolbarButton.PADDING</c>。</summary>
        private const float padding = 3;

        private Box hoverBackground = null!;
        private Box flashBackground = null!;

        private DigitalClockDisplay digital = null!;
        private AnalogClockDisplay analog = null!;

        private DisplayMode displayMode = DisplayMode.Full;

        /// <summary>对应 osu! 的 <c>OsuSetting.Prefer24HourTime</c> 默认值。</summary>
        private readonly bool prefer24HourTime =
            !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("tt");

        /// <summary>osu! 的 <c>ToolbarClockDisplayMode</c>。</summary>
        private enum DisplayMode
        {
            Analog,
            Digital,
            DigitalWithRuntime,
            Full,
        }

        public TopBarClock()
        {
            RelativeSizeAxes = Axes.Y;
            AutoSizeAxes = Axes.X;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    AutoSizeAxes = Axes.X,
                    Padding = new MarginPadding(padding),
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            CornerRadius = 6,
                            CornerExponent = 3f,
                            Children = new Drawable[]
                            {
                                hoverBackground = new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Colour = OsuColour.Gray(80).Opacity(180),
                                    Blending = BlendingParameters.Additive,
                                    Alpha = 0,
                                },
                                flashBackground = new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Alpha = 0,
                                    Colour = Color4.White.Opacity(100),
                                    Blending = BlendingParameters.Additive,
                                },
                            },
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.Y,
                            AutoSizeAxes = Axes.X,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(5),
                            Padding = new MarginPadding(10),
                            Children = new Drawable[]
                            {
                                analog = new AnalogClockDisplay
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                },
                                digital = new DigitalClockDisplay
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                },
                            },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            digital.Use24HourDisplay = prefer24HourTime;
            applyDisplayMode();
        }

        private void applyDisplayMode()
        {
            bool showAnalog = displayMode == DisplayMode.Analog || displayMode == DisplayMode.Full;
            bool showDigital = displayMode != DisplayMode.Analog;
            bool showRuntime = displayMode == DisplayMode.DigitalWithRuntime || displayMode == DisplayMode.Full;

            digital.FadeTo(showDigital ? 1 : 0);
            digital.ShowRuntime = showRuntime;

            analog.FadeTo(showAnalog ? 1 : 0);
        }

        protected override bool OnClick(ClickEvent e)
        {
            flashBackground.FadeOutFromOne(800, Easing.OutQuint);

            cycleDisplayMode();

            return base.OnClick(e);
        }

        protected override bool OnHover(HoverEvent e)
        {
            hoverBackground.FadeIn(200);

            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverBackground.FadeOut(200);

            base.OnHoverLost(e);
        }

        protected override HoverSounds CreateHoverSounds(HoverSampleSet sampleSet) => new HoverClickSounds(sampleSet);

        private void cycleDisplayMode()
        {
            displayMode = displayMode switch
            {
                DisplayMode.Analog => DisplayMode.Full,
                DisplayMode.Digital => DisplayMode.Analog,
                DisplayMode.DigitalWithRuntime => DisplayMode.Digital,
                _ => DisplayMode.DigitalWithRuntime,
            };

            applyDisplayMode();
        }
    }

    /// <summary>移植自 osu! 的 <c>osu.Game.Overlays.Toolbar.ClockDisplay</c>：每秒刷新一次显示。</summary>
    public abstract partial class ClockDisplay : CompositeDrawable
    {
        private int? lastSecond;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            UpdateDisplay(DateTimeOffset.Now);
            FinishTransforms(true);
        }

        protected override void Update()
        {
            base.Update();

            var now = DateTimeOffset.Now;

            if (now.Second != lastSecond)
            {
                lastSecond = now.Second;
                UpdateDisplay(now);
            }
        }

        protected abstract void UpdateDisplay(DateTimeOffset now);
    }

    /// <summary>
    /// 移植自 osu! 的 <c>osu.Game.Overlays.Toolbar.DigitalClockDisplay</c>：
    /// 等宽数字时间 + 下方粉色的「running 运行时长」小字。
    /// </summary>
    public partial class DigitalClockDisplay : ClockDisplay
    {
        private OsuSpriteText realTime = null!;
        private OsuSpriteText gameTime = null!;

        private FillFlowContainer runningText = null!;

        private bool showRuntime = true;

        public bool ShowRuntime
        {
            get => showRuntime;
            set
            {
                if (showRuntime == value)
                    return;

                showRuntime = value;
                updateMetrics();
            }
        }

        private bool use24HourDisplay;

        public bool Use24HourDisplay
        {
            get => use24HourDisplay;
            set
            {
                if (use24HourDisplay == value)
                    return;

                use24HourDisplay = value;

                updateMetrics();
                UpdateDisplay(DateTimeOffset.Now);
            }
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            AutoSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                realTime = new OsuSpriteText
                {
                    Font = OsuFont.Default.With(fixedWidth: true),
                    Spacing = new Vector2(-1.5f, 0),
                },
                runningText = new FillFlowContainer
                {
                    Y = 14,
                    Colour = colours.PinkLight,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(2, 0),
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = "running",
                            Font = OsuFont.Default.With(size: 10, weight: FontWeight.SemiBold),
                        },
                        gameTime = new OsuSpriteText
                        {
                            Font = OsuFont.Default.With(size: 10, fixedWidth: true, weight: FontWeight.SemiBold),
                            Spacing = new Vector2(-0.5f, 0),
                        },
                    },
                },
            };

            updateMetrics();
        }

        protected override void UpdateDisplay(DateTimeOffset now)
        {
            realTime.Text = now.ToString(use24HourDisplay ? @"HH:mm:ss" : @"h:mm:ss tt");
            gameTime.Text = $"{new TimeSpan(TimeSpan.TicksPerSecond * (int)(Clock.CurrentTime / 1000)):c}";
        }

        private void updateMetrics()
        {
            runningText.FadeTo(showRuntime ? 1 : 0);
        }
    }

    /// <summary>
    /// 移植自 osu! 的 <c>osu.Game.Overlays.Toolbar.AnalogClockDisplay</c>：
    /// 22px 白色描边表盘，时针/分针白色、秒针粉色，换点时用 OutElastic 弹一下。
    /// </summary>
    public partial class AnalogClockDisplay : ClockDisplay
    {
        private const float hand_thickness = 2.4f;

        private Drawable hour = null!;
        private Drawable minute = null!;
        private Drawable second = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            Size = new Vector2(22);

            InternalChildren = new Drawable[]
            {
                new CircularContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    BorderThickness = 2,
                    BorderColour = Color4.White,
                    Child = new Box
                    {
                        AlwaysPresent = true,
                        Alpha = 0,
                        RelativeSizeAxes = Axes.Both,
                    },
                },
                hour = new LargeHand(0.34f),
                minute = new LargeHand(0.48f),
                second = new SecondHand(),
                new CentreCircle
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
            };
        }

        protected override void UpdateDisplay(DateTimeOffset now)
        {
            float secondFractional = now.Second / 60f;
            float minuteFractional = (now.Minute + secondFractional) / 60f;
            float hourFractional = ((minuteFractional + now.Hour) % 12) / 12f;

            updateRotation(hour, hourFractional);
            updateRotation(minute, minuteFractional);
            updateRotation(second, secondFractional);
        }

        private void updateRotation(Drawable hand, float fraction)
        {
            const float duration = 320;

            float rotation = fraction * 360 - 90;

            // 指针越过 0° 时先向前转到下一圈再回弹，避免倒着转一整圈。
            if (Math.Abs(hand.Rotation - rotation) > 180)
            {
                float animRotation = rotation;
                while (animRotation < hand.Rotation)
                    animRotation += 180;

                hand.RotateTo(animRotation, duration, Easing.OutElastic)
                    .Then()
                    .RotateTo(rotation);
            }
            else
                hand.RotateTo(rotation, duration, Easing.OutElastic);
        }

        private partial class CentreCircle : CompositeDrawable
        {
            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                InternalChildren = new Drawable[]
                {
                    new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(hand_thickness),
                        Colour = Color4.White,
                    },
                    new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(hand_thickness * 0.7f),
                        Colour = colours.PinkLight,
                    },
                };
            }
        }

        private partial class SecondHand : CompositeDrawable
        {
            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                RelativeSizeAxes = Axes.X;
                Width = 0.66f;

                Height = hand_thickness * 0.7f;
                Anchor = Anchor.Centre;
                Origin = Anchor.Custom;

                OriginPosition = new Vector2(Height * 2, Height / 2);

                InternalChildren = new Drawable[]
                {
                    new Circle
                    {
                        Colour = colours.PinkLight,
                        RelativeSizeAxes = Axes.Both,
                    },
                };
            }
        }

        private partial class LargeHand : CompositeDrawable
        {
            public LargeHand(float length)
            {
                Width = length;
            }

            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                Anchor = Anchor.Centre;
                Origin = Anchor.Custom;

                // x 也偏半个线宽，保证指针中心落在表盘圆心上。
                OriginPosition = new Vector2(hand_thickness / 2);

                Height = hand_thickness;

                InternalChildren = new Drawable[]
                {
                    new Circle
                    {
                        Colour = Color4.White,
                        RelativeSizeAxes = Axes.Both,
                        BorderThickness = 0.7f,
                        BorderColour = colours.Gray2,
                    },
                };

                RelativeSizeAxes = Axes.X;
            }
        }
    }
}
