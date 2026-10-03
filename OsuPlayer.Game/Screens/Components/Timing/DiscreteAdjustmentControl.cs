using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Screens.Edit.Timing;

namespace OsuPlayer.Game.Screens.Components.Timing
{
    /// <summary>
    /// Port of osu! editor's <c>DiscreteAdjustmentControl</c>: a click-and-hold pad whose horizontal
    /// bands add 1x / 2x / 5x / 10x of the base increment (that is the -4…4 wedge with the floating
    /// "+x" readout in the editor). Only the editor's undo hooks were dropped - the interaction
    /// (band multipliers, hover readout, hold-to-repeat with the notch sample) is unchanged.
    /// </summary>
    public partial class DiscreteAdjustmentControl : CompositeDrawable
    {
        /// <summary>Invoked with the signed increment to apply.</summary>
        public Action<double>? Action;

        private const int max_multiplier = 10;
        private const int adjust_levels = 4;

        private readonly double baseIncrement;

        private readonly Box background;
        private readonly OsuSpriteText incrementText;
        private readonly RepeatingButtonBehaviour repeatBehaviour;

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        public DiscreteAdjustmentControl(double baseIncrement)
        {
            this.baseIncrement = baseIncrement;

            CornerRadius = 5;
            Masking = true;
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Depth = float.MaxValue,
                },
                incrementText = new OsuSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = OsuFont.Default.With(size: 14, weight: FontWeight.Bold),
                    Padding = new MarginPadding(4),
                    Alpha = 0,
                    Depth = float.MinValue,
                },
                repeatBehaviour = new RepeatingButtonBehaviour(this),
            };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            background.Colour = colourProvider.Dark2;

            for (int i = 1; i <= adjust_levels; i++)
            {
                AddInternal(new IncrementBox(i, baseIncrement));
                AddInternal(new IncrementBox(-i, baseIncrement));
            }
        }

        private IncrementBox? hoveredBox => InternalChildren.OfType<IncrementBox>().FirstOrDefault(d => d.IsHovered);

        private IncrementBox? lastHovered;

        protected override void Update()
        {
            base.Update();

            if (hoveredBox == lastHovered)
                return;

            lastHovered = hoveredBox;

            if (lastHovered != null)
            {
                incrementText
                    .MoveToX(Math.Sign(lastHovered.Multiplier) * Math.Abs(lastHovered.Index), 400, Easing.OutQuint)
                    .FadeTo(0.8f, 200, Easing.OutQuint);

                incrementText.Text = $"{(lastHovered.Multiplier > 0 ? "+" : string.Empty)}"
                                     + (lastHovered.Amount * lastHovered.Multiplier).ToString("0.##", CultureInfo.InvariantCulture);
            }
            else
            {
                incrementText
                    .MoveToX(0, 200, Easing.OutQuint)
                    .FadeOut(200, Easing.OutQuint);
            }
        }

        protected override bool OnHover(HoverEvent e) => true;

        protected override bool OnClick(ClickEvent e)
        {
            if (hoveredBox is not { } box)
                return false;

            Action?.Invoke(baseIncrement * box.Multiplier);

            box.Flash();

            incrementText
                .MoveToX(Math.Sign(box.Multiplier) * (Math.Abs(box.Index) * 5))
                .MoveToX(Math.Sign(box.Multiplier) * Math.Abs(box.Index), 700, Easing.OutQuint);

            incrementText.FadeTo(1).FadeTo(0.8f, 1400, Easing.OutQuint);

            repeatBehaviour.SampleFrequencyModifier = (double)box.Multiplier / max_multiplier * 0.2;
            return true;
        }

        /// <summary>One horizontal band: width encodes its multiplier, hover highlights it.</summary>
        private partial class IncrementBox : CompositeDrawable
        {
            public readonly double Amount;
            public readonly int Multiplier;
            public readonly float Index;

            private readonly Box box;

            [Resolved]
            private OverlayColourProvider colourProvider { get; set; } = null!;

            public IncrementBox(int index, double amount)
            {
                Index = index;
                Amount = amount;
                Multiplier = Math.Sign(index) * convertMultiplier(index);

                float ratio = (float)index / adjust_levels;

                RelativeSizeAxes = Axes.Both;
                Width = 0.5f * Math.Abs(ratio);

                Anchor direction = index < 0 ? Anchor.x2 : Anchor.x0;

                Origin |= direction;
                Depth = Math.Abs(index);
                Anchor = Anchor.TopCentre;

                InternalChildren = new Drawable[]
                {
                    box = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Blending = BlendingParameters.Additive,
                    },
                };
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                box.Colour = colourProvider.Background1;
                box.Alpha = 0.1f;
            }

            private static int convertMultiplier(int m) => Math.Abs(m) switch
            {
                2 => 2,
                3 => 5,
                4 => max_multiplier,
                _ => 1,
            };

            protected override bool OnHover(HoverEvent e)
            {
                box.Colour = colourProvider.Colour0;
                box.FadeTo(0.2f, 100, Easing.OutQuint);
                return true;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                box.Colour = colourProvider.Background1;
                box.FadeTo(0.1f, 500, Easing.OutQuint);
                base.OnHoverLost(e);
            }

            public void Flash() => box
                .FadeTo(0.4f, 40, Easing.OutQuint)
                .Then()
                .FadeTo(0.2f, 700, Easing.OutPow10);
        }
    }
}
