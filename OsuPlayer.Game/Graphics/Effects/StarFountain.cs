// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Textures;
using osu.Framework.Threading;
using osu.Framework.Utils;
using osu.Game.Graphics;
using osuTK;

namespace OsuPlayer.Game.Graphics.Effects
{
    /// <summary>
    /// osu!'s StarFountain with the skin layer removed: a plain container holding the
    /// particle spewer; the star texture ("Menu/fountain-star") comes from osu! resources.
    /// </summary>
    public partial class StarFountain : Container
    {
        private StarFountainSpewer spewer = null!;

        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            InternalChild = spewer = new StarFountainSpewer(textures.Get("Menu/fountain-star"));
        }

        public void Shoot(int direction) => spewer.Shoot(direction);

        public partial class StarFountainSpewer : ParticleSpewer
        {
            private const int particle_duration_min = 300;
            private const int particle_duration_max = 1000;

            private double? lastShootTime;

            protected int LastShootDirection { get; private set; }

            protected override float ParticleGravity => 800;

            protected virtual double ShootDuration => 800;

            public StarFountainSpewer(Texture? texture)
                : base(null, 240, particle_duration_max)
            {
                Texture = texture;
            }

            protected override FallingParticle CreateParticle()
            {
                return new FallingParticle
                {
                    StartPosition = new Vector2(0, 50),
                    Duration = RNG.NextSingle(particle_duration_min, particle_duration_max),
                    StartAngle = getRandomVariance(4),
                    EndAngle = getRandomVariance(2),
                    EndScale = 2.2f + getRandomVariance(0.4f),
                    Velocity = new Vector2(GetCurrentAngle(), -1400 + getRandomVariance(100)),
                };
            }

            protected virtual float GetCurrentAngle()
            {
                const float x_velocity_random_variance = 60;
                const float x_velocity_from_direction = 500;

                return LastShootDirection * x_velocity_from_direction * (float)(1 - 2 * (Clock.CurrentTime - lastShootTime!.Value) / ShootDuration) + getRandomVariance(x_velocity_random_variance);
            }

            private ScheduledDelegate? deactivateDelegate;

            public void Shoot(int direction)
            {
                Active.Value = true;

                deactivateDelegate?.Cancel();
                deactivateDelegate = Scheduler.AddDelayed(() => Active.Value = false, ShootDuration);

                lastShootTime = Clock.CurrentTime;
                LastShootDirection = direction;
            }

            private static float getRandomVariance(float variance) => RNG.NextSingle(-variance, variance);
        }
    }
}
