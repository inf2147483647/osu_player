using System;
using System.Threading;
using osu.Framework;
using osu.Framework.Platform;
using OsuPlayer.Game;

namespace OsuPlayer.Desktop
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            // 线程池下限。框架的纹理加载是 Task.Run(() => 同步下载)，一次滚出上百个封面时，
            // 默认（CPU 核数）个线程会被瞬间占满；线程池补线程只有每秒 1~2 个，
            // 期间异步续体和取消定时器都排不上队，网络请求会集体卡死几十秒。
            // 抬高下限后突发时线程立即可用（线程仍是按需创建，空闲时没有额外开销）。
            int minThreads = Math.Max(64, Environment.ProcessorCount * 8);
            ThreadPool.SetMinThreads(minThreads, minThreads);

            // 第一个参数是存储目录名（%APPDATA%\OsuPlayer），不能改；窗口标题走 FriendlyGameName，
            // 否则框架会把它设成默认的 osu!framework (running "OsuPlayer")。
            using (GameHost host = Host.GetSuitableDesktopHost(@"OsuPlayer", new HostOptions { FriendlyGameName = @"osu!_player" }))
            using (osu.Framework.Game game = new OsuPlayerGame(args))
                host.Run(game);
        }
    }
}