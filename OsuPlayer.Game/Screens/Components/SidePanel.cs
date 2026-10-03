using osu.Framework.Allocation;
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
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Input.Bindings;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
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
                                        Text = "发现",
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
            PanelKind.Search => "发现",
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

            switch (kind)
            {
                case PanelKind.Local:
                    buildLocal();
                    break;

                case PanelKind.Search:
                    buildSearch();
                    break;

                case PanelKind.Recommend:
                    loadSongList(() => player.GetRecommendAsync());
                    break;

                case PanelKind.PersonalFm:
                    loadSongList(() => player.GetPersonalFmAsync());
                    break;

                case PanelKind.Liked:
                    showSongs(player.Liked.ToList());
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

                default:
                    setPlaceholder(kind == PanelKind.Comments
                        ? "评论功能即将上线。"
                        : kind == PanelKind.Notifications
                            ? "暂无新通知。"
                            : "设置项即将上线。");
                    break;
            }
        }

        // -------- Search --------

        private void buildSearch()
        {
            contentHost.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[]
                {
                    searchBox = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        PlaceholderText = "搜索歌曲、歌手，回车播放",
                    },
                },
            });

            searchBox.OnCommit += (_, _) =>
            {
                string keyword = searchBox.Text;
                if (string.IsNullOrWhiteSpace(keyword)) return;
                AppLog.Ui.Add($"提交搜索：“{keyword.Trim()}”", LogLevel.Important);
                pushNavigation($"搜索：{keyword.Trim()}", buildSearch);
                loadSongList(() => player.SearchAsync(keyword.Trim()));
            };

            Schedule(() => GetContainingFocusManager()?.ChangeFocus(searchBox));
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

        private void showSongs(IReadOnlyList<SongInfo> songs)
        {
            currentList = songs;
            contentHost.Clear();

            if (songs.Count == 0)
            {
                setPlaceholder("暂无内容。");
                return;
            }

            var flow = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
            };

            for (int i = 0; i < songs.Count; i++)
            {
                int index = i;
                flow.Add(new SongRow(player, songs[i])
                {
                    Action = () => _ = player.PlayAtAsync(currentList, index),
                });
            }

            contentHost.Add(flow);

            // 列表一出现就在后台预解析前几首，用户点下去时多半已命中缓存（首次请求同时预热网关连接）。
            player.PrewarmList(songs);

            int vip = songs.Count(s => s.Fee == SongFee.Vip);
            int paid = songs.Count(s => s.Fee == SongFee.Paid);

            if (vip > 0 || paid > 0)
                AppLog.Ui.Add($"列表付费标识：VIP {vip} 首，付费 {paid} 首（共 {songs.Count} 首）");
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
                for (int i = 0; i < currentList.Count; i++)
                {
                    int index = i;
                    flow.Add(new SongRow(player, currentList[i])
                    {
                        Action = () => _ = player.PlayAtAsync(currentList, index),
                    });
                }
            }

            contentHost.Add(flow);
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
                buildQrLogin();
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

        private void buildQrLogin()
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
                        Width = 220,
                        Height = 220,
                        Anchor = Anchor.TopLeft,
                        Origin = Anchor.TopLeft,
                        Child = qrSprite,
                    },
                    statusText,
                    refreshButton,
                },
            });

            refreshButton.Action = () =>
            {
                AppLog.Ui.Add("刷新登录二维码");
                refreshButton.Alpha = 0;
                beginQr(qrSprite, statusText, refreshButton);
            };

            beginQr(qrSprite, statusText, refreshButton);
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

        // -------- Misc --------

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
            base.Dispose(isDisposing);
        }

        /// <summary>A header title that reveals its full text on hover when it had to be truncated.</summary>
        private partial class TruncatingTitle : SpriteText, IHasTooltip
        {
            public LocalisableString TooltipText => IsTruncated ? Text : string.Empty;
        }
    }
}
