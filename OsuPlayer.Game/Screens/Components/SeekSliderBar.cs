using System.Numerics;
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
    /// Progress slider over osu!'s <see cref="RoundedSliderBar{T}"/>: binds its number to
    /// track time and only performs an actual seek in response to user interaction (so
    /// per-frame playback updates don't seek BASS).
    /// </summary>
    public partial class SeekSliderBar : RoundedSliderBar<double>
    {
        public Action<double>? OnSeek;

        protected override void OnUserChange(double value)
        {
            OnSeek?.Invoke(value);

            // While dragging, dozens of change events fire per second; only log the single
            // commit produced by a click (no active drag). Drag commits are logged on drag end.
            if (!IsDragged)
                AppLog.Ui.Add($"点击跳转进度 → {value / 1000:0.0}s");
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            AppLog.Ui.Add($"拖动进度 → {Current.Value / 1000:0.0}s");
            base.OnDragEnd(e);
        }
    }
}
