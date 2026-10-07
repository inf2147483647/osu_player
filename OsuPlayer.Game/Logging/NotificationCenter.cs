using osu.Framework.Logging;

namespace OsuPlayer.Game.Logging
{
    /// <summary>
    /// 面向用户的错误通知中心。
    ///
    /// 数据源是 <see cref="Logger.NewEntry"/> 里 <see cref="LogLevel.Error"/> 级别的条目 ——
    /// 也就是说任何地方用 Error 级记录的失败都会自动出现在通知里，不需要调用方额外接线；
    /// 日志文件照旧写全，这里只是给用户看的一份投影。
    ///
    /// 线程：日志可能来自任意线程（音频分析、网络、后台任务），因此内部用锁保护，
    /// 对外只暴露快照与 <see cref="Changed"/>/<see cref="Posted"/> 事件；
    /// UI 侧收到事件后必须自己 <c>Schedule</c> 回更新线程再改 Drawable。
    /// </summary>
    public static class NotificationCenter
    {
        /// <summary>一条通知（已把异常信息并入文本，便于直接展示）。</summary>
        public sealed record Entry(DateTimeOffset Time, string Source, string Message);

        /// <summary>条目上限：异常反复触发时不能无限涨（详情始终在日志文件里）。</summary>
        private const int max_entries = 200;

        private static readonly object gate = new object();
        private static readonly List<Entry> entries = new List<Entry>();

        private static int unreadCount;
        private static bool hooked;

        /// <summary>列表内容或未读数变化（可能在任意线程触发）。</summary>
        public static event Action? Changed;

        /// <summary>新增了一条通知（可能在任意线程触发）。</summary>
        public static event Action<Entry>? Posted;

        public static int UnreadCount
        {
            get
            {
                lock (gate)
                    return unreadCount;
            }
        }

        /// <summary>最旧的在前。每次调用返回独立副本，可安全跨线程持有。</summary>
        public static IReadOnlyList<Entry> Snapshot()
        {
            lock (gate)
                return entries.ToArray();
        }

        public static void MarkAllRead()
        {
            lock (gate)
            {
                if (unreadCount == 0)
                    return;

                unreadCount = 0;
            }

            Changed?.Invoke();
        }

        public static void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                unreadCount = 0;
            }

            Changed?.Invoke();
        }

        /// <summary>把 Error 级日志接到通知中心。幂等，重复调用只挂一次。</summary>
        public static void Hook()
        {
            lock (gate)
            {
                if (hooked)
                    return;

                hooked = true;
            }

            Logger.NewEntry += onNewEntry;
        }

        /// <summary>手动上报一条错误（给没有走 Logger 的失败路径用）。</summary>
        public static void Report(string source, string message, Exception? exception = null)
        {
            // 多数调用点已经把 e.Message 写进消息里了（"失败 xxx：{e.Message}"），
            // 少数只挂了异常、没写进消息（AppLog.TimeAsync）。这里按需补，避免同一句话出现两遍。
            string text = exception is not null && !message.Contains(exception.Message, StringComparison.Ordinal)
                ? $"{message}：{exception.Message}"
                : message;

            var entry = new Entry(DateTimeOffset.Now, source, text);

            lock (gate)
            {
                entries.Add(entry);

                // 顺序不能反：先加条目再裁剪，否则刚上报的会被自己挤掉。
                if (entries.Count > max_entries)
                    entries.RemoveRange(0, entries.Count - max_entries);

                unreadCount++;
            }

            Posted?.Invoke(entry);
            Changed?.Invoke();
        }

        private static void onNewEntry(LogEntry entry)
        {
            // Message 里不含异常（框架把异常只写进控制台/日志文件），所以这里补上。
            if (entry?.Message is null || entry.Level != LogLevel.Error)
                return;

            Report(entry.LoggerName ?? entry.Target?.ToString() ?? "app", entry.Message, entry.Exception);
        }
    }
}
