using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 底栏「音质 / 倍数」的下拉浮层：点按钮后在按钮上方展开。
    /// 音质直接用 osu! 自己的 <see cref="OsuContextMenu"/>（与复制菜单同一套控件）；倍速是一张卡片，
    /// 里面放 osu! 的 <see cref="RoundedSliderBar{T}"/>（0.5x–3.0x，步进 0.01）。
    ///
    /// 挂在 <see cref="Screens.MainScreen"/> 最上层而不是底栏内部 —— 底栏只在最下面那条控制带上接收
    /// 输入，放在里面弹出来的菜单收不到点击。
    ///
    /// 没打开时整层对输入完全透明；打开时盖住全屏，点卡片/菜单以外的地方都能收起。
    /// </summary>
    public partial class PlaybackOptionsPopup : Container
    {
        /// <summary>弹出内容与按钮之间留的空隙。</summary>
        private const float gap = 6;

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        private OsuContextMenu menu = null!;
        private Container host = null!;

        /// <summary>倍速卡片；未打开时为 null。卡片（连同里面的滑块）每次打开都重建。</summary>
        private Container? tempoCard;

        private SpriteText? tempoValueText;

        /// <summary>弹出内容要贴着的锚点（局部坐标，按钮左上角）。</summary>
        private Vector2 anchor;

        public bool IsOpen => tempoCard is not null || menu.State == MenuState.Open;

        /// <summary>当前弹出的内容（倍速卡片或音质菜单），没有则为 null。</summary>
        private Drawable? activeContent
        {
            get
            {
                if (tempoCard is not null)
                    return tempoCard;

                return menu.State == MenuState.Open ? menu : null;
            }
        }

        public PlaybackOptionsPopup()
        {
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                // 常驻实例：菜单本身只开关，不随每次弹出重建（重建会把正在处理点击的自己 dispose 掉）。
                menu = new OsuContextMenu(playSamples: true)
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                },
                host = new Container
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                },
            };
        }

        /// <summary>在 <paramref name="screenAnchor"/>（按钮左上角）上方弹出音质选项。</summary>
        public void ShowQuality(Vector2 screenAnchor)
        {
            clearCard();

            var items = new List<MenuItem>();

            foreach ((string value, string label) in MusicPlayer.QualityOptions)
            {
                string choice = value;

                items.Add(new OsuMenuItem(
                    label,
                    // 当前音质用 osu! 菜单自带的高亮样式标出来。
                    player.Quality.Value == value ? MenuItemType.Highlighted : MenuItemType.Standard,
                    () => _ = player.SetQualityAsync(choice)));
            }

            menu.Items = items;
            anchor = ToLocalSpace(screenAnchor);
            menu.Open();
        }

        /// <summary>在 <paramref name="screenAnchor"/>（按钮左上角）上方弹出倍速滑块。</summary>
        public void ShowTempo(Vector2 screenAnchor)
        {
            menu.Close();

            tempoValueText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold),
                Colour = Color4.White,
                Text = $"{player.Tempo.Value:0.00}x",
            };

            tempoCard = new Container
            {
                AutoSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = 8,
                EdgeEffect = new EdgeEffectParameters
                {
                    Type = EdgeEffectType.Shadow,
                    Radius = 10,
                    Colour = new Color4(0, 0, 0, 0.5f),
                    Offset = new Vector2(0, 2),
                },
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(28, 28, 38, 255),
                    },
                    new Container
                    {
                        AutoSizeAxes = Axes.Both,
                        Padding = new MarginPadding(14),
                        Child = new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 10),
                            Children = new Drawable[]
                            {
                                new SpriteText
                                {
                                    Font = OsuFont.GetFont(size: 15),
                                    Colour = new Color4(175, 175, 190, 255),
                                    Text = "倍速 0.50x - 3.00x",
                                },
                                new Container
                                {
                                    AutoSizeAxes = Axes.Both,
                                    Child = new CommittingRoundedSliderBar<double>
                                    {
                                        Width = 220,
                                        Current = player.Tempo,
                                        OnDragEndCommitted = v => player.SetTempo(v),
                                    },
                                },
                                tempoValueText,
                            },
                        },
                    },
                },
            };

            host.Clear();
            host.Add(tempoCard);
            anchor = ToLocalSpace(screenAnchor);
        }

        public void Close()
        {
            menu.Close();
            clearCard();
        }

        private void clearCard()
        {
            host.Clear();
            tempoCard = null;
            tempoValueText = null;
        }

        protected override void Update()
        {
            base.Update();

            if (tempoValueText is not null)
                tempoValueText.Text = $"{player.Tempo.Value:0.00}x";

            Drawable? active = activeContent;

            if (active is null)
                return;

            // 菜单/卡片是自动尺寸的，弹出时还量不到大小，这里每帧把它摆到锚点上方并夹回窗口内
            // （与 osu! 自己的 ContextMenuContainer 同一套做法）。
            var pos = new Vector2(anchor.X, anchor.Y - active.DrawHeight - gap);

            if (pos.X + active.DrawWidth > DrawWidth)
                pos.X = Math.Max(0, DrawWidth - active.DrawWidth);
            if (pos.X < 0)
                pos.X = 0;
            if (pos.Y < 0)
                pos.Y = 0;

            active.Position = pos;
        }

        /// <summary>关闭时整层不参与输入。</summary>
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => IsOpen && base.ReceivePositionalInputAt(screenSpacePos);

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (!IsOpen)
                return false;

            // 点在弹出内容上（含它的空白处）不算「点外面」，交给菜单/滑块自己处理。
            Drawable? active = activeContent;

            if (active is not null && active.ReceivePositionalInputAt(e.ScreenSpaceMousePosition))
                return false;

            Close();

            // 吞掉这次点击，避免顺手触发下面的歌词跳转 / 圆饼操作。
            return true;
        }

        protected override bool OnTouchDown(TouchDownEvent e)
        {
            if (!IsOpen)
                return false;

            Drawable? active = activeContent;

            if (active is not null && active.ReceivePositionalInputAt(e.ScreenSpaceTouch.Position))
                return false;

            Close();
            return true;
        }

        /// <summary>浮层打开时滚轮不该穿透去滚歌词。</summary>
        protected override bool OnScroll(ScrollEvent e) => IsOpen;
    }
}
