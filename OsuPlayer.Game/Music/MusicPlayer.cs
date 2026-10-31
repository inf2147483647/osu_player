using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using OsuPlayer.Game.KuGou;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music.BeatSync.Onnx;
using OsuPlayer.Game.Net;
using osu.Framework.Logging;

namespace OsuPlayer.Game.Music
{
    /// <summary>
    /// Central playback engine: owns the KuGou API, the online track/texture stores, the play queue
    /// and the current BASS track. UI binds to the exposed bindables; all network work runs async.
    /// </summary>
    public partial class MusicPlayer : Component
    {
        [Resolved]
        private AudioManager audio { get; set; } = null!;

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

        /// <summary>
        /// osu!'s notification overlay, used to surface the non-member trial notice. Optional so the
        /// engine still works in contexts without an overlay (visual test scenes).
        /// </summary>
        [Resolved]
        private INotificationOverlay? notifications { get; set; }

        private KuGouApi api = null!;
        private ITrackStore trackStore = null!;
        private ITrackStore localTrackStore = null!;
        public TextureStore OnlineTextures { get; private set; } = null!;

        // Now playing state
        public Bindable<SongInfo?> CurrentSong { get; } = new();
        public BindableBool IsPlaying { get; } = new();
        public BindableDouble CurrentTimeMs { get; } = new();
        public BindableDouble LengthMs { get; } = new();
        public BindableFloat Volume { get; } = new(0.8f);
        public BindableDouble Tempo { get; } = new(1);
        public Bindable<string> Quality { get; } = new("128");
        public Bindable<RepeatMode> Repeat { get; } = new(RepeatMode.All);

        /// <summary>
        /// BPM 检测模式（原生 / ONNX）。换歌时决定用哪条检测管线，随播放状态持久化。
        /// 设置页的面板写它，<c>MusicBeatSyncProvider</c> 读它。
        /// </summary>
        public Bindable<BpmDetectionMode> BpmMode { get; } = new(BpmDetectionMode.Native);

        /// <summary>
        /// 调试模式：开启后应用进入"超级诊断模式"——全量日志 + osu! 原生性能分析 + 性能叠加层 +
        /// 周期性能快照。由设置页的开关控制，<c>OsuPlayerGame</c> 监听它来实际开关。
        /// </summary>
        public BindableBool DebugMode { get; } = new();

        /// <summary>
        /// 是否显示右下角的 osu! 原生帧率计数器（由设置页「图形 → 显示帧率」控制）。
        /// <c>OsuPlayerGame</c> 会把它同步到 <c>OsuSetting.ShowFpsDisplay</c>，供 <c>FPSCounter</c> 读取。
        /// </summary>
        public BindableBool ShowFps { get; } = new(true);

        /// <summary>
        /// 是否开启全屏特效（播放器最小化时铺在桌面上的两侧闪光 + kiai 星星喷泉）。
        /// 关掉只是不再显示特效，<b>特效进程仍然保留</b>（进程里还有托盘图标与顶层效果窗口），
        /// 由 <c>ExternalOverlayBridge</c> 改成不展开而已。
        /// </summary>
        public BindableBool FullscreenEffects { get; } = new(true);

        /// <summary>
        /// 圆饼旋转方式：跟随歌词（默认）或匀速。设置页的下拉框写它，
        /// 主界面据此在「按歌词滚动角度转动」与「固定周期匀速自转」之间切换（两者不会叠加）。
        /// </summary>
        public Bindable<CoverRotationMode> CoverRotation { get; } = new(CoverRotationMode.FollowLyrics);

        public Bindable<string> StatusText { get; } = new(string.Empty);
        public Bindable<string> CurrentCover { get; } = new(string.Empty);

        /// <summary>Bumped whenever a new track starts (effects use it to trigger bursts/flashes).</summary>
        public BindableInt PlayGeneration { get; } = new();

        /// <summary>timing 编辑器每次提交后自增，节拍源据此重建控制点（不重新分析音频）。</summary>
        public BindableInt TimingRevision { get; } = new();

        /// <summary>手动调整过的曲目时间轴，按 hash 记忆并随播放状态持久化。</summary>
        private readonly Dictionary<string, BeatmapTiming> timingOverrides = new();

        public BindableList<SongInfo> Queue { get; } = [];
        public BindableList<SongInfo> History { get; } = [];
        public BindableList<SongInfo> Liked { get; } = [];

        /// <summary>
        /// 已登录时「我喜欢」对应的账号歌单 id（见 <see cref="RefreshLikedAsync"/>）；0 表示尚未解析。
        /// 未登录时始终为 0，此时 <see cref="Liked"/> 是本地维护的列表。
        /// </summary>
        private long likedListId;

        /// <summary>账号歌单一次拉取的曲目上限（酷狗 App 云歌单同款页大小）。</summary>
        private const int liked_page_size = 300;

        /// <summary>正在进行的歌单同步：并发调用（打开面板 + 点喜欢）合并成一次请求。</summary>
        private Task<IReadOnlyList<SongInfo>?>? likedSyncInFlight;

        /// <summary>最近一次歌单同步的时间，用于给强制刷新设下限。</summary>
        private DateTime likedSyncedAt;

        /// <summary>强制刷新之间的最小间隔：面板每次打开都强制拉一次，在风控下就变成一串十几秒的请求。</summary>
        private static readonly TimeSpan liked_force_min_interval = TimeSpan.FromMinutes(2);

        /// <summary>曲目从磁盘导入（见 <see cref="SongInfo.LocalPath"/>），随播放状态一起持久化。</summary>
        public BindableList<SongInfo> LocalTracks { get; } = [];

        private Track? currentTrack;
        private int currentIndex = -1;
        private bool endHandled;
        private long playSerial;

        /// <summary>
        /// True while the loaded track is a non-member 60-second trial clip. The progress bar still
        /// spans the whole song (so the total time reads e.g. 03:28), but only the first
        /// <see cref="previewLimitMs"/> are seekable.
        /// </summary>
        private bool isPreviewTrack;

        /// <summary>Decoded length of the trial clip in milliseconds (0 when a full track is loaded).</summary>
        private double previewLimitMs;

        /// <summary>Position to restore once the next track starts (used when re-resolving after a login).</summary>
        private double resumeOnStartMs;

        /// <summary>
        /// 界面当前停留的定位目标（拖动时是用户拖到的位置，松手/点击时是提交的目标）。
        /// 拖动回调在 input 线程、<see cref="Update"/> 在 update 线程，所以用 <see cref="double"/>
        /// + <see cref="bool"/> 而不是 <c>double?</c>：8 字节的 double 读写是原子的，16 字节的
        /// Nullable 可能被撕裂。
        /// </summary>
        private double holdDisplayMs;

        /// <summary>是否存在界面停留目标。拖动中与定位提交后为 true，直到音轨到位。</summary>
        private bool holdActive;

        /// <summary>
        /// 该目标是否已经提交给音轨。拖动中为 false —— 拖动只更新界面、**不动音轨**
        /// （原因见 <see cref="Scrub"/>）；只有提交过的定位才做落地/超时判定。
        /// </summary>
        private bool holdSubmitted;

        /// <summary>提交给音轨的时刻，用于定位未落地时的超时兜底。</summary>
        private double holdSubmittedAtMs;

        /// <summary>判定一次异步 seek 已落地的容差（毫秒）。</summary>
        private const double seek_settle_tolerance_ms = 60;

        /// <summary>定位提交后，界面停留目标的最长保留时间，避免未落地时显示被永久钉住。</summary>
        private const double seek_hold_timeout_ms = 2000;

        /// <summary>
        /// 定位请求的合并状态（见 <see cref="requestTrackSeek"/>）。音频线程的一帧会把
        /// <c>PendingActions</c> 队列**全部**串行跑完（<c>AudioComponent.Update</c> 的 while 出队），
        /// 所以每多排队一个 seek，那一帧就多卡一次：快速拖动/点击会把几十上百次定位塞进同一帧，
        /// 实测导致音频线程单帧长达 12.8 秒（表现为"音频线程几乎不走"）。
        /// 这里保证**同时只有一个 seek 在飞行中**、且只保留最新目标 —— 不引入任何人为延迟，
        /// 第一次定位立刻发出，音频以 BASS 能处理的最快速度跟手，来不及处理的中间位置直接丢弃。
        /// </summary>
        private readonly object seekLock = new();

        private double seekTargetMs;
        private bool hasSeekTarget;
        private bool seekInFlight;

        /// <summary>Slack when deciding a track has reached its end; also catches a seek onto the last sample.</summary>
        private const double end_epsilon_ms = 150;

        /// <summary>Distance kept clear of a trial clip's last sample so a seek cannot park the decoder at its end.</summary>
        private const double preview_seek_guard_ms = 300;

        /// <summary>
        /// True while a follow-up track's URL/stream is still loading. The previous track keeps reporting
        /// HasCompleted during that window; without this gate Update() would auto-advance again every frame,
        /// firing a play-URL request for the entire queue in a tight loop (observed: 90+ requests/second).
        /// </summary>
        private bool trackSwitchInProgress;

        /// <summary>The currently loaded BASS track (also acts as the beat-sync clock), or null.</summary>
        public Track? CurrentTrack => currentTrack;

        /// <summary>当前已加载音轨对应的曲目 hash；加载失败时用来判断是否该停掉正在播的这首。</summary>
        private string? currentTrackHash;

        /// <summary>Resolved audio URL of <see cref="CurrentTrack"/>, used for offline beat analysis.</summary>
        public string? CurrentStreamUrl { get; private set; }

        /// <summary>
        /// 可选音质，顺序即底栏「音质」下拉里的顺序：<c>Value</c> 是接口参数，<c>Label</c> 是界面文案。
        /// 五档与 EchoMusic 一致；<c>super</c> 是酷狗返回 DSD 格式音频的档位（见 KuGouMusicApi 文档）。
        /// </summary>
        public static readonly (string Value, string Label)[] QualityOptions =
        {
            ("128", "标准"),
            ("320", "高品"),
            ("flac", "无损"),
            ("high", "Hi-Res无损"),
            ("super", "DSD臻品"),
        };

        // -------- 淡入淡出（EchoMusic 式切歌/暂停过渡，避免爆音与突兀起停） --------

        /// <summary>Current fade multiplier of the playing track (0 silent .. 1 full).</summary>
        private double fadeLevel = 1;
        private double fadeTarget = 1;

        /// <summary>True while a fade-to-silence is scheduled to end in a real pause.</summary>
        private bool pausing;

        /// <summary>Track kept alive after a switch just long enough to fade out of the mix.</summary>
        private Track? fadingOutTrack;
        private double fadingOutLevel;

        private const double fade_in_duration = 420;
        private const double fade_out_duration = 320;

        // -------- 播放地址缓存与预解析（切歌/重播不再等一次网络往返） --------

        /// <summary>hash|quality → 已解析的播放地址与过期时间。命中即可直接播放。</summary>
        private readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> urlCache = new();

        /// <summary>正在后台解析的地址，避免同一首歌重复发起请求。</summary>
        private readonly ConcurrentDictionary<string, byte> prefetching = new();

        /// <summary>恢复登录时因平台身份变更需要提示的文案，延后到 LoadComplete 弹出。</summary>
        private string? pendingLoginNotice;

        /// <summary>
        /// 网关要求安全验证时的说明（errcode 20028）。酷狗风控会对播放请求下发人机验证事件，
        /// 完成验证前该账号的线上播放都会被拒；这里记住一次，避免对每首歌反复发请求继续触发风控。
        /// </summary>
        private string? verificationRequired;

        /// <summary>验证失败/取消后的冷却截止时间：期间不再弹验证页，避免反复打扰。</summary>
        private DateTime verificationRetryAfter;

        /// <summary>
        /// 风控冷却时长。酷狗一旦对某次请求下发 20028（人机验证），会连带把该账号后续请求拖慢到
        /// 十几到二十秒（实测 17–21s）；此时继续打领取 / 升级 / 歌单同步只会让整个会话一直挂在
        /// 验证事件上，所以这里统一进入冷却。
        /// </summary>
        private static readonly TimeSpan risk_control_delay = TimeSpan.FromMinutes(5);

        /// <summary>风控冷却截止时间：期间跳过自动的领取/升级与歌单同步。</summary>
        private DateTime riskControlUntil;

        /// <summary>
        /// 触发风控的那次操作。浏览器验证通过后立即重试它 —— 验证的目的就是解除这次 20028，
        /// 验证完了却继续被冷却挡住等于白做。
        /// </summary>
        private Func<Task>? pendingVerificationRetry;

        /// <summary>本会话是否已经跑过自动领取流程（启动恢复登录 + 扫码登录成功都会触发刷新）。</summary>
        private bool vipClaimAttempted;

        /// <summary>本机记录的最后一次概念版权益领取日期（每次会话内避免重复领取）。</summary>
        private string? vipClaimDate;

        /// <summary>验证失败后到可以再次发起之间的间隔。</summary>
        private static readonly TimeSpan verification_retry_delay = TimeSpan.FromMinutes(1);

        /// <summary>地址缓存有效期（酷狗地址带时效，过期后回源重新解析）。</summary>
        private static readonly TimeSpan url_lifetime = TimeSpan.FromMinutes(20);

        /// <summary>写入 player-state.json 的地址条数上限。</summary>
        private const int url_cache_persisted = 40;

        /// <summary>写入 player-state.json 的手动套用歌词条数上限（歌词文本较长，避免状态文件膨胀）。</summary>
        private const int lyric_overrides_persisted = 40;

        /// <summary>
        /// 播放历史上限。
        ///
        /// <para>为什么要有上限、以及这个数字是怎么来的：整份播放状态（历史是其中最大的一块）在每次变化后
        /// 都会在更新线程上整体序列化并原子写盘，所以"能存多少条"的真正约束是<b>状态文件体积</b>，不是调用栈——
        /// 实测一条历史约 430 字节（其中 timing 占一半、封面地址占两成），5000 条约 2MB：
        /// 一次保存约几十毫秒，且经过 1.5 秒防抖，只在操作停下来后发生一次，交互上察觉不到；
        /// 再往上加就会在每次保存时产生可感知的卡顿。</para>
        ///
        /// <para>关于栈溢出：保存与恢复全程都是循环遍历——<see cref="addHistory"/> 的裁剪、
        /// <c>JArray</c>/<c>JObject</c> 的构造、JSON 的读写，没有任何随条数增长而加深的递归，
        /// 所以条数再多也不会栈溢出；会先耗尽的是磁盘与内存，不是调用栈。</para>
        /// </summary>
        private const int history_max = 5000;

        // -------- 播放状态持久化 --------

        private const string state_file = "player-state.json";

        private ScheduledDelegate? stateSaveDelegate;

        [BackgroundDependencyLoader]
        private void load()
        {
            var httpStore = new HttpResourceStore();
            api = new KuGouApi();
            trackStore = audio.GetTrackStore(httpStore);
            localTrackStore = audio.GetTrackStore(new LocalFileResourceStore());
            OnlineTextures = new TextureStore(renderer, new TextureLoaderStore(httpStore), useAtlas: false);

            // 客户端身份：本播放器以酷狗概念版（lite）身份请求，appid/clientver/盐值都与标准版不同。
            AppLog.Player.Add($"酷狗客户端身份：{KuGouConstants.DisplayName(api.Session.Platform)}，"
                              + $"appid={KuGouConstants.AppId(api.Session.Platform)}，"
                              + $"clientver={KuGouConstants.ClientVer(api.Session.Platform)}", LogLevel.Important);

            Volume.BindValueChanged(_ => applyTrackVolume(currentTrack), true);

            // 倍速的可调范围与分度（界面上由底栏「倍数」下拉里的滑块调整，0.5x–3.0x，步进 0.01）。
            Tempo.MinValue = 0.5;
            Tempo.MaxValue = 3.0;
            Tempo.Precision = 0.01;

            Tempo.BindValueChanged(v =>
            {
                if (currentTrack is not null)
                    currentTrack.Tempo.Value = v.NewValue;
            });

            restoreState();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Everything worth remembering between sessions (EchoMusic-style persistence).
            // Bound here rather than in load() so the restored values do not immediately re-save.
            Volume.BindValueChanged(_ => queueStateSave());
            Tempo.BindValueChanged(_ => queueStateSave());
            Quality.BindValueChanged(_ => queueStateSave());
            Repeat.BindValueChanged(_ => queueStateSave());
            BpmMode.BindValueChanged(_ => queueStateSave());
            DebugMode.BindValueChanged(_ => queueStateSave());
            ShowFps.BindValueChanged(_ => queueStateSave());
            FullscreenEffects.BindValueChanged(_ => queueStateSave());
            CoverRotation.BindValueChanged(_ => queueStateSave());
            Liked.BindCollectionChanged((_, _) => queueStateSave());
            History.BindCollectionChanged((_, _) => queueStateSave());
            LocalTracks.BindCollectionChanged((_, _) => queueStateSave());

            // 恢复登录时若因平台身份变更丢弃了凭证，此时覆盖层已就绪，弹出提示。
            if (pendingLoginNotice is { } notice)
            {
                notifications?.Post(new SimpleNotification
                {
                    Text = notice,
                    IsImportant = true,
                });

                pendingLoginNotice = null;
            }

            // 启动预热：网关（tracker.kugou.com）的首次连接可能要十几秒，期间任何点歌都会一直停在
            // 「正在加载…」。用最近播放的一首歌在后台先把连接和新地址准备好。
            if (History.FirstOrDefault(s => !s.IsLocal) is { } recent)
            {
                AppLog.Player.Add($"启动预热地址与连接：{describe(recent)}");
                _ = prefetchUrlAsync(recent, Quality.Value);
            }

            // 已恢复登录时补领当天的概念版权益（换机器/换天启动都能自动生效）。
            if (IsLoggedIn)
            {
                _ = claimDailyVipAsync();

                // 「喜欢」跟着账号走：启动时同步一次账号的「我喜欢」歌单。
                _ = RefreshLikedAsync(force: true);
            }
        }

        public bool IsLoggedIn => api.Session.IsLoggedIn;

        /// <summary>
        /// 本机是否还留着可用的登录凭证（token）。
        /// 「快速登录」用它判断有没有东西可换 —— 凭证被清掉（登出/换了客户端身份）时只能走扫码、验证码或密码。
        /// </summary>
        public bool HasStoredCredential => !string.IsNullOrEmpty(api.Session.Token);

        public BindableBool LoggedIn { get; } = new();

        public long LoginUserId => api.Session.UserId;

        public void Logout()
        {
            AppLog.Player.Add($"退出登录（原用户 ID：{api.Session.UserId}）", LogLevel.Important);
            api.Session.Logout();
            api.ResetPlayAuth(); // 授权串是按账号签发的，换账号必须重取
            likedListId = 0;     // 「喜欢」退回本地列表
            vipClaimAttempted = false;  // 换账号后重新跑一次自动领取
            riskControlUntil = default; // 风控是按账号/会话的，重登时给它一次机会
            LoggedIn.Value = false;
            saveState();
        }

        // -------- 数据获取 --------
        public Task<IReadOnlyList<SongInfo>> SearchAsync(string keyword)
            => AppLog.Player.TimeAsync($"搜索歌曲“{keyword}”",
                () => mapAsync(() => api.SearchSongsAsync(keyword, 1, 30), KuGouMapper.SearchResults),
                songs => $"，{songs.Count} 条结果");

        /// <summary>首页推荐缓存。推荐页只在点「刷新推荐」时重新拉取，进入页面直接显示这份缓存。</summary>
        public IReadOnlyList<SongInfo>? CachedRecommend { get; private set; }

        /// <summary>单次拉取的推荐条数（酷狗接口的 pagesize）。</summary>
        private const int recommend_page_size = 50;

        public async Task<IReadOnlyList<SongInfo>> GetRecommendAsync()
        {
            IReadOnlyList<SongInfo> songs = await AppLog.Player.TimeAsync("获取首页推荐",
                () => mapAsync(() => api.GetHomeRecommendAsync(recommend_page_size), KuGouMapper.HomeRecommend),
                r => $"，{r.Count} 首").ConfigureAwait(true);

            CachedRecommend = songs;
            return songs;
        }

        public Task<IReadOnlyList<SongInfo>> GetPersonalFmAsync()
            => AppLog.Player.TimeAsync("获取私人 FM",
                () => mapAsync(() => api.GetPersonalFmAsync(), KuGouMapper.PersonalFm),
                songs => $"，{songs.Count} 首");

        public Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync()
            => AppLog.Player.TimeAsync("获取歌单广场",
                () => mapAsync(() => api.GetRecommendedPlaylistsAsync(0, 1, 30), KuGouMapper.Playlists),
                lists => $"，{lists.Count} 个歌单");

        public Task<IReadOnlyList<SongInfo>> GetPlaylistTracksAsync(string collectionId)
            => AppLog.Player.TimeAsync($"获取歌单歌曲（collection {collectionId}）",
                async () =>
                {
                    var all = new List<SongInfo>();

                    for (int page = 1; page <= 5; page++)
                    {
                        JObject chunk = await api.GetPlaylistTracksAsync(collectionId, page, 100).ConfigureAwait(false);
                        var songs = KuGouMapper.PlaylistTracks(chunk);
                        all.AddRange(songs);

                        long count = chunk["data"]?["count"]?.Value<long>() ?? 0;
                        AppLog.Player.Add($"歌单 {collectionId} 第 {page} 页：本页 {songs.Count} 首，共 {count} 首");

                        if (all.Count >= count || songs.Count == 0)
                            break;
                    }

                    return (IReadOnlyList<SongInfo>)all;
                },
                songs => $"，{songs.Count} 首");

        private static async Task<T> mapAsync<T>(Func<Task<JObject>> request, Func<JObject, T> map)
            => map(await request().ConfigureAwait(false));

        private static string describe(SongInfo? song)
            => song is null ? "(空)" : $"《{song.Name}》- {song.Artist} [{song.Hash[..Math.Min(8, song.Hash.Length)]}]";

        // -------- 播放队列 --------
        public async Task PlayAtAsync(IReadOnlyList<SongInfo> songs, int index)
        {
            if (songs.Count == 0 || index < 0 || index >= songs.Count)
            {
                AppLog.Player.Add($"忽略无效播放请求（列表 {songs.Count} 首，索引 {index}）", LogLevel.Important);
                return;
            }

            AppLog.Player.Add($"播放列表第 {index + 1}/{songs.Count} 首：{describe(songs[index])}", LogLevel.Important);

            Queue.Clear();
            Queue.AddRange(songs);
            currentIndex = index;
            await playCurrentAsync().ConfigureAwait(false);
        }

        public async Task NextAsync()
        {
            if (Queue.Count == 0)
            {
                AppLog.Player.Add("下一首：队列为空，忽略");
                return;
            }

            if (currentIndex < Queue.Count - 1)
            {
                currentIndex++;
                AppLog.Player.Add($"手动下一首 → 第 {currentIndex + 1}/{Queue.Count} 首：{describe(Queue[currentIndex])}");
                await playCurrentAsync().ConfigureAwait(false);
            }
            else if (Repeat.Value == RepeatMode.All)
            {
                currentIndex = 0;
                AppLog.Player.Add($"列表循环：回到第 1/{Queue.Count} 首：{describe(Queue[0])}");
                await playCurrentAsync().ConfigureAwait(false);
            }
            else
            {
                AppLog.Player.Add("已到队列末尾且未开启列表循环，停止播放");
                endHandled = true;
                trackSwitchInProgress = false;
                Schedule(fadeOutAndPause);
            }
        }

        public async Task PreviousAsync()
        {
            if (Queue.Count == 0)
            {
                AppLog.Player.Add("上一首：队列为空，忽略");
                return;
            }

            if (currentIndex > 0)
            {
                currentIndex--;
                AppLog.Player.Add($"手动上一首 → 第 {currentIndex + 1}/{Queue.Count} 首：{describe(Queue[currentIndex])}");
                await playCurrentAsync().ConfigureAwait(false);
            }
            else
            {
                AppLog.Player.Add("已是第一首，回到曲首重新播放");
                Schedule(() =>
                {
                    Seek(0);
                    currentTrack?.Start();
                });
            }
        }

        public void TogglePlay()
        {
            if (currentTrack is null)
            {
                if (Queue.Count > 0)
                {
                    AppLog.Player.Add("无已加载曲目，开始播放队列");
                    Schedule(() =>
                    {
                        if (currentIndex < 0) currentIndex = 0;
                        _ = playCurrentAsync();
                    });
                }
                else
                {
                    AppLog.Player.Add("播放/暂停：队列与音轨均为空，忽略");
                }
                return;
            }

            if (currentTrack.IsRunning && !pausing)
            {
                AppLog.Player.Add("暂停（淡出）");
                pausing = true;
                fadeTarget = 0;
            }
            else
            {
                AppLog.Player.Add("继续播放（淡入）");
                pausing = false;
                fadeTarget = 1;
                currentTrack.Start();
            }
        }

        /// <summary>
        /// 从指定时间开始播放（osu! timing 面板的播放键：<c>editorClock.Seek(t); Start();</c>）。
        /// </summary>
        public void PlayFrom(double timeMs)
        {
            if (currentTrack is null)
            {
                TogglePlay();
                return;
            }

            Seek(timeMs);
            pausing = false;
            fadeTarget = 1;
            currentTrack.Start();
        }

        /// <summary>
        /// 停止并回到指定时间（osu! timing 面板的停止键：<c>editorClock.Stop(); Seek(t);</c>）。
        /// </summary>
        public void StopAt(double timeMs)
        {
            if (currentTrack is not null)
            {
                pausing = true;
                fadeTarget = 0;
            }

            Seek(timeMs);
        }

        /// <summary>一次性精确跳转：点击进度条、松手提交、程序化定位（恢复/循环等）。</summary>
        public void Seek(double timeMs)
        {
            endHandled = false;

            double clamped = clampSeek(timeMs);

            holdDisplayMs = clamped;
            holdActive = true;
            holdSubmitted = true;
            holdSubmittedAtMs = Clock.CurrentTime;
            CurrentTimeMs.Value = clamped;

            requestTrackSeek(clamped);
        }

        /// <summary>
        /// 拖动进度条时的连续定位。是否让音频实时跟手取决于当前音质：
        ///   · 标准 / 高品（mp3）：每个拖动位置立即交给音轨，音频实时跟手；
        ///   · 无损及以上（flac / Hi-Res / DSD）：拖动中**不碰音轨**（只更新界面），松手时由
        ///     <see cref="Seek"/> 提交一次 —— 这些格式在拖动中频繁定位代价高，容易把音频拖卡。
        /// 实时路径下的合并/串行化由 <see cref="requestTrackSeek"/> 负责，不会把几十次定位堆进音频线程同一帧。
        /// </summary>
        public void Scrub(double timeMs)
        {
            endHandled = false;

            double clamped = clampSeek(timeMs);

            holdDisplayMs = clamped;
            holdActive = true;
            holdSubmitted = false;
            CurrentTimeMs.Value = clamped;

            if (isLosslessOrHigher(Quality.Value))
                return;

            requestTrackSeek(clamped);
        }

        /// <summary>无损及以上的音质档位：拖动中不做实时定位（见 <see cref="Scrub"/>）。</summary>
        private static bool isLosslessOrHigher(string quality) => quality is "flac" or "high" or "super";

        /// <summary>
        /// 登记一次定位请求并**立刻**发往音频线程（拖动中的每个位置、点击、松手提交都走这里）。
        ///
        /// 必须合并的原因：音频线程的一帧会把 <c>PendingActions</c> 队列全部串行跑完
        /// （<c>AudioComponent.Update</c> 的 while 出队），每多排队一个 seek 那一帧就多卡一次；
        /// 快速拖动/点击会瞬间塞进几十上百次定位，实测音频线程单帧长达 12.8 秒。
        /// 这里不设任何延迟：没有在飞行中的 seek 就立即发出；已有在飞行中的，只更新目标，
        /// 等它完成后由 <see cref="seekAndReleaseAsync"/> 的收尾接着发最新目标。
        /// 于是音频以 BASS 能处理的最快速度跟手，来不及处理的中间位置被丢弃（不堆积、不卡死）。
        /// </summary>
        private void requestTrackSeek(double timeMs)
        {
            double? immediate = null;

            lock (seekLock)
            {
                seekTargetMs = timeMs;
                hasSeekTarget = true;

                if (!seekInFlight)
                {
                    immediate = timeMs;
                    hasSeekTarget = false;
                    seekInFlight = true;
                }
            }

            if (immediate is { } target)
                _ = seekAndReleaseAsync(target);
        }

        private async Task seekAndReleaseAsync(double target)
        {
            try
            {
                // SeekAsync 只把动作入队给音频线程（seekInternal → ChannelSetPosition 在那边跑），
                // await 只是串行化：保证同时只有一个定位在音频线程上执行。
                if (currentTrack is { } track)
                    await track.SeekAsync(target);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"定位失败：{e.Message}", LogLevel.Error, e);
            }
            finally
            {
                double? next = null;

                lock (seekLock)
                {
                    seekInFlight = false;

                    // 拖动/连点期间登记的中间位置已被合并，这里只取最新的那个接着发。
                    if (hasSeekTarget)
                    {
                        next = seekTargetMs;
                        hasSeekTarget = false;
                        seekInFlight = true;
                    }
                }

                if (next is { } t)
                    _ = seekAndReleaseAsync(t);
            }
        }

        /// <summary>丢弃尚未发出的定位目标（切歌/释放时调用，避免把旧歌的位置 seek 到新歌）。</summary>
        private void clearPendingSeek()
        {
            lock (seekLock)
                hasSeekTarget = false;
        }

        private double clampSeek(double timeMs)
        {
            // Trial clips are only playable up to their decoded length; the bar itself still spans
            // the full song, so a drag past the clip end snaps back into the valid region.
            double max = previewLimitMs > 0 ? Math.Min(previewLimitMs, LengthMs.Value) : LengthMs.Value;

            // There is no data past a trial clip's end: seeking onto its very last sample would park
            // the decoder there with nothing left to play (and unable to resume), so stay just clear.
            if (previewLimitMs > 0)
                max = Math.Max(0, max - preview_seek_guard_ms);

            return Math.Clamp(timeMs, 0, Math.Max(0, max));
        }

        // -------- 淡入淡出 --------

        private void beginFadeIn()
        {
            fadeLevel = 0;
            fadeTarget = 1;
            pausing = false;
            applyTrackVolume(currentTrack);
        }

        private void beginSwitchFadeOut(Track outgoing)
        {
            // If a previous fade is still running, retire it now so only one outgoing track exists.
            retireFadingTrack();

            fadingOutTrack = outgoing;
            fadingOutLevel = fadeLevel;
        }

        private void fadeOutAndPause()
        {
            pausing = true;
            fadeTarget = 0;
        }

        private void retireFadingTrack()
        {
            if (fadingOutTrack is not { } outgoing)
                return;

            fadingOutTrack = null;
            outgoing.Stop();
            outgoing.Dispose();
        }

        /// <summary>Applies the user volume scaled by the current fade, without spamming the bindable.</summary>
        private void applyTrackVolume(Track? track)
        {
            if (track is null)
                return;

            double target = Volume.Value * fadeLevel;

            if (Math.Abs(track.Volume.Value - target) > 0.001)
                track.Volume.Value = target;
        }

        private void updateFades()
        {
            double elapsed = Clock.ElapsedFrameTime;

            fadeLevel = advance(fadeLevel, fadeTarget, fadeTarget > fadeLevel ? fade_in_duration : fade_out_duration, elapsed);
            applyTrackVolume(currentTrack);

            if (pausing && fadeLevel <= 0)
            {
                pausing = false;
                currentTrack?.Stop();
                AppLog.Player.Add("暂停（淡出完成）");
            }

            if (fadingOutTrack is not { } outgoing)
                return;

            fadingOutLevel = advance(fadingOutLevel, 0, fade_out_duration, elapsed);

            double volume = Volume.Value * fadingOutLevel;

            if (Math.Abs(outgoing.Volume.Value - volume) > 0.001)
                outgoing.Volume.Value = volume;

            if (fadingOutLevel <= 0)
                retireFadingTrack();
        }

        private static double advance(double value, double target, double durationMs, double elapsedMs)
        {
            if (durationMs <= 0 || elapsedMs <= 0)
                return target;

            double step = elapsedMs / durationMs;

            return Math.Abs(target - value) <= step ? target : value + Math.Sign(target - value) * step;
        }

        // -------- 播放地址缓存与预解析 --------

        private static string prefetchKey(string hash, string quality) => $"{hash}|{quality}";

        /// <summary>
        /// Returns a still-valid resolved URL for this track (and quality), if one is cached.
        /// The entry is kept (not consumed) so replaying the same track stays instant.
        /// </summary>
        private string? cachedUrl(SongInfo song)
        {
            string key = prefetchKey(song.Hash, Quality.Value);

            if (!urlCache.TryGetValue(key, out var cached))
                return null;

            if (cached.Expiry > DateTime.UtcNow)
                return cached.Url;

            urlCache.TryRemove(key, out _);
            return null;
        }

        /// <summary>Drops a cached URL that turned out to be unusable, so the next attempt resolves afresh.</summary>
        private void invalidateCachedUrl(SongInfo song)
        {
            if (song.IsLocal)
                return;

            if (urlCache.TryRemove(prefetchKey(song.Hash, Quality.Value), out _))
                AppLog.Player.Add($"丢弃失效的缓存地址：{describe(song)}");
        }

        /// <summary>
        /// Resolves a play URL in the background and caches it, so a later click on the same track starts
        /// without waiting for the gateway. Used for the next queue item and for the first rows of a list
        /// the user is looking at.
        /// </summary>
        private Task prefetchUrlAsync(SongInfo song, string quality)
        {
            if (song.IsLocal || verificationRequired is not null)
                return Task.CompletedTask;

            string key = prefetchKey(song.Hash, quality);

            if (urlCache.ContainsKey(key) || !prefetching.TryAdd(key, 0))
                return Task.CompletedTask;

            return Task.Run(async () =>
            {
                try
                {
                    JObject response = await api.GetSongUrlAsync(song.Hash, quality, song.AlbumAudioId).ConfigureAwait(false);

                    if (response["url"] is JArray { Count: > 0 } urls)
                    {
                        urlCache[key] = (urls[0]!.ToString(), DateTime.UtcNow + url_lifetime);
                        AppLog.Player.Debug($"已预解析地址：{describe(song)}");
                        queueStateSave();
                    }
                    else
                    {
                        verificationRequiredHasBeenRaised(response);
                        AppLog.Player.Debug($"预解析未取到地址（status={response["status"]}，{failureText(response)}）：{describe(song)}");
                    }
                }
                catch (Exception e)
                {
                    AppLog.Player.Debug($"预解析地址失败：{describe(song)}：{e.Message}");
                }
                finally
                {
                    prefetching.TryRemove(key, out _);
                }
            });
        }

        /// <summary>
        /// Warms up the first few tracks of a list the user is about to click through: their play URLs are
        /// resolved in the background, and the very first request also establishes the connection to the
        /// gateway (a cold connection to the track router can take a long time, which used to stall the
        /// first play after launch).
        /// </summary>
        public void PrewarmList(IReadOnlyList<SongInfo> songs, int count = 2)
        {
            string quality = Quality.Value;
            int warmed = 0;

            for (int i = 0; i < songs.Count && warmed < count; i++)
            {
                if (songs[i].IsLocal)
                    continue;

                _ = prefetchUrlAsync(songs[i], quality);
                warmed++;
            }
        }

        /// <summary>
        /// Warms up the URL of the track that will play next, so pressing 下一首 (or the queue running on)
        /// does not pay for a round trip. Exactly one track is kept warm, in line with Repeat.
        /// </summary>
        private void prefetchNextTrack()
        {
            if (Queue.Count == 0 || currentIndex < 0)
                return;

            int next = currentIndex + 1;

            if (next >= Queue.Count)
            {
                if (Repeat.Value != RepeatMode.All)
                    return;

                next = 0;
            }

            if (next == currentIndex)
                return;

            _ = prefetchUrlAsync(Queue[next], Quality.Value);
        }

        // -------- 播放状态持久化 --------

        private void queueStateSave()
        {
            stateSaveDelegate?.Cancel();
            stateSaveDelegate = Scheduler.AddDelayed(saveState, 1500);
        }

        private void saveState()
        {
            try
            {
                // 顺手清掉过期地址，避免长时间运行后内存缓存无限增长。
                foreach (var stale in urlCache.Where(e => e.Value.Expiry <= DateTime.UtcNow).ToList())
                    urlCache.TryRemove(stale.Key, out _);

                var state = new JObject
                {
                    ["volume"] = Volume.Value,
                    ["quality"] = Quality.Value,
                    ["tempo"] = Tempo.Value,
                    ["repeat"] = Repeat.Value.ToString(),
                    ["bpmMode"] = BpmMode.Value.ToString(),
                    ["debugMode"] = DebugMode.Value,
                    ["showFps"] = ShowFps.Value,
                    ["fullscreenEffects"] = FullscreenEffects.Value,
                    ["coverRotation"] = CoverRotation.Value.ToString(),
                    ["liked"] = new JArray(Liked.Select(toJson)),
                    ["history"] = new JArray(History.Select(toJson)),
                    ["local"] = new JArray(LocalTracks.Select(toJson)),
                    ["timings"] = new JObject(timingOverrides.Select(p => new JProperty(p.Key, timingToJson(p.Value)))),
                    // 手动套用的歌词（本地曲目没有在线 hash，靠歌名搜来的）：重启后仍然生效。
                    ["lyrics"] = new JObject(lyricOverrides.Take(lyric_overrides_persisted)
                                                          .Select(p => new JProperty(p.Key, p.Value))),
                    // 已解析的播放地址：重启后这些歌立刻可播，无需再等网关。
                    ["urls"] = new JArray(urlCache
                        .Where(e => e.Value.Expiry > DateTime.UtcNow)
                        .OrderByDescending(e => e.Value.Expiry)
                        .Take(url_cache_persisted)
                        .Select(e => new JObject
                        {
                            ["key"] = e.Key,
                            ["url"] = e.Value.Url,
                            ["expiry"] = new DateTimeOffset(e.Value.Expiry).ToUnixTimeSeconds(),
                        })),
                    // 登录凭证：不持久化就会「重启即掉登录」。platform 一并记录，因为概念版与
                    // 标准版的 token 不通用，身份变了就必须重新登录。
                    ["session"] = api.Session.IsLoggedIn
                        ? new JObject
                        {
                            ["token"] = api.Session.Token,
                            ["userid"] = api.Session.UserId,
                            ["vipType"] = api.Session.VipType,
                            ["vipToken"] = api.Session.VipToken,
                            ["platform"] = api.Session.Platform.ToString(),
                        }
                        : null,
                    // 设备身份：mid 由 guid 派生，必须跨启动稳定，否则每次重启都是一个「新设备」，
                    // 服务端会认为账号换了机器而拒绝会员/授权请求。
                    ["device"] = new JObject
                    {
                        ["guid"] = api.Session.Device.Guid,
                        ["dev"] = api.Session.Device.Dev,
                    },
                };

                // 原子写入：先写同目录临时文件，Dispose 时才替换 player-state.json，
                // 中途崩溃/断电不会留下半截文件。
                using Stream stream = host.Storage.CreateFileSafely(state_file);
                using var writer = new StreamWriter(stream);
                writer.Write(state.ToString(Formatting.None));
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"保存播放状态失败：{e.Message}", LogLevel.Error, e);
            }
        }

        private void restoreState()
        {
            try
            {
                using Stream? stream = host.Storage.GetStream(state_file, FileAccess.Read, FileMode.Open);

                if (stream is null)
                    return;

                using var reader = new StreamReader(stream);

                if (JToken.Parse(reader.ReadToEnd()) is not JObject state)
                    return;

                Volume.Value = (float)(state["volume"]?.Value<double>() ?? Volume.Value);
                Tempo.Value = state["tempo"]?.Value<double>() ?? Tempo.Value;
                Repeat.Value = Enum.TryParse(state["repeat"]?.ToString(), out RepeatMode repeat) ? repeat : Repeat.Value;
                BpmMode.Value = Enum.TryParse(state["bpmMode"]?.ToString(), out BpmDetectionMode bpmMode) ? bpmMode : BpmMode.Value;
                DebugMode.Value = state["debugMode"]?.Value<bool>() ?? DebugMode.Value;
                ShowFps.Value = state["showFps"]?.Value<bool>() ?? ShowFps.Value;
                FullscreenEffects.Value = state["fullscreenEffects"]?.Value<bool>() ?? FullscreenEffects.Value;
                CoverRotation.Value = Enum.TryParse(state["coverRotation"]?.ToString(), out CoverRotationMode coverRotation)
                    ? coverRotation
                    : CoverRotation.Value;

                string quality = state["quality"]?.ToString() ?? Quality.Value;
                Quality.Value = quality;

                foreach (JToken liked in state["liked"] ?? new JArray())
                {
                    if (liked is JObject stored)
                        Liked.Add(fromJson(stored));
                }

                foreach (JToken history in state["history"] ?? new JArray())
                {
                    if (history is JObject stored)
                        History.Add(fromJson(stored));
                }

                // 文件里的条数可能多于当前上限（旧版本写的、或手工改过的存档），恢复后立刻收敛到上限，
                // 否则这份超限的历史会一直被原样写回去，还会在历史面板里一次性铺出上万行。
                for (int i = History.Count - history_max; i > 0; i--)
                    History.RemoveAt(History.Count - 1);

                foreach (JToken local in state["local"] ?? new JArray())
                {
                    if (local is JObject stored)
                        LocalTracks.Add(fromJson(stored));
                }

                // 设备身份先于登录恢复：mid 由它派生，登录凭证要跟着同一台「设备」走。
                if (state["device"] is JObject device)
                {
                    if (device["guid"]?.ToString() is { Length: > 0 } guid)
                        api.Session.Device.Guid = guid;

                    if (device["dev"]?.ToString() is { Length: > 0 } dev)
                        api.Session.Device.Dev = dev;
                }

                bool restoredLogin = restoreSession(state["session"] as JObject);

                if (state["timings"] is JObject timings)
                {
                    foreach (JProperty entry in timings.Properties())
                    {
                        if (timingFromJson(entry.Value) is { } restoredTiming)
                            timingOverrides[entry.Name] = restoredTiming;
                    }
                }

                // 手动套用的歌词：本地曲目重启后也应该直接有歌词，不必重新搜索。
                if (state["lyrics"] is JObject lyrics)
                {
                    foreach (JProperty entry in lyrics.Properties())
                    {
                        if (entry.Value.ToString() is { Length: > 0 } text)
                            lyricOverrides[entry.Name] = text;
                    }
                }

                // 上次会话解析过的播放地址：直接复用，重启后点这些歌不再等网关。
                foreach (JToken cached in state["urls"] ?? new JArray())
                {
                    if (cached is not JObject entry)
                        continue;

                    string? key = entry["key"]?.ToString();
                    string? url = entry["url"]?.ToString();
                    long expiry = entry["expiry"]?.Value<long>() ?? 0;

                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url) || expiry <= 0)
                        continue;

                    DateTime expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;

                    if (expiresAt > DateTime.UtcNow)
                        urlCache[key] = (url, expiresAt);
                }

                AppLog.Player.Add($"已恢复播放状态：音量 {Volume.Value:0.##}，音质 {Quality.Value}，倍速 {Tempo.Value:0.##}x，循环 {Repeat.Value}，喜欢 {Liked.Count} 首，历史 {History.Count} 首，本地 {LocalTracks.Count} 首，手改 timing {timingOverrides.Count} 首，缓存地址 {urlCache.Count} 首"
                                  + (restoredLogin ? $"，已恢复登录（用户 ID {LoginUserId}）" : string.Empty), LogLevel.Important);
            }
            catch (FileNotFoundException)
            {
                // First run: nothing restored.
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"读取播放状态失败：{e.Message}", LogLevel.Error, e);
            }
        }

        private static JObject toJson(SongInfo song) => new()
        {
            ["hash"] = song.Hash,
            ["name"] = song.Name,
            ["artist"] = song.Artist,
            ["album"] = song.Album,
            ["duration"] = song.DurationMs,
            ["audioId"] = song.AlbumAudioId,
            ["cover"] = song.CoverUrl,
            ["localPath"] = song.LocalPath,
            ["timing"] = song.Timing is { } timing ? timingToJson(timing) : null,
        };

        private static JArray timingToJson(BeatmapTiming timing) => new(timing.Lines.Select(l => new JObject
        {
            ["time"] = l.TimeMs,
            ["beatLength"] = l.BeatLengthMs,
            ["meter"] = l.Meter,
            ["uninherited"] = l.Uninherited,
            ["kiai"] = l.Kiai,
        }));

        private static SongInfo fromJson(JObject stored) => new(
            stored["hash"]?.ToString() ?? string.Empty,
            stored["name"]?.ToString() ?? string.Empty,
            stored["artist"]?.ToString() ?? string.Empty,
            stored["album"]?.ToString() ?? string.Empty,
            stored["duration"]?.Value<long>() ?? 0,
            stored["audioId"]?.Value<long>() ?? 0,
            stored["cover"]?.ToString(),
            stored["localPath"]?.ToString())
        {
            Timing = timingFromJson(stored["timing"]),
        };

        private static BeatmapTiming? timingFromJson(JToken? stored)
        {
            if (stored is not JArray array || array.Count == 0)
                return null;

            var lines = new List<BeatTimingLine>(array.Count);

            foreach (JToken token in array)
            {
                if (token is not JObject line)
                    continue;

                lines.Add(new BeatTimingLine(
                    line["time"]?.Value<double>() ?? 0,
                    line["beatLength"]?.Value<double>() ?? 0,
                    line["meter"]?.Value<int>() ?? 4,
                    line["uninherited"]?.Value<bool>() ?? false,
                    line["kiai"]?.Value<bool>() ?? false));
            }

            return lines.Count > 0 ? new BeatmapTiming(lines) : null;
        }

        /// <summary>
        /// Restores a persisted login; returns whether a usable session came back.
        /// Tokens are issued per client platform and are NOT interchangeable (酷狗官方说明：
        /// 不同版本的平台的 token 是不通用的), so a token stored under another platform is dropped
        /// instead of being sent with requests that would then fail.
        /// </summary>
        private bool restoreSession(JObject? stored)
        {
            string token = stored?["token"]?.ToString() ?? string.Empty;
            long userId = stored?["userid"]?.Value<long>() ?? 0;

            if (string.IsNullOrEmpty(token) || userId == 0)
                return false;

            string savedPlatform = stored?["platform"]?.ToString() ?? KuGouPlatform.Standard.ToString();

            if (!Enum.TryParse(savedPlatform, out KuGouPlatform platform) || platform != api.Session.Platform)
            {
                AppLog.Player.Add($"登录凭证属于{KuGouConstants.DisplayName(platform)}，当前客户端身份为"
                                  + $"{KuGouConstants.DisplayName(api.Session.Platform)}，凭证不通用，需要重新扫码登录",
                    LogLevel.Important);

                // 通知覆盖层此时可能尚未加载完成，延后到 LoadComplete 再弹。
                pendingLoginNotice = $"已切换为酷狗{KuGouConstants.DisplayName(api.Session.Platform)}身份，请重新扫码登录";

                return false;
            }

            api.Session.SetLogin(token, userId, stored?["vipType"]?.Value<int>() ?? 0, stored?["vipToken"]?.ToString() ?? string.Empty);
            LoggedIn.Value = true;

            // 恢复登录后确认凭证是否仍然有效（酷狗 token 会过期），失效时在日志里留痕。
            _ = probeAccountAsync();

            // 恢复登录后同样确认一次概念版会员状态（也在日志里留痕）。
            _ = fetchConceptVipAsync();

            return true;
        }

        /// <summary>
        /// 恢复登录后校验一次凭证有效性（module/user_info.js）：登录失效时在日志里立刻能看出来。
        /// 复用档案读取，因此也顺带把昵称/性别等缓存进日志，仅记录、不打断启动。
        /// </summary>
        private async Task probeAccountAsync()
        {
            AccountProfile? profile = await GetProfileAsync().ConfigureAwait(false);

            if (profile is not null)
                AppLog.Player.Add($"登录凭证校验：昵称={profile.Nickname}，性别={profile.Gender}，"
                                  + $"生日={profile.Birthday}，乐龄={profile.MusicAge}", LogLevel.Important);
        }

        /// <summary>
        /// Commits a timeline edited in the timing dialog: it is remembered for this song (persisted by
        /// hash, so it survives switching tracks and restarts), pushed into the live song object and
        /// published to the beat source through <see cref="TimingRevision"/>.
        /// </summary>
        public void ApplyTiming(BeatmapTiming timing)
        {
            if (CurrentSong.Value is not { } song)
                return;

            timingOverrides[song.Hash] = timing;

            SongInfo updated = song with { Timing = timing };
            CurrentSong.Value = updated;

            // 队列与本地库里同 hash 的条目一起更新，切歌回来时仍然生效。
            for (int i = 0; i < Queue.Count; i++)
            {
                if (Queue[i].Hash == song.Hash)
                    Queue[i] = updated;
            }

            for (int i = 0; i < LocalTracks.Count; i++)
            {
                if (LocalTracks[i].Hash == song.Hash)
                    LocalTracks[i] = updated;
            }

            AppLog.Player.Add($"timing 已更新：{describe(updated)}｜变速 {timing.TempoLines.Count()} 处，kiai {timing.KiaiSections.Count} 段", LogLevel.Important);

            queueStateSave();
            TimingRevision.Value++;
        }

        // -------- 会员 / 付费 状态 --------
        // 徽标来自列表返回的 privilege/feetype（见 KuGouMapper.fee），随歌曲一起解析，
        // 不需要额外请求，因此列表一渲染出来标识就位。

        public void CycleRepeat()
        {
            Repeat.Value = Repeat.Value switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.None,
            };
            AppLog.Player.Add($"循环模式切换为：{Repeat.Value}", LogLevel.Important);
        }

        /// <summary>切换音质并按新音质重新解析/加载当前曲目（音质变了必须换一个流）。</summary>
        public async Task SetQualityAsync(string quality)
        {
            if (string.Equals(Quality.Value, quality, StringComparison.Ordinal))
                return;

            Quality.Value = quality;
            AppLog.Player.Add($"音质切换为：{quality}，重新加载当前曲目", LogLevel.Important);

            if (currentIndex >= 0)
                await playCurrentAsync().ConfigureAwait(false);
        }

        /// <summary>设置倍速。滑块拖动时直接改 <see cref="Tempo"/> 实时生效，这里只在拖拽结束时记一条日志。</summary>
        public void SetTempo(double tempo)
        {
            Tempo.Value = tempo;
            AppLog.Player.Add($"倍速切换为：{Tempo.Value:0.00}x", LogLevel.Important);
        }

        /// <summary>
        /// 「喜欢」列表的来源：登录后读的是酷狗账号里的「我喜欢」歌单，未登录时退回本地列表。
        /// 返回同步后的完整列表；未登录 / 歌单不可用时返回 null（调用方应改用本地列表）。
        /// </summary>
        /// <param name="force">true 时忽略已缓存的歌单 id 重新拉取。</param>
        /// <param name="onUpdated">同步成功后于更新线程回调刷新的列表，供已打开的面板重绘。</param>
        public async Task<IReadOnlyList<SongInfo>?> RefreshLikedAsync(bool force = false,
            Action<IReadOnlyList<SongInfo>>? onUpdated = null)
        {
            if (!api.Session.IsLoggedIn)
            {
                likedListId = 0;
                return null;
            }

            // 已解析过就直接用内存里的列表（调用方在更新线程，可安全读取）。
            if (likedListId != 0 && !force)
                return Liked.ToList();

            if (riskControlActive())
            {
                AppLog.Player.Add("风控冷却期内跳过账号「我喜欢」同步，沿用当前列表", LogLevel.Important);
                return likedListId != 0 ? Liked.ToList() : null;
            }

            // 并发调用合并：打开面板和点喜欢可能同时进来，没必要各打一次十几秒的请求。
            if (likedSyncInFlight is { } running)
                return await running.ConfigureAwait(true);

            if (force && DateTime.UtcNow - likedSyncedAt < liked_force_min_interval)
            {
                AppLog.Player.Add($"距上次同步不足 {liked_force_min_interval.TotalMinutes:0} 分钟，跳过强制刷新");
                return likedListId != 0 ? Liked.ToList() : null;
            }

            Task<IReadOnlyList<SongInfo>?> sync = syncLikedAsync(onUpdated);
            likedSyncInFlight = sync;

            try
            {
                return await sync.ConfigureAwait(true);
            }
            finally
            {
                likedSyncInFlight = null;
                likedSyncedAt = DateTime.UtcNow;
            }
        }

        private async Task<IReadOnlyList<SongInfo>?> syncLikedAsync(Action<IReadOnlyList<SongInfo>>? onUpdated)
        {
            try
            {
                long listId = await resolveLikedListIdAsync().ConfigureAwait(false);

                if (listId == 0)
                    return null;

                likedListId = listId;

                JObject tracks = await AppLog.Player.TimeAsync("同步账号「我喜欢」",
                    () => api.GetUserPlaylistTracksAsync(listId, 1, liked_page_size),
                    r => $"，{countOf(r)} 首").ConfigureAwait(false);

                IReadOnlyList<SongInfo> songs = KuGouMapper.UserPlaylistTracks(tracks);

                // 酷狗「我喜欢」按加入时间从旧到新返回；界面统一按从新到旧展示。
                songs = songs.Reverse().ToList();

                AppLog.Player.Add($"账号「我喜欢」共 {songs.Count} 首", LogLevel.Important);

                Schedule(() =>
                {
                    Liked.Clear();

                    foreach (SongInfo song in songs)
                        Liked.Add(song);

                    onUpdated?.Invoke(songs);
                });

                return songs;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"同步账号「我喜欢」失败：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        /// <summary>在账号的歌单里找「我喜欢」：优先名字含「喜欢」，其次是标记为默认收藏的歌单。</summary>
        private async Task<long> resolveLikedListIdAsync()
        {
            JObject response = await AppLog.Player.TimeAsync("查询账号歌单",
                () => api.GetUserPlaylistsAsync(1, 100),
                r => $"，{countOf(r)} 个").ConfigureAwait(false);

            IReadOnlyList<UserPlaylistInfo> playlists = KuGouMapper.UserPlaylists(response);

            UserPlaylistInfo? liked = playlists.FirstOrDefault(p => p.Name.Contains("喜欢", StringComparison.Ordinal))
                                      ?? playlists.FirstOrDefault(p => p.IsDefault);

            if (liked is null)
                AppLog.Player.Add($"账号里没有「我喜欢」歌单（共 {playlists.Count} 个歌单）", LogLevel.Important);

            return liked?.ListId ?? 0;
        }

        /// <summary>歌单接口的曲目数组长度（仅用于日志）。</summary>
        private static int countOf(JObject response)
            => ((JArray?)response["data"]?["info"] ?? (JArray?)response["data"]?["songs"])?.Count ?? 0;

        /// <summary>收藏状态（登录后即账号歌单里的状态）。</summary>
        public bool IsLiked(SongInfo song) => findLiked(song) is not null;

        /// <summary>按 hash 匹配（来自不同接口的同一首歌，专辑/文件 id 可能不同，不能用整条 record 比较）。</summary>
        private SongInfo? findLiked(SongInfo song)
            => song.Hash.Length == 0
                ? null
                : Liked.FirstOrDefault(s => string.Equals(s.Hash, song.Hash, StringComparison.OrdinalIgnoreCase));

        public async Task ToggleLikeAsync()
        {
            SongInfo? song = CurrentSong.Value;

            if (song is null)
            {
                AppLog.Player.Add("喜欢/取消喜欢：无当前曲目，忽略");
                return;
            }

            // 未登录或本地导入的曲目没有酷狗 hash：只能记在本地。
            if (!api.Session.IsLoggedIn || song.IsLocal || song.Hash.Length == 0)
            {
                toggleLocalLike(song);
                return;
            }

            IReadOnlyList<SongInfo>? account = await RefreshLikedAsync().ConfigureAwait(true);

            if (account is null || likedListId == 0)
            {
                AppLog.Player.Add("账号「我喜欢」歌单不可用，本次收藏只记在本地", LogLevel.Important);
                toggleLocalLike(song);
                return;
            }

            SongInfo? existing = account.FirstOrDefault(s => string.Equals(s.Hash, song.Hash, StringComparison.OrdinalIgnoreCase));

            try
            {
                if (existing is not null)
                {
                    SongInfo toRemove = existing;

                    JObject response = await AppLog.Player.TimeAsync("账号「我喜欢」删除曲目",
                        () => api.DeleteSongsFromPlaylistAsync(likedListId, new[] { fileIdOf(toRemove) }),
                        failureText).ConfigureAwait(false);

                    if (!isPlaylistOk(response))
                    {
                        AppLog.Player.Add($"取消喜欢失败：{failureText(response)}", LogLevel.Error);
                        return;
                    }

                    Schedule(() => Liked.Remove(toRemove));
                    AppLog.Player.Add($"取消喜欢（账号歌单）：{describe(song)}", LogLevel.Important);
                }
                else
                {
                    JObject response = await AppLog.Player.TimeAsync("账号「我喜欢」添加曲目",
                        () => api.AddSongToPlaylistAsync(likedListId, song.Name, song.Hash, song.AlbumId, song.AlbumAudioId),
                        failureText).ConfigureAwait(false);

                    if (!isPlaylistOk(response))
                    {
                        AppLog.Player.Add($"添加喜欢失败：{failureText(response)}", LogLevel.Error);
                        return;
                    }

                    Schedule(() =>
                    {
                        if (findLiked(song) is null)
                            Liked.Add(song);
                    });

                    AppLog.Player.Add($"添加喜欢（账号歌单）：{describe(song)}", LogLevel.Important);
                }
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"喜欢操作失败：{e.Message}", LogLevel.Error, e);
            }
        }

        /// <summary>歌单接口成功时 status=1；部分响应只有 error_code/errcode=0。</summary>
        private static bool isPlaylistOk(JObject response)
        {
            if (response["status"] is not null)
                return response["status"]!.Value<int>() == 1;

            return (response["error_code"] ?? response["errcode"])?.Value<int>() == 0;
        }

        /// <summary>删歌用 fileid；接口没给时回落到 album_audio_id（参考实现同样回落到 mixSongId）。</summary>
        private static long fileIdOf(SongInfo song) => song.FileId != 0 ? song.FileId : song.AlbumAudioId;

        /// <summary>未登录（或本地曲目）时的纯本地收藏。</summary>
        private void toggleLocalLike(SongInfo song)
        {
            SongInfo? existing = findLiked(song);

            if (existing is not null)
            {
                Liked.Remove(existing);
                AppLog.Player.Add($"取消喜欢：{describe(song)}");
            }
            else
            {
                // 与账号「我喜欢」一致：最新的排最前。
                Liked.Insert(0, song);
                AppLog.Player.Add($"添加喜欢：{describe(song)}");
            }
        }

        // -------- 本地音乐 --------

        /// <summary>
        /// Imports files chosen by the user (.osz / .olz beatmap archives and common audio formats) into
        /// <see cref="LocalTracks"/>. Parsing and archive extraction run on a thread-pool thread; entries
        /// already in the library (same file) are skipped. Returns how many tracks were added.
        /// </summary>
        public async Task<int> ImportLocalAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                return 0;

            var importer = new LocalImporter(host.Storage);
            IReadOnlyList<SongInfo> imported;

            try
            {
                imported = await Task.Run(() => importer.ImportAll(paths)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"本地导入失败：{e.Message}", LogLevel.Error, e);
                return 0;
            }

            var completion = new TaskCompletionSource<int>();

            Schedule(() =>
            {
                int added = 0;

                foreach (SongInfo song in imported)
                {
                    if (song.LocalPath is not { } localPath)
                        continue;

                    if (LocalTracks.Any(t => string.Equals(t.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    LocalTracks.Add(song);
                    added++;
                }

                AppLog.Player.Add($"本地导入完成：新增 {added} 首，本地库共 {LocalTracks.Count} 首", LogLevel.Important);
                completion.SetResult(added);
            });

            return await completion.Task.ConfigureAwait(false);
        }

        private async Task playCurrentAsync()
        {
            // 本次加载请求的序号；加载失败时用它判断是否还有更新的请求在跑（有的话由它收尾）。
            long serial = ++playSerial;

            // Raise the switch gate synchronously, before the first await: while this song is loading,
            // Update() must not treat the previous (completed) track's HasCompleted as a new auto-advance.
            trackSwitchInProgress = true;

            // Trial state from the previous song must not leak into the one loading now; it is
            // re-established below once the new track is actually decoded.
            isPreviewTrack = false;
            previewLimitMs = 0;

            SongInfo song = Queue[currentIndex];

            // 手动调整过的 timing 优先（按 hash 记忆），覆盖谱面自带或分析得到的时间轴。
            if (timingOverrides.TryGetValue(song.Hash, out BeatmapTiming? editedTiming))
                song = song with { Timing = editedTiming };

            AppLog.Player.Add($"开始加载：{describe(song)}，音质 {Quality.Value}，时长 {song.DurationMs / 1000:0}s");
            CurrentSong.Value = song;
            CurrentCover.Value = song.CoverUrl ?? string.Empty;
            LyricsReset();
            // 评论同样按曲目隔离：不清空的话，新歌加载期间评论区还挂着上一首的评论。
            CommentsReset();
            StatusText.Value = "正在加载…";
            endHandled = false;
            CurrentStreamUrl = null;
            addHistory(song);

            string url;
            bool preview;

            if (song.IsLocal)
            {
                // 本地曲目：直接解码磁盘文件，不走网关，也没有会员试听。
                url = song.LocalPath!;
                preview = false;
                AppLog.Player.Add($"本地曲目，直接解码：{url}");
            }
            else
            {
                if (await resolveOnlineUrlAsync(song, serial).ConfigureAwait(false) is not { } resolved)
                {
                    abortPlayback(serial, song, "解析播放地址失败");
                    return;
                }

                (url, preview) = resolved;
            }

            // 被更新的请求取代后不要再改共享状态：CurrentStreamUrl 会被节拍分析当成「当前曲目」的
            // 地址，写错了就是给新歌分析上一首的音频（音轨本身的丢弃守卫在下面的 Schedule 里）。
            if (serial != playSerial)
            {
                AppLog.Player.Add($"丢弃过期的解析结果：{describe(song)}");
                return;
            }

            CurrentStreamUrl = url;

            Track? track;
            try
            {
                track = await Task.Run(() => song.IsLocal ? localTrackStore.Get(url) : trackStore.Get(url)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"音频流加载异常：{describe(song)}：{AppLog.SafeUrl(url)}", LogLevel.Error, e);
                invalidateCachedUrl(song);
                StatusText.Value = "音频流加载失败";
                abortPlayback(serial, song, "音频流加载异常");
                return;
            }

            if (track is null)
            {
                AppLog.Player.Add($"音频流加载失败（trackStore 返回 null）：{describe(song)}：{AppLog.SafeUrl(url)}", LogLevel.Error);
                invalidateCachedUrl(song);
                StatusText.Value = "音频流加载失败";
                abortPlayback(serial, song, "音频流加载失败");
                return;
            }

            // TrackBass.Start/Stop must run on the audio/update thread (they WaitSafely
            // internally and throw when called from an async continuation).
            var previous = currentTrack;

            Schedule(() =>
            {
                if (serial != playSerial)
                {
                    // A newer play request superseded this one.
                    AppLog.Player.Add($"丢弃过期音轨加载结果：{describe(song)}");
                    track.Dispose();
                    return;
                }

                currentTrack = track;
                currentTrackHash = song.Hash;
                holdActive = false;
                holdSubmitted = false;
                clearPendingSeek();
                track.Tempo.Value = Tempo.Value;

                // 试听片段：进度条仍展示整首歌长度，但只允许拖到片段实际解码长度。
                isPreviewTrack = preview;
                previewLimitMs = preview ? (track.Length > 0 ? track.Length : 60000) : 0;

                // New track fades up from silence; the outgoing one is kept in the mix for one
                // short fade instead of being cut off, which is what makes switching click-free.
                beginFadeIn();

                if (previous is not null)
                    beginSwitchFadeOut(previous);

                endHandled = false;
                track.Start();
                IsPlaying.Value = true;
                trackSwitchInProgress = false;

                // 解码开始后 track.Length 才可靠，用它校正片段长度与进度条总时长。
                syncPreviewLimit(track);

                double total = displayedLength(song, track, preview);
                if (total > 0)
                    LengthMs.Value = total;

                // 登录后重解析当前曲目时，尽量从中断处继续（试听升级为完整播放不应回到曲首）。
                if (resumeOnStartMs > 0)
                {
                    double resume = resumeOnStartMs;
                    resumeOnStartMs = 0;
                    Seek(resume);
                }

                AppLog.Player.Add($"开始播放：{describe(song)}（音轨长度 {track.Length / 1000:0.#}s）{(preview ? "·非会员试听片段" : string.Empty)}", LogLevel.Important);

                // 选歌跟踪：点的歌、列表标签右侧显示的时长、会员/试听状态、进度条总时长。
                AppLog.Player.Add(
                    $"选歌跟踪：点的歌={describe(song)}｜标签时长={durationLabel(song.DurationMs)}｜"
                    + $"会员状态={feeLabel(song.Fee)}·{(preview ? $"试听片段 {previewLimitMs / 1000:0.#}s" : "完整播放")}"
                    + $"｜进度条总时长={durationLabel(LengthMs.Value)}", LogLevel.Important);
            });

            StatusText.Value = preview ? "非会员试听 60 秒" : string.Empty;

            if (preview)
            {
                // osu! 通知系统：非会员命中试听片段时提示一次。
                notifications?.Post(new SimpleNotification
                {
                    Text = "非会员仅支持试听 60 秒",
                    IsImportant = false,
                });
            }

            PlayGeneration.Value++;

            // 本地曲目没有酷狗歌词可查。
            if (!song.IsLocal)
                _ = loadLyricsAsync(song);

            Schedule(prefetchNextTrack);
        }

        /// <summary>
        /// 加载失败时的统一收尾。切歌失败必须把上一首停掉并释放：否则旧音轨会一直播放（界面显示的是新歌），
        /// 同时还占着 BASS 解码器与 HTTP 连接，越切越容易加载不出来。
        /// 另外要把切歌闸门放开，否则 Update() 里的自动切歌会被永久卡住。
        ///
        /// 例外：失败的是「当前正在播放的这首歌」（例如登录后重解析），此时不打断它。
        /// </summary>
        private void abortPlayback(long serial, SongInfo song, string reason)
        {
            Schedule(() =>
            {
                if (serial != playSerial)
                {
                    // 已有更新的播放请求在跑，交给它收尾。
                    return;
                }

                endHandled = true;
                trackSwitchInProgress = false;

                if (currentTrackHash is not null && currentTrackHash == song.Hash)
                {
                    AppLog.Player.Add($"重新解析失败，继续播放当前音轨（{reason}）：{describe(song)}");
                    return;
                }

                retireFadingTrack();

                Track? stale = currentTrack;
                currentTrack = null;
                currentTrackHash = null;
                holdActive = false;
                holdSubmitted = false;
                clearPendingSeek();

                if (stale is not null)
                {
                    stale.Stop();
                    stale.Dispose();
                    AppLog.Player.Add($"已停止并释放上一首（{reason}）", LogLevel.Important);
                }

                // 复位淡入淡出状态，否则下一首会从 0 音量淡入而听起来像没声音。
                fadeLevel = 1;
                fadeTarget = 1;
                pausing = false;
                isPreviewTrack = false;
                previewLimitMs = 0;
                resumeOnStartMs = 0;
                CurrentStreamUrl = null;
                CurrentTimeMs.Value = 0;
                LengthMs.Value = 0;
                IsPlaying.Value = false;
            });
        }

        /// <summary>
        /// Resolves a playable URL for a KuGou track, falling back to the 60-second trial clip when the
        /// full stream is refused. Returns null when nothing is playable - the status text and logs are
        /// already written in that case.
        /// </summary>
        private async Task<(string Url, bool Preview)?> resolveOnlineUrlAsync(SongInfo song, long serial)
        {
            // 选歌请求可能在本方法的网络等待期间被更新的请求取代（用户又点了别的歌）。
            // 取代发生后本次续段绝不能再写任何共享状态：否则新歌会拿到上一首的封面、状态文案
            // 甚至播放态 —— 也就是"播放一首歌却继承了上一首的部分条目"。所有共享写入前都要过这个判断。
            bool stale() => serial != playSerial;

            JObject response;
            string? prefetched = cachedUrl(song);

            if (prefetched is not null)
            {
                AppLog.Player.Add($"命中缓存地址，跳过解析：{describe(song)}", LogLevel.Important);
                response = new JObject
                {
                    ["url"] = new JArray(prefetched),
                    ["status"] = 1,
                };
            }
            else if (verificationRequired is { } blocked)
            {
                // 风控未解除前不再发请求：继续点歌只会让网关把整个会话继续挂在验证事件上。
                AppLog.Player.Add($"跳过线上解析（{blocked}）：{describe(song)}");

                if (!stale())
                {
                    StatusText.Value = blocked;
                    IsPlaying.Value = false;
                    PlayGeneration.Value++;
                    endHandled = true;
                }

                return null;
            }
            else
            {
                try
                {
                    response = await AppLog.Player.TimeAsync($"解析播放地址 {describe(song)}",
                        () => api.GetSongUrlAsync(song.Hash, Quality.Value, song.AlbumAudioId),
                        r => $"，status={r["status"]}, urls={(r["url"] as JArray)?.Count ?? 0}");
                }
                catch (Exception e)
                {
                    AppLog.Player.Add($"播放地址获取失败：{describe(song)}：{e.Message}", LogLevel.Error, e);

                    if (!stale())
                    {
                        StatusText.Value = $"播放地址获取失败：{e.Message}";
                        IsPlaying.Value = false;
                        // Keep the completed previous track from retriggering auto-advance every frame.
                        endHandled = true;
                    }

                    return null;
                }
            }

            // 正式地址被拒时依次尝试：备用音源（relate_goods）→ 概念版免费听授权 → 60 秒试听片段。
            verificationRequiredHasBeenRaised(response);
            bool preview = false;

            if (response["url"] is not JArray { Count: > 0 } && verificationRequired is null
                && await tryRelatedHashUrlsAsync(song).ConfigureAwait(false) is { } related)
            {
                response = related;
            }

            if (response["url"] is not JArray { Count: > 0 } && verificationRequired is null && IsLoggedIn
                && await tryAuthorizedUrlAsync(song).ConfigureAwait(false) is { } authorized)
            {
                response = authorized;
            }

            if (response["url"] is not JArray { Count: > 0 } && verificationRequired is null)
            {
                if (await tryTrialUrlAsync(song).ConfigureAwait(false) is { } trial)
                {
                    response = trial;
                    preview = true;
                }
            }

            var urls = response["url"] as JArray;
            if (urls is not { Count: > 0 })
            {
                // 已被更新的请求取代：下面整段都是「上一首」的失败 / 会员提示，不能再写进新歌的状态。
                if (stale())
                    return null;

                if (verificationRequired is { } required)
                {
                    // 网关要求人机验证（errcode 20028）：这与会员/版权无关，再重试也是同样的结果。
                    AppLog.Player.Add($"无可播放地址（{required}）：{describe(song)}", LogLevel.Important);
                    StatusText.Value = required;
                }
                else
                {
                    // status=2 / priv_status=0：曲目为会员专享，账号在该权益下无播放权限。
                    bool restricted = response["status"]?.Value<int>() == 2 || response["priv_status"]?.Value<int>() == 0;

                    if (restricted)
                    {
                        AppLog.Player.Add($"该曲目需要会员权益（{failureText(response)}）：{describe(song)}", LogLevel.Important);
                        StatusText.Value = "该曲目需要酷狗概念版会员权益";
                    }
                    else
                    {
                        AppLog.Player.Add($"无可播放地址（status={response["status"]}，{failureText(response)}）：{describe(song)}", LogLevel.Important);
                        StatusText.Value = $"该曲目暂无可播放地址（{failureText(response)}）";
                    }
                }

                IsPlaying.Value = false;
                PlayGeneration.Value++; // still pulse the UI once
                endHandled = true;
                return null;
            }

            // The hi-res cover rides along with the play response; only the online path has one.
            // 过期请求不得覆盖封面：这是"新歌显示上一首封面"最直观的来源。
            string? cover = response["trans_param"]?["union_cover"]?.ToString();

            if (!stale() && !string.IsNullOrEmpty(cover))
                CurrentCover.Value = cover.Replace("{size}", "240");

            string resolvedUrl = urls[0]!.ToString();

            // 完整曲目的地址入缓存（试听片段不缓存：下次仍应尝试拿完整地址）。
            // 命中缓存时用同样的逻辑顺延有效期，让反复播放的歌一直免解析。
            if (!preview)
            {
                urlCache[prefetchKey(song.Hash, Quality.Value)] = (resolvedUrl, DateTime.UtcNow + url_lifetime);
                queueStateSave();
            }

            return (resolvedUrl, preview);
        }

        /// <summary>
        /// Asks KuGou for the 60-second trial clip of a member-only track (IsFreePart=1). Returns the
        /// response when a playable clip came back, or null when even the trial is unavailable - in which
        /// case the caller reports the track as VIP-only.
        /// </summary>
        private async Task<JObject?> tryTrialUrlAsync(SongInfo song)
        {
            try
            {
                JObject trial = await AppLog.Player.TimeAsync($"解析试听地址 {describe(song)}",
                    () => api.GetSongUrlAsync(song.Hash, Quality.Value, song.AlbumAudioId, freePart: true),
                    r => $"，status={r["status"]}, urls={(r["url"] as JArray)?.Count ?? 0}").ConfigureAwait(false);

                if (trial["url"] is JArray { Count: > 0 })
                {
                    AppLog.Player.Add($"非会员试听：{describe(song)}（60 秒片段）", LogLevel.Important);
                    return trial;
                }

                verificationRequiredHasBeenRaised(trial);
                AppLog.Player.Add($"无可试听片段（status={trial["status"]}，{failureText(trial)}）：{describe(song)}", LogLevel.Important);
                return null;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"试听地址解析失败：{describe(song)}：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        /// <summary>
        /// 备用音源：向 /privilege/lite 查询该曲目的 relate_goods（其它 hash / 音质版本），逐个尝试拿地址。
        /// 参考实现（EchoMusic）就是靠这一步把会员曲目换成可播放的版本。
        /// </summary>
        private async Task<JObject?> tryRelatedHashUrlsAsync(SongInfo song)
        {
            try
            {
                JObject privilege = await AppLog.Player.TimeAsync($"查询备用音源 {describe(song)}",
                    () => api.GetSongPrivilegeLiteAsync(song.Hash),
                    r => $"，status={r["status"]}, relate={relateGoodsCount(r)}").ConfigureAwait(false);

                if (privilege["data"] is not JArray { Count: > 0 } records
                    || records[0] is not JObject record
                    || record["relate_goods"] is not JArray goods)
                {
                    AppLog.Player.Add($"无备用音源（{failureText(privilege)}）：{describe(song)}");
                    return null;
                }

                foreach (JToken good in goods)
                {
                    string candidateHash = good["hash"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrEmpty(candidateHash)
                        || candidateHash.Equals(song.Hash, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string candidateQuality = good["quality"]?.ToString() ?? Quality.Value;

                    JObject response = await api.GetSongUrlAsync(candidateHash, candidateQuality, song.AlbumAudioId)
                        .ConfigureAwait(false);

                    if (response["url"] is JArray { Count: > 0 })
                    {
                        AppLog.Player.Add($"使用备用音源（{candidateQuality}）：{describe(song)}", LogLevel.Important);
                        return response;
                    }

                    verificationRequiredHasBeenRaised(response);

                    if (verificationRequired is not null)
                        return null;
                }

                AppLog.Player.Add($"备用音源均不可播：{describe(song)}");
                return null;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"查询备用音源失败：{describe(song)}：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        private static int relateGoodsCount(JObject response)
        {
            if (response["data"] is JArray { Count: > 0 } records && records[0] is JObject record
                && record["relate_goods"] is JArray goods)
                return goods.Count;

            return 0;
        }

        /// <summary>
        /// 概念版「免费听」：先向授权服务换 auth/open_time，再向网关换正式地址（完整曲目）。
        /// 未登录或账号在概念版下没有权益时授权拿不到，返回 null，由调用方回退到 60 秒试听。
        /// </summary>
        private async Task<JObject?> tryAuthorizedUrlAsync(SongInfo song)
        {
            try
            {
                JObject? response = await AppLog.Player.TimeAsync($"解析概念版免费听地址 {describe(song)}",
                    () => api.TryGetAuthorizedUrlAsync(song.Hash, Quality.Value, song.AlbumAudioId),
                    r => $"，status={r?["status"]}, urls={(r?["url"] as JArray)?.Count ?? 0}").ConfigureAwait(false);

                if (response?["url"] is JArray { Count: > 0 })
                {
                    AppLog.Player.Add($"概念版免费听：{describe(song)}（完整曲目）", LogLevel.Important);
                    return response;
                }

                if (response is not null)
                    verificationRequiredHasBeenRaised(response);

                AppLog.Player.Add($"概念版免费听未取到地址（status={response?["status"]}，{failureTextOrEmpty(response)}）：{describe(song)}");
                return null;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"概念版免费听解析失败：{describe(song)}：{e.Message}", LogLevel.Error, e);
                return null;
            }
        }

        /// <summary>
        /// 识别网关下发的「需要验证」人机校验（errcode 20028 / error 文本含“需要验证”）。命中后记录一次、
        /// 弹一次提示并短路后续解析——否则每点一首歌都会继续累积风控事件。
        /// </summary>
        private bool verificationRequiredHasBeenRaised(JObject response)
        {
            bool challenged = response["errcode"]?.Value<long>() == 20028
                              || response["error_code"]?.Value<long>() == 20028
                              || (response["error"]?.ToString()?.Contains("需要验证", StringComparison.Ordinal) ?? false);

            if (!challenged)
                return false;

            // 风控一旦触发，后续请求会被拖到十几到二十秒：先记冷却，让自动流程停止继续打请求。
            riskControlUntil = DateTime.UtcNow + risk_control_delay;

            if (verificationRequired is not null)
                return true;

            if (DateTime.UtcNow < verificationRetryAfter)
            {
                AppLog.Player.Add($"处于安全验证冷却期，暂不再次发起（{failureText(response)}）");
                return true;
            }

            verificationRequired = "酷狗要求安全验证，请在浏览器弹出的验证页完成验证";
            AppLog.Player.Add($"网关要求安全验证（{failureText(response)}），开始验证流程", LogLevel.Important);

            notifications?.Post(new SimpleNotification
            {
                Text = "酷狗要求安全验证：已打开浏览器验证页，完成后自动继续播放",
                IsImportant = true,
            });

            string eventId = api.LastSsaCode ?? string.Empty;

            if (string.IsNullOrEmpty(eventId))
            {
                AppLog.Player.Add("响应未携带 ssa-code，无法发起验证", LogLevel.Error);
                abortVerification();
                notifications?.Post(new SimpleNotification { Text = "安全验证事件获取失败，稍后请重试", IsImportant = true });
                return true;
            }

            _ = beginVerificationAsync(eventId);
            return true;
        }

        /// <summary>
        /// 走完酷狗的人机验证：取验证配置 → 系统浏览器完成腾讯验证码 → 提交校验。成功后解除拦截并续播当前曲目。
        /// </summary>
        private async Task beginVerificationAsync(string eventId)
        {
            try
            {
                JObject info = await AppLog.Player.TimeAsync("获取安全验证配置",
                    () => api.GetVerifyInfoAsync(eventId),
                    r => $"，status={r["status"]}").ConfigureAwait(false);

                if (info["data"] is not JObject verifyInfo)
                {
                    AppLog.Player.Add($"获取安全验证配置失败：{failureText(info)}", LogLevel.Error);
                    abortVerification();
                    return;
                }

                string txAppId = verifyInfo["txappid"]?.ToString() ?? string.Empty;
                int verifyType = verifyInfo["v_type"]?.Value<int>() ?? 0;

                if (string.IsNullOrEmpty(txAppId))
                {
                    AppLog.Player.Add($"暂不支持的验证方式（v_type={verifyType}），请先在酷狗官方客户端完成验证",
                        LogLevel.Important);
                    abortVerification();
                    return;
                }

                AppLog.Player.Add($"开始安全验证：v_type={verifyType}，txappid={txAppId}", LogLevel.Important);

                CaptchaBrowser.CaptchaResult? result =
                    await CaptchaBrowser.SolicitAsync(txAppId, TimeSpan.FromMinutes(3)).ConfigureAwait(false);

                if (result is null)
                {
                    AppLog.Player.Add("安全验证未完成（超时或取消）", LogLevel.Important);
                    abortVerification();
                    notifications?.Post(new SimpleNotification { Text = "安全验证未完成，稍后重新点歌可再试一次" });
                    return;
                }

                string verifyCode = $"{KuGouConstants.tcaptcha_code_prefix}|{new JObject
                {
                    ["ticket"] = result.Ticket,
                    ["randstr"] = result.RandStr,
                    ["txappid"] = txAppId,
                }.ToString(Formatting.None)}";

                JObject submitted = await AppLog.Player.TimeAsync("提交安全验证",
                    () => api.SubmitVerificationAsync(eventId, verifyType, verifyCode),
                    r => $"，status={r["status"]}").ConfigureAwait(false);

                if (submitted["status"]?.Value<int>() != 1)
                {
                    // 验证接口只会给一个笼统的 code（例如 20010），原始响应必须留下，否则事后无法判断原因。
                    AppLog.Player.Add($"安全验证未通过：{failureText(submitted)}，原始响应 {submitted.ToString(Formatting.None)}",
                        LogLevel.Error);
                    abortVerification();
                    notifications?.Post(new SimpleNotification { Text = "安全验证未通过，稍后请重试", IsImportant = true });
                    return;
                }

                verificationRequired = null;
                verificationRetryAfter = default;

                // 验证通过就等于风控解除：立刻结束冷却，否则后续操作还会被自己挡住。
                riskControlUntil = default;
                AppLog.Player.Add("安全验证通过，风控已解除，恢复正常播放", LogLevel.Important);
                notifications?.Post(new SimpleNotification { Text = "安全验证已通过" });

                // 重试那次触发验证的操作（例如被 20028 拦下的会员领取）。
                if (pendingVerificationRetry is { } retry)
                {
                    pendingVerificationRetry = null;
                    AppLog.Player.Add("验证完成，重试刚才被风控拦截的操作", LogLevel.Important);
                    _ = retry();
                }
                else if (CurrentSong.Value is { } song)
                {
                    AppLog.Player.Add($"验证完成，重新解析当前曲目：{describe(song)}", LogLevel.Important);
                    resumeOnStartMs = CurrentTimeMs.Value;
                    _ = playCurrentAsync();
                }
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"安全验证流程失败：{e.Message}", LogLevel.Error, e);
                abortVerification();
            }
        }

        /// <summary>验证没能完成：解除拦截并进入冷却，稍后重新点歌即可再次发起。</summary>
        private void abortVerification()
        {
            verificationRequired = null;
            pendingVerificationRetry = null;
            verificationRetryAfter = DateTime.UtcNow + verification_retry_delay;
        }

        /// <summary>
        /// 主动把用户送去浏览器完成安全验证（账号面板在风控期间点「领取/升级」时调用）。
        /// 20028 只能靠完成人机验证解除，冷却期间静默跳过只会让人以为功能坏了。
        /// </summary>
        public void RequestBrowserVerification()
        {
            if (!api.Session.IsLoggedIn)
                return;

            if (verificationRequired is not null)
            {
                AppLog.Player.Add("安全验证流程已在进行中（浏览器验证页已打开）");
                return;
            }

            if (DateTime.UtcNow < verificationRetryAfter)
            {
                AppLog.Player.Add("上一次安全验证被取消或超时，请稍后再试");
                return;
            }

            string eventId = api.LastSsaCode ?? string.Empty;

            if (string.IsNullOrEmpty(eventId))
            {
                AppLog.Player.Add("暂无可用的验证事件（还在等网关下发 20028），稍后重试即可发起验证", LogLevel.Important);
                notifications?.Post(new SimpleNotification { Text = "暂无可用的验证事件，稍后再试", IsImportant = true });
                return;
            }

            verificationRequired = "酷狗要求安全验证，请在浏览器弹出的验证页完成验证";
            AppLog.Player.Add("按要求转入浏览器验证", LogLevel.Important);

            notifications?.Post(new SimpleNotification
            {
                Text = "已打开浏览器验证页，完成后会自动继续",
                IsImportant = true,
            });

            _ = beginVerificationAsync(eventId);
        }

        /// <summary>是否处于风控冷却期（20028 之后）：期间不再自动打易触发风控的接口。</summary>
        private bool riskControlActive() => DateTime.UtcNow < riskControlUntil;

        /// <summary>风控期间给账号面板/状态栏的统一文案。</summary>
        private static string riskControlText()
            => "酷狗要求安全验证（20028）：已转入浏览器验证，完成后会自动重试";

        /// <summary>把网关的业务错误整理成 “code message”，便于日志与状态栏直接展示。</summary>
        private static string failureText(JObject response)
        {
            string code = (response["errcode"] ?? response["error_code"] ?? response["err_code"])?.ToString() ?? "?";
            string message = (response["error"] ?? response["errmsg"] ?? response["err_msg"] ?? response["errMsg"]
                              ?? response["msg"] ?? response["message"] ?? response["error_msg"])?.ToString()
                             ?? "未知错误";
            return $"{code} {message}";
        }

        private static string failureTextOrEmpty(JObject? response)
            => response is null ? "无响应" : failureText(response);

        public BindableList<LrcLine> Lyrics { get; } = [];

        /// <summary>
        /// 手动套用的歌词（按当前曲目 hash 记忆）：本地导入的音乐没有在线 hash，用歌名搜到同名歌曲后
        /// 把它的歌词借过来。同一次会话内切歌回来仍然生效。
        /// </summary>
        private readonly Dictionary<string, string> lyricOverrides = new();

        private void LyricsReset()
        {
            Lyrics.Clear();
            activeLyricIndex = -1;
            ActiveLyricIndex.Value = -1;
        }

        public BindableInt ActiveLyricIndex { get; } = new(-1);
        private int activeLyricIndex = -1;

        // -------- 评论（只读） --------

        /// <summary>当前曲目的评论。切歌即清空。接口是只读的，这里不提供发表/点赞/回复。</summary>
        public BindableList<SongComment> Comments { get; } = [];

        /// <summary>评论总数（接口返回的 count）。</summary>
        public BindableInt CommentTotal { get; } = new();

        /// <summary>评论区状态文案：加载中 / 失败 / 不支持（本地曲目）。空串表示可以正常展示列表。</summary>
        public Bindable<string> CommentStatus { get; } = new(string.Empty);

        private int commentPage;
        private bool commentsLoading;
        private string? commentsForHash;

        private void CommentsReset()
        {
            Comments.Clear();
            CommentTotal.Value = 0;
            CommentStatus.Value = string.Empty;
            commentPage = 0;
            commentsLoading = false;
            commentsForHash = null;
        }

        /// <summary>读取一页评论并解析。UI 与诊断入口共用同一实现，避免两份口径。</summary>
        public async Task<(int Total, List<SongComment> Items)> FetchCommentsAsync(SongInfo song, int page,
            int pageSize = 20)
        {
            JObject response = await api.GetSongCommentsAsync(song.Hash, song.AlbumAudioId, song.Name, page, pageSize)
                .ConfigureAwait(false);

            int total = response["count"]?.Value<int>() ?? 0;
            var items = new List<SongComment>();

            foreach (JToken item in response["list"] as JArray ?? [])
            {
                SongComment? comment = parseComment(item);

                if (comment is not null)
                    items.Add(comment);
            }

            return (total, items);
        }

        /// <summary>
        /// 拉取（<paramref name="append"/> = false）或追加下一页评论。
        /// 结果经 <c>Schedule</c> 写回并校验 hash：切歌后到达的旧请求结果一律丢弃
        /// （不然上一首的评论会显示在新歌下面）。
        /// </summary>
        public async Task LoadCommentsAsync(SongInfo song, bool append = false)
        {
            if (song.IsLocal)
            {
                CommentStatus.Value = "本地导入的曲目没有在线评论。";
                return;
            }

            if (song.AlbumAudioId == 0)
            {
                CommentStatus.Value = "这首歌缺少评论标识（mixsongid），无法读取评论。";
                return;
            }

            if (commentsLoading)
                return;

            commentsLoading = true;
            int page = append ? commentPage + 1 : 1;

            if (!append)
            {
                commentsForHash = song.Hash;

                Schedule(() =>
                {
                    Comments.Clear();
                    CommentTotal.Value = 0;
                    CommentStatus.Value = "评论加载中…";
                });
            }

            try
            {
                (int total, List<SongComment> items) = await FetchCommentsAsync(song, page).ConfigureAwait(false);

                AppLog.Player.Add($"评论加载完成：{describe(song)}，第 {page} 页 {items.Count} 条 / 共 {total} 条");

                Schedule(() =>
                {
                    if (commentsForHash != song.Hash)
                        return;

                    commentPage = page;
                    CommentTotal.Value = total;
                    Comments.AddRange(items);

                    if (items.Count == 0)
                        CommentStatus.Value = CommentTotal.Value == 0 ? "这首歌还没有评论。" : "没有更多评论了。";
                    else
                        CommentStatus.Value = string.Empty;
                });
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"评论加载失败：{describe(song)}：{e.Message}", LogLevel.Error, e);

                Schedule(() =>
                {
                    if (commentsForHash == song.Hash)
                        CommentStatus.Value = $"评论加载失败：{e.Message}";
                });
            }
            finally
            {
                commentsLoading = false;
            }
        }

        /// <summary>把接口的一条评论映射成模型；没有正文的条目（纯图片/被过滤）直接丢弃。</summary>
        private static SongComment? parseComment(JToken item)
        {
            string? content = item["content"]?.ToString();

            if (string.IsNullOrEmpty(content))
                return null;

            return new SongComment(
                item["id"]?.Value<long>() ?? 0,
                item["user_name"]?.ToString() ?? "匿名用户",
                content,
                item["like"]?["likenum"]?.Value<int>() ?? 0,
                item["addtime"]?.ToString() ?? string.Empty,
                item["user_pic"]?.ToString(),
                item["location"]?.ToString(),
                item["puser"]?.ToString(),
                item["pcontent"]?.ToString());
        }

        private async Task loadLyricsAsync(SongInfo song)
        {
            try
            {
                // 手动套用过的歌词优先，其次才按曲目自身的 hash 找。
                string? text = lyricOverrides.TryGetValue(song.Hash, out string? applied)
                    ? applied
                    : await api.GetLyricTextAsync(song.Hash, song.DurationMs).ConfigureAwait(false);
                if (CurrentSong.Value != song)
                {
                    AppLog.Player.Debug($"歌词到达但已切歌，丢弃：{describe(song)}");
                    return;
                }

                if (string.IsNullOrEmpty(text))
                {
                    AppLog.Player.Add($"无歌词：{describe(song)}");
                    return;
                }

                var lines = LrcParser.Parse(text);
                AppLog.Player.Add($"歌词加载完成：{describe(song)}，{lines.Count} 行");
                Schedule(() =>
                {
                    if (CurrentSong.Value != song) return;
                    Lyrics.Clear();
                    Lyrics.AddRange(lines);
                });
            }
            catch (Exception e)
            {
                // Lyrics are decorative for this screen; keep the failure visible in logs only.
                AppLog.Player.Add($"歌词加载失败：{describe(song)}：{e.Message}", LogLevel.Error, e);
            }
        }

        /// <summary>
        /// 取在线搜索结果 <paramref name="source"/> 的歌词并套用到**当前正在播放**的曲目上，
        /// 供「在线搜索歌词」用：本地导入的音乐没有在线 hash，拿同名歌曲的歌词来补。
        /// 返回给面板展示的结果文案。
        /// </summary>
        public async Task<string> ApplyLyricsFromAsync(SongInfo source)
        {
            if (CurrentSong.Value is not { } current)
                return "当前没有正在播放的曲目";

            try
            {
                string? text = await api.GetLyricTextAsync(source.Hash, source.DurationMs).ConfigureAwait(false);

                if (string.IsNullOrEmpty(text))
                    return $"《{source.Name}》没有可用歌词";

                var lines = LrcParser.Parse(text);

                if (lines.Count == 0)
                    return $"《{source.Name}》的歌词解析为空";

                // 记住这次套用：当前曲目自身没有在线歌词，切歌回来还要用。
                lyricOverrides[current.Hash] = text;
                queueStateSave();

                Schedule(() =>
                {
                    if (CurrentSong.Value?.Hash != current.Hash)
                        return;

                    LyricsReset();
                    Lyrics.AddRange(lines);
                });

                AppLog.Player.Add($"在线歌词已套用：《{source.Name}》→ {describe(current)}，{lines.Count} 行",
                    LogLevel.Important);

                return $"已套用《{source.Name}》的歌词（{lines.Count} 行）";
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"在线歌词获取失败：《{source.Name}》：{e.Message}", LogLevel.Error, e);
                return $"获取失败：{e.Message}";
            }
        }

        private void addHistory(SongInfo song)
        {
            History.Remove(song);
            History.Insert(0, song);

            // 迭代裁剪：先算清要丢掉几条，再逐条删除；不是递归，条数再大也不会加深调用栈。
            for (int i = History.Count - history_max; i > 0; i--)
                History.RemoveAt(History.Count - 1);
        }

        protected override void Update()
        {
            base.Update();

            updateFades();

            if (currentTrack is not { } track) return;

            IsPlaying.Value = track.IsRunning;

            // 试听片段要展示整首歌的总时长（不能被 60 秒片段覆盖），只持续校正可拖动上限；
            // 完整曲目的解码长度可能晚一步就绪，这里持续补齐。
            if (isPreviewTrack)
                syncPreviewLimit(track);
            else if (track.Length > 0)
                LengthMs.Value = track.Length;

            double actualMs = track.CurrentTime;

            // 界面停留：拖动中一直停在用户拖到的位置（此时不 seek 音轨，不做落地判定）；
            // 松手/点击提交后，停在目标位置直到音轨真正到位 —— 否则会被"旧的播放位置"拽回去。
            if (holdActive && holdSubmitted
                && (Math.Abs(actualMs - holdDisplayMs) <= seek_settle_tolerance_ms
                    || Clock.CurrentTime - holdSubmittedAtMs > seek_hold_timeout_ms))
                holdActive = false;

            CurrentTimeMs.Value = holdActive ? holdDisplayMs : actualMs;

            // Active lyric line.
            int found = -1;
            for (int i = 0; i < Lyrics.Count; i++)
            {
                if (Lyrics[i].TimeMs <= track.CurrentTime)
                    found = i;
                else
                    break;
            }
            if (found != activeLyricIndex)
            {
                activeLyricIndex = found;
                ActiveLyricIndex.Value = found;
            }

            if (trackFinished(track) && !endHandled && !trackSwitchInProgress)
            {
                endHandled = true;
                if (Repeat.Value == RepeatMode.One)
                {
                    AppLog.Player.Add("单曲循环：曲目结束，重新开始");
                    track.Seek(0);
                    track.Start();
                    endHandled = false;
                }
                else
                {
                    AppLog.Player.Add("曲目自然播放结束，自动切下一首");
                    Schedule(() => _ = NextAsync());
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="track"/> has finished. <see cref="Track.HasCompleted"/> alone misses
        /// the case where a seek parked the decoder on the last sample: the channel stops without ever
        /// reporting completion, which used to leave playback stuck (the play button could not resume).
        /// </summary>
        private static bool trackFinished(Track track)
            => track.HasCompleted
               || (track.Length > 0 && !track.IsRunning && track.CurrentTime >= track.Length - end_epsilon_ms);

        /// <summary>
        /// The length the progress bar should span for a just-started track. A trial clip must not
        /// shrink it: the bar keeps covering the whole song, and only falls back to the clip length
        /// when the song's metadata length is unknown. Full tracks use the decoded length, which is
        /// only trustworthy once decoding has started.
        /// </summary>
        private static double displayedLength(SongInfo song, Track track, bool preview)
        {
            if (preview && song.DurationMs > 0)
                return song.DurationMs;

            return track.Length > 0 ? track.Length : song.DurationMs;
        }

        /// <summary>
        /// A trial clip's decoded length only becomes known once decoding has started (it is 0 before
        /// <see cref="Track.Start"/>), so the seekable cap is re-read from the track every frame.
        /// </summary>
        private void syncPreviewLimit(Track track)
        {
            if (isPreviewTrack && track.Length > 0)
                previewLimitMs = track.Length;
        }

        /// <summary>A duration for logs, using the same formatting as the song lists.</summary>
        private static string durationLabel(double durationMs)
        {
            string text = SongInfo.FormatDuration(durationMs);
            return text.Length > 0 ? text : "(未知)";
        }

        private static string feeLabel(SongFee fee) => fee switch
        {
            SongFee.Vip => "VIP",
            SongFee.Paid => "付费",
            SongFee.Free => "免费",
            _ => "未知",
        };

        // -------- 扫码登录 --------
        public async Task<(string Key, string QrImageDataUri)> CreateQrAsync()
        {
            JObject response = await AppLog.Player.TimeAsync("创建登录二维码",
                () => api.CreateQrKeyAsync(),
                r =>
                {
                    string key = r["data"]?["qrcode"]?.ToString() ?? string.Empty;
                    return $"，errcode={r["errcode"]}, key={key[..Math.Min(12, key.Length)]}";
                });

            return (response["data"]?["qrcode"]?.ToString() ?? string.Empty,
                    response["data"]?["qrcode_img"]?.ToString() ?? string.Empty);
        }

        /// <summary>Returns the QR login inner status: 0 expired, 1 waiting, 2 scanned, 4 authorized.</summary>
        public async Task<int> PollQrAsync(string key)
        {
            JObject response = await api.CheckQrAsync(key).ConfigureAwait(false);
            int status = response["data"]?["status"]?.Value<int>() ?? 0;

            AppLog.Player.Add($"二维码登录状态轮询 → {status}");

            if (status == 4)
                completeLogin("扫码登录");

            return status;
        }

        // -------- 其它登录方式（验证码 / 账号密码 / 微信 / token 快速登录） --------
        // 逐条对齐 EchoMusic 的登录模块；接口本身在 KuGouApi，这里只负责「调用 → 判定 →
        // 登录后的收尾」，让各方式的收尾完全一致。

        /// <summary>发送手机短信验证码；返回 null 表示已发出，否则是给用户看的失败原因。</summary>
        public async Task<string?> SendSmsCodeAsync(string mobile)
        {
            try
            {
                JObject response = await AppLog.Player.TimeAsync("发送短信验证码",
                    () => api.SendMobileCodeAsync(mobile),
                    r => $"，status={r["status"]}，errcode={r["errcode"]}").ConfigureAwait(false);

                if (response["status"]?.Value<int>() == 1)
                    return null;

                return describeLoginFailure(response);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"发送短信验证码失败：{e.Message}", LogLevel.Error, e);
                return e.Message;
            }
        }

        public Task<string?> LoginBySmsAsync(string mobile, string code)
            => finishLoginAsync("验证码登录", () => api.LoginByVerifyCodeAsync(mobile, code));

        public Task<string?> LoginByPasswordAsync(string username, string password)
            => finishLoginAsync("账号密码登录", () => api.LoginByPasswordAsync(username, password));

        /// <summary>token 快速登录：用手里的登录凭证换一份新登录态，不用扫码也不用密码。</summary>
        public Task<string?> LoginByTokenAsync()
            => finishLoginAsync("快速登录", () => api.LoginByTokenAsync());

        /// <summary>微信扫码登录第一步：拿二维码（uuid + 图片 data-uri）。</summary>
        public async Task<(string Uuid, string QrImage)> CreateWeChatQrAsync()
        {
            (string uuid, string image) = await api.CreateWeChatQrAsync().ConfigureAwait(false);

            AppLog.Player.Add($"已创建微信登录二维码（uuid={uuid[..Math.Min(12, uuid.Length)]}）");

            return (uuid, image);
        }

        /// <summary>长轮询微信扫码结果（wx_errcode：408 未扫、404 已扫待确认、405 已确认、403 取消/过期）。</summary>
        public Task<JObject> CheckWeChatQrAsync(string uuid) => api.CheckWeChatQrAsync(uuid);

        /// <summary>微信确认后，用它给的授权码换酷狗登录态。</summary>
        public Task<string?> LoginByWeChatAsync(string wxCode)
            => finishLoginAsync("微信登录", () => api.LoginByOpenPlatAsync(wxCode));

        /// <summary>
        /// 各登录方式的公共收尾：判定响应里有没有真的拿到登录态，成功就刷新会员、重读「喜欢」并立刻落盘。
        /// 返回 null 表示登录成功，否则是给用户看的失败原因。
        /// </summary>
        private async Task<string?> finishLoginAsync(string what, Func<Task<JObject>> login)
        {
            JObject response;

            try
            {
                response = await AppLog.Player.TimeAsync(what, login,
                    r => $"，status={r["status"]}，errcode={r["errcode"]}").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"{what}失败：{e.Message}", LogLevel.Error, e);
                return e.Message;
            }

            if (!api.Session.IsLoggedIn)
            {
                string reason = describeLoginFailure(response);
                AppLog.Player.Add($"{what}未成功：{reason}", LogLevel.Important);
                return reason;
            }

            completeLogin(what);
            return null;
        }

        /// <summary>
        /// 登录成功后的统一收尾。扫码登录（<see cref="PollQrAsync"/>）也走这里，
        /// 保证几种登录方式在「会员状态 / 喜欢歌单 / 落盘」上的行为完全一致。
        /// </summary>
        private void completeLogin(string what)
        {
            LoggedIn.Value = true;
            AppLog.Player.Add($"{what}成功，用户 ID：{LoginUserId}", LogLevel.Important);
            refreshMemberState();
            _ = RefreshLikedAsync(force: true); // 登录后「喜欢」改为读账号歌单
            saveState();                        // 登录凭证立即落盘，重启后仍然保持登录
        }

        /// <summary>把登录失败响应翻译成一句话（各接口的字段名不统一，逐个兜）。</summary>
        private static string describeLoginFailure(JObject response)
        {
            foreach (string field in new[] { "error", "msg", "message", "errmsg" })
            {
                if (response[field]?.ToString() is { Length: > 0 } text)
                    return text;
            }

            string? code = response["errcode"]?.ToString() ?? response["error_code"]?.ToString();

            return code is { Length: > 0 } and not "0"
                ? $"酷狗返回错误码 {code}"
                : "登录未成功，请检查填写内容";
        }

        /// <summary>
        /// Re-applies the account's member rights right after a login/logout. The play-URL gateway reads
        /// the credentials off the session on every request, so a currently playing 60-second trial is
        /// re-resolved immediately (continuing at the same position) instead of waiting for the next song.
        /// </summary>
        private void refreshMemberState()
        {
            AppLog.Player.Add(
                $"会员状态刷新：已登录={api.Session.IsLoggedIn}，用户 ID={LoginUserId}，"
                + $"VIP 类型={api.Session.VipType}，VIP 凭证={(string.IsNullOrEmpty(api.Session.VipToken) ? "无" : "有")}",
                LogLevel.Important);

            // 概念版权益按天领取：登录后立刻补领「畅听会员」并尝试升级「概念会员」，
            // 领取流程结束后会自行刷新会员状态并重解析当前曲目。
            _ = claimDailyVipAsync();
        }

        /// <summary>
        /// 概念版会员权益是每日领取的（KGLiteVIP 的自动领取流程）：
        /// 查询本月领取记录 → 未领取则领「畅听会员」→ 每次尝试升级「概念会员」→ 刷新会员状态。
        /// </summary>
        private async Task claimDailyVipAsync()
        {
            if (!api.Session.IsLoggedIn)
                return;

            // 启动恢复登录与扫码登录成功都会走到这里，同一次会话只自动跑一遍。
            if (vipClaimAttempted)
            {
                AppLog.Player.Add("自动领取本次会话已跑过，跳过");
                return;
            }

            vipClaimAttempted = true;

            if (riskControlActive())
            {
                AppLog.Player.Add("处于风控冷却期，跳过自动领取与升级", LogLevel.Important);
                return;
            }

            // 领取日按北京时间计算（接口要求的 receive_day 即北京时间日期）。
            string today = beijingToday();

            if (vipClaimDate == today)
            {
                // 今天已经处理过：仍尝试升级（升级是幂等的），然后刷新状态。
                await UpgradeConceptVipAsync().ConfigureAwait(false);
            }
            else if (await alreadyClaimedTodayAsync(today).ConfigureAwait(false))
            {
                vipClaimDate = today;
                AppLog.Player.Add($"畅听会员：今日（{today}）已领取");
                await UpgradeConceptVipAsync().ConfigureAwait(false);
            }
            else
            {
                await ClaimDailyListenVipAsync().ConfigureAwait(false);
                await UpgradeConceptVipAsync().ConfigureAwait(false);
            }

            // 领取过程触发风控时不必再刷新会员状态（这一步也是要打接口的）。
            if (riskControlActive())
                return;

            await fetchConceptVipAsync().ConfigureAwait(false);
            RetryCurrentTrackAfterVipChange();
        }

        /// <summary>领取当日「畅听会员」（账号面板按钮 + 自动流程共用）。返回可直接展示的结果文案。</summary>
        public async Task<string> ClaimDailyListenVipAsync()
        {
            if (!api.Session.IsLoggedIn)
                return "请先登录";

            if (riskControlActive())
            {
                // 冷却不是终点：把用户送去浏览器验证，并在验证通过后自动重试这次领取。
                AppLog.Player.Add("畅听会员领取处于风控冷却期，转入浏览器验证", LogLevel.Important);
                pendingVerificationRetry = () => ClaimDailyListenVipAsync();
                RequestBrowserVerification();
                return riskControlText();
            }

            string today = beijingToday();

            try
            {
                JObject claim = await AppLog.Player.TimeAsync("领取每日畅听会员",
                    () => api.ClaimDailyListenVipAsync(today),
                    r => $"，status={r["status"]}，{resultText(r)}").ConfigureAwait(false);

                // 20028 是风控/人机验证，不是普通业务失败：转入浏览器验证，验证通过后自动重试领取。
                if (verificationRequiredHasBeenRaised(claim))
                {
                    AppLog.Player.Add($"领取畅听会员触发风控：{failureText(claim)}", LogLevel.Important);
                    pendingVerificationRetry = () => ClaimDailyListenVipAsync();
                    return riskControlText();
                }

                // 成功：status=1；已领取：msg 里带「已领取」。
                bool claimed = claim["status"]?.Value<int>() == 1
                               || (claim["msg"]?.ToString()?.Contains("已领取", StringComparison.Ordinal) ?? false);

                if (claimed)
                    vipClaimDate = today;

                string message = claimed ? $"畅听会员：{resultText(claim)}" : $"畅听会员领取失败：{failureText(claim)}";
                AppLog.Player.Add(message, claimed ? LogLevel.Important : LogLevel.Error);
                return message;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"领取畅听会员失败：{e.Message}", LogLevel.Error, e);
                return $"领取畅听会员失败：{e.Message}";
            }
        }

        /// <summary>升级为「概念会员」（账号面板按钮 + 自动流程共用）。</summary>
        public async Task<string> UpgradeConceptVipAsync()
        {
            if (!api.Session.IsLoggedIn)
                return "请先登录";

            if (riskControlActive())
            {
                AppLog.Player.Add("概念会员升级处于风控冷却期，转入浏览器验证", LogLevel.Important);
                pendingVerificationRetry = () => UpgradeConceptVipAsync();
                RequestBrowserVerification();
                return riskControlText();
            }

            try
            {
                JObject upgrade = await AppLog.Player.TimeAsync("升级概念版会员",
                    () => api.UpgradeConceptVipAsync(),
                    r => $"，status={r["status"]}，{resultText(r)}").ConfigureAwait(false);

                if (verificationRequiredHasBeenRaised(upgrade))
                {
                    AppLog.Player.Add($"升级概念会员触发风控：{failureText(upgrade)}", LogLevel.Important);
                    pendingVerificationRetry = () => UpgradeConceptVipAsync();
                    return riskControlText();
                }

                // 成功：status=1；297002 表示今日已升级；msg 里带「已升级」同理。
                bool upgraded = upgrade["status"]?.Value<int>() == 1
                                || upgrade["error_code"]?.Value<int>() == 297002
                                || (upgrade["msg"]?.ToString()?.Contains("已升级", StringComparison.Ordinal) ?? false);

                string message = upgraded ? $"概念会员：{resultText(upgrade)}" : $"概念会员升级失败：{failureText(upgrade)}";
                AppLog.Player.Add(message, upgraded ? LogLevel.Important : LogLevel.Error);
                return message;
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"升级概念会员失败：{e.Message}", LogLevel.Error, e);
                return $"升级概念会员失败：{e.Message}";
            }
        }

        /// <summary>账号档案（昵称 / 头像 / 用户 ID / 性别 / 生日 / 乐龄），供账号面板展示。</summary>
        public sealed record AccountProfile(string Nickname, string? AvatarUrl, long UserId,
            string Gender, string Birthday, string MusicAge);

        /// <summary>概念版两种会员的到期时间文案（畅听 = tvip，概念 = svip）。</summary>
        public sealed record AccountVip(string ChangTingExpiry, string ConceptExpiry);

        /// <summary>
        /// 读取账号档案（module/user_info.js）。字段名随接口版本变化，这里按候选名宽松查找；
        /// 取不到的一律显示「未知」，不影响其它字段。
        /// </summary>
        public async Task<AccountProfile?> GetProfileAsync()
        {
            if (!api.Session.IsLoggedIn)
                return null;

            string nickname = string.Empty;
            string? avatar = null;
            string gender = "未知";
            string birthday = "未设置";
            string musicAge = "未知";

            JObject response = new();

            // 会话内首次调用偶发被服务端以 20006 拒绝，隔一会儿重试即可拿到数据。
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(attempt * 1000).ConfigureAwait(false);

                try
                {
                    response = await AppLog.Player.TimeAsync("查询账号资料",
                        () => api.GetMyUserInfoAsync(),
                        r => $"，status={r["status"]}").ConfigureAwait(false);

                    if (response["data"] is JObject)
                        break;
                }
                catch (Exception e)
                {
                    AppLog.Player.Add($"查询账号资料失败（第 {attempt} 次）：{e.Message}", LogLevel.Error, e);
                }
            }

            try
            {
                if (response["data"] is not JObject scope)
                {
                    AppLog.Player.Add($"账号资料未返回数据：{failureText(response)}");
                }
                else
                {
                    // 资料可能直接挂在 data 下，也可能再包一层 detail / user。
                    JToken detail = scope["detail"] is JObject d ? d : scope;

                    nickname = firstString(scope, "nickname", "user_name", "username", "name", "nick_name", "nickName")
                               ?? firstString(detail, "nickname", "user_name", "username", "name", "nick_name", "nickName")
                               ?? string.Empty;

                    avatar = firstString(scope, "pic", "user_pic", "userpic", "avatar", "img", "photo")
                             ?? firstString(detail, "pic", "user_pic", "userpic", "avatar", "img", "photo");

                    gender = formatGender(firstString(scope, "gender", "sex") ?? firstString(detail, "gender", "sex"));

                    birthday = formatDateTime(
                                   firstString(scope, "birthday", "birth", "birth_day", "birthday_time")
                                   ?? firstString(detail, "birthday", "birth", "birth_day", "birthday_time"),
                                   "yyyy-MM-dd")
                               // 接口未设置生日时 birthday 为空串，改用 birth_info.descr（如「未设置」）。
                               ?? firstString(scope["birth_info"] ?? new JObject(), "descr")
                               ?? "未设置";

                    musicAge = formatMusicAge(scope) ?? formatMusicAge(detail) ?? "未知";

                    // 字段名不确定，把原始 data 记一段，便于之后按真实字段名校正解析。
                    string raw = scope.ToString(Newtonsoft.Json.Formatting.None);
                    AppLog.Player.Add($"账号资料：昵称={(string.IsNullOrEmpty(nickname) ? "(空)" : nickname)}，"
                                      + $"性别={gender}，生日={birthday}，乐龄={musicAge}，"
                                      + $"头像={(string.IsNullOrEmpty(avatar) ? "无" : "有")}，状态={failureText(response)}");
                    AppLog.Player.Add($"账号资料原始：{(raw.Length <= 600 ? raw : raw[..600] + "…")}");
                }
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"解析账号资料失败：{e.Message}", LogLevel.Error, e);
            }

            return new AccountProfile(
                string.IsNullOrEmpty(nickname) ? $"酷狗用户 {api.Session.UserId}" : nickname,
                avatar,
                api.Session.UserId,
                gender,
                birthday,
                musicAge);
        }

        /// <summary>
        /// 畅听会员（tvip）与概念会员（svip）的到期时间：来自 module/user_vip_detail.js 的
        /// <c>data.busi_vip[]</c>，其 <c>vip_end_time</c> 本身就是「yyyy-MM-dd HH:mm:ss」（精确到秒）。
        /// </summary>
        public async Task<AccountVip> GetVipExpiriesAsync()
        {
            if (!api.Session.IsLoggedIn)
                return new AccountVip("未登录", "未登录");

            try
            {
                JObject response = await api.GetUserVipDetailAsync().ConfigureAwait(false);

                if (nested(response, "data", "busi_vip") is not JArray products || products.Count == 0)
                {
                    string reason = $"未获取到（{failureText(response)}）";
                    return new AccountVip(reason, reason);
                }

                return new AccountVip(productExpiry(products, "tvip"), productExpiry(products, "svip"));
            }
            catch (Exception e)
            {
                string reason = $"查询失败：{e.Message}";
                return new AccountVip(reason, reason);
            }
        }

        /// <summary>从 busi_vip 取指定产品的到期时间；未开通时给出明确文案。</summary>
        private static string productExpiry(JArray products, string productType)
        {
            JToken? product = products.FirstOrDefault(p => p["product_type"]?.ToString() == productType);

            if (product is null || product["is_vip"]?.Value<int>() != 1)
                return "未开通";

            string? expiry = formatDateTime(product["vip_end_time"]?.ToString(), "yyyy-MM-dd HH:mm:ss");

            return string.IsNullOrEmpty(expiry) ? "已开通（无到期时间）" : $"到期 {expiry}";
        }

        /// <summary>性别：0 女 / 1 男 / 2 保密。</summary>
        private static string formatGender(string? raw) => raw switch
        {
            "1" => "男",
            "0" => "女",
            "2" => "保密",
            _ => "未知",
        };

        /// <summary>
        /// 规范化时间字段：接口可能给「yyyy-MM-dd HH:mm:ss」字符串，也可能给秒/毫秒级 Unix 时间戳。
        /// 取不到返回 null；字符串原样返回（含秒的 vip_end_time 因此保持精确到秒）。
        /// </summary>
        private static string? formatDateTime(string? raw, string format)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string value = raw.Trim();

            if (long.TryParse(value, out long stamp) && stamp > 0)
            {
                DateTimeOffset time = stamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(stamp)
                    : DateTimeOffset.FromUnixTimeSeconds(stamp);

                return time.ToOffset(TimeSpan.FromHours(8)).ToString(format);
            }

            // 已是日期字符串：请求日期（yyyy-MM-dd）时把后面的时间部分截掉。
            if (format == "yyyy-MM-dd" && value.Length > 10 && value[10] == ' ')
                return value[..10];

            return value;
        }

        /// <summary>乐龄：优先读接口给出的年龄字段，否则用注册时间换算成「x 年」。</summary>
        private static string? formatMusicAge(JToken scope)
        {
            if (firstString(scope, "music_age", "user_age", "listen_age", "age", "yue_ling") is { } age
                && int.TryParse(age, out int years) && years > 0)
                return $"{years} 年";

            string? registered = firstString(scope, "reg_time", "regtime", "rege_time", "register_time",
                "create_time", "first_time", "join_time");

            if (registered is null || formatDateTime(registered, "yyyy-MM-dd") is not { } date)
                return null;

            if (!DateTime.TryParse(date, out DateTime reg))
                return null;

            int span = DateTime.Now.Year - reg.Year;

            if (reg.Date > DateTime.Now.AddYears(-span).Date)
                span--;

            return span > 0 ? $"{span} 年" : "不足 1 年";
        }

        /// <summary>接口要求的 receive_day 为北京时间日期。</summary>
        private static string beijingToday()
            => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");

        /// <summary>权益变化后重试当前曲目（面板上手动领取/升级成功后调用）。</summary>
        public void RetryCurrentTrackAfterVipChange()
        {
            if (verificationRequired is not null || CurrentSong.Value is not { } song)
                return;

            AppLog.Player.Add($"权益变化，重新解析当前曲目：{describe(song)}");
            resumeOnStartMs = CurrentTimeMs.Value;
            _ = playCurrentAsync();
        }

        /// <summary>按顺序取第一个非空字符串字段（响应结构随接口变化，做成宽松查找）。</summary>
        private static string? firstString(JToken token, params string[] keys)
        {
            if (token is not JObject obj)
                return null;

            foreach (string key in keys)
            {
                if (obj[key]?.ToString() is { Length: > 0 } value)
                    return value;
            }

            return null;
        }

        /// <summary>本月领取记录里是否已有今天（避免与手机端/其它客户端重复领取）。</summary>
        private async Task<bool> alreadyClaimedTodayAsync(string today)
        {
            try
            {
                JObject record = await api.GetMonthVipRecordAsync().ConfigureAwait(false);

                if (nested(record, "data", "list") is not JArray list)
                    return false;

                foreach (JToken item in list)
                {
                    if (item["day"]?.ToString() == today)
                        return true;
                }
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"查询畅听会员领取记录失败：{e.Message}");
            }

            return false;
        }

        /// <summary>从领取/升级响应里取一句可读结果（msg/message，缺省视为成功）。</summary>
        private static string resultText(JObject response)
            => (response["msg"] ?? response["message"])?.ToString() is { Length: > 0 } text ? text : "成功";

        /// <summary>
        /// 安全取嵌套字段：路径上任一层不是 JSON 对象时返回 null。
        /// 直接用 <c>token["a"]["b"]</c> 在中间层是字符串/数字时会抛 JValue 索引异常。
        /// </summary>
        private static JToken? nested(JToken? token, params string[] path)
        {
            JToken? current = token;

            foreach (string key in path)
            {
                if (current is not JObject obj)
                    return null;

                current = obj[key];
            }

            return current;
        }

        /// <summary>
        /// 查询账号在酷狗概念版下的会员产品（module/user_vip_detail.js）。
        /// 概念版与酷狗音乐会员是两个独立体系，必须看这里的 tvip（畅听会员）/ svip（概念会员）
        /// 才知道账号在概念版下有没有权益；播放权益本身仍由网关按 token 判定。
        /// </summary>
        private async Task fetchConceptVipAsync()
        {
            try
            {
                JObject response = await AppLog.Player.TimeAsync("查询概念版会员",
                    () => api.GetUserVipDetailAsync(),
                    r => $"，status={r["status"]}").ConfigureAwait(false);

                if (nested(response, "data", "busi_vip") is not JArray products || products.Count == 0)
                {
                    AppLog.Player.Add($"概念版会员：未获取到产品列表（{failureText(response)}）", LogLevel.Important);
                    return;
                }

                var active = new List<string>();

                foreach (JToken product in products)
                {
                    if (product["is_vip"]?.Value<int>() != 1)
                        continue;

                    active.Add(product["product_type"]?.ToString() switch
                    {
                        "tvip" => "畅听会员",
                        "svip" => "概念会员",
                        var other => other ?? "未知产品",
                    });
                }

                AppLog.Player.Add(active.Count > 0
                    ? $"概念版会员：{string.Join("、", active)}"
                    : "概念版会员：未开通（畅听会员 / 概念会员均无）", LogLevel.Important);
            }
            catch (Exception e)
            {
                AppLog.Player.Add($"查询概念版会员失败：{e.Message}", LogLevel.Error, e);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            stateSaveDelegate?.Cancel();
            saveState();

            base.Dispose(isDisposing);
            currentTrack?.Dispose();
            retireFadingTrack();
            OnlineTextures?.Dispose();
        }
    }
}
