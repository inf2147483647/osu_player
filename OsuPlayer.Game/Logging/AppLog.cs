using System.Diagnostics;
using osu.Framework.Logging;

namespace OsuPlayer.Game.Logging
{
    /// <summary>
    /// Central log registry. Every operation in the app writes to one of these named targets;
    /// osu!framework persists each one as its own file under %APPDATA%/OsuPlayer/logs
    /// (ui.log / network.log / kugou.log / player.log / beat.log) in addition to runtime.log.
    ///
    /// Rules:
    ///  - Never put tokens/signatures/full URLs with query strings into messages.
    ///  - User actions and operation results use Verbose (kept in every build);
    ///    high-frequency/chattery events use Debug (debug builds only);
    ///    failures use Warning/Error with the exception attached.
    /// </summary>
    public static class AppLog
    {
        public static readonly Logger Ui = Logger.GetLogger("ui");
        public static readonly Logger Net = Logger.GetLogger("network");
        public static readonly Logger Api = Logger.GetLogger("kugou");
        public static readonly Logger Player = Logger.GetLogger("player");
        public static readonly Logger Beat = Logger.GetLogger("beat");

        /// <summary>Run an asynchronous operation, logging start / success (+duration) / failure.</summary>
        public static async Task<T> TimeAsync<T>(this Logger logger, string operation, Func<Task<T>> action,
            Func<T, string?>? describe = null)
        {
            var sw = Stopwatch.StartNew();
            logger.Add($"开始 {operation}");

            try
            {
                T result = await action().ConfigureAwait(false);
                logger.Add($"完成 {operation}{describe?.Invoke(result)}，耗时 {sw.ElapsedMilliseconds}ms");
                return result;
            }
            catch (OperationCanceledException)
            {
                logger.Add($"取消 {operation}，耗时 {sw.ElapsedMilliseconds}ms", LogLevel.Important);
                throw;
            }
            catch (Exception e)
            {
                logger.Add($"失败 {operation}，耗时 {sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                throw;
            }
        }

        /// <summary>Run an asynchronous operation without a result value.</summary>
        public static Task TimeAsync(this Logger logger, string operation, Func<Task> action)
            => TimeAsync(logger, operation, async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            }, null);

        /// <summary>Run a synchronous operation, logging duration / failure.</summary>
        public static T Time<T>(this Logger logger, string operation, Func<T> action, Func<T, string?>? describe = null)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                T result = action();
                logger.Add($"完成 {operation}{describe?.Invoke(result)}，耗时 {sw.ElapsedMilliseconds}ms");
                return result;
            }
            catch (Exception e)
            {
                logger.Add($"失败 {operation}，耗时 {sw.ElapsedMilliseconds}ms", LogLevel.Error, e);
                throw;
            }
        }

        /// <summary>Strip the query/credential part of a URL before logging.</summary>
        public static string SafeUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return "(空)";

            int query = url.IndexOf('?');
            string withoutQuery = query >= 0 ? url[..query] : url;

            return url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? "data:（内联数据）"
                : withoutQuery;
        }
    }
}
