// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Trimmed copy of osu!'s MenuSideFlashes: skin/user/supporter hooks removed,
// the glow colour is fixed to osu! blue. Beat/kiai flash logic is unchanged.

#nullable disable

using osu.Framework.Audio.Track;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Graphics.Containers;
using osuTK.Graphics;

namespace OsuPlayer.Game.Graphics.Effects
{
    public partial class MenuSideFlashes : BeatSyncedContainer
    {
        protected virtual float Intensity => 2;

        private Box leftBox;
        private Box rightBox;

        private const float amplitude_dead_zone = 0.25f;
        private const float alpha_multiplier = (1 - amplitude_dead_zone) / 0.55f;
        private const float kiai_multiplier = (1 - amplitude_dead_zone * 0.95f) / 0.8f;

        private const int box_max_alpha = 200;
        private const double box_fade_in_time = 65;
        private const int box_width = 200;

        public MenuSideFlashes()
        {
            EarlyActivationMilliseconds = box_fade_in_time;

            RelativeSizeAxes = Axes.Both;
            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
        }

        [osu.Framework.Allocation.BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                leftBox = new Box
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.Y,
                    Width = box_width * Intensity,
                    Height = 1.5f,
                    // align off-screen to make sure our edges don't become visible during parallax.
                    X = -box_width,
                    Alpha = 0,
                    Blending = BlendingParameters.Additive
                },
                rightBox = new Box
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    RelativeSizeAxes = Axes.Y,
                    Width = box_width * Intensity,
                    Height = 1.5f,
                    X = box_width,
                    Alpha = 0,
                    Blending = BlendingParameters.Additive
                }
            };

            updateColour();
        }

        protected override void OnNewBeat(int beatIndex, TimingControlPoint timingPoint, EffectControlPoint effectPoint, ChannelAmplitudes amplitudes)
        {
            if (beatIndex < 0)
                return;

            if (effectPoint.KiaiMode ? beatIndex % 2 == 0 : beatIndex % timingPoint.TimeSignature.Numerator == 0)
                flash(leftBox, timingPoint.BeatLength, effectPoint.KiaiMode, amplitudes);
            if (effectPoint.KiaiMode ? beatIndex % 2 == 1 : beatIndex % timingPoint.TimeSignature.Numerator == 0)
                flash(rightBox, timingPoint.BeatLength, effectPoint.KiaiMode, amplitudes);
        }

        private void flash(Drawable d, double beatLength, bool kiai, ChannelAmplitudes amplitudes)
        {
            d.FadeTo(Math.Clamp(0.1f + ((ReferenceEquals(d, leftBox) ? amplitudes.LeftChannel : amplitudes.RightChannel) - amplitude_dead_zone) / (kiai ? kiai_multiplier : alpha_multiplier), 0.1f, 1),
                 box_fade_in_time)
             .Then()
             .FadeOut(beatLength, Easing.In);
        }

        // osu!'s signature menu blue (OsuColour.Blue).
        protected virtual Color4 GetBaseColour() => new Color4(102, 204, 255, 255);

        private void updateColour()
        {
            Color4 baseColour = GetBaseColour();
            // linear colour looks better in this case, so let's use it for now.
            Color4 gradientDark = baseColour.Opacity(0).ToLinear();
            Color4 gradientLight = baseColour.Opacity(0.6f).ToLinear();

            leftBox.Colour = ColourInfo.GradientHorizontal(gradientLight, gradientDark);
            rightBox.Colour = ColourInfo.GradientHorizontal(gradientDark, gradientLight);
        }
    }
}
