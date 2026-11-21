using System.Diagnostics;

using System.Linq;

namespace OsuPlayer.Game.Music.BeatSync
{
    /// <summary>
    /// 训练进度共享状态：后台批量任务写、界面线程读。
    ///
    /// 训练界面以前是一块纯黑窗口（osu!framework 启动了但没有推送任何 Screen），
    /// 批量任务跑到一半完全看不出在干什么。这个类只做计数与计时，读侧每 250ms 取一次快照，
    /// 不持有任何 Drawable，方便在无界面的命令行路径里传 null。
    /// </summary>
    public sealed class TrainingProgress
    {
        private readonly Stopwatch clock = new();

        private int total;
        private int completed;
        private int skipped;
        private int threads = 1;
        private volatile string stage = "准备中…";
        private volatile bool finished;
        private volatile bool failed;

        /// <summary>最近若干首完成的时刻，用来按「近期速度」估计剩余时间。</summary>
        private readonly Queue<double> recentCompletions = new();
        private const int recent_window = 64;

        /// <summary>实际参与训练的线程数（命令行 <c>--train-threads</c>）。</summary>
        public int Threads
        {
            get => threads;
            private set => threads = value;
        }

        public void Begin(int totalSongs, int workerThreads, string stageName)
        {
            total = Math.Max(0, totalSongs);
            Threads = Math.Max(1, workerThreads);
            completed = 0;
            skipped = 0;
            finished = false;
            failed = false;
            stage = stageName;

            lock (recentCompletions)
                recentCompletions.Clear();

            clock.Restart();
        }

        public void SetStage(string value) => stage = value;

        /// <summary>一首歌处理结束（无论成功、跳过还是失败都计入完成）。</summary>
        public void SongCompleted(bool skippedSong = false)
        {
            if (skippedSong)
                Interlocked.Increment(ref skipped);

            Interlocked.Increment(ref completed);

            lock (recentCompletions)
            {
                recentCompletions.Enqueue(clock.Elapsed.TotalSeconds);

                if (recentCompletions.Count > recent_window)
                    recentCompletions.Dequeue();
            }
        }

        public void Finish()
        {
            finished = true;

            if (!failed)
                stage = "完成";

            clock.Stop();
        }

        /// <summary>批量任务抛异常：窗口不要显示「完成」，也不要把失败原因吞掉。</summary>
        public void Fail(string reason)
        {
            failed = true;
            finished = true;
            stage = string.IsNullOrWhiteSpace(reason) ? "失败" : $"失败：{reason}";
            clock.Stop();
        }

        public Snapshot Read()
        {
            int totalValue = Volatile.Read(ref total);
            int completedValue = Volatile.Read(ref completed);
            int skippedValue = Volatile.Read(ref skipped);
            double elapsed = clock.Elapsed.TotalSeconds;
            int remaining = Math.Max(0, totalValue - completedValue);

            // 完成 0 首时无法估计速度；结束时不再显示「预计剩余」。
            // 速度按最近 64 首算：并行解码前几首往往最慢（BASS 争用），
            // 用全程平均会把 ETA 估得偏大很多（实测 302 张：全程平均估 45s，实际剩余 21s）。
            double? eta = null;

            if (!finished && completedValue > 0 && remaining > 0)
            {
                double rate;

                lock (recentCompletions)
                {
                    if (recentCompletions.Count >= 8)
                        rate = (recentCompletions.Count - 1) / Math.Max(0.001, recentCompletions.Last() - recentCompletions.Peek());
                    else
                        rate = completedValue / Math.Max(0.001, elapsed);
                }

                eta = rate > 0 ? remaining / rate : null;
            }

            return new Snapshot(stage, totalValue, completedValue, skippedValue, remaining,
                Threads, elapsed, eta, finished, failed);
        }

        public readonly record struct Snapshot(string Stage, int Total, int Completed, int Skipped,
                                               int Remaining, int Threads, double ElapsedSeconds,
                                               double? EtaSeconds, bool Finished, bool Failed);
    }
}
