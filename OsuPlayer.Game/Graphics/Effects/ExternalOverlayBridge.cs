using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Overlays;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Graphics.Effects
{
    /// <summary>
    /// 「桌面全屏特效」子进程（<c>..\osu!avalonia</c> 下的 OsuPlayer.Overlay）的宿主侧桥接。
    ///
    /// <para>为什么需要它：osu!framework 的窗口不支持透明背景，所以「屏幕两侧闪光 + kiai 星星喷泉」
    /// 没法直接铺在桌面上。子进程用 Avalonia 画一个全屏透明 + 点击穿透 + 置顶的窗口，
    /// 只有播放器最小化（用户看不到播放器）时才显示。</para>
    ///
    /// <para>一致性做法：本桥接不改写特效逻辑，只是把 osu! 自己的 <see cref="MenuSideFlashes"/> /
    /// <see cref="KiaiFountains"/> 真正播放的动画参数（亮度、拍长、喷发方向）逐条转发给子进程，
    /// 子进程照着放同一段动画 —— 两边因此逐帧一致，也不会因为各算各的节拍而漂移。</para>
    ///
    /// <para>生命周期：启动后不久预热拉起（保持隐藏，等第一次最小化时立刻展开），之后常驻复用；
    /// 本进程退出时会关掉它的标准输入，子进程读到 EOF 自行退出，不会留下幽灵窗口。</para>
    ///
    /// <para>另外，播放器每弹一条<b>提醒</b>（通知）都会通知子进程亮出它的「顶层效果窗口」
    /// —— 那个窗口常驻任务栏、始终置顶、且设计上无法关闭（语义对齐 ClassIsland 的
    /// <c>Views/TopmostEffectWindow</c>）。触发靠订阅 <see cref="INotificationOverlay.UnreadCount"/> 的增量。</para>
    /// </summary>
    public partial class ExternalOverlayBridge : Component
    {
        /// <summary>子进程工程所在目录（与 osu_player 同级）。</summary>
        private const string overlay_project_folder = "osu!avalonia";

        private const string overlay_exe_name = "OsuPlayer.Overlay.exe";

        /// <summary>UIAccess 提权助手（与特效进程工程同级目录下的 UIAccess/ 子工程）。</summary>
        private const string uiaccess_exe_name = "OsuPlayer.UIAccess.exe";

        /// <summary>助手工程在 <c>osu!avalonia</c> 下的子目录名。</summary>
        private const string uiaccess_project_folder = "UIAccess";

        /// <summary>
        /// 是否用 UIAccess 提权助手启动特效进程（环境变量 OSUPLAYER_OVERLAY_UIACCESS=1 开启）。
        /// 默认关：提权助手的实现已移除，开着只会在每次启动时白找一次。
        /// </summary>
        private static readonly bool use_uiaccess =
            Environment.GetEnvironmentVariable("OSUPLAYER_OVERLAY_UIACCESS") == "1";

        /// <summary>启动失败后的重试间隔（例如子进程还没构建出来）。</summary>
        private static readonly TimeSpan spawn_retry_delay = TimeSpan.FromSeconds(10);

        /// <summary>启动后多久预热特效子进程（错开播放器自身的启动 / 分析高峰）。</summary>
        private static readonly TimeSpan prewarm_delay = TimeSpan.FromSeconds(4);

        [Resolved]
        private GameHost host { get; set; } = null!;

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notifications { get; set; } = null!;

        /// <summary>桌面歌词圆饼的波谱数据源（与主界面 LogoVisualisation 同一个）。</summary>
        [Resolved]
        private osu.Game.Beatmaps.IBeatSyncProvider beatSync { get; set; } = null!;

        /// <summary>
        /// 未读提醒数的镜像。直接用 <c>notifications.UnreadCount.BindValueChanged</c> 订阅是没法取消的
        /// （<see cref="IBindable{T}"/> 只提供 <c>BindTo</c> 方向的解绑），所以按 osu! 的习惯做法
        /// 用一块自己的 Bindable 镜像它，释放时 <c>UnbindAll()</c> 就能干净地断开。
        /// 声明成接口类型是有意的：<c>Bindable&lt;int&gt;.BindTo</c> 只接受同类型实参，
        /// 而 <c>UnreadCount</c> 暴露的是 <c>IBindable&lt;int&gt;</c>。
        /// </summary>
        private readonly IBindable<int> unreadMirror = new Bindable<int>();

        /// <summary>当前歌词行的镜像（同样是为了能干净解绑）。</summary>
        private readonly IBindable<int> lyricIndexMirror = new Bindable<int>();

        private Process? child;
        private StreamWriter? childInput;

        private readonly ConcurrentQueue<string> outbox = new();
        private readonly SemaphoreSlim outboxSignal = new(0);

        private bool writerRunning;
        private bool disposed;
        private bool missingExecutableReported;

        /// <summary>是否已经有线程在启动子进程（防止两次最小化并发拉起两个子进程）。</summary>
        private bool spawnInProgress;

        /// <summary>本次子进程是提权助手拉起来的：助手启动完就退出，不能拿它判断"子进程还活着"。</summary>
        private bool spawnedViaHelper;

        /// <summary>提权路径下"特效进程还没连上命名管道"的窗口期：此时也算活着，否则会被重复拉起。</summary>
        private bool helperPipePending;

        /// <summary>全屏特效（两侧闪光 + 星星喷泉）当前是否铺在桌面上。</summary>
        private bool effectsVisible;

        /// <summary>桌面歌词（可拖动的缩小圆饼 + 环形歌词）当前是否显示。</summary>
        private bool lyricsVisible;

        private DateTime lastSpawnAttempt = DateTime.MinValue;

        /// <summary>
        /// 最近一次「窗口正常显示」时的窗口中心（物理像素）。最小化之后 <c>Position</c> 会变成
        /// 系统的占位值，所以必须提前缓存，用来告诉子进程该铺在哪块显示器上。
        /// </summary>
        private int lastWindowCentreX;
        private int lastWindowCentreY;

        /// <summary>全屏特效当前是否铺在桌面上。</summary>
        public bool IsOverlayVisible => effectsVisible;

        /// <summary>接上两个特效控件的动画事件（在 <c>MainScreen</c> 里调用一次）。</summary>
        public void Attach(MenuSideFlashes flashes, KiaiFountains fountains)
        {
            flashes.Flashed += e =>
            {
                if (!effectsVisible)
                    return;

                send(new JObject
                {
                    ["t"] = "flash",
                    ["l"] = e.Left,
                    ["a"] = e.Alpha,
                    ["b"] = e.BeatLength,
                    ["k"] = e.Kiai,
                }.ToString(Formatting.None));
            };

            fountains.Fired += (left, right) =>
            {
                if (!effectsVisible)
                    return;

                send(new JObject
                {
                    ["t"] = "shoot",
                    ["l"] = left,
                    ["r"] = right,
                }.ToString(Formatting.None));
            };

            // 桌面歌词的圆饼要复刻圆饼的节拍动画：拍点收缩、涟漪、kiai 闪白都靠这条消息。
            fountains.Beat += e =>
            {
                if (!lyricsVisible)
                    return;

                send(new JObject
                {
                    ["t"] = "beat",
                    ["b"] = e.BeatLength,
                    ["k"] = e.Kiai,
                    ["a"] = e.Amplitude,
                    ["d"] = e.Downbeat,
                }.ToString(Formatting.None));
            };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // 播放器每弹一条提醒（通知），就把子进程的「顶层效果窗口」亮出来。
            // 未读数在 NotificationOverlay.Post 里同步 +1（弹窗进 toast tray 即为未读），
            // 所以"变大"就是"刚弹了一条提醒"；变小是提醒被关掉/已读，不该触发。
            unreadMirror.BindTo(notifications.UnreadCount);
            unreadMirror.BindValueChanged(onUnreadCountChanged);

            // 上面这条订阅之前可能已经有提醒积压。MusicPlayer 会在它自己的 LoadComplete 里补发
            // 「已切换身份，请重新扫码登录」这类启动提醒，而它比 MainScreen 的子级更早走完加载
            // （实测那条提醒就落在这段空档里），所以这里补判一次现状。
            if (notifications.UnreadCount.Value > 0)
                raiseReminder(notifications.UnreadCount.Value, 0);

            // 桌面歌词的数据源：歌词列表、当前行、封面与播放状态。
            // 只在歌词层显示时下发，隐藏时不做任何序列化开销。
            player.Lyrics.BindCollectionChanged((_, _) =>
            {
                if (lyricsVisible)
                    sendLyricsSnapshot();
            });

            player.CurrentCover.BindValueChanged(e =>
            {
                if (lyricsVisible)
                    sendCover(e.NewValue);
            });

            player.IsPlaying.BindValueChanged(e =>
            {
                if (lyricsVisible)
                    sendPlaying(e.NewValue);
            });

            lyricIndexMirror.BindTo(player.ActiveLyricIndex);
            lyricIndexMirror.BindValueChanged(e =>
            {
                if (lyricsVisible)
                    sendActiveLyric(e.NewValue);
            });
        }

        private void onUnreadCountChanged(ValueChangedEvent<int> e)
        {
            if (e.NewValue <= e.OldValue)
                return;

            raiseReminder(e.NewValue, e.OldValue);
        }

        private void raiseReminder(int unreadNow, int unreadBefore)
        {
            AppLog.Player.Add($"播放器弹出提醒（未读 {unreadBefore}→{unreadNow}），通知特效进程亮出顶层效果窗口");

            // 提醒可能早于预热到达：这里兜底把子进程拉起来（ensureChild 是单飞的，重复调用无副作用）。
            ensureChild();

            send(new JObject { ["t"] = "reminder" }.ToString(Formatting.None));
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // 预热：提前把子进程拉起来（保持隐藏）。Avalonia 冷启动要几秒，第一次最小化时才去启动的话
            // 用户会先看到几秒空档；这里延后一点起，错开播放器自身的启动与 BPM 分析高峰。
            Scheduler.AddDelayed(() => ensureChild(immediate: true), prewarm_delay.TotalMilliseconds);

            // 桌面歌词圆饼的波谱：与主界面 LogoVisualisation 同样的 50ms 更新节奏。
            Scheduler.AddDelayed(sendSpectrum, 50, true);
        }

        protected override void Update()
        {
            base.Update();

            IWindow? window = host.Window;

            if (window is null)
                return;

            if (window.WindowState != WindowState.Minimised)
            {
                lastWindowCentreX = window.Position.X + window.ClientSize.Width / 2;
                lastWindowCentreY = window.Position.Y + window.ClientSize.Height / 2;
            }

            // 两个层各管各的：全屏特效受设置开关控制，桌面歌词只跟最小化走。
            bool minimized = window.WindowState == WindowState.Minimised;
            bool effectsShouldShow = player.FullscreenEffects.Value && minimized;
            bool lyricsShouldShow = minimized;

            if (effectsShouldShow != effectsVisible)
            {
                effectsVisible = effectsShouldShow;

                if (effectsShouldShow)
                    prepareChild();

                send(new JObject { ["t"] = "visible", ["on"] = effectsShouldShow }.ToString(Formatting.None));

                AppLog.Player.Add(effectsShouldShow
                    ? "播放器已最小化，展开桌面全屏特效"
                    : "播放器已恢复，收起桌面全屏特效");
            }

            if (lyricsShouldShow != lyricsVisible)
            {
                lyricsVisible = lyricsShouldShow;

                if (lyricsShouldShow)
                {
                    prepareChild();

                    // 展开时先补一份完整快照，之后只发增量（当前行 / 封面 / 播放状态）。
                    sendLyricsSnapshot();
                    sendCover(player.CurrentCover.Value);
                    sendPlaying(player.IsPlaying.Value);
                }

                send(new JObject { ["t"] = "lyrics_visible", ["on"] = lyricsShouldShow }.ToString(Formatting.None));

                AppLog.Player.Add(lyricsShouldShow
                    ? "播放器已最小化，显示桌面歌词"
                    : "播放器已恢复，隐藏桌面歌词");
            }
        }

        // ===================== 子进程 =====================

        private void ensureChild(bool immediate = false)
        {
            if (disposed)
                return;

            if (childIsAlive())
                return;

            // 单飞：已经有启动线程在跑就不再排队，否则两次最小化会先后拉起两个子进程
            // （两座全屏窗口叠在一起，谁也管不住谁）。
            if (spawnInProgress)
                return;

            if (!immediate && DateTime.UtcNow - lastSpawnAttempt < spawn_retry_delay)
                return;

            spawnInProgress = true;
            lastSpawnAttempt = DateTime.UtcNow;

            // 专用线程，而不是线程池：播放器启动时会跑 BPM / ONNX 分析把线程池占满，
            // Task.Run 排进去的启动任务会被压到几分钟后才执行（实测最小化后 2 分半才起来），
            // 表现就是"最小化了但特效迟迟不出现"。
            new Thread(() =>
            {
                try
                {
                    spawnChild();
                }
                catch (Exception e)
                {
                    AppLog.Player.Add($"启动全屏特效子进程失败：{e.Message}", LogLevel.Important, e);
                }
                finally
                {
                    spawnInProgress = false;
                }
            })
            {
                IsBackground = true,
                Name = "overlay-spawn",
            }.Start();
        }

        private void spawnChild()
        {
            string? executable = resolveOverlayExecutable();

            if (executable is null)
            {
                if (!missingExecutableReported)
                {
                    missingExecutableReported = true;
                    AppLog.Player.Add($"未找到全屏特效子进程 {overlay_exe_name}（{overlay_project_folder} 还没构建？），最小化时不会有桌面特效",
                        LogLevel.Important);
                }

                return;
            }

            if (spawnWithUiAccess(executable))
                return;

            Process? process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                // 必须显式指定 UTF-8：StandardInput 默认用 Console.InputEncoding（中文系统上是 GBK/936），
                // 而子进程按 UTF-8 读 JSON —— 不写这一行，中文歌词会全部变成 U+FFFD（实测如此）。
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            });

            if (process is null)
            {
                AppLog.Player.Add("全屏特效子进程启动失败（Process.Start 返回空）", LogLevel.Important);
                return;
            }

            childInput = process.StandardInput;
            childInput.AutoFlush = false;

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => AppLog.Player.Add($"全屏特效子进程已退出（{describeExit(process)}）");

            child = process;
            startWriter();

            AppLog.Player.Add($"已启动全屏特效子进程：{executable}", LogLevel.Important);
        }

        /// <summary>
        /// 走提权助手启动特效进程：由助手复制 explorer 的令牌、加上 UIAccess 标志后再创建它，
        /// 于是特效进程能以 UIAccess 压住全屏程序与系统界面（播放器自己不提权）。
        ///
        /// <para><b>默认开启</b>：提权助手在清单里声明了 requireAdministrator，启动特效进程时会弹一次 UAC。
        /// 不想弹就把环境变量 <c>OSUPLAYER_OVERLAY_UIACCESS</c> 设为 0。</para>
        ///
        /// <para>返回 false 表示没走这条路（已关闭 / 没有助手 / 被拒绝提权），调用方继续按普通方式启动。</para>
        /// </summary>
        private bool spawnWithUiAccess(string executable)
        {
            if (!use_uiaccess)
                return false;

            string? helper = resolveUiAccessHelper();

            if (helper is null)
            {
                AppLog.Player.Add($"未找到 UIAccess 提权助手 {uiaccess_exe_name}，本次按普通方式启动特效进程", LogLevel.Important);
                return false;
            }

            try
            {
                // 提权必须走 shell：UseShellExecute=true 才会由系统弹出 UAC。
                // 之前用 UseShellExecute=false + 继承管道句柄，结果直接 Win32Exception 740
                // （ERROR_ELEVATION_REQUIRED）—— 因为绕过了 shell，没人能弹提权对话框。
                //
                // 而 UseShellExecute=true 又不能继承句柄，所以标准输入改成**命名管道**：
                // 名字写在命令行上交给助手，助手把它转给特效进程，特效进程自己去连（见 OverlayWindow）。
                string pipeName = $"osuplayer-overlay-{Environment.ProcessId}-{Guid.NewGuid():N}";
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                var startInfo = new ProcessStartInfo(helper)
                {
                    UseShellExecute = true,
                    Arguments = $"--exe \"{executable}\" --stdin -1 -- --pipe {pipeName}",
                };

                // 这一句必须写在 Process.Start 之前：它会一直阻塞到你回应 UAC，
                // 卡在弹窗上时若没有这条日志，看起来就像"什么都没发生"。
                AppLog.Player.Add("正在请求 UIAccess 提权：会弹出 UAC 窗口，需要你点「是」", LogLevel.Important);

                Process? helperProcess = Process.Start(startInfo);

                if (helperProcess is null)
                {
                    pipe.Dispose();
                    AppLog.Player.Add("UIAccess 提权助手启动失败（Process.Start 返回空）", LogLevel.Important);
                    return false;
                }

                AppLog.Player.Add($"UIAccess 提权助手已启动：pid={helperProcess.Id}，等特效进程连管道", LogLevel.Important);

                // 助手如果失败会立刻带非零码退出（例如借不到带 UIAccess 的令牌）——
                // 那种情况别干等管道，直接回退普通启动，免得特效白白消失两分钟。
                bool failed = helperProcess.WaitForExit(20_000) && helperProcess.ExitCode != 0;

                if (failed)
                {
                    int code = helperProcess.ExitCode;
                    helperProcess.Dispose();
                    pipe.Dispose();
                    AppLog.Player.Add($"UIAccess 提权助手失败退出（code={code}），本次回退普通启动", LogLevel.Important);
                    return false;
                }

                helperProcess.Dispose();

                // 助手已经退出（成功）或还在收尾都不影响：这里等的是"特效进程连上来"。
                spawnedViaHelper = true;
                helperPipePending = true;
                awaitHelperPipeAsync(pipe);
                return true;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"UIAccess 提权启动失败，回退普通方式：{e.Message}", LogLevel.Important, e);
                return false;
            }
        }

        /// <summary>
        /// 等特效进程连上命名管道。连上之前 outbox 里的消息会一直排队（drainOutbox 见 childInput 为空就直接返回），
        /// 所以不会丢消息；期间 childIsAlive() 靠 helperPipePending 保持为真，避免又被拉起一个。
        /// </summary>
        private async void awaitHelperPipeAsync(NamedPipeServerStream pipe)
        {
            try
            {
                // 提权对话框可能被人放着不点，给一个较长的上限免得永久挂着。
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

                childInput = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
                helperPipePending = false;
                startWriter();

                AppLog.Player.Add("特效进程已通过 UIAccess 管道接上，开始下发特效消息", LogLevel.Important);
            }
            catch (Exception e)
            {
                helperPipePending = false;
                spawnedViaHelper = false;
                AppLog.Player.Add($"UIAccess 管道未接通（{e.Message}），下次最小化会重新启动特效进程", LogLevel.Important);
            }
        }

        private static string describeExit(Process process)
        {
            try
            {
                return $"code={process.ExitCode}";
            }
            catch
            {
                return "code=?";
            }
        }

        /// <summary>
        /// 特效进程是否还活着。
        /// 普通方式看进程本身；提权方式下我们只持有那条管道（助手早就退出了），
        /// 管道写失败时 <see cref="drainOutbox"/> 会把 childInput 清空，据此判定。
        /// </summary>
        private bool childIsAlive()
            => helperPipePending || (spawnedViaHelper ? childInput is not null : child is { HasExited: false });

        /// <summary>
        /// 定位 UIAccess 提权助手：与特效进程同一套查找逻辑（从输出目录逐级向上找
        /// <c>UIAccess/bin/&lt;配置&gt;</c>），可用环境变量 OSUPLAYER_UIACCESS_EXE 覆盖。
        /// </summary>
        private static string? resolveUiAccessHelper()
        {
            string? overridden = Environment.GetEnvironmentVariable("OSUPLAYER_UIACCESS_EXE");

            if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
                return overridden;

            string[] configurations = ["Debug", "Release"];
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                foreach (string configuration in configurations)
                {
                    string bin = Path.Combine(directory.FullName, overlay_project_folder, uiaccess_project_folder, "bin", configuration);

                    if (!Directory.Exists(bin))
                        continue;

                    string? found = Directory.EnumerateFiles(bin, uiaccess_exe_name, SearchOption.AllDirectories).FirstOrDefault();

                    if (found is not null)
                        return found;
                }
            }

            return null;
        }

        private void startWriter()
        {
            if (writerRunning)
                return;

            writerRunning = true;

            Task.Run(async () =>
            {
                while (!disposed)
                {
                    try
                    {
                        await outboxSignal.WaitAsync(500).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    drainOutbox();
                }
            });
        }

        private void drainOutbox()
        {
            StreamWriter? writer = childInput;

            if (writer is null)
                return;

            try
            {
                while (outbox.TryDequeue(out string? line))
                    writer.WriteLine(line);

                writer.Flush();
            }
            catch (Exception e)
            {
                // 子进程已经不在了：丢掉这批消息，下次最小化会重新拉起一个。
                outbox.Clear();
                childInput = null;
                AppLog.Player.Add($"向全屏特效子进程写入失败：{e.Message}");
            }
        }

        private void send(string line)
        {
            if (disposed)
                return;

            outbox.Enqueue(line);
            outboxSignal.Release();
        }

        private void sendScreenHint()
            => send(new JObject
            {
                ["t"] = "hello",
                ["x"] = lastWindowCentreX,
                ["y"] = lastWindowCentreY,
            }.ToString(Formatting.None));

        /// <summary>
        /// 让子进程起来并告诉它该铺在哪块显示器上。全屏特效与桌面歌词都会用到；
        /// 顺序要紧：先给屏幕提示，再让子进程起来，消息会按序落到它的标准输入上。
        /// </summary>
        private void prepareChild()
        {
            sendScreenHint();
            ensureChild();
        }

        /// <summary>把整份歌词（含当前行）发给子进程；切歌、歌词到达、展开桌面歌词时调用。</summary>
        private void sendLyricsSnapshot()
        {
            var lines = new JArray();

            foreach (LrcLine line in player.Lyrics)
            {
                lines.Add(new JObject
                {
                    ["ms"] = line.TimeMs,
                    ["text"] = line.Text,
                });
            }

            // 追踪日志：歌词在跨进程边界前后的样子（内容 + Unicode 码点），用于排查乱码。
            string firstText = player.Lyrics.Count > 0 ? player.Lyrics[0].Text : string.Empty;

            AppLog.Player.Add($"[桌面歌词] 下发歌词快照：{player.Lyrics.Count} 行，当前行 {player.ActiveLyricIndex.Value}，"
                              + $"首行=\"{truncate(firstText, 40)}\"（{describeCodePoints(firstText, 8)}）", LogLevel.Important);

            send(new JObject
            {
                ["t"] = "lyrics",
                ["lines"] = lines,
                ["index"] = player.ActiveLyricIndex.Value,
            }.ToString(Formatting.None));
        }

        private static string truncate(string? text, int length)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.Length <= length ? text : text[..length] + "…";
        }

        /// <summary>把前几个字符的 Unicode 码点写出来，一眼能看出是编码问题还是字体问题。</summary>
        private static string describeCodePoints(string? text, int count)
        {
            if (string.IsNullOrEmpty(text))
                return "空";

            return string.Join(" ", text.Take(count).Select(c => $"U+{(int)c:X4}"));
        }

        private void sendActiveLyric(int index)
            => send(new JObject { ["t"] = "lyric", ["i"] = index }.ToString(Formatting.None));

        private void sendCover(string? url)
            => send(new JObject { ["t"] = "cover", ["url"] = url ?? string.Empty }.ToString(Formatting.None));

        private void sendPlaying(bool playing)
            => send(new JObject { ["t"] = "playing", ["on"] = playing }.ToString(Formatting.None));

        /// <summary>
        /// 把 256 维频谱发给桌面歌词（圆饼外圈的波谱条）。
        /// 只在桌面歌词可见且子进程已接上时发：这是每 50ms 一条的高频消息，
        /// 子进程还没启动时积在队列里会导致连上后一次性灌入、延迟数秒。
        /// </summary>
        private void sendSpectrum()
        {
            if (disposed || !lyricsVisible)
                return;

            if (childInput is null && !helperPipePending)
                return;

            ReadOnlySpan<float> amplitudes = beatSync.CurrentAmplitudes.FrequencyAmplitudes.Span;
            var values = new JArray();

            foreach (float value in amplitudes)
                values.Add(MathF.Round(value, 3));

            send(new JObject { ["t"] = "spectrum", ["v"] = values }.ToString(Formatting.None));
        }

        /// <summary>
        /// 定位子进程可执行文件：从自身输出目录逐级向上找与 osu_player 同级的 <c>osu!avalonia</c>
        /// 的构建产物（TFM 目录不写死，免得换目标框架后失效）；可用环境变量 OSUPLAYER_OVERLAY_EXE 覆盖。
        /// </summary>
        private static string? resolveOverlayExecutable()
        {
            string? overridden = Environment.GetEnvironmentVariable("OSUPLAYER_OVERLAY_EXE");

            if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
                return overridden;

            // 本工程的开发流程是 Debug，优先找它，其次 Release。
            string[] configurations = ["Debug", "Release"];

            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                foreach (string configuration in configurations)
                {
                    string bin = Path.Combine(directory.FullName, overlay_project_folder, "bin", configuration);

                    if (!Directory.Exists(bin))
                        continue;

                    string? found = Directory.EnumerateFiles(bin, overlay_exe_name, SearchOption.AllDirectories).FirstOrDefault();

                    if (found is not null)
                        return found;
                }
            }

            return null;
        }

        protected override void Dispose(bool isDisposing)
        {
            disposed = true;

            // 断开对提醒未读数的镜像订阅（子进程由下面关掉标准输入后自行退出）。
            unreadMirror.UnbindAll();
            lyricIndexMirror.UnbindAll();

            try
            {
                outboxSignal.Release();
            }
            catch
            {
                // 信号量已释放完毕时忽略。
            }

            Process? process = child;
            StreamWriter? writer = childInput;
            child = null;
            childInput = null;

            // 提权方式下没有 Process 可关，只有这条管道：关掉它 = 特效进程读到 EOF 自行退出。
            if (writer is not null)
            {
                try
                {
                    writer.Dispose();
                }
                catch
                {
                    // 已经断开时忽略。
                }
            }

            if (process is not null)
            {
                // 关掉标准输入 = 让子进程读到 EOF 后自行退出。
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                    // 已经退出时会抛，忽略。
                }

                try
                {
                    if (!process.WaitForExit(1500))
                        process.Kill();
                }
                catch
                {
                    // 进程已消失。
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 忽略。
                }
            }

            base.Dispose(isDisposing);
        }
    }
}