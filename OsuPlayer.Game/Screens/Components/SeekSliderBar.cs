using System.Numerics;
using osu.Framework.Bindables;
using osu.Framework.Input.Events;
using osu.Game.Graphics.UserInterface;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Screens.Components
{
    /// <summary>
    /// Rounded osu! slider which reports a single committed value when a drag ends.
    /// Used by the volume slider so one log line is produced per adjustment.
    /// </summary>
    public partial class CommittingRoundedSliderBar<T> : RoundedSliderBar<T>
        where T : struct, INumber<T>, IMinMaxValue<T>
    {
        /// <summary>Fired once when a drag finishes, carrying the committed final value.</summary>
        public Action<T>? OnDragEndCommitted;

        protected override void OnDragEnd(DragEndEvent e)
        {
            OnDragEndCommitted?.Invoke(Current.Value);
            base.OnDragEnd(e);
        }
    }

    /// <summary>
    /// 进度滑块。显示值由 <see cref="Source"/> 每帧**单向**拉取，而不是与播放进度做双向绑定。
    ///
    /// 为什么不能用双向绑定：播放循环每帧都会把 <c>CurrentTimeMs</c> 写成播放位置，
    /// 双向绑定下用户拖到 X 之后，同一帧的播放循环又会把位置写回同一个 bindable ——
    /// 而 BASS 的位置更新要等音频线程处理完这次 seek，于是滑块被"旧位置"拽回去、
    /// 下一帧再跳回来，表现为拖动时来回抖（卡顿）。拖动期间完全由滑块自己的值主导，
    /// 松手后再由播放进度接手，两者不再互相覆盖。
    ///
    /// 拖动中的每个位置都走 <see cref="OnScrub"/>（是否让音频实时跟随由 <c>MusicPlayer</c> 按音质决定，
    /// 不阻塞输入线程）；点击与松手再调用 <see cref="OnSeek"/> 提交一次精确位置。
    /// </summary>
    public partial class SeekSliderBar : RoundedSliderBar<double>
    {
        /// <summary>播放进度来源（通常是 <c>MusicPlayer.CurrentTimeMs</c>）。</summary>
        public BindableDouble? Source;

        /// <summary>点击 / 松手时的一次性精确跳转。</summary>
        public Action<double>? OnSeek;

        /// <summary>拖动过程中的连续定位；音频是否实时跟随由实现方按音质决定（见 <c>MusicPlayer.Scrub</c>）。</summary>
        public Action<double>? OnScrub;

        /// <summary>程序化同步期间置位，避免把"同步"误判成用户操作而触发 seek。</summary>
        private bool syncing;

        /// <summary>最近一次用户值，松手时用它做精确提交。</summary>
        private double lastValue;

        protected override void Update()
        {
            base.Update();

            if (syncing || IsDragged || Source is null)
                return;

            if (Current.Value == Source.Value)
                return;

            syncing = true;

            try
            {
                Current.Value = Source.Value;
            }
            finally
            {
                syncing = false;
            }
        }

        protected override void OnUserChange(double value)
        {
            if (syncing)
                return;

            lastValue = value;

            // 拖动中每个变更事件都会走到这里（每秒几十次）。定位交给 OnScrub 立即处理，
            // 但不能在这里做同步 seek —— 输入线程上会一直阻塞到音频线程定位完。
            OnScrub?.Invoke(value);
        }

        protected override bool OnClick(ClickEvent e)
        {
            bool handled = base.OnClick(e);

            if (handled)
            {
                OnSeek?.Invoke(Current.Value);
                AppLog.Ui.Add($"点击跳转进度 → {Current.Value / 1000:0.0}s");
            }

            return handled;
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            base.OnDragEnd(e);

            // 拖动期间只做了限频 scrub，松手时补一次精确提交。
            OnSeek?.Invoke(lastValue);
            AppLog.Ui.Add($"拖动进度 → {lastValue / 1000:0.0}s");
        }
    }
}
