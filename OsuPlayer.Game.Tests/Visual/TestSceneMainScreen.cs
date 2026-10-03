using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using OsuPlayer.Game.Screens;

namespace OsuPlayer.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneMainScreen : OsuPlayerTestScene
    {
        public TestSceneMainScreen()
        {
            Add(new ScreenStack(new MainScreen()) { RelativeSizeAxes = Axes.Both });
        }
    }
}