using osu.Framework;
using osu.Framework.Platform;
using OsuPlayer.Game;

namespace OsuPlayer.Desktop
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // 第一个参数是存储目录名（%APPDATA%\OsuPlayer），不能改；窗口标题走 FriendlyGameName，
            // 否则框架会把它设成默认的 osu!framework (running "OsuPlayer")。
            using (GameHost host = Host.GetSuitableDesktopHost(@"OsuPlayer", new HostOptions { FriendlyGameName = @"osu!_player" }))
            using (osu.Framework.Game game = new OsuPlayerGame(args))
                host.Run(game);
        }
    }
}