using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game;
using osu.Game.Online.API;
using osuTK;

namespace OsuPlayer.Game
{
    /// <summary>
    /// Shared base for the game and the visual test browser.
    ///
    /// Derives from osu!'s <see cref="OsuGameBase"/> (same path as the osu!-design reference
    /// project): fonts (Torus / Noto CJK / Venera), UI sound samples, the osu! cursor,
    /// tooltip container and drawable control-point/beat-sync types all come from osu! itself.
    /// </summary>
    public partial class OsuPlayerGameBase : OsuGameBase
    {
        /// <summary>
        /// Placeholder API provider: this player never talks to osu! servers.
        /// Must be set in the constructor so <see cref="OsuGameBase"/> never constructs
        /// a real <c>APIAccess</c>.
        /// </summary>
        protected readonly DummyAPIAccess DummyApi = new();

        protected OsuPlayerGameBase()
        {
            API = DummyApi;
        }

        /// <summary>
        /// Keep the fixed 1366x768 design-draft coordinate space; contents scale with
        /// the window size exactly as under the previous framework-only host.
        /// </summary>
        protected override Container CreateScalingContainer() => new DrawSizePreservingFillContainer
        {
            TargetDrawSize = new Vector2(1366, 768)
        };

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // The dummy provider is a drawable that pumps scheduled state changes.
            // OsuGameBase only adds the real APIAccess to the hierarchy automatically.
            Add(DummyApi);
        }
    }
}
