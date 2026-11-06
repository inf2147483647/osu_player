using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using Newtonsoft.Json.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using OsuPlayer.Game.Music.BeatSync.Onnx;
using OsuPlayer.Game.Net;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Slide-in panel under the top navigation, hosting all list/account views of the app.
    /// One instance, retargeted via <see cref="Show"/> / <see cref="HidePanel"/>.
    /// </summary>
    public partial class SidePanel : Container, IKeyBindingHandler<GlobalAction>
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        /// <summary>框架级设置（帧率上限等），搬自 FemboyTest 的「图形」分区。</summary>
        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        private const float panel_width = 400;

        /// <summary>Left + right margin of the header/content flow inside the panel.</summary>
        private const float header_padding = 18 + 12;

        /// <summary>Space kept clear on the right of the title for the 关闭 button and on the left for the 返回 button.</summary>
        private const float title_close_reserve = 62;
        private const float title_back_reserve = 66;

        /// <summary>Title width budget, so long playlist names truncate instead of running under the header buttons.</summary>
        private const float title_max_width = panel_width - header_padding - title_close_reserve;
        private const float title_max_width_with_back = title_max_width - title_back_reserve;

        private SpriteText titleText = null!;
        private UiTextButton backButton = null!;
        private Container contentHost = null!;
        private OsuTextBox searchBox = null!;

        /// <summary>The top-level panel kind currently shown; used to restore its title after going back.</summary>
        private PanelKind currentKind;

        /// <summary>When non-null, a secondary page is open and this rebuilds its parent page.</summary>
        private Action? backAction;

        private ScheduledDelegate? qrPollDelegate;

        /// <summary>登录方式切换按钮的两种底色：选中的是播放器的粉色，其余压暗。</summary>
        private static readonly Color4 tab_active_colour = new Color4(255, 102, 171, 255);
        private static readonly Color4 tab_idle_colour = new Color4(58, 58, 72, 255);
        private string? qrKey;

        private IReadOnlyList<SongInfo> currentList = Array.Empty<SongInfo>();

        [BackgroundDependencyLoader]
        private void load()
        {
            Anchor = Anchor.TopLeft;
            Origin = Anchor.TopLeft;
            Y = UiLayout.TopBarHeight;
            Width = panel_width;
            Height = 768 - UiLayout.TopBarHeight;
            X = -panel_width - 10;
            Alpha = 0;

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(20, 20, 28, 242),
                    },
                    new Box
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopLeft,
                        Width = 2,
                        RelativeSizeAxes = Axes.Y,
                        Colour = new Color4(70, 70, 92, 255),
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding { Top = 16, Bottom = 12, Left = 18, Right = 12 },
                        Spacing = new Vector2(0, 12),
                        Children = new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Children = new Drawable[]
                                {
                                    backButton = new UiTextButton("返回", goBack, 18)
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        X = -56,
                                        Alpha = 0,
                                    },
                                    titleText = new TruncatingTitle
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Font = OsuFont.GetFont(size: 26, weight: FontWeight.Bold),
                                        Text = "搜索",
                                        MaxWidth = title_max_width,
                                        Truncate = true,
                                    },
                                    new UiTextButton("关闭", HidePanel, 18)
                                    {
                                        Anchor = Anchor.CentreRight,
                                        Origin = Anchor.CentreRight,
                                    },
                                },
                            },
                            new OsuScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                ScrollbarOverlapsContent = false,
                                Child = contentHost = new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    // 30px trailing space: works around an upstream ScrollContainer
                                    // clipping/scroll-extent issue at the end of scrolled content.
                                    Padding = new MarginPadding { Right = 8, Bottom = 30 },
                                },
                            },
                        },
                    },
                },
            };
        }

        /// <summary>
        /// 设计稿是 16:9（画布 1366×768），但窗口比例不同时画布会被拉得更长/更宽：
        /// 面板必须跟着铺满顶栏以下的全部高度，否则列表底部会留出一截背景（看起来"被截短"）。
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (Parent is not null)
                Height = Math.Max(0, Parent.ChildSize.Y - Y);
        }

        public void Show(PanelKind kind)
        {
            currentKind = kind;
            AppLog.Ui.Add($"打开面板：{kind}", LogLevel.Important);
            stopQrPolling();
            clearNavigation(false);

            this.FadeIn(220, Easing.OutQuint)
                .MoveToX(0, 280, Easing.OutQuint);

            buildContent(kind);
        }

        // -------- Secondary-page navigation --------

        private void pushNavigation(string title, Action back)
        {
            titleText.Text = title;
            titleText.MaxWidth = title_max_width_with_back;
            backAction = back;

            backButton.FadeIn(160);
            backButton.MoveToX(0, 240, Easing.OutQuint);
            titleText.MoveToX(60, 240, Easing.OutQuint);
        }

        private void clearNavigation(bool animate = true)
        {
            backAction = null;
            titleText.Text = titleFor(currentKind);
            titleText.MaxWidth = title_max_width;

            if (!animate)
            {
                backButton.Alpha = 0;
                backButton.X = -56;
                titleText.X = 0;
                return;
            }

            backButton.FadeOut(160);
            backButton.MoveToX(-56, 240, Easing.OutQuint);
            titleText.MoveToX(0, 240, Easing.OutQuint);
        }

        private void goBack()
        {
            var back = backAction;
            if (back == null)
                return;

            AppLog.Ui.Add("返回上一级页面", LogLevel.Important);
            clearNavigation();
            back.Invoke();
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Action != GlobalAction.Back || Alpha == 0)
                return false;

            if (backAction != null)
                goBack();
            else
                HidePanel();

            return true;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        public void HidePanel()
        {
            AppLog.Ui.Add("关闭侧滑面板");
            stopQrPolling();
            this.FadeOut(220, Easing.OutQuint)
                .MoveToX(-panel_width - 10, 280, Easing.OutQuint);
        }

        private static string titleFor(PanelKind kind) => kind switch
        {
            PanelKind.Local => "本地音乐",
            PanelKind.Search => "搜索",
            PanelKind.Recommend => "推荐",
            PanelKind.PersonalFm => "私人 FM",
            PanelKind.Liked => "我的喜欢",
            PanelKind.History => "播放历史",
            PanelKind.Playlists => "歌单广场",
            PanelKind.Queue => "播放队列",
            PanelKind.LyricsSearch => "在线歌词",
            PanelKind.Account => "账号",
            PanelKind.Settings => "设置",
            PanelKind.Notifications => "通知",
            PanelKind.Comments => "评论",
            _ => "面板",
        };

        private void buildContent(PanelKind kind)
        {
            contentHost.Clear();
            // 离开通知/评论面板时不再需要它们的刷新回调，避免面板换了还在重建旧内容。
            detachNotificationRefresh();
            detachCommentsRefresh();

            switch (kind)
            {
                case PanelKind.Local:
                    buildLocal();
                    break;

                case PanelKind.Search:
                    buildSearch();
                    break;

                case PanelKind.Recommend:
                    buildRecommend();
                    break;

                case PanelKind.PersonalFm:
                    loadSongList(() => player.GetPersonalFmAsync());
                    break;

                case PanelKind.Liked:
                    // 先显示当前列表（未登录时就是本地收藏），再从账号「我喜欢」歌单同步一次。
                    showSongs(player.Liked.ToList());
                    _ = player.RefreshLikedAsync(force: true, onUpdated: songs =>
                    {
                        if (currentKind == PanelKind.Liked)
                            showSongs(songs);
                    });
                    break;

                case PanelKind.History:
                    showSongs(player.History.ToList());
                    break;

                case PanelKind.Playlists:
                    buildPlaylists();
                    break;

                case PanelKind.Queue:
                    showSongs(player.Queue.ToList());
                    break;

                case PanelKind.LyricsSearch:
                    buildLyricsSearch();
                    break;

                case PanelKind.Account:
                    buildAccount();
                    break;

                case PanelKind.Settings:
                    buildSettings();
                    break;

                case PanelKind.Notifications:
                    // 打开即视为已读（与 osu! 的通知覆盖层一致）；列表本身保留全部历史。
                    NotificationCenter.MarkAllRead();
                    attachNotificationRefresh();
                    renderNotifications();
                    break;

                case PanelKind.Comments:
                    buildComments();
                    break;

                default:
                    setPlaceholder("设置项即将上线。");
                    break;
            }
        }

        // -------- 推荐（仅手动刷新） --------

        /// <summary>
        /// 推荐页：进入时只显示上一次的缓存，**不自动请求**；只有点「刷新推荐」才会重新拉取。
        /// </summary>
        private void buildRecommend()
        {
            var status = new SpriteText
            {
                Font = OsuFont.GetFont(size: 16),
                Colour = Color4.Gray,
            };

            var refreshButton = new UiTextButton("刷新推荐", null, 18);

            var header = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[] { refreshButton, status },
            };

            IReadOnlyList<SongInfo> songs = player.CachedRecommend ?? Array.Empty<SongInfo>();

            status.Text = songs.Count > 0
                ? $"共 {songs.Count} 首 · 仅手动刷新（进入本页不会重新获取）"
                : "尚未加载，点「刷新推荐」获取。";

            showSongs(songs, header);

            refreshButton.Action = () => _ = refreshAsync();

            async Task refreshAsync()
            {
                refreshButton.Enabled.Value = false;
                status.Text = "正在刷新…";

                try
                {
                    IReadOnlyList<SongInfo> fresh = await player.GetRecommendAsync();
                    AppLog.Ui.Add($"推荐刷新：{fresh.Count} 首", LogLevel.Important);

                    if (currentKind == PanelKind.Recommend)
                        buildRecommend();
                }
                catch (Exception e)
                {
                    AppLog.Ui.Add($"推荐刷新失败：{e.Message}", LogLevel.Error, e);
                    status.Text = $"刷新失败：{e.Message}";
                    refreshButton.Enabled.Value = true;
                }
            }
        }

        // -------- 设置 --------

        /// <summary>
        /// 设置页。目前只有「BPM 检测模式」一项：
        /// <list type="bullet">
        /// <item>原生：纯 C# 频谱分析（多频带谱通量 + 梳状滤波 + Ellis DP），零依赖、离线可用。</item>
        /// <item>ONNX：跑 Beat This!（JKU，ISMIR 2024，MIT）节拍模型。它与频谱通量完全独立，
        /// 因此能提供一份真正的第二意见；代价是 120MB 模型和更长的分析时间。</item>
        /// </list>
        /// 模型不进安装包，所以这里同时负责它的下载与状态显示。
        /// </summary>
        private void buildSettings()
        {
            contentHost.Clear();

            var downloadButton = new UiTextButton("下载模型", null, 17);

            var statusText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 17),
                Colour = Color4.LightGray,
            };

            // kiai 高潮检测模型（302 张谱面真值训练的小模型，随程序/APPDATA 提供，无需下载）。
            var kiaiStatusText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 17),
                Colour = Color4.LightGray,
            };

            // 全局 BPM 层级模型（Ballroom 698 首真值训练，同样随程序提供）。
            var bpmStatusText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 17),
                Colour = Color4.LightGray,
            };

            var hint = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 15))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            hint.AddText("原生：纯 C# 频谱分析，离线可用，无需额外文件。\n"
                         + "ONNX：Beat This! 节拍模型，与频谱通量方法相互独立，通常更准；"
                         + "分析更慢，且未下载模型时会自动回退到原生结果。", t => t.Colour = Color4.Gray);

            var debugHint = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 15))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            debugHint.AddText("开启后进入「超级诊断模式」：全量日志 + osu! 原生性能叠加层（input / update / audio / draw "
                              + "四线程帧率与帧时间曲线）+ 每 2 秒把线程/GC 快照写进 performance.log。排查卡顿时开启。",
                t => t.Colour = Color4.Gray);

            downloadButton.Action = () =>
            {
                downloadButton.Enabled.Value = false;
                statusText.Text = "正在下载模型…";

                // 进度回调在线程池线程上触发，落到界面必须回到更新线程。
                var progress = new Progress<double>(p => Schedule(() => statusText.Text = $"正在下载模型… {p * 100:0}%"));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await BeatThisAssets.DownloadAsync(progress).ConfigureAwait(false);
                        Schedule(refresh);
                    }
                    catch (Exception e)
                    {
                        AppLog.Beat.Add($"下载 Beat This! 模型失败：{e.Message}", LogLevel.Error, e);
                        Schedule(() =>
                        {
                            statusText.Text = $"下载失败：{e.Message}";
                            downloadButton.Enabled.Value = true;
                        });
                    }
                });
            };

            contentHost.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[]
                {
                    new BpmModeDropdown
                    {
                        Caption = "BPM 检测模式",
                        Margin = new MarginPadding { Top = 4 },
                        Current = player.BpmMode,
                    },
                    hint,
                    statusText,
                    downloadButton,
                    new SpriteText
                    {
                        Text = "高潮检测模型",
                        Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold),
                        Margin = new MarginPadding { Top = 8 },
                    },
                    kiaiStatusText,
                    new SpriteText
                    {
                        Text = "BPM 层级模型",
                        Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold),
                        Margin = new MarginPadding { Top = 8 },
                    },
                    bpmStatusText,
                    new FormCheckBox
                    {
                        Caption = "调试模式",
                        Margin = new MarginPadding { Top = 8 },
                        Current = player.DebugMode,
                    },
                    debugHint,
                    new SpriteText
                    {
                        Text = "图形",
                        Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold),
                        Margin = new MarginPadding { Top = 8 },
                    },
                    new CoverRotationDropdown
                    {
                        Caption = "封面旋转方式",
                        HintText = "跟随歌词：歌词滚动多少角度，封面就转多少角度；匀速：封面按固定周期匀速自转",
                        Current = player.CoverRotation,
                    },
                    new FrameLimiterDropdown
                    {
                        Caption = "帧率上限",
                        HintText = "限制渲染帧率：越高越流畅也越耗电，垂直同步可避免画面撕裂",
                        Current = frameworkConfig.GetBindable<FrameSync>(FrameworkSetting.FrameSync),
                    },
                    new FormCheckBox
                    {
                        Caption = "显示帧率",
                        HintText = "在窗口右下角显示 osu! 的帧率与帧时间计数器",
                        Current = player.ShowFps,
                    },
                    new FormCheckBox
                    {
                        Caption = "全屏特效",
                        HintText = "播放器最小化时在桌面上铺开两侧闪光与 kiai 星星喷泉。关闭后不再显示特效，但特效进程会保留（托盘图标与顶层效果窗口仍在）",
                        Current = player.FullscreenEffects,
                    },
                },
            });

            refresh();

            void refresh()
            {
                downloadButton.Text = BeatThisAssets.IsReady ? "重新下载模型" : "下载模型（约 120MB）";
                downloadButton.Enabled.Value = true;

                statusText.Text = BeatThisAssets.IsReady
                    ? "模型已就绪。"
                    : "模型未下载：ONNX 模式会自动回退到原生结果。";

                kiaiStatusText.Text = KiaiAssets.IsReady
                    ? "kiai ML 模型已就绪（自动用于高潮检测）。"
                    : "kiai ML 模型未安装：使用能量 + chroma 启发式高潮检测。";

                bpmStatusText.Text = BpmMl.IsAvailable
                    ? BpmMl.Status
                    : "BPM 模型未安装：使用原生 BPM 估计。";
            }
        }

        // -------- Search --------

        private SpriteText? searchNotice;

        private void buildSearch()
        {
            // 返回上一级时这个页面会被重新构建，而 contentHost 里还留着搜索结果；
            // 不先清空的话搜索框/历史会叠在结果列表上面。
            contentHost.Clear();

            searchBox = new OsuTextBox
            {
                RelativeSizeAxes = Axes.X,
                PlaceholderText = "搜索歌曲、歌手，回车播放",
            };

            searchNotice = new SpriteText
            {
                Font = OsuFont.GetFont(size: 15),
                Colour = Color4.OrangeRed,
                Alpha = 0,
            };

            contentHost.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[]
                {
                    searchBox,
                    searchNotice,
                    buildSearchHistory(),
                },
            });

            // 输入阶段就抹掉敏感词，避免它出现在输入框里；粘贴命中时同样处理。
            searchBox.Current.BindValueChanged(e =>
            {
                string text = e.NewValue ?? string.Empty;
                string filtered = stripBlockedTerms(text);

                if (!string.Equals(text, filtered, StringComparison.Ordinal))
                {
                    searchBox.Text = filtered;
                    showSearchNotice("该关键词已被拦截，请换个关键词。");
                    return;
                }

                if (searchNotice.Alpha > 0)
                    searchNotice.FadeOut(160);
            });

            searchBox.OnCommit += (_, _) => submitSearch(searchBox.Text);

            Schedule(() => GetContainingFocusManager()?.ChangeFocus(searchBox));
        }

        /// <summary>搜索栏下方的历史记录：点一条即可重新搜索。</summary>
        private Drawable buildSearchHistory()
        {
            var flow = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 4),
            };

            IReadOnlyList<string> history = player.SearchHistory;

            if (history.Count == 0)
            {
                flow.Add(new SpriteText
                {
                    Font = OsuFont.GetFont(size: 16),
                    Colour = new Color4(120, 120, 132, 255),
                    Text = "暂无搜索历史",
                });

                return flow;
            }

            flow.Add(new SpriteText
            {
                Font = OsuFont.GetFont(size: 16),
                Colour = new Color4(150, 150, 165, 255),
                Text = "搜索历史",
            });

            foreach (string keyword in history)
            {
                string captured = keyword;

                flow.Add(new OsuClickableContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 30,
                    Action = () =>
                    {
                        searchBox.Text = captured;
                        submitSearch(captured);
                    },
                    Child = new TruncatingSpriteText
                    {
                        Text = captured,
                        Font = OsuFont.GetFont(size: 18),
                        Colour = new Color4(210, 210, 220, 255),
                        RelativeSizeAxes = Axes.X,
                        Padding = new MarginPadding { Left = 2, Vertical = 4 },
                    },
                });
            }

            return flow;
        }

        private void submitSearch(string raw)
        {
            string keyword = raw.Trim();

            if (keyword.Length == 0)
                return;

            if (MusicPlayer.ContainsBlockedKeyword(keyword))
            {
                AppLog.Ui.Add($"搜索关键词被拦截：“{keyword}”", LogLevel.Important);
                showSearchNotice("该关键词已被拦截，请换个关键词。");
                return;
            }

            AppLog.Ui.Add($"提交搜索：“{keyword}”", LogLevel.Important);
            player.RecordSearch(keyword);
            pushNavigation($"搜索：{keyword}", buildSearch);
            loadSongList(() => player.SearchAsync(keyword));
        }

        private static string stripBlockedTerms(string text)
        {
            foreach (string term in MusicPlayer.BlockedKeywords)
                text = text.Replace(term, string.Empty, StringComparison.OrdinalIgnoreCase);

            return text;
        }

        private void showSearchNotice(string message)
        {
            if (searchNotice is null)
                return;

            searchNotice.Text = message;
            searchNotice.FadeIn(120);
        }

        // -------- 在线搜索歌词 --------

        /// <summary>
        /// 本地导入的音乐通常没有歌词。这里用「当前歌名」做关键词搜在线歌曲，点选其中一首就把
        /// 它的歌词套用到当前正在播放的曲目上（不切换播放）。
        /// </summary>
        private void buildLyricsSearch()
        {
            SongInfo? current = player.CurrentSong.Value;

            if (current is null)
            {
                setPlaceholder("当前没有正在播放的曲目。");
                return;
            }

            contentHost.Clear();

            var box = new OsuTextBox
            {
                RelativeSizeAxes = Axes.X,
                PlaceholderText = "输入歌名，回车搜索",
                Text = current.Name,
            };

            var hint = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 15))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            hint.AddText($"为《{current.DisplayText}》找歌词：搜索后点选同名歌曲即可套用它的歌词（不会切换播放）。",
                t => t.Colour = Color4.Gray);

            contentHost.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[] { box, hint },
            });

            searchBox = box;

            box.OnCommit += (_, _) =>
            {
                string keyword = box.Text;
                if (string.IsNullOrWhiteSpace(keyword)) return;

                AppLog.Ui.Add($"搜索歌词：“{keyword.Trim()}”", LogLevel.Important);
                pushNavigation($"歌词：{keyword.Trim()}", buildLyricsSearch);
                loadLyricsResults(keyword.Trim());
            };

            Schedule(() => GetContainingFocusManager()?.ChangeFocus(box));
        }

        private async void loadLyricsResults(string keyword)
        {
            setPlaceholder("搜索中…");

            try
            {
                IReadOnlyList<SongInfo> songs = await player.SearchAsync(keyword);
                AppLog.Ui.Add($"歌词候选渲染：{songs.Count} 行");
                showLyricsResults(songs);
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"歌词搜索失败：{e.Message}", LogLevel.Error, e);
                setPlaceholder($"搜索失败：{e.Message}");
            }
        }

        private void showLyricsResults(IReadOnlyList<SongInfo> songs)
        {
            contentHost.Clear();

            if (songs.Count == 0)
            {
                setPlaceholder("没有搜到歌曲。");
                return;
            }

            var status = new SpriteText
            {
                Font = OsuFont.GetFont(size: 16),
                Colour = Color4.Khaki,
                Text = "点选一首歌套用它的歌词",
            };

            var flow = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[] { status },
            };

            foreach (SongInfo song in songs)
            {
                SongInfo captured = song;

                flow.Add(new SongRow(player, captured)
                {
                    // 与普通列表不同：这里不播放，只借歌词。
                    Action = () => _ = applyLyricsAsync(captured, status),
                });
            }

            contentHost.Add(flow);
        }

        private async Task applyLyricsAsync(SongInfo source, SpriteText status)
        {
            AppLog.Ui.Add($"套用歌词：《{source.Name}》- {source.Artist}", LogLevel.Important);
            status.Text = $"正在获取《{source.Name}》的歌词…";

            string message = await player.ApplyLyricsFromAsync(source);

            Scheduler.Add(() => status.Text = message);
        }

        // -------- Song lists --------

        private async void loadSongList(Func<Task<IReadOnlyList<SongInfo>>> loader)
        {
            setPlaceholder("加载中…");

            try
            {
                IReadOnlyList<SongInfo> songs = await loader();
                AppLog.Ui.Add($"列表渲染：{songs.Count} 行");
                showSongs(songs);
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"歌曲列表加载失败：{e.Message}", LogLevel.Error, e);
                setPlaceholder($"加载失败：{e.Message}");
            }
        }

        private void showSongs(IReadOnlyList<SongInfo> songs, Drawable? header = null)
        {
            currentList = songs;
            contentHost.Clear();

            if (songs.Count == 0)
            {
                // 有表头时（例如推荐页的刷新按钮）表头本身带说明，别再压一行「暂无内容」。
                if (header is null)
                {
                    setPlaceholder("暂无内容。");
                    return;
                }

                contentHost.Add(header);
                return;
            }

            var flow = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
            };

            if (header is not null)
                flow.Add(header);

            contentHost.Add(flow);
            addSongRowsBatched(flow, songs);

            // 列表一出现就在后台预解析前几首，用户点下去时多半已命中缓存（首次请求同时预热网关连接）。
            player.PrewarmList(songs);

            int vip = songs.Count(s => s.Fee == SongFee.Vip);
            int paid = songs.Count(s => s.Fee == SongFee.Paid);

            if (vip > 0 || paid > 0)
                AppLog.Ui.Add($"列表付费标识：VIP {vip} 首，付费 {paid} 首（共 {songs.Count} 首）");
        }

        /// <summary>
        /// 一批一帧地往 <paramref name="flow"/> 里加 <see cref="SongRow"/>。
        /// 长列表（几百首）一次性建完会把更新线程卡住一整帧甚至更久（每个 SongRow 都要建封面和多个文本子节点、
        /// 触发封面加载），所以改成逐帧增量添加：列表"长出来"的过程界面全程可用。
        /// </summary>
        private void addSongRowsBatched(FillFlowContainer<Drawable> flow, IReadOnlyList<SongInfo> songs)
        {
            const int batch_size = 12;
            int next = 0;

            void addBatch()
            {
                // 面板切走或列表重绘后 flow 已从树上移除（contentHost.Clear() 会把它销毁），必须停手。
                if (flow.Parent is null)
                    return;

                int end = Math.Min(next + batch_size, songs.Count);

                for (; next < end; next++)
                {
                    int index = next;
                    flow.Add(new SongRow(player, songs[index])
                    {
                        Action = () => _ = player.PlayAtAsync(currentList, index),
                    });
                }

                if (next < songs.Count)
                    Scheduler.AddDelayed(addBatch, 0);
            }

            addBatch();
        }

        // -------- 本地音乐 --------

        private void buildLocal()
        {
            currentList = player.LocalTracks.ToList();
            contentHost.Clear();

            var importButton = new OsuPlayerButton
            {
                Width = 150,
                Height = 38,
                Text = "导入音乐",
                Action = importLocalAsync,
            };

            var hint = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 16))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            hint.AddText("支持 osu! 谱面（.osz / .olz）与 mp3 / flac / wav / ogg / m4a 等音频，可多选", t => t.Colour = Color4.Gray);

            var flow = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[]
                {
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 6),
                        Margin = new MarginPadding { Bottom = 8 },
                        Children = new Drawable[] { importButton, hint },
                    },
                },
            };

            contentHost.Add(flow);

            if (currentList.Count == 0)
            {
                var empty = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 20))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                };
                empty.AddText("本地库为空，点上面的按钮导入。", t => t.Colour = Color4.LightGray);
                flow.Add(empty);
            }
            else
            {
                addSongRowsBatched(flow, currentList);
            }
        }

        private async void importLocalAsync()
        {
            AppLog.Ui.Add("点击导入本地音乐", LogLevel.Important);

            string[] paths;

            try
            {
                // The native dialog blocks until the user is done, so it must not run on the update thread.
                paths = await Task.Run(() => FilePicker.PickMediaFiles());
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"打开文件对话框失败：{e.Message}", LogLevel.Error, e);
                return;
            }

            if (paths.Length == 0)
            {
                AppLog.Ui.Add("导入已取消（未选择文件）");
                return;
            }

            AppLog.Ui.Add($"已选择 {paths.Length} 个文件，开始导入", LogLevel.Important);

            try
            {
                int added = await player.ImportLocalAsync(paths);
                AppLog.Ui.Add($"本地导入结果：新增 {added} 首，本地库共 {player.LocalTracks.Count} 首", LogLevel.Important);
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"本地导入失败：{e.Message}", LogLevel.Error, e);
            }

            Schedule(() =>
            {
                if (currentKind == PanelKind.Local)
                    buildLocal();
            });
        }

        // -------- Playlists --------

        private async void buildPlaylists()
        {
            setPlaceholder("加载中…");

            try
            {
                IReadOnlyList<PlaylistInfo> playlists = await player.GetPlaylistsAsync();
                AppLog.Ui.Add($"歌单广场渲染：{playlists.Count} 个歌单");
                contentHost.Clear();

                var flow = new FillFlowContainer<Drawable>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 6),
                };

                foreach (PlaylistInfo playlist in playlists)
                {
                    var captured = playlist;
                    flow.Add(new PlaylistRow(player, captured)
                    {
                        Action = () => openPlaylist(captured),
                    });
                }

                contentHost.Add(flow);
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"歌单广场加载失败：{e.Message}", LogLevel.Error, e);
                setPlaceholder($"加载失败：{e.Message}");
            }
        }

        private async void openPlaylist(PlaylistInfo playlist)
        {
            AppLog.Ui.Add($"打开歌单：《{playlist.Name}》（collection {playlist.CollectionId}）", LogLevel.Important);
            pushNavigation(playlist.Name, buildPlaylists);
            setPlaceholder("加载中…");

            try
            {
                showSongs(await player.GetPlaylistTracksAsync(playlist.CollectionId));
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"歌单歌曲加载失败《{playlist.Name}》：{e.Message}", LogLevel.Error, e);
                setPlaceholder($"加载失败：{e.Message}");
            }
        }

        // -------- Account / QR login --------

        private void buildAccount()
        {
            // 其它面板都会先清空；这里以前漏了，导致登录成功后账号信息被追加在二维码下面、
            // 二维码一直留在界面上。
            contentHost.Clear();

            if (!player.LoggedIn.Value)
            {
                buildLoginPanel();
                return;
            }

            buildProfile();

            void buildProfile()
            {
                var avatar = new OnlineSprite(player.OnlineTextures)
                {
                    Width = 96,
                    Height = 96,
                    Colour = Color4.White,
                };

                var nicknameText = new SpriteText
                {
                    Text = "加载中…",
                    Font = OsuFont.GetFont(size: 24, weight: FontWeight.Bold),
                };

                var idText = new SpriteText
                {
                    Text = $"酷狗用户 ID：{player.LoginUserId}",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var ageText = new SpriteText
                {
                    Text = "乐龄：查询中…",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var genderText = new SpriteText
                {
                    Text = "性别：查询中…",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var birthdayText = new SpriteText
                {
                    Text = "生日：查询中…",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var changTingText = new SpriteText
                {
                    Text = "畅听会员：查询中…",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var conceptText = new SpriteText
                {
                    Text = "概念会员：查询中…",
                    Font = OsuFont.GetFont(size: 18),
                    Colour = Color4.LightGray,
                };

                var messageText = new SpriteText
                {
                    Text = string.Empty,
                    Font = OsuFont.GetFont(size: 16),
                    Colour = Color4.Khaki,
                };

                var claimButton = new OsuPlayerButton
                {
                    Width = 150,
                    Height = 38,
                    Text = "领取畅听会员",
                };

                var upgradeButton = new OsuPlayerButton
                {
                    Width = 150,
                    Height = 38,
                    Text = "升级概念会员",
                };

                var logoutButton = new OsuPlayerButton
                {
                    Width = 150,
                    Height = 38,
                    Text = "退出登录",
                };

                contentHost.Add(new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 14),
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            Width = 96,
                            Height = 96,
                            Child = avatar,
                        },
                        nicknameText,
                        idText,
                        ageText,
                        genderText,
                        birthdayText,
                        changTingText,
                        conceptText,
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(10, 0),
                            Children = new Drawable[]
                            {
                                claimButton,
                                upgradeButton,
                            },
                        },
                        messageText,
                        logoutButton,
                    },
                });

                logoutButton.Action = () =>
                {
                    AppLog.Ui.Add("点击退出登录", LogLevel.Important);
                    player.Logout();
                    buildAccount();
                };

                bool busy = false;

                async void runVipAction(string label, Func<Task<string>> action)
                {
                    if (busy)
                        return;

                    busy = true;
                    AppLog.Ui.Add($"点击{label}", LogLevel.Important);
                    Scheduler.Add(() => messageText.Text = $"{label}中…");

                    string message = await action();

                    Scheduler.Add(() =>
                    {
                        messageText.Text = message;
                        busy = false;
                    });

                    await refreshVipLines();

                    // 权益可能已经变化，重试当前曲目。
                    Scheduler.Add(player.RetryCurrentTrackAfterVipChange);
                }

                claimButton.Action = () => runVipAction("领取畅听会员", player.ClaimDailyListenVipAsync);
                upgradeButton.Action = () => runVipAction("升级概念会员", player.UpgradeConceptVipAsync);

                async Task refreshVipLines()
                {
                    MusicPlayer.AccountVip vip = await player.GetVipExpiriesAsync();
                    Scheduler.Add(() =>
                    {
                        changTingText.Text = $"畅听会员：{vip.ChangTingExpiry}";
                        conceptText.Text = $"概念会员：{vip.ConceptExpiry}";
                    });
                }

                async void loadProfile()
                {
                    MusicPlayer.AccountProfile? profile = await player.GetProfileAsync();

                    if (profile is not null)
                    {
                        Scheduler.Add(() =>
                        {
                            nicknameText.Text = profile.Nickname;
                            ageText.Text = $"乐龄：{profile.MusicAge}";
                            genderText.Text = $"性别：{profile.Gender}";
                            birthdayText.Text = $"生日：{profile.Birthday}";

                            if (!string.IsNullOrEmpty(profile.AvatarUrl))
                                avatar.SetUrl(profile.AvatarUrl);
                        });
                    }

                    await refreshVipLines();
                }

                loadProfile();
            }
        }

        /// <summary>
        /// 未登录时的面板：上面一排登录方式，下面按选择切换表单。
        /// 除「酷狗扫码」外的几种都对齐 EchoMusic 的登录模块（验证码 / 账号密码 / 微信 / token 快速登录）。
        /// </summary>
        private void buildLoginPanel()
        {
            var methodRow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(8, 0),
            };

            var formHost = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 12),
            };

            contentHost.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 16),
                Children = new Drawable[] { methodRow, formHost },
            });

            var buttons = new List<(string Kind, OsuPlayerButton Button)>();

            void select(string kind)
            {
                // 切方式时把上一个方式的轮询停掉，否则二维码会继续在后台轮询。
                stopQrPolling();
                stopWeChatPolling();

                foreach ((string k, OsuPlayerButton button) in buttons)
                    button.BackgroundColour = k == kind ? tab_active_colour : tab_idle_colour;

                formHost.Clear();

                switch (kind)
                {
                    case "sms":
                        buildSmsLoginForm(formHost);
                        break;

                    case "password":
                        buildPasswordLoginForm(formHost);
                        break;

                    case "wechat":
                        buildWeChatLoginForm(formHost);
                        break;

                    case "token":
                        buildTokenLoginForm(formHost);
                        break;

                    default:
                        buildQrLoginForm(formHost);
                        break;
                }
            }

            foreach ((string kind, string label) in new[]
                     {
                         ("qrcode", "酷狗扫码"), ("sms", "验证码"), ("password", "账号密码"),
                         ("wechat", "微信"), ("token", "快速登录"),
                     })
            {
                var button = new OsuPlayerButton
                {
                    Width = 96,
                    Height = 34,
                    Text = label,
                };

                string captured = kind;
                button.Action = () => select(captured);

                buttons.Add((kind, button));
                methodRow.Add(button);
            }

            select("qrcode");
        }

        /// <summary>酷狗 App 扫码登录（原本的唯一入口）。</summary>
        private void buildQrLoginForm(FillFlowContainer host)
        {
            var statusText = new SpriteText
            {
                Text = "请使用酷狗音乐 App 扫码登录",
                Font = OsuFont.GetFont(size: 20),
                Colour = Color4.LightGray,
            };

            var qrSprite = new OnlineSprite(player.OnlineTextures)
            {
                Width = 220,
                Height = 220,
                Colour = Color4.White,
            };

            var refreshButton = new OsuPlayerButton
            {
                Width = 160,
                Height = 38,
                Text = "刷新二维码",
                Alpha = 0,
            };

            host.Add(new Container
            {
                Width = 220,
                Height = 220,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Child = qrSprite,
            });

            host.Add(statusText);
            host.Add(refreshButton);

            refreshButton.Action = () =>
            {
                AppLog.Ui.Add("刷新登录二维码");
                refreshButton.Alpha = 0;
                beginQr(qrSprite, statusText, refreshButton);
            };

            beginQr(qrSprite, statusText, refreshButton);
        }

        /// <summary>手机号 + 验证码登录。</summary>
        private void buildSmsLoginForm(FillFlowContainer host)
        {
            OsuTextBox mobile = loginField("手机号");
            OsuTextBox code = loginField("短信验证码");
            OsuPlayerButton send = loginAction("发送验证码");
            OsuPlayerButton login = loginAction("登录");
            SpriteText status = loginHint("未注册的手机号将自动注册");

            send.Action = async () =>
            {
                string number = mobile.Text.Trim();

                if (number.Length == 0)
                {
                    status.Text = "请先填写手机号";
                    return;
                }

                send.Enabled.Value = false;
                status.Text = "正在发送验证码…";

                string? error = await player.SendSmsCodeAsync(number);

                send.Enabled.Value = true;
                status.Text = error ?? "验证码已发送，请查收短信";
            };

            login.Action = async () =>
            {
                string number = mobile.Text.Trim();
                string smsCode = code.Text.Trim();

                if (number.Length == 0 || smsCode.Length == 0)
                {
                    status.Text = "请填写手机号和验证码";
                    return;
                }

                login.Enabled.Value = false;
                status.Text = "正在登录…";

                string? error = await player.LoginBySmsAsync(number, smsCode);

                login.Enabled.Value = true;

                if (error is null)
                {
                    buildAccount();
                    return;
                }

                status.Text = error;
            };

            host.Add(mobile);
            host.Add(code);
            host.Add(actionRow(send, login));
            host.Add(status);
        }

        /// <summary>酷狗账号 + 密码登录。</summary>
        private void buildPasswordLoginForm(FillFlowContainer host)
        {
            OsuTextBox username = loginField("酷狗账号（手机号 / 邮箱 / 用户名）");
            OsuTextBox password = loginField("密码", password: true);
            OsuPlayerButton login = loginAction("登录");
            SpriteText status = loginHint("密码登录由酷狗账号服务校验，不走网页授权");

            login.Action = async () =>
            {
                string account = username.Text.Trim();
                string secret = password.Text;

                if (account.Length == 0 || secret.Length == 0)
                {
                    status.Text = "请填写账号和密码";
                    return;
                }

                login.Enabled.Value = false;
                status.Text = "正在登录…";

                string? error = await player.LoginByPasswordAsync(account, secret);

                login.Enabled.Value = true;

                if (error is null)
                {
                    buildAccount();
                    return;
                }

                status.Text = error;
            };

            host.Add(username);
            host.Add(password);
            host.Add(actionRow(login));
            host.Add(status);
        }

        /// <summary>微信扫码登录（走微信开放平台，再用 openid 换酷狗登录态）。</summary>
        private void buildWeChatLoginForm(FillFlowContainer host)
        {
            var statusText = loginHint("正在获取微信二维码…");

            var qrSprite = new OnlineSprite(player.OnlineTextures)
            {
                Width = 220,
                Height = 220,
                Colour = Color4.White,
            };

            var refreshButton = loginAction("刷新二维码", 160);

            host.Add(new Container
            {
                Width = 220,
                Height = 220,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Child = qrSprite,
            });

            host.Add(statusText);
            host.Add(refreshButton);

            refreshButton.Action = () => beginWeChatQr(qrSprite, statusText, refreshButton);
            beginWeChatQr(qrSprite, statusText, refreshButton);
        }

        /// <summary>
        /// token 快速登录：用手里的登录凭证换一份新登录态。
        /// 必须是「本机还有凭证」才有意义（凭证已被清掉时只能走上面几种）。
        /// </summary>
        private void buildTokenLoginForm(FillFlowContainer host)
        {
            bool hasCredential = player.HasStoredCredential;

            SpriteText status = loginHint(hasCredential
                ? "本机仍有登录凭证，可以直接换一份新的登录态，无需扫码或密码"
                : "本机没有可复用的登录凭证（凭证已被清掉或换了客户端身份），请改用上面几种方式登录");

            var login = loginAction("快速登录");
            login.Enabled.Value = hasCredential;

            login.Action = async () =>
            {
                login.Enabled.Value = false;
                status.Text = "正在快速登录…";

                string? error = await player.LoginByTokenAsync();

                login.Enabled.Value = true;

                if (error is null)
                {
                    buildAccount();
                    return;
                }

                status.Text = error;
            };

            host.Add(login);
            host.Add(status);
        }

        /// <summary>登录表单的输入框（密码框用 osu! 自带的遮字版本）。</summary>
        private static OsuTextBox loginField(string placeholder, bool password = false)
        {
            OsuTextBox box = password ? new OsuPasswordTextBox() : new OsuTextBox();

            box.RelativeSizeAxes = Axes.X;
            box.PlaceholderText = placeholder;
            return box;
        }

        private static SpriteText loginHint(string text) => new SpriteText
        {
            Text = text,
            Font = OsuFont.GetFont(size: 16),
            Colour = Color4.LightGray,
        };

        private static OsuPlayerButton loginAction(string text, float width = 130) => new OsuPlayerButton
        {
            Width = width,
            Height = 36,
            Text = text,
        };

        private static FillFlowContainer actionRow(params Drawable[] children)
        {
            var row = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(8, 0),
            };

            row.AddRange(children);
            return row;
        }

        private async void beginQr(OnlineSprite qrSprite, SpriteText statusText, OsuPlayerButton refreshButton)
        {
            stopQrPolling();

            try
            {
                (string key, string qrImage) = await player.CreateQrAsync();
                qrKey = key;
                qrSprite.SetUrl(qrImage);
                statusText.Text = "请使用酷狗音乐 App 扫码登录";
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"二维码获取失败：{e.Message}", LogLevel.Error, e);
                statusText.Text = "二维码获取失败，请刷新";
                refreshButton.Alpha = 1;
                return;
            }

            async void pollOnce()
            {
                if (string.IsNullOrEmpty(qrKey))
                    return;

                int status;
                try
                {
                    status = await player.PollQrAsync(qrKey);
                }
                catch (Exception e)
                {
                    AppLog.Ui.Add($"二维码轮询网络异常：{e.Message}", LogLevel.Error, e);
                    statusText.Text = "网络异常，请刷新二维码";
                    refreshButton.Alpha = 1;
                    stopQrPolling();
                    return;
                }

                switch (status)
                {
                    case 0:
                        AppLog.Ui.Add("登录二维码已过期");
                        statusText.Text = "二维码已过期，请刷新";
                        refreshButton.Alpha = 1;
                        stopQrPolling();
                        break;

                    case 2:
                        AppLog.Ui.Add("二维码已扫码，等待手机确认");
                        statusText.Text = "已扫码，请在手机上确认";
                        break;

                    case 4:
                        statusText.Text = "登录成功";
                        stopQrPolling();
                        // 立刻切成账号面板：二维码与「请扫码」提示要马上消失。
                        buildAccount();
                        break;
                }
            }

            qrPollDelegate = Scheduler.AddDelayed(pollOnce, 2000, true);
            Scheduler.Add(pollOnce);
        }

        private void stopQrPolling()
        {
            qrPollDelegate?.Cancel();
            qrPollDelegate = null;
            qrKey = null;
        }

        // -------- 微信扫码登录 --------

        private ScheduledDelegate? weChatPollDelegate;
        private string? weChatUuid;

        private async void beginWeChatQr(OnlineSprite qrSprite, SpriteText statusText, OsuPlayerButton refreshButton)
        {
            stopWeChatPolling();
            statusText.Text = "正在获取微信二维码…";
            refreshButton.Alpha = 0;

            try
            {
                (string uuid, string image) = await player.CreateWeChatQrAsync();

                if (string.IsNullOrEmpty(image))
                {
                    statusText.Text = "微信二维码获取失败，请刷新";
                    refreshButton.Alpha = 1;
                    return;
                }

                weChatUuid = uuid;
                qrSprite.SetUrl(image);
                statusText.Text = "请使用微信扫码，并在手机上确认";
                AppLog.Ui.Add("已获取微信登录二维码");
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"微信二维码获取失败：{e.Message}", LogLevel.Error, e);
                statusText.Text = $"微信二维码获取失败：{e.Message}";
                refreshButton.Alpha = 1;
                return;
            }

            void pollOnce() => _ = pollWeChatOnce(statusText, refreshButton);

            weChatPollDelegate = Scheduler.AddDelayed(pollOnce, 3000, true);
            Scheduler.Add(pollOnce);
        }

        private async Task pollWeChatOnce(SpriteText statusText, OsuPlayerButton refreshButton)
        {
            if (weChatUuid is not { Length: > 0 } uuid)
                return;

            JObject response;

            try
            {
                response = await player.CheckWeChatQrAsync(uuid);
            }
            catch (Exception e)
            {
                AppLog.Ui.Add($"微信扫码轮询异常：{e.Message}", LogLevel.Error, e);
                statusText.Text = "网络异常，请刷新二维码";
                refreshButton.Alpha = 1;
                stopWeChatPolling();
                return;
            }

            int wxCode = response["wx_errcode"]?.Value<int>() ?? 0;

            switch (wxCode)
            {
                case 404:
                    statusText.Text = "已扫码，请在微信里确认";
                    break;

                case 405:
                    stopWeChatPolling();
                    string? error = await loginWithWeChat(response["wx_code"]?.ToString() ?? string.Empty, statusText);

                    if (error is not null)
                    {
                        statusText.Text = error;
                        refreshButton.Alpha = 1;
                    }

                    break;

                case 403:
                    statusText.Text = "二维码已取消或过期，请刷新";
                    refreshButton.Alpha = 1;
                    stopWeChatPolling();
                    break;
            }
        }

        private async Task<string?> loginWithWeChat(string wxCode, SpriteText statusText)
        {
            if (wxCode.Length == 0)
                return "微信未返回授权码，请刷新二维码重试";

            statusText.Text = "正在完成微信登录…";

            string? error = await player.LoginByWeChatAsync(wxCode);

            if (error is null)
            {
                buildAccount();
                return null;
            }

            return error;
        }

        private void stopWeChatPolling()
        {
            weChatPollDelegate?.Cancel();
            weChatPollDelegate = null;
            weChatUuid = null;
        }

        // -------- Misc --------

        private Action? notificationRefresh;

        private void attachNotificationRefresh()
        {
            detachNotificationRefresh();

            notificationRefresh = () => Schedule(() =>
            {
                // 面板开着时收到新通知就即时刷新（Changed 可能来自任意线程）。
                if (currentKind == PanelKind.Notifications)
                    renderNotifications();
            });

            NotificationCenter.Changed += notificationRefresh;
        }

        private void detachNotificationRefresh()
        {
            if (notificationRefresh is null)
                return;

            NotificationCenter.Changed -= notificationRefresh;
            notificationRefresh = null;
        }

        /// <summary>通知列表：最新的排最前，含时间与来源，长文本自动换行。</summary>
        private void renderNotifications()
        {
            contentHost.Clear();

            IReadOnlyList<NotificationCenter.Entry> all = NotificationCenter.Snapshot();

            if (all.Count == 0)
            {
                setPlaceholder("暂无通知。");
                return;
            }

            var flow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
            };

            for (int i = all.Count - 1; i >= 0; i--)
                flow.Add(buildNotificationRow(all[i]));

            contentHost.Add(flow);
        }

        private static Drawable buildNotificationRow(NotificationCenter.Entry entry)
        {
            var message = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 16))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            message.AddText(entry.Message, t => t.Colour = Color4.White);

            return new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(255, 255, 255, 0.04f),
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding(8),
                        Spacing = new Vector2(0, 2),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                Text = $"{entry.Time:HH:mm:ss}   [{entry.Source}]",
                                Font = OsuFont.GetFont(size: 13),
                                Colour = Color4.Gray,
                            },
                            message,
                        },
                    },
                },
            };
        }

        // -------- 评论（只读） --------

        private Action<ValueChangedEvent<SongInfo?>>? commentsSongChanged;

        private void buildComments()
        {
            detachCommentsRefresh();

            // 面板开着切歌必须立刻重绘：MusicPlayer 切歌时清空了 Comments，但已生成的条目还在画面上。
            commentsSongChanged = _ =>
            {
                if (currentKind == PanelKind.Comments)
                    Schedule(renderComments);
            };

            player.CurrentSong.ValueChanged += commentsSongChanged;
            renderComments();
        }

        private void detachCommentsRefresh()
        {
            if (commentsSongChanged is null)
                return;

            player.CurrentSong.ValueChanged -= commentsSongChanged;
            commentsSongChanged = null;
        }

        private void renderComments()
        {
            contentHost.Clear();

            if (player.CurrentSong.Value is not { } song)
            {
                setPlaceholder("当前没有正在播放的曲目。");
                return;
            }

            // 本地曲目没有 hash / mixsongid，评论池无从定位。
            if (song.IsLocal || song.AlbumAudioId == 0)
            {
                setPlaceholder(song.IsLocal
                    ? "本地导入的曲目没有在线评论。"
                    : "这首歌缺少评论标识（mixsongid），无法读取评论。");
                return;
            }

            var flow = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 12),
            };

            flow.Add(new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 14))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            }.With(t => t.AddText(
                player.CommentTotal.Value > 0 ? $"共 {player.CommentTotal.Value} 条评论（只读）" : "歌曲评论（只读）",
                c => c.Colour = Color4.Gray)));

            foreach (SongComment comment in player.Comments)
                flow.Add(buildCommentRow(comment));

            string status = player.CommentStatus.Value;

            if (!string.IsNullOrEmpty(status))
            {
                var text = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 16))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                };
                text.AddText(status, t => t.Colour = Color4.LightGray);
                flow.Add(text);
            }

            if (player.Comments.Count > 0 && player.Comments.Count < player.CommentTotal.Value)
                flow.Add(new UiTextButton("加载更多", () => loadComments(song, true), 20));

            contentHost.Add(flow);

            // 首次进入且既没有内容也没有状态：自动拉第一页。
            if (player.Comments.Count == 0 && string.IsNullOrEmpty(status))
                loadComments(song, false);
        }

        /// <summary>
        /// 拉评论并在完成后重绘。MusicPlayer 负责状态与 hash 守卫；这里只管面板重绘。
        /// </summary>
        private void loadComments(SongInfo song, bool append)
        {
            _ = loadCommentsAsync(song, append);

            async Task loadCommentsAsync(SongInfo target, bool appendPage)
            {
                await player.LoadCommentsAsync(target, appendPage).ConfigureAwait(false);

                Schedule(() =>
                {
                    if (currentKind == PanelKind.Comments)
                        renderComments();
                });
            }
        }

        private Drawable buildCommentRow(SongComment comment)
        {
            var message = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 17))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            message.AddText(comment.Content, t => t.Colour = Color4.White);

            var column = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 3),
                Children = new Drawable[]
                {
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                Text = comment.UserName,
                                Font = OsuFont.GetFont(size: 15, weight: FontWeight.SemiBold),
                                Colour = Color4.White,
                            },
                            new SpriteText
                            {
                                Text = comment.MetaText,
                                Font = OsuFont.GetFont(size: 13),
                                Colour = Color4.Gray,
                            },
                            new SpriteText
                            {
                                Text = comment.LikeCount > 0 ? $"♥ {comment.LikeCount}" : string.Empty,
                                Font = OsuFont.GetFont(size: 13),
                                Colour = Color4.Gray,
                            },
                        },
                    },
                    message,
                },
            };

            // 子楼：把被回复者与原文压成一行引用，免得看不懂在回谁。
            if (comment.IsReply)
            {
                var quote = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 14))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                };
                quote.AddText($"回复 @{comment.ReplyToUser}：{comment.ReplyToContent}", t => t.Colour = Color4.Gray);
                column.Add(quote);
            }

            return new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Children = new Drawable[]
                {
                    new OnlineSprite(player.OnlineTextures)
                    {
                        Position = new Vector2(4, 2),
                        Size = new Vector2(32),
                        FillMode = FillMode.Fill,
                    }.With(s => s.SetUrl(comment.AvatarUrl)),
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Left = 46, Right = 4 },
                        Child = column,
                    },
                },
            };
        }

        private void setPlaceholder(string message)
        {
            contentHost.Clear();

            // Errors in particular can be a whole sentence; wrap them inside the panel
            // instead of letting them run past its right edge.
            var text = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: 20))
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };

            text.AddText(message, t => t.Colour = Color4.LightGray);

            contentHost.Add(text);
        }

        protected override void Dispose(bool isDisposing)
        {
            stopQrPolling();
            detachNotificationRefresh();
            detachCommentsRefresh();
            base.Dispose(isDisposing);
        }

        /// <summary>A header title that reveals its full text on hover when it had to be truncated.</summary>
        private partial class TruncatingTitle : SpriteText, IHasTooltip
        {
            public LocalisableString TooltipText => IsTruncated ? Text : string.Empty;
        }

        /// <summary>
        /// BPM 检测模式下拉框（与「图形」分区同一套 osu! 原生控件），选项文字用中文。
        /// </summary>
        private partial class BpmModeDropdown : FormEnumDropdown<BpmDetectionMode>
        {
            protected override LocalisableString GenerateItemText(BpmDetectionMode item) => item switch
            {
                BpmDetectionMode.Native => "原生（纯 C# 频谱分析）",
                BpmDetectionMode.Onnx => "ONNX（Beat This! 节拍模型）",
                _ => base.GenerateItemText(item),
            };
        }

        /// <summary>
        /// 封面旋转方式下拉框：跟随歌词 / 匀速。
        /// </summary>
        private partial class CoverRotationDropdown : FormEnumDropdown<CoverRotationMode>
        {
            protected override LocalisableString GenerateItemText(CoverRotationMode item) => item switch
            {
                CoverRotationMode.FollowLyrics => "跟随歌词",
                CoverRotationMode.Constant => "匀速",
                _ => base.GenerateItemText(item),
            };
        }

        /// <summary>
        /// 帧率上限下拉框（搬自 FemboyTest）：osu! 用 <see cref="FormEnumDropdown{T}"/>，
        /// 这里只把选项文字换成中文（按枚举名匹配，未知取值回落到 osu! 自己的文案）。
        /// </summary>
        private partial class FrameLimiterDropdown : FormEnumDropdown<FrameSync>
        {
            protected override LocalisableString GenerateItemText(FrameSync item) => item.ToString() switch
            {
                "VSync" => "垂直同步（跟随显示器刷新率）",
                "Limit2x" => "刷新率 ×2",
                "Limit4x" => "刷新率 ×4",
                "Limit8x" => "刷新率 ×8",
                "Unlimited" => "不限制",
                _ => base.GenerateItemText(item),
            };
        }
    }
}
