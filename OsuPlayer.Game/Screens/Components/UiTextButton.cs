using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osuTK.Graphics;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Minimal top/bottom-bar text button used across the design draft, built on osu!'s
    /// <see cref="OsuAnimatedButton"/>: official hover fade, press squash, elastic release,
    /// click flash and hover/click samples, with a tooltip and post-construction relabel.
    /// </summary>
    public partial class UiTextButton : OsuAnimatedButton, IHasTooltip
    {
        private readonly SpriteText label;

        public string Text
        {
            get => label.Text.ToString();
            set => label.Text = value;
        }

        public float FontSize
        {
            get => label.Font.Size;
            set => label.Font = OsuFont.GetFont(size: value);
        }

        private Color4 baseColour = Color4.White;

        public Color4 LabelColour
        {
            get => baseColour;
            set
            {
                baseColour = value;
                label.Colour = value;
            }
        }

        public UiTextButton(string text, Action? action = null, float fontSize = 22,
                            MarginPadding? padding = null)
        {
            Action = action;
            AutoSizeAxes = Axes.Both;

            Add(label = new SpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Margin = padding ?? new MarginPadding { Horizontal = 12, Vertical = 8 },
                Text = text,
                Font = OsuFont.GetFont(size: fontSize),
                Colour = Color4.White,
            });
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // A text label needs no background plate; keep the official hover/flash overlay
            // but drop the permanent rounded shadow container styling.
            Content.CornerRadius = 6;
            Content.EdgeEffect = default;
            label.Colour = baseColour;
        }
    }
}
