using osu.Game.Graphics.UserInterface;
using osuTK.Graphics;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// The app's standard action button: a real osu! <see cref="OsuButton"/> (rounded 5px,
    /// two-stage hover, press squash + elastic release, click flash and osu! hover/click
    /// samples) tinted with osu!'s pink accent.
    /// </summary>
    public partial class OsuPlayerButton : OsuButton
    {
        public OsuPlayerButton()
        {
            BackgroundColour = new Color4(255, 102, 171, 255);
        }
    }
}
