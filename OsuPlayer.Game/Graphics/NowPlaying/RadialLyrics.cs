using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Graphics;
using osuTK;
using osuTK.Graphics;
using OsuPlayer.Game.Logging;
using OsuPlayer.Game.Music;
using OsuPlayer.Game.Screens;
using OsuPlayer.Game.Screens.Components;

namespace OsuPlayer.Game.Graphics.NowPlaying
{
    /// <summary>
    /// Lyrics fanning radially out of the disc's right edge, as drawn in the UI draft:
    /// the line playback is on stays horizontal, bold, bright and pink; preceding lines rotate
    /// upwards and following lines rotate downwards around the disc centre, fading with distance.
    ///
    /// The fan is also the seek affordance: the mouse wheel or a drag turns the whole fan so any
    /// line can be brought to the horizontal focal position, and clicking a line jumps playback to it.
    /// The browsed line is white; the line playback is on is always pink. 字号固定，长句按空格折行
    /// （单词绝不被拦腰截断），辐条夹角按每行折行后的实际高度自动放大，因此多行也不会互相重叠。
    /// Positioned at <see cref="UiLayout.DiscCentre"/>.
    /// </summary>
    public partial class RadialLyrics : Container
    {
        [Resolved]
        private MusicPlayer player { get; set; } = null!;

        private Container lineHost = null!;

        /// <summary>Visible lyric lines, keyed by their index in the full lyric list.</summary>
        private readonly Dictionary<int, LyricLine> lines = new();

        /// <summary>Lyric index the fan is centred on while browsing; null while following playback.</summary>
        private int? browseIndex;

        /// <summary>Accumulates sub-line scroll deltas (trackpads) until a whole line is reached.</summary>
        private float scrollAccumulator;

        /// <summary>Accumulates sub-line drag distance (mouse or touch) until a whole line is reached.</summary>
        private float dragAccumulator;

        private bool dragging;

        private double lastBrowseTime;

        private const int visible_lines_each_side = 3;

        /// <summary>
        /// 相邻歌词辐条的最小夹角。字号固定，行数多（折了行）时由
        /// <see cref="stepFor"/> 按实测文字块高度自动放大夹角，保证不重叠。
        /// </summary>
        private const float min_angular_step = 7f;

        /// <summary>相邻文字块之间保留的垂直间隙。</summary>
        private const float line_gap = 8f;

        private const float line_start_gap = 16;

        /// <summary>Where the lyric text begins, measured from the disc centre.</summary>
        private const float text_start_offset = UiLayout.DiscRadius + line_start_gap;

        /// <summary>Keeps a wrapped lyric clear of the screen's right edge.</summary>
        private const float text_right_margin = 24;

        /// <summary>Font size of the focal line; the other lines are scaled down from it so the size can be tweened.</summary>
        private const float active_font_size = 34;
        private const float inactive_font_size = 25;

        /// <summary>Accent used to mark the line playback is actually on while the fan is browsed away from it.</summary>
        private static readonly Color4 playing_colour = new Color4(255, 102, 171, 255);

        private static readonly Color4 dim_colour = new Color4(170, 170, 185, 255);

        /// <summary>How close (in pixels) a click must land to a line for it to count as a hit.</summary>
        private const float click_tolerance = 16;

        /// <summary>Vertical drag distance that walks the fan by one lyric line.</summary>
        private const float drag_step_pixels = 45;

        /// <summary>Browsing snaps back to the playing line after this much wheel idle time.</summary>
        private const double browse_return_delay = 5000;

        private const double transition_duration = 420;
        private const double fade_duration = 320;
        private const Easing transition_easing = Easing.OutQuint;

        [BackgroundDependencyLoader]
        private void load()
        {
            Anchor = Anchor.TopLeft;
            Origin = Anchor.TopLeft;
            RelativeSizeAxes = Axes.Both;

            // The spokes pivot around the disc centre; the fan itself fills the screen so the
            // whole area to the right of the disc can receive the wheel and click input.
            Child = lineHost = new Container
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Position = UiLayout.DiscCentre,
                Size = Vector2.One,
            };

            player.Lyrics.BindCollectionChanged((_, _) => Schedule(refreshAll));
            player.ActiveLyricIndex.BindValueChanged(_ => Schedule(updateLines));
        }

        protected override void Update()
        {
            base.Update();

            // Lyrics arriving late can race with the first active-index change.
            if (lines.Count == 0 && player.Lyrics.Count > 0)
            {
                updateLines();
                return;
            }

            // Browsing is transient: settle back onto the playing line when the wheel goes quiet.
            if (browseIndex.HasValue && Clock.CurrentTime - lastBrowseTime > browse_return_delay)
            {
                browseIndex = null;
                updateLines();
            }

            // 文字块折行后的高度要等布局完才准，每帧重排一次让间距收敛（角度没变时是空操作）。
            applyLayout();
        }

        /// <summary>
        /// Only the fan itself takes positional input, so the rest of this full-screen container stays
        /// click/hover-through - otherwise it would sit above the disc and swallow the disc's own hover
        /// (which drives the osu!-logo hover heartbeat).
        /// </summary>
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => base.ReceivePositionalInputAt(screenSpacePos) && isInLyricArea(screenSpacePos);

        protected override bool OnScroll(ScrollEvent e)
        {
            if (player.Lyrics.Count == 0 || player.ActiveLyricIndex.Value < 0 || !isInLyricArea(e.ScreenSpaceMousePosition))
                return false;

            scrollAccumulator += e.ScrollDelta.Y;

            int steps = (int)scrollAccumulator;

            if (steps == 0)
                return true; // still accumulating sub-line precision scroll

            scrollAccumulator -= steps;

            // Wheel up (positive delta) walks back through the lyrics.
            moveFocus(-steps);
            return true;
        }

        /// <summary>
        /// 长按歌词弹出「复制这段歌词」后，松手时不应再顺带触发一次「点击歌词跳转」。
        /// </summary>
        private bool suppressNextClick;

        protected override bool OnClick(ClickEvent e)
        {
            if (suppressNextClick)
            {
                suppressNextClick = false;
                return true;
            }

            if (player.Lyrics.Count == 0)
                return false;

            Vector2 fromCentre = ToLocalSpace(e.ScreenSpaceMousePosition) - UiLayout.DiscCentre;

            if (fromCentre.X <= 0 || fromCentre.Length < UiLayout.DiscRadius)
                return false;

            // Pick the line whose spoke the click landed closest to (measured against the current,
            // possibly still tweening, rotation so the hit test matches what is on screen).
            float clickedAngle = MathF.Atan2(fromCentre.Y, fromCentre.X) * 180f / MathF.PI;

            int hitIndex = -1;
            float closestOffset = float.MaxValue;

            foreach (var pair in lines)
            {
                float offset = MathF.Abs(fromCentre.Length * MathF.Sin((clickedAngle - pair.Value.Rotation) * MathF.PI / 180f));

                if (offset < closestOffset)
                {
                    closestOffset = offset;
                    hitIndex = pair.Key;
                }
            }

            if (hitIndex < 0 || hitIndex >= player.Lyrics.Count || closestOffset > click_tolerance)
                return false;

            double target = player.Lyrics[hitIndex].TimeMs;
            AppLog.Player.Add($"点击歌词跳转：第 {hitIndex + 1}/{player.Lyrics.Count} 行 → {target / 1000:0.0}s", LogLevel.Important);

            player.Seek(target);

            browseIndex = null;
            scrollAccumulator = 0;
            updateLines();

            return true;
        }

        /// <summary>
        /// Dragging the fan (mouse or touch) browses it the same way the wheel does, so the same
        /// gesture works on a touchscreen. A drag never seeks - releasing without moving taps a line.
        /// </summary>
        protected override bool OnDragStart(DragStartEvent e)
        {
            if (player.Lyrics.Count == 0 || player.ActiveLyricIndex.Value < 0 || !isInLyricArea(e.ScreenSpaceMousePosition))
                return false;

            dragging = true;
            dragAccumulator = 0;
            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            if (!dragging)
                return;

            // Dragging down pulls earlier lyrics into focus, matching the wheel (content follows the pointer).
            dragAccumulator -= e.Delta.Y;

            int steps = (int)(dragAccumulator / drag_step_pixels);

            if (steps == 0)
                return;

            dragAccumulator -= steps * drag_step_pixels;
            moveFocus(steps);
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            dragging = false;
            dragAccumulator = 0;
        }

        /// <summary>The lyric fan (and therefore the wheel/click surface) lives to the right of the disc, below the top bar.</summary>
        private bool isInLyricArea(Vector2 screenSpacePos)
        {
            Vector2 local = ToLocalSpace(screenSpacePos);

            if (local.Y <= UiLayout.TopBarHeight)
                return false;

            Vector2 fromCentre = local - UiLayout.DiscCentre;

            return fromCentre.X > 0 && fromCentre.Length > UiLayout.DiscRadius;
        }

        private void moveFocus(int delta)
        {
            int active = player.ActiveLyricIndex.Value;
            int target = Math.Clamp((browseIndex ?? active) + delta, 0, player.Lyrics.Count - 1);

            browseIndex = target == active ? null : target;
            lastBrowseTime = Clock.CurrentTime;

            updateLines();
        }

        /// <summary>Song changed: drop every line so the next update builds the new lyrics from scratch.</summary>
        private void refreshAll()
        {
            browseIndex = null;
            scrollAccumulator = 0;
            dragAccumulator = 0;
            dragging = false;
            lines.Clear();
            lineHost.Clear();
            updateLines();
        }

        /// <summary>
        /// Re-targets the visible lines. Existing lines keep their drawables, so scrolling the fan
        /// rotates the whole stack while the focal line grows and lights up.
        /// </summary>
        private void updateLines()
        {
            int active = player.ActiveLyricIndex.Value;

            if (player.Lyrics.Count == 0)
            {
                clearLines();
                return;
            }

            // 还没唱到第一句（active < 0）时把焦点放在 -1：焦点槽位留空，开头的几句全部落在下方的
            // 「等待区」，唱到第一句时再整体往下推一格，而不是在此之前整屏没有歌词。
            int focus = active < 0 ? -1 : Math.Clamp(browseIndex ?? active, 0, player.Lyrics.Count - 1);

            float wrapWidth = wrapWidthForCurrentSize();

            int first = Math.Max(0, focus - visible_lines_each_side);
            int last = Math.Min(player.Lyrics.Count - 1, focus + visible_lines_each_side);

            foreach (int index in lines.Keys.ToList())
            {
                if (index >= first && index <= last)
                    continue;

                LyricLine leaving = lines[index];
                lines.Remove(index);

                // Keep the departing line travelling outward instead of fading in place, so a
                // multi-line scroll reads as one continuous turn rather than two overlapping fans.
                leaving.Leave(leaving.CurrentAngle + Math.Sign(index - focus) * min_angular_step);
                leaving.Expire();
            }

            for (int i = first; i <= last; i++)
            {
                string text = player.Lyrics[i].Text;

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                int distance = i - focus;

                if (!lines.TryGetValue(i, out LyricLine? line))
                {
                    line = new LyricLine(text, wrapWidth, () => suppressNextClick = true);
                    lines[i] = line;
                    lineHost.Add(line);
                    line.PrepareEntrance(distance);
                }
                else
                    line.SetWrapWidth(wrapWidth);

                line.Apply(distance, isPlaying: i == active);
            }

            applyLayout();
        }

        /// <summary>
        /// 从焦点行向两侧累积夹角：相邻两行的夹角由它们折行后的实际高度决定
        /// （<see cref="stepFor"/>），所以多行歌词会把后面的行推得更开，而不是叠在一起。
        /// 每帧都会跑一遍 —— 文字块的高度要等它布局完才知道，第一帧量到 0 时先按最小夹角摆放，
        /// 下一帧自动纠正；只有目标角度真的变了才会触发旋转，不会打断正在跑的补间。
        /// </summary>
        private void applyLayout()
        {
            if (lines.Count == 0)
                return;

            int active = player.ActiveLyricIndex.Value;
            int focus = active < 0 ? -1 : Math.Clamp(browseIndex ?? active, 0, player.Lyrics.Count - 1);

            // 焦点槽位允许为空（还没唱到第一句时它只是「等待区」的起点），此时高度按 0 计。
            float focalHeight = 0;

            if (lines.TryGetValue(focus, out LyricLine? focal))
            {
                focal.Place(0);
                focalHeight = focal.VisualHeight;
            }

            // 焦点行之后的行：夹角依次累加（屏幕上向下）。
            float previousHeight = focalHeight;
            float angle = 0;

            for (int i = focus + 1; i <= focus + visible_lines_each_side; i++)
            {
                if (!lines.TryGetValue(i, out LyricLine? line))
                    continue;

                angle += stepFor(previousHeight, line.VisualHeight);
                line.Place(angle);
                previousHeight = line.VisualHeight;
            }

            // 焦点行之前的行：夹角依次累减（屏幕上向上）。
            previousHeight = focalHeight;
            angle = 0;

            for (int i = focus - 1; i >= focus - visible_lines_each_side; i--)
            {
                if (!lines.TryGetValue(i, out LyricLine? line))
                    continue;

                angle -= stepFor(line.VisualHeight, previousHeight);
                line.Place(angle);
                previousHeight = line.VisualHeight;
            }
        }

        /// <summary>
        /// 两个绕着圆饼中心旋转的文字块之间的最小夹角。文字内缘（半径
        /// <see cref="text_start_offset"/>）处两者最贴近：要让垂直间隙不小于
        /// <see cref="line_gap"/>，需要 r·sinθ ≥ (h₁ + h₂)/2 + gap。
        /// </summary>
        private static float stepFor(float heightA, float heightB)
        {
            double needed = (heightA + heightB) / 2f + line_gap;
            double radians = Math.Asin(Math.Clamp(needed / text_start_offset, 0, 0.95));

            return Math.Max(min_angular_step, (float)(radians * 180 / Math.PI));
        }

        /// <summary>Room left for a lyric before it would run off the screen, so long lines fold instead.</summary>
        private float wrapWidthForCurrentSize()
            => Math.Max(120, DrawWidth - UiLayout.DiscCentre.X - text_start_offset - text_right_margin);

        private void clearLines()
        {
            foreach (LyricLine line in lines.Values)
            {
                line.FadeOut(fade_duration, transition_easing);
                line.Expire();
            }

            lines.Clear();
        }

        /// <summary>
        /// A single lyric spoke: a zero-sized container rotated around the disc centre with the text
        /// hanging off its outer end. The font size is fixed; other lines are scaled down so the
        /// emphasis can be tweened instead of snapping between two font textures. Long lines wrap at
        /// spaces (a word is never cut in half) and the spoke spacing grows to fit the extra rows.
        /// </summary>
        private partial class LyricLine : Container
        {
            private readonly TextFlowContainer text;

            /// <summary>本行可用的最大宽度（右边界留有 <see cref="text_right_margin"/>）。</summary>
            private float wrapWidth;

            /// <summary>目标旋转角度，避免每帧重复触发补间。</summary>
            private float targetAngle;

            /// <summary>目标缩放（焦点行 1，其余行按字号比例），用来估算文字块的实际高度。</summary>
            private float targetScale = 1f;

            /// <summary>当前实际的旋转角度，供离场动画接着往外转。</summary>
            public float CurrentAngle => Rotation;

            /// <summary>文字块在屏幕上的实际高度（含缩放），用来算相邻辐条要拉开多少角度。</summary>
            public float VisualHeight => text.DrawHeight * targetScale;

            public LyricLine(string content, float wrapWidth, Action onLongPress)
            {
                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;
                this.wrapWidth = wrapWidth;

                // 长按 / 右键歌词块可以复制这一句；不吞左键，歌词行原本的点击跳转、拖动浏览、
                // 滚轮翻页都照旧（长按由计时器判定，移动或滚轮会取消）。
                Child = new CopyContextTarget
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new Vector2(text_start_offset, 0),
                    AutoSizeAxes = Axes.Both,
                    ItemText = "复制这段歌词",
                    ContentProvider = () => content,
                    ConsumeLeftClick = false,
                    OnLongPress = onLongPress,
                    Child = text = new TextFlowContainer(t => t.Font = OsuFont.GetFont(size: active_font_size, weight: FontWeight.Bold))
                    {
                        // 固定宽度 + Y 自动：内部 FillFlowContainer 按 X 相对流动，因此只在**空格**处折行，
                        // 英语单词不会被拦腰截断（SpriteText 的自动换行是按字符断的）。
                        Width = wrapWidth,
                        AutoSizeAxes = Axes.Y,
                        TextAnchor = Anchor.TopLeft,
                    },
                };

                text.AddText(content);
            }

            /// <summary>Updates the available width after a window resize.</summary>
            public void SetWrapWidth(float wrapWidth)
            {
                if (Math.Abs(this.wrapWidth - wrapWidth) <= 0.5f)
                    return;

                this.wrapWidth = wrapWidth;
                text.Width = wrapWidth;
            }

            /// <summary>Rotates to <paramref name="angle"/>；角度没变时什么都不做，不打断进行中的补间。</summary>
            public void Place(float angle)
            {
                if (Math.Abs(targetAngle - angle) <= 0.5f)
                    return;

                targetAngle = angle;
                this.RotateTo(angle, transition_duration, transition_easing);
            }

            /// <param name="distance">Offset from the focal line, in lines (0 = the focal/browsed line).</param>
            /// <param name="isPlaying">Whether this is the line playback is currently on.</param>
            public void Apply(int distance, bool isPlaying)
            {
                bool isFocal = distance == 0;

                // The line playback is on is always the accent colour, even while the fan is browsed
                // away from it; the browsed (focal) line is white unless it happens to be that line.
                float alpha = isFocal ? 1f : isPlaying ? 0.85f : Math.Abs(distance) switch
                {
                    1 => 0.6f,
                    2 => 0.38f,
                    _ => 0.24f,
                };

                moveTo(distance, isPlaying ? playing_colour : isFocal ? Color4.White : dim_colour);
                this.FadeTo(alpha, transition_duration, transition_easing);
            }

            /// <summary>
            /// Carries a line that just left the visible window one further step outward while it
            /// fades, so the fan keeps turning instead of the old stack hanging around mid-air.
            /// </summary>
            /// <param name="angle">接着往外转到的角度。</param>
            public void Leave(float angle)
            {
                Place(angle);
                text.FadeColour(dim_colour, transition_duration, transition_easing);
                this.FadeOut(fade_duration, transition_easing);
            }

            /// <summary>
            /// Places a freshly created line just outside its target slot - one step further out,
            /// transparent and small - so <see cref="Apply"/> tweens it into place instead of popping in.
            /// </summary>
            public void PrepareEntrance(int distance)
            {
                float offset = (distance + Math.Sign(distance)) * min_angular_step;

                Rotation = offset;
                targetAngle = offset;
                Alpha = 0;

                targetScale = inactive_font_size / active_font_size;
                text.Scale = new Vector2(targetScale);
                text.Colour = dim_colour;
            }

            /// <summary>文字大小、颜色与绘制层级（旋转交给 <see cref="Place"/>，因为它每帧都可能修正）。</summary>
            private void moveTo(int distance, Color4 colour)
            {
                float scale = distance == 0 ? 1f : inactive_font_size / active_font_size;
                targetScale = scale;

                text.ScaleTo(scale, transition_duration, transition_easing);
                text.FadeColour(colour, transition_duration, transition_easing);

                // Keep the focal line in front of the ones fanning away from it.
                (Parent as Container)?.ChangeChildDepth(this, Math.Abs(distance));
            }
        }
    }
}
