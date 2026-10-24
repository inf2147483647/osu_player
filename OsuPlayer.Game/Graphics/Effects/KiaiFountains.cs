// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Framework.Utils;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Graphics.Containers;

namespace OsuPlayer.Game.Graphics.Effects
{
    /// <summary>
    /// Two star fountains at the bottom corners (osu!'s KiaiMenuFountains layout).
    /// Mirrors osu!: a single burst is fired when playback enters a kiai section,
    /// not repeatedly for the whole duration, together with osu!'s fountain SFX
    /// (<c>StarFountainSounds</c>).
    /// </summary>
    public partial class KiaiFountains : BeatSyncedContainer
    {
        private StarFountain leftFountain = null!;
        private StarFountain rightFountain = null!;

        /// <summary>Whether the previous frame was already inside a kiai section (used for rising-edge detection).</summary>
        private bool isTriggered;

        /// <summary>
        /// 每次喷发时触发（参数是左右两座喷泉各自的方向）。外部全屏特效子进程据此播放同一动画，
        /// 因此它不需要自己判断 kiai 边界 —— 两端不会漂移。
        /// </summary>
        public event Action<int, int>? Fired;

        /// <summary>一拍的全部信息（供桌面歌词复刻圆饼的节拍脉冲与 kiai 闪白）。</summary>
        public readonly record struct BeatEvent(double BeatLength, bool Kiai, float Amplitude, bool Downbeat);

        /// <summary>
        /// 每个节拍触发一次（由 osu! 的 BeatSyncedContainer 驱动）。
        /// 桌面歌词用它驱动圆饼的拍点收缩/涟漪，以及 kiai 时当前歌词行的闪白。
        /// </summary>
        public event Action<BeatEvent>? Beat;

        [Resolved]
        private GameHost host { get; set; } = null!;

        /// <summary>osu! StarFountainSounds：喷发样本与循环样本。</summary>
        private Sample? shootSample;
        private Sample? loopSample;

        /// <summary>常驻的循环通道（<see cref="SampleChannel.ManualFree"/>，停止后不被样本回收）。</summary>
        private SampleChannel? loopChannel;

        /// <summary>osu! 的常量：两次喷发之间的最小间隔 / 循环音的淡出时长。</summary>
        private const int shoot_retrigger_delay = 500;
        private const int loop_fade_duration = 500;

        private double? lastPlayback;

        private ScheduledDelegate? loopFadeDelegate;
        private ScheduledDelegate? loopStopDelegate;

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            // osu! 的喷泉样本存在 Argon 皮肤资源里（StarFountainSounds 里的名字是 Gameplay/fountain-*）。
            shootSample = audio.Samples.Get(@"Gameplay/Argon/fountain-shoot");
            loopSample = audio.Samples.Get(@"Gameplay/Argon/fountain-loop");

            loopChannel = loopSample?.GetChannel();

            if (loopChannel is not null)
            {
                loopChannel.Looping = true;
                // 常驻通道：默认通道会在停止播放时被样本回收/释放，就无法再次淡入了。
                loopChannel.ManualFree = true;
            }

            RelativeSizeAxes = Axes.Both;

            Children = new Drawable[]
            {
                leftFountain = new StarFountain
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    X = 250,
                },
                rightFountain = new StarFountain
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    X = -250,
                },
            };
        }

        protected override void OnNewBeat(int beatIndex, TimingControlPoint timingPoint, EffectControlPoint effectPoint,
            ChannelAmplitudes amplitudes)
        {
            base.OnNewBeat(beatIndex, timingPoint, effectPoint, amplitudes);

            if (beatIndex < 0)
                return;

            // 只转发数据，不做任何判定：桌面歌词的圆饼按同样的拍点做收缩/涟漪与 kiai 闪白。
            // Downbeat 用 OsuLogo 的口径：每小节第一拍播放心跳的"落点"样本。
            Beat?.Invoke(new BeatEvent(
                timingPoint.BeatLength,
                effectPoint.KiaiMode,
                Math.Max(0, amplitudes.Maximum),
                beatIndex % timingPoint.TimeSignature.Numerator == 0));
        }

        protected override void Update()
        {
            base.Update();

            // osu! fires the fountains once per kiai effect point (i.e. on the rising edge into a
            // kiai section), rather than continuously for the section's whole duration.
            if (EffectPoint.KiaiMode && !isTriggered)
            {
                // Only when the playhead is actually at the section's effect point; guards against
                // firing a stray burst when analysis results arrive late, mid-section.
                bool isNearEffectPoint = Math.Abs(BeatSyncSource.Clock.CurrentTime - EffectPoint.Time) < 500;

                if (isNearEffectPoint)
                    Shoot();
            }

            isTriggered = EffectPoint.KiaiMode;
        }

        public void Shoot()
        {
            int direction = RNG.Next(-1, 2);

            // osu! 的三个分支：-1 = 两座向中间喷，0 = 同向喷，1 = 两座向两边喷。
            int leftDirection = direction;
            int rightDirection = direction;

            if (direction == -1)
            {
                leftDirection = 1;
                rightDirection = -1;
            }
            else if (direction == 1)
            {
                leftDirection = -1;
                rightDirection = 1;
            }

            leftFountain.Shoot(leftDirection);
            rightFountain.Shoot(rightDirection);

            Fired?.Invoke(leftDirection, rightDirection);

            playSounds();
        }

        /// <summary>
        /// 移植自 osu! 的 <c>StarFountainSounds.Play()</c>：距上次播放超过 500ms 就整段放「喷发」样本；
        /// 否则改用循环样本「淡入 → 500ms 后淡出并停止」，这样连续进入 kiai 段时声音是接续的而不是叠一堆。
        /// </summary>
        private void playSounds()
        {
            // osu! 同样会在窗口未激活时静音，避免后台一直响。
            if (!host.IsActive.Value)
                return;

            loopFadeDelegate?.Cancel();
            loopStopDelegate?.Cancel();

            try
            {
                if (lastPlayback == null || Time.Current - lastPlayback > shoot_retrigger_delay)
                {
                    loopChannel?.Stop();
                    shootSample?.Play();
                    return;
                }

                if (loopChannel is not null && !loopChannel.Playing)
                {
                    this.TransformBindableTo(loopChannel.Volume, 1);
                    loopChannel.Play();
                }

                loopFadeDelegate = Scheduler.AddDelayed(() =>
                {
                    if (loopChannel is not null)
                        this.TransformBindableTo(loopChannel.Volume, 0, loop_fade_duration);

                    loopStopDelegate = Scheduler.AddDelayed(() => loopChannel?.Stop(), loop_fade_duration);
                }, shoot_retrigger_delay);
            }
            finally
            {
                lastPlayback = Time.Current;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            // ManualFree 的通道不会被样本自动回收，这里自己收尾。
            loopChannel?.Dispose();
            loopChannel = null;
        }
    }
}
