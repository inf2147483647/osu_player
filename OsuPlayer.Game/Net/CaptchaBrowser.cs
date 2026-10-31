using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OsuPlayer.Game.Logging;

namespace OsuPlayer.Game.Net
{
    /// <summary>
    /// 用系统默认浏览器完成酷狗要求的腾讯验证码，并把结果回传给调用方。
    ///
    /// 为什么需要它：网关对高风险播放请求返回 errcode 20028，必须完成人机验证才能继续。腾讯验证码是一个
    /// 交互式网页组件，osu!framework 里没有浏览器环境，所以这里起一个只监听回环地址的迷你 HTTP 服务，
    /// 托管一个加载 TCaptcha.js 的页面，验证通过后页面把 ticket/randstr POST 回来。
    ///
    /// 故意不用 <see cref="HttpListener"/>：它在 Windows 上需要 URL ACL 或管理员权限；直接使用
    /// <see cref="TcpListener"/> 只需回环端口，普通用户即可运行。
    /// </summary>
    public static class CaptchaBrowser
    {
        public sealed record CaptchaResult(string Ticket, string RandStr);

        /// <summary>
        /// 平台接管的「打开验证页」钩子（安卓端用系统浏览器 <c>Intent.ACTION_VIEW</c> 实现）。
        /// 参数为页面 URL（回环地址），返回是否成功拉起浏览器；不设置则走桌面 <see cref="Process.Start"/>。
        /// </summary>
        public static Func<string, bool>? ExternalPageOpener;

        /// <summary>打开验证页并等待用户完成验证；超时或取消返回 null。</summary>
        public static async Task<CaptchaResult?> SolicitAsync(string tencentAppId, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var completion = new TaskCompletionSource<CaptchaResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            _ = acceptLoop(listener, completion, tencentAppId);

            string pageUrl = $"http://127.0.0.1:{port}/";

            try
            {
                AppLog.Net.Add($"打开浏览器进行安全验证：{pageUrl}", osu.Framework.Logging.LogLevel.Important);

                bool opened = ExternalPageOpener is { } opener
                    ? opener(pageUrl)
                    : Process.Start(new ProcessStartInfo(pageUrl) { UseShellExecute = true }) is not null;

                if (!opened)
                    throw new InvalidOperationException("浏览器未拉起（平台未处理打开请求）");
            }
            catch (Exception e)
            {
                AppLog.Net.Add($"打开验证页面失败：{e.Message}", osu.Framework.Logging.LogLevel.Error, e);
                listener.Stop();
                return null;
            }

            using (linked.Token.Register(() => completion.TrySetResult(null)))
            {
                CaptchaResult? result = await completion.Task.ConfigureAwait(false);
                listener.Stop();
                return result;
            }
        }

        private static async Task acceptLoop(TcpListener listener, TaskCompletionSource<CaptchaResult?> completion,
            string tencentAppId)
        {
            try
            {
                while (!completion.Task.IsCompleted)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = handleAsync(client, completion, tencentAppId);
                }
            }
            catch
            {
                // 监听被关闭（验证完成或超时）属于正常退出路径。
            }
        }

        private static async Task handleAsync(TcpClient client, TaskCompletionSource<CaptchaResult?> completion,
            string tencentAppId)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    (string method, string path, string body) = await readRequestAsync(stream).ConfigureAwait(false);

                    if (method == "POST" && path.StartsWith("/result", StringComparison.Ordinal))
                    {
                        if (tryParseResult(body) is { } result)
                            completion.TrySetResult(result);

                        await writeResponseAsync(stream, "200 OK", "application/json",
                            "{\"ok\":true}").ConfigureAwait(false);
                        return;
                    }

                    await writeResponseAsync(stream, "200 OK", "text/html; charset=utf-8",
                        buildPage(tencentAppId)).ConfigureAwait(false);
                }
            }
            catch
            {
                // 浏览器断开/半包等异常无需上报，验证靠超时兜底。
            }
        }

        private static CaptchaResult? tryParseResult(string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("ticket", out JsonElement ticket)
                    && document.RootElement.TryGetProperty("randstr", out JsonElement randStr))
                    return new CaptchaResult(ticket.GetString() ?? string.Empty, randStr.GetString() ?? string.Empty);
            }
            catch (JsonException)
            {
                // 忽略格式错误的回调。
            }

            return null;
        }

        private static async Task<(string Method, string Path, string Body)> readRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var chunk = new byte[1024];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(chunk).ConfigureAwait(false);

                if (read <= 0)
                    break;

                buffer.AddRange(chunk.AsSpan(0, read));
                headerEnd = indexOfHeaderEnd(buffer);
            }

            string headers = Encoding.ASCII.GetString(buffer.ToArray());
            string[] lines = headers.Split("\r\n");
            string[] requestLine = lines.Length > 0 ? lines[0].Split(' ') : [];
            string method = requestLine.Length > 0 ? requestLine[0] : string.Empty;
            string path = requestLine.Length > 1 ? requestLine[1] : "/";

            int contentLength = 0;

            foreach (string line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }

            var bodyBytes = new List<byte>();

            if (headerEnd >= 0 && buffer.Count > headerEnd)
                bodyBytes.AddRange(buffer.GetRange(headerEnd, buffer.Count - headerEnd));

            while (bodyBytes.Count < contentLength)
            {
                int read = await stream.ReadAsync(chunk).ConfigureAwait(false);

                if (read <= 0)
                    break;

                bodyBytes.AddRange(chunk.AsSpan(0, read));
            }

            return (method, path, Encoding.UTF8.GetString(bodyBytes.ToArray()));
        }

        private static int indexOfHeaderEnd(List<byte> buffer)
        {
            for (int i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i + 4;
            }

            return -1;
        }

        private static async Task writeResponseAsync(NetworkStream stream, string status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n"
                            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string buildPage(string tencentAppId)
        {
            string script = KuGou.KuGouConstants.tcaptcha_script_url;

            return $$"""
                <!doctype html>
                <html lang="zh-CN">
                <head>
                <meta charset="utf-8">
                <title>酷狗安全验证</title>
                <style>
                  body { margin:0; height:100vh; display:flex; align-items:center; justify-content:center;
                         background:#111; color:#eee; font:14px/1.6 "Microsoft YaHei",sans-serif; }
                  #status { max-width:520px; padding:0 24px; text-align:center; }
                </style>
                </head>
                <body>
                <div id="status">正在加载验证码…请在下方完成验证，完成后本页会自动关闭。</div>
                <script src="{{script}}"></script>
                <script>
                  var APPID = "{{tencentAppId}}";
                  function setStatus(text) { document.getElementById('status').textContent = text; }
                  function submit(ticket, randstr) {
                    setStatus('验证通过，正在返回播放器…');
                    fetch('/result', {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify({ ticket: ticket, randstr: randstr })
                    }).then(function () { setTimeout(function () { window.close(); }, 800); })
                      .catch(function () { setStatus('结果回传失败，请关闭本页后在播放器中重试。'); });
                  }
                  window.onload = function () {
                    if (typeof TencentCaptcha === 'undefined') { setStatus('验证码脚本加载失败，请检查网络后刷新本页。'); return; }
                    var captcha = new TencentCaptcha(APPID, function (res) {
                      if (res && res.ret === 0) submit(res.ticket, res.randstr);
                      else setStatus('已取消验证，刷新本页可重新验证。');
                    }, { type: '', showHeader: false });
                    captcha.show();
                  };
                </script>
                </body>
                </html>
                """;
        }
    }
}
