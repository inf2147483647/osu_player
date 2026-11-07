using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Graphics;
using OsuPlayer.Game.Music;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>A selectable song entry in any of the side panel's lists.</summary>
    public partial class SongRow : OsuClickableContainer, IHasTooltip
    {
        private static readonly Color4 hover_colour = new(48, 48, 64, 255);
        private static readonly Color4 active_colour = new(255, 102, 171, 90);
        private static readonly Color4 selection_colour = new(255, 102, 171, 45);
        private static readonly Color4 delete_colour = new(190, 48, 60, 255);

        /// <summary>左滑时露出的删除区宽度；滑过 55% 即视为删除。</summary>
        private const float delete_reveal_width = 84;

        /// <summary>Member-only tracks get a gold tag, purchase-only tracks a red one.</summary>
        private static readonly Color4 vip_colour = new(255, 199, 64, 255);
        private static readonly Color4 paid_colour = new(236, 74, 74, 255);

        private readonly SongInfo song;
        private Box hoverBg = null!;
        private Box? selectionBg;
        private Circle? selectionInner;
        private Container rowContent = null!;
        private Container deleteArea = null!;
        private SpriteText nameText = null!;
        private SpriteText artistText = null!;
        private SpriteText badgeText = null!;
        private float swipeOffset;
        private bool dragged;

        /// <summary>左滑超过阈值后触发；为 null 时该行不启用左滑删除。</summary>
        public Action? SwipeDeleteRequested { get; set; }

        /// <summary>多选模式：点击整行只切换选中状态，不触发播放。</summary>
        public bool Selectable { get; set; }

        public bool IsSelected { get; private set; }

        /// <summary>选中状态切换后的回调，用于刷新工具栏的删除计数。</summary>
        public Action? SelectionToggled { get; set; }

        /// <summary>The full name/artist, shown on hover when the row had to truncate them.</summary>
        public override LocalisableString TooltipText => nameText.IsTruncated || artistText.IsTruncated ? song.DisplayText : string.Empty;

        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        public SongRow(MusicPlayer resolvedPlayer, SongInfo song)
        {
            player = resolvedPlayer;
            this.song = song;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = 54;
            Masking = true;
            CornerRadius = 6;

            string duration = song.DurationText;

            selectionBg = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = selection_colour,
                Alpha = 0,
            };

            hoverBg = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = hover_colour,
                Alpha = 0,
            };

            badgeText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 15, weight: FontWeight.Bold),
            };

            nameText = new SpriteText
            {
                Font = OsuFont.GetFont(size: 20, weight: FontWeight.SemiBold),
                Text = song.Name,
                Colour = Color4.White,
                MaxWidth = 196,
                Truncate = true,
            };

            artistText = new SpriteText
            {
                Position = new Vector2(58, 30),
                Font = OsuFont.GetFont(size: 15),
                Text = song.Artist,
                Colour = Color4.Gray,
                MaxWidth = 240,
                Truncate = true,
            };

            var contentChildren = new List<Drawable>
            {
                selectionBg,
                hoverBg,
                new OnlineSprite(player.OnlineTextures)
                {
                    Position = new Vector2(6, 6),
                    Size = new Vector2(42),
                    FillMode = FillMode.Fill,
                }.With(s => s.SetUrl(song.CoverUrl)),
                new FillFlowContainer
                {
                    Position = new Vector2(58, 7),
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(5, 0),
                    AutoSizeAxes = Axes.Both,
                    Children = new Drawable[] { badgeText, nameText },
                },
                artistText,
                new SpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(Selectable ? -36 : -8, 0),
                    Font = OsuFont.GetFont(size: 15),
                    Text = duration,
                    Colour = Color4.Gray,
                },
            };

            if (Selectable)
            {
                contentChildren.Add(new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-10, 0),
                    Size = new Vector2(18),
                    Children = new Drawable[]
                    {
                        new Circle
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = new Color4(255, 255, 255, 80),
                        },
                        selectionInner = new Circle
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new Vector2(10),
                            Colour = new Color4(255, 102, 171, 255),
                            Alpha = 0,
                        },
                    },
                });
            }

            Children = new Drawable[]
            {
                // 左滑后露出的删除底板。宽度跟随滑动距离：未滑动时宽度为 0，避免透出红色和“删除”字样。
                deleteArea = new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    RelativeSizeAxes = Axes.Y,
                    Width = 0,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = delete_colour,
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Text = "删除",
                            Font = OsuFont.GetFont(size: 16, weight: FontWeight.Bold),
                        },
                    },
                },
                rowContent = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = contentChildren,
                },
            };

            player.CurrentSong.BindValueChanged(_ => Schedule(updateActive), true);
            updateFeeBadge();
            updateSelectionVisual();
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            updateSelectionVisual();
        }

        private void updateSelectionVisual()
        {
            selectionBg?.FadeTo(IsSelected ? 1 : 0, 120);
            selectionInner?.FadeTo(IsSelected ? 1 : 0, 120);
        }

        private void updateFeeBadge()
        {
            switch (song.Fee)
            {
                case SongFee.Vip:
                    badgeText.Text = "[VIP]";
                    badgeText.Colour = vip_colour;
                    break;

                case SongFee.Paid:
                    badgeText.Text = "[付费]";
                    badgeText.Colour = paid_colour;
                    break;

                default:
                    badgeText.Text = string.Empty;
                    break;
            }
        }

        private void updateActive()
        {
            bool active = player.CurrentSong.Value?.Hash == song.Hash;
            hoverBg.Colour = active ? active_colour : hover_colour;
            hoverBg.FadeTo(active ? 1f : IsHovered ? 0.6f : 0f, 100);
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (player.CurrentSong.Value?.Hash != song.Hash)
                hoverBg.FadeTo(0.6f, 100);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            if (player.CurrentSong.Value?.Hash != song.Hash)
                hoverBg.FadeOut(100);
            base.OnHoverLost(e);
        }

        protected override bool OnClick(ClickEvent e)
        {
            // 刚完成一次左滑时不要再触发播放。
            if (dragged)
            {
                dragged = false;
                return true;
            }

            if (Selectable)
            {
                SetSelected(!IsSelected);
                SelectionToggled?.Invoke();
                return true;
            }

            return base.OnClick(e);
        }

        protected override bool OnDragStart(DragStartEvent e)
        {
            if (SwipeDeleteRequested is null || Selectable)
                return false;

            // 只有明显横向的手势才接管为左滑删除；纵向手势返回 false，
            // 让外层 ScrollContainer 拿到拖拽，保证上下滚动不被左滑抢占。
            // 1.5 倍的比例是给"斜着滑"留的容错：宁可少触发左滑，也不能卡住列表滚动。
            if (Math.Abs(e.Delta.X) < Math.Abs(e.Delta.Y) * 1.5f)
                return false;

            dragged = false;
            swipeOffset = 0;
            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            if (SwipeDeleteRequested is null)
            {
                base.OnDrag(e);
                return;
            }

            swipeOffset = Math.Clamp(swipeOffset + e.Delta.X, -delete_reveal_width, 0);

            if (Math.Abs(e.Delta.X) > 0.5f)
                dragged = true;

            rowContent.X = swipeOffset;
            deleteArea.Width = Math.Abs(swipeOffset);
            base.OnDrag(e);
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            base.OnDragEnd(e);

            if (SwipeDeleteRequested is null)
                return;

            if (swipeOffset <= -delete_reveal_width * 0.55f)
            {
                deleteArea.ResizeWidthTo(delete_reveal_width, 80, Easing.OutQuint);
                rowContent.MoveToX(-delete_reveal_width, 80, Easing.OutQuint);
                SwipeDeleteRequested.Invoke();
            }

            // 确认框出现后把行复位；若确认删除，整行会被列表重建替换。
            Schedule(resetSwipe);
            Schedule(() => dragged = false);
        }

        private void resetSwipe()
        {
            swipeOffset = 0;
            deleteArea.ResizeWidthTo(0, 180, Easing.OutCubic);
            rowContent.MoveToX(0, 180, Easing.OutCubic);
        }
    }

    /// <summary>A selectable playlist entry in the playlist square view.</summary>
    public partial class PlaylistRow : OsuClickableContainer, IHasTooltip
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        private readonly PlaylistInfo playlist;
        private Box hoverBg = null!;
        private SpriteText nameText = null!;

        /// <summary>The full playlist name, shown on hover when the row had to truncate it.</summary>
        public override LocalisableString TooltipText => nameText.IsTruncated ? playlist.Name : string.Empty;

        public PlaylistRow(MusicPlayer resolvedPlayer, PlaylistInfo playlist)
        {
            player = resolvedPlayer;
            this.playlist = playlist;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = 54;
            Masking = true;
            CornerRadius = 6;

            Children = new Drawable[]
            {
                hoverBg = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(48, 48, 64, 255),
                    Alpha = 0,
                },
                new OnlineSprite(player.OnlineTextures)
                {
                    Position = new Vector2(6, 6),
                    Size = new Vector2(42),
                    FillMode = FillMode.Fill,
                }.With(s => s.SetUrl(playlist.CoverUrl)),
                nameText = new SpriteText
                {
                    Position = new Vector2(58, 8),
                    Font = OsuFont.GetFont(size: 20, weight: FontWeight.SemiBold),
                    Text = playlist.Name,
                    Colour = Color4.White,
                    MaxWidth = 240,
                    Truncate = true,
                },
                new SpriteText
                {
                    Position = new Vector2(58, 31),
                    Font = OsuFont.GetFont(size: 15),
                    Text = $"{playlist.SongCount} 首",
                    Colour = Color4.Gray,
                },
            };
        }

        protected override bool OnHover(HoverEvent e)
        {
            hoverBg.FadeTo(0.6f, 100);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverBg.FadeOut(100);
            base.OnHoverLost(e);
        }
    }
}
