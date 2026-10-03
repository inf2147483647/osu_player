using osu.Framework.Testing;

namespace OsuPlayer.Game.Tests.Visual
{
    public abstract partial class OsuPlayerTestScene : TestScene
    {
        protected override ITestSceneTestRunner CreateRunner() => new OsuPlayerTestSceneTestRunner();

        private partial class OsuPlayerTestSceneTestRunner : OsuPlayerGameBase, ITestSceneTestRunner
        {
            private TestSceneTestRunner.TestRunner runner = null!;

            protected override void LoadAsyncComplete()
            {
                base.LoadAsyncComplete();
                Add(runner = new TestSceneTestRunner.TestRunner());
            }

            public void RunTestBlocking(TestScene test) => runner.RunTestBlocking(test);
        }
    }
}