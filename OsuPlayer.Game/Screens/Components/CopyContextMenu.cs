using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Input;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// 文本内容的「右键 / 触摸长按 → 复制」菜单。直接用 osu! 自己的 <see cref="OsuContextMenu"/>
    /// （同款圆角+阴影+音效），自己按按下的位置定位；没打开时整层对输入完全透明，
    /// 不会挡住下面的歌词、圆饼。
    /// </summary>
    public partial class CopyContextMenu : Container
    {
        [Resolved]
        private Clipboard clipboard { get; set; } = null!;

        private OsuContextMenu menu = null!;

        public CopyContextMenu()
        {
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = menu = new OsuContextMenu(playSamples: true)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
            };
        }

        /// <summary>在屏幕坐标处弹出只有一项的菜单。</summary>
        public void Show(string itemText, string content, Vector2 screenSpacePosition)
        {
            menu.Items = new[] { new MenuItem(itemText, () => copy(content, itemText)) };
            menu.Position = ToLocalSpace(screenSpacePosition);
            menu.Open();
        }

        private void copy(string content, string itemText)
        {
            clipboard.SetText(content);
            AppLog.Ui.Add($"已复制（{itemText}）：{content}", LogLevel.Important);
        }

        /// <summary>菜单没打开时整层不参与输入；打开时盖住全屏，好让任意点击都能把它关掉。</summary>
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
            => menu.State == MenuState.Open && base.ReceivePositionalInputAt(screenSpacePos);

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            if (menu.State != MenuState.Open)
                return;

            // 菜单是自动尺寸的，弹出时量不到大小，这里每帧把它夹回窗口内（与 osu! 自己的
            // ContextMenuContainer 同一套做法），避免贴右/下边时被切掉。
            Vector2 pos = menu.Position;
            Vector2 overflow = pos + menu.DrawSize - DrawSize;

            if (overflow.X > 0)
                pos.X -= Math.Clamp(overflow.X, 0, menu.DrawWidth);
            if (overflow.Y > 0)
                pos.Y -= Math.Clamp(overflow.Y, 0, menu.DrawHeight);

            if (pos.X < 0)
                pos.X += Math.Clamp(-pos.X, 0, menu.DrawWidth);
            if (pos.Y < 0)
                pos.Y += Math.Clamp(-pos.Y, 0, menu.DrawHeight);

            menu.Position = pos;
        }

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (menu.State != MenuState.Open)
                return false;

            menu.Close();

            // 右键继续往下传，让新的目标立刻弹出它自己的菜单；左键吞掉，避免顺手触发歌词跳转。
            return e.Button != MouseButton.Right;
        }

        protected override bool OnTouchDown(TouchDownEvent e)
        {
            if (menu.State != MenuState.Open)
                return false;

            menu.Close();
            return true;
        }
    }

    /// <summary>
    /// 把一段文本包成「右键 / 触摸长按可复制」的目标。
    ///
    /// 左键默认不吞事件，只是起一个长按计时器：这样歌词行原本的「点击跳转」「拖动浏览」照旧生效，
    /// 只有按住不动超过 <see cref="long_press_delay"/> 才弹出菜单，并通过 <see cref="OnLongPress"/>
    /// 通知宿主抑制随后那次点击（否则松手会顺带跳转）。
    /// </summary>
    public partial class CopyContextTarget : Container
    {
        /// <summary>菜单项文案，如「复制歌名」。</summary>
        public string ItemText = "复制";

        /// <summary>取当前要复制的内容；返回空值时不弹菜单。</summary>
        public Func<string?>? ContentProvider;

        /// <summary>左键 / 触摸按下是否吞掉事件。默认吞（目标自己没有其它点击语义时用）。</summary>
        public bool ConsumeLeftClick = true;

        /// <summary>短按抬起（未触发长按）时的回调。</summary>
        public Action? OnTap;

        /// <summary>长按已弹出菜单时的回调。</summary>
        public Action? OnLongPress;

        [Resolved]
        private CopyContextMenu menu { get; set; } = null!;

        private const double long_press_delay = 500;

        /// <summary>移动超过这么多像素就认为是在拖动（滚动歌词），不再算长按。</summary>
        private const float drag_cancel_distance = 6;

        private ScheduledDelegate? longPressDelegate;
        private Vector2 pressPosition;
        private bool pressed;

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Right)
            {
                pressPosition = e.ScreenSpaceMousePosition;
                return show();
            }

            if (e.Button != MouseButton.Left)
                return false;

            beginPress(e.ScreenSpaceMousePosition);
            return ConsumeLeftClick;
        }

        protected override bool OnTouchDown(TouchDownEvent e)
        {
            pressPosition = e.ScreenSpaceTouch.Position;
            beginPress(pressPosition);
            return ConsumeLeftClick;
        }

        protected override bool OnMouseMove(MouseMoveEvent e)
        {
            cancelIfMoved(e.ScreenSpaceMousePosition);
            return base.OnMouseMove(e);
        }

        protected override void OnTouchMove(TouchMoveEvent e)
        {
            cancelIfMoved(e.ScreenSpaceTouch.Position);
            base.OnTouchMove(e);
        }

        protected override void OnMouseUp(MouseUpEvent e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButton.Left)
                endPress();
        }

        protected override void OnTouchUp(TouchUpEvent e)
        {
            base.OnTouchUp(e);
            endPress();
        }

        /// <summary>滚轮是在浏览歌词，同样不算长按。</summary>
        protected override bool OnScroll(ScrollEvent e)
        {
            cancelPress();
            return base.OnScroll(e);
        }

        private void beginPress(Vector2 screenSpacePosition)
        {
            pressPosition = screenSpacePosition;
            pressed = true;

            longPressDelegate?.Cancel();
            longPressDelegate = Scheduler.AddDelayed(() =>
            {
                longPressDelegate = null;
                pressed = false;

                OnLongPress?.Invoke();
                show();
            }, long_press_delay);
        }

        private void endPress()
        {
            if (!pressed)
                return;

            bool wasShortPress = cancelPress();
            pressed = false;

            if (wasShortPress)
                OnTap?.Invoke();
        }

        private void cancelIfMoved(Vector2 position)
        {
            if (pressed && (position - pressPosition).Length > drag_cancel_distance)
                cancelPress();
        }

        /// <summary>取消计时；返回取消时它是否还算一次「短按」（没到长按时长）。</summary>
        private bool cancelPress()
        {
            if (longPressDelegate is null)
                return false;

            longPressDelegate.Cancel();
            longPressDelegate = null;
            pressed = false;
            return true;
        }

        private bool show()
        {
            string? content = ContentProvider?.Invoke();

            if (string.IsNullOrWhiteSpace(content))
                return false;

            menu.Show(ItemText, content, pressPosition);
            return true;
        }
    }
}
