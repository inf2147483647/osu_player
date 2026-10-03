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

        /// <summary>Member-only tracks get a gold tag, purchase-only tracks a red one.</summary>
        private static readonly Color4 vip_colour = new(255, 199, 64, 255);
        private static readonly Color4 paid_colour = new(236, 74, 74, 255);

        private readonly SongInfo song;
        private Box hoverBg = null!;
        private SpriteText nameText = null!;
        private SpriteText artistText = null!;
        private SpriteText badgeText = null!;

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

            Children = new Drawable[]
            {
                hoverBg = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = hover_colour,
                    Alpha = 0,
                },
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
                    Children = new Drawable[]
                    {
                        badgeText = new SpriteText
                        {
                            Font = OsuFont.GetFont(size: 15, weight: FontWeight.Bold),
                        },
                        nameText = new SpriteText
                        {
                            Font = OsuFont.GetFont(size: 20, weight: FontWeight.SemiBold),
                            Text = song.Name,
                            Colour = Color4.White,
                            MaxWidth = 196,
                            Truncate = true,
                        },
                    },
                },
                artistText = new SpriteText
                {
                    Position = new Vector2(58, 30),
                    Font = OsuFont.GetFont(size: 15),
                    Text = song.Artist,
                    Colour = Color4.Gray,
                    MaxWidth = 240,
                    Truncate = true,
                },
                new SpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new Vector2(-8, 0),
                    Font = OsuFont.GetFont(size: 15),
                    Text = duration,
                    Colour = Color4.Gray,
                },
            };

            player.CurrentSong.BindValueChanged(_ => Schedule(updateActive), true);
            updateFeeBadge();
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
