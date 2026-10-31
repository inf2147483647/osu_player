using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using osu.Framework.Android;
using OsuPlayer.Game;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Net;

namespace OsuPlayer.Android
{
    /// <summary>
    /// 安卓入口：与桌面版 <c>OsuPlayer.Desktop/Program.cs</c> 等价，只是宿主从
    /// <c>Host.GetSuitableDesktopHost</c> 换成 osu!framework 的 <see cref="AndroidGameActivity"/>
    /// （它内部用 SDL3 创建 <c>AndroidGameHost</c> 并跑同一个 <see cref="OsuPlayerGame"/>）。
    /// 界面坐标仍固定 1366x768（见 OsuPlayerGameBase.CreateScalingContainer），因此强制横屏。
    /// </summary>
    [Activity(
        ConfigurationChanges = DEFAULT_CONFIG_CHANGES,
        Exported = true,
        LaunchMode = DEFAULT_LAUNCH_MODE,
        MainLauncher = true,
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        Theme = "@android:style/Theme.NoTitleBar.Fullscreen")]
    public class MainActivity : AndroidGameActivity
    {
        protected override osu.Framework.Game CreateGame() => new OsuPlayerGame();

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // 全屏 + 常亮：播放器/圆盘特效不应被系统状态栏或息屏打断。
            Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

            // 安全验证（errcode 20028）：桌面端用 Process.Start 打开验证页，安卓没有 shell，
            // 改跳系统浏览器。CaptchaBrowser 的验证页挂在设备回环地址上，浏览器在同一台设备内
            // 可直接访问，验证结果（ticket/randstr）由页面 POST 回该服务，流程与桌面一致。
            CaptchaBrowser.ExternalPageOpener = url =>
            {
                try
                {
                    RunOnUiThread(() =>
                    {
                        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url))
                            .AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                    });

                    return true;
                }
                catch (Exception e)
                {
                    AppLog.Net.Add($"拉起系统浏览器失败：{e.Message}", osu.Framework.Logging.LogLevel.Error, e);
                    return false;
                }
            };
        }
    }
}
