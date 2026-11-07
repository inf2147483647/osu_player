using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Utils;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Graphics.Containers;
using osu.Game.Screens.Menu;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;
using OsuPlayer.Game.Graphics;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Graphics.NowPlaying
{
    /// <summary>
    /// The main circular record: album cover (or a placeholder icon), a thin outer ring and slow
    /// vinyl rotation while playing.
    ///
    /// Every animation below is a port of osu!'s main-menu logo (<c>OsuLogo</c>), so the disc behaves
    /// exactly like the pink osu! circle:
    ///  - hover scales up, press squashes and release springs back;
    ///  - dragging moves it with a rubber-band feel and it springs back to centre;
    ///  - clicking flashes white;
    ///  - every beat contracts the disc and emits an expanding ripple ring (kiai boosts both);
    ///  - overall scale tracks the track's current loudness;
    ///  - while hovered, each beat plays the osu! heartbeat sample (downbeat plays the heavier one).
    /// </summary>
    public partial class NowPlayingDisc : BeatSyncedContainer
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        /// <summary>osu!'s OsuLogo fires its beat animation this many milliseconds early so it lands on the beat.</summary>
        private const double early_activation = 60;

        private const float scale_adjust_cutoff = 0.4f;

        private const float rotation_period_ms = 24000;

        /// <summary>Random pitch spread of the hover heartbeat, matching osu!'s logo.</summary>
        private const double beat_sample_variance = 0.1;

        private const float hover_scale = 1.1f;
        private const float press_scale = 0.9f;
        private const double hover_transition = 500;
        private const double press_transition = 1000;
        private const double release_transition = 500;

        private const float flash_alpha = 0.4f;
        private const double flash_fade_out = 1500;
        private const float kiai_flash_alpha = 0.2f;

        private const float ripple_alpha = 0.15f;
        private const float ripple_expand = 0.04f;

        /// <summary>波谱常驻透明度（osu! OsuLogo.visualizer_default_alpha），kiai 拍点会短暂提亮。</summary>
        private const float visualiser_default_alpha = 0.5f;

        /// <summary>osu! 主菜单圆饼悬停时的心跳/落点音效。</summary>
        private Sample? sampleBeat;
        private Sample? sampleDownbeat;

        /// <summary>Hover scale (osu!'s logoHoverContainer).</summary>
        private Container hoverContainer = null!;

        /// <summary>Press/drag scale &amp; offset (osu!'s logoBounceContainer).</summary>
        private Container bounceContainer = null!;

        /// <summary>Loudness-driven scale (osu!'s logoAmplitudeContainer), damped every frame.</summary>
        private Container amplitudeContainer = null!;

        /// <summary>Beat-driven scale (osu!'s logoBeatContainer).</summary>
        private Container beatContainer = null!;

        /// <summary>osu! 的 MenuLogoVisualisation：围绕圆饼向外的音频波谱（加色混合）。</summary>
        private LogoVisualisation visualiser = null!;

        /// <summary>Per-beat expanding ring (osu!'s logo ripple).</summary>
        private CircularContainer ripple = null!;

        /// <summary>Additive white flash used on click and during kiai.</summary>
        private Box flashLayer = null!;

        private Container rotationContainer = null!;
        private OnlineSprite cover = null!;
        private SpriteIcon placeholderIcon = null!;

        private int lastBeatIndex;

        public NowPlayingDisc()
        {
            EarlyActivationMilliseconds = early_activation;
        }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            // Same samples osu!'s main-menu logo uses when hovered.
            sampleBeat = audio.Samples.Get(@"Menu/osu-logo-heartbeat");
            sampleDownbeat = audio.Samples.Get(@"Menu/osu-logo-downbeat");

            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
            RelativeSizeAxes = Axes.Both;

            Child = hoverContainer = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.Both,
                Child = bounceContainer = new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        // Sits behind the disc so the ring appears to expand out from underneath it.
                        ripple = new CircularContainer
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            BorderThickness = 4,
                            BorderColour = Color4.White,
                            Alpha = 0,
                            Child = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Alpha = 0,
                                AlwaysPresent = true,
                            },
                        },
                        amplitudeContainer = new Container
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            RelativeSizeAxes = Axes.Both,
                            Child = beatContainer = new Container
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                RelativeSizeAxes = Axes.Both,
                                Children = new Drawable[]
                                {
                                    // osu! 主菜单：波谱与圆饼同尺寸、画在圆饼之下，条从圆周向外长。
                                    visualiser = new LogoVisualisation
                                    {
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        RelativeSizeAxes = Axes.Both,
                                        Alpha = visualiser_default_alpha,
                                    },
                                    new CircularContainer
                                    {
                                    Anchor = Anchor.Centre,
                                    Origin = Anchor.Centre,
                                    RelativeSizeAxes = Axes.Both,
                                    Masking = true,
                                    BorderThickness = 5,
                                    BorderColour = new Color4(235, 235, 245, 255),
                                    Children = new Drawable[]
                                    {
                                        new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Colour = new Color4(30, 30, 42, 255),
                                        },
                                        // 只有封面参与自转/跟随歌词旋转，外圈边框与占位图标保持静止。
                                        rotationContainer = new Container
                                        {
                                            Anchor = Anchor.Centre,
                                            Origin = Anchor.Centre,
                                            RelativeSizeAxes = Axes.Both,
                                            Child = cover = new OnlineSprite(player.OnlineTextures)
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                Anchor = Anchor.Centre,
                                                Origin = Anchor.Centre,
                                                FillMode = FillMode.Fill,
                                                Alpha = 0,
                                            },
                                        },
                                        placeholderIcon = new SpriteIcon
                                        {
                                            Anchor = Anchor.Centre,
                                            Origin = Anchor.Centre,
                                            Size = new Vector2(110),
                                            Icon = FontAwesome.Solid.Music,
                                            Colour = new Color4(90, 90, 110, 255),
                                        },
                                        // Clipped to the circle, like osu!'s flash layer.
                                        flashLayer = new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Blending = BlendingParameters.Additive,
                                            Colour = Color4.White,
                                            Alpha = 0,
                                        },
                                    },
                                },
                                },
                            },
                        },
                    },
                },
            };

            player.CurrentCover.BindValueChanged(onCoverChanged, true);
        }

        private void onCoverChanged(ValueChangedEvent<string> e)
        {
            if (string.IsNullOrEmpty(e.NewValue))
            {
                cover.Alpha = 0;
                placeholderIcon.Alpha = 1;
                cover.SetUrl(null);
            }
            else
            {
                placeholderIcon.Alpha = 0;
                cover.Alpha = 1;
                cover.SetUrl(e.NewValue);
            }
        }

        /// <summary>
        /// 「跟随歌词」模式：由主界面按歌词扇形滚动驱动，只让饼内封面绕中心旋转，
        /// 外圈边框、波谱与占位图标保持静止。
        /// </summary>
        public void RotateCoverTo(float angle, double duration, Easing easing)
            => rotationContainer.RotateTo(angle, duration, easing);

        protected override void Update()
        {
            base.Update();

            // 匀速模式才自转；「跟随歌词」模式由主界面按歌词扇形滚动驱动，两种旋转不叠加。
            if (player.CoverRotation.Value == CoverRotationMode.Constant && player.IsPlaying.Value)
                rotationContainer.Rotation += (float)(360 * Time.Elapsed / rotation_period_ms);

            // osu! OsuLogo.Update: the disc shrinks slightly while the track is loud, damped smoothly.
            float maxAmplitude = lastBeatIndex >= 0 ? BeatSyncSource.CurrentAmplitudes.Maximum : 0;

            amplitudeContainer.Scale = new Vector2((float)Interpolation.Damp(
                amplitudeContainer.Scale.X,
                1 - Math.Max(0, maxAmplitude - scale_adjust_cutoff) * 0.04f,
                0.9f,
                Time.Elapsed));
        }

        protected override void OnNewBeat(int beatIndex, TimingControlPoint timingPoint, EffectControlPoint effectPoint, ChannelAmplitudes amplitudes)
        {
            base.OnNewBeat(beatIndex, timingPoint, effectPoint, amplitudes);

            lastBeatIndex = beatIndex;

            if (beatIndex < 0)
                return;

            double beatLength = timingPoint.BeatLength;
            float amplitudeAdjust = Math.Min(1, 0.4f + amplitudes.Maximum);

            // osu! logo pulse: contract on the beat, then expand back over two beat lengths.
            beatContainer
                .ScaleTo(1 - 0.02f * amplitudeAdjust, early_activation, Easing.Out).Then()
                .ScaleTo(1, beatLength * 2, Easing.OutQuint);

            // Ripple ring: jump to the loudness-scaled size, then expand and fade over one beat.
            ripple.ClearTransforms();
            ripple
                .ScaleTo(amplitudeContainer.Scale)
                .ScaleTo(amplitudeContainer.Scale * (1 + ripple_expand * amplitudeAdjust), beatLength, Easing.OutQuint)
                .FadeTo(ripple_alpha * amplitudeAdjust).FadeOut(beatLength, Easing.OutQuint);

            // Kiai raises the flash baseline (osu! also brightens the logo visualiser here).
            if (effectPoint.KiaiMode && flashLayer.Alpha < flash_alpha)
            {
                flashLayer.ClearTransforms();
                flashLayer
                    .FadeTo(kiai_flash_alpha * amplitudeAdjust, early_activation, Easing.Out).Then()
                    .FadeOut(beatLength);

                // 与 osu! 一致：kiai 拍点上波谱先按响度提亮，再落回常驻透明度。
                visualiser.ClearTransforms();
                visualiser
                    .FadeTo(visualiser_default_alpha * 1.8f * amplitudeAdjust, early_activation, Easing.Out).Then()
                    .FadeTo(visualiser_default_alpha, beatLength);
            }

            // osu! OsuLogo: while the logo is hovered each beat plays a heartbeat, and the measure
            // downbeat plays the heavier landing sound instead. Sampled at the real beat time, since
            // OnNewBeat fires `early_activation` milliseconds ahead of it.
            if (IsHovered)
            {
                bool downbeat = beatIndex % timingPoint.TimeSignature.Numerator == 0;

                this.Delay(early_activation).Schedule(() =>
                {
                    if (downbeat)
                    {
                        sampleDownbeat?.Play();
                    }
                    else if (sampleBeat is not null)
                    {
                        var channel = sampleBeat.GetChannel();
                        channel.Frequency.Value = 1 - beat_sample_variance / 2 + RNG.NextDouble(beat_sample_variance);
                        channel.Play();
                    }
                });
            }
        }

        protected override bool OnHover(HoverEvent e)
        {
            hoverContainer.ScaleTo(hover_scale, hover_transition, Easing.OutElastic);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverContainer.ScaleTo(1, hover_transition, Easing.OutElastic);
            base.OnHoverLost(e);
        }

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (e.Button != MouseButton.Left)
                return true;

            bounceContainer.ScaleTo(press_scale, press_transition, Easing.Out);
            return true;
        }

        protected override void OnMouseUp(MouseUpEvent e)
        {
            if (e.Button != MouseButton.Left)
                return;

            bounceContainer.ScaleTo(1, release_transition, Easing.OutElastic);
        }

        protected override bool OnClick(ClickEvent e)
        {
            flashLayer.ClearTransforms();
            flashLayer.Alpha = flash_alpha;
            flashLayer.FadeOut(flash_fade_out, Easing.OutExpo);

            return true;
        }

        public override bool DragBlocksClick => false;

        protected override bool OnDragStart(DragStartEvent e) => true;

        protected override void OnDrag(DragEvent e)
        {
            Vector2 change = e.MousePosition - e.MouseDownPosition;

            // Diminish the drag distance as we go further to simulate "rubber band" feeling.
            change *= change.Length <= 0 ? 0 : MathF.Pow(change.Length, 0.6f) / change.Length;

            bounceContainer.MoveTo(change);
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            bounceContainer.MoveTo(Vector2.Zero, 800, Easing.OutElastic);
            base.OnDragEnd(e);
        }
    }
}
