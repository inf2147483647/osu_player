using System.Globalization;

namespace OsuPlayer.Game.NetEase
{
    /// <summary>
    /// 网易云登录态。与酷狗完全独立：这里只保存网易云自己的 Cookie（核心是 <c>MUSIC_U</c>）
    /// 和账号资料，不与酷狗的 token / userid 互相影响。
    /// </summary>
    public sealed class NetEaseSession
    {
        private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

        /// <summary>eapi 请求头里的设备标识：52 位大写 hex，进程内固定即可。</summary>
        public string DeviceId { get; } = generateDeviceId();

        public long UserId { get; private set; }

        public string Nickname { get; private set; } = string.Empty;

        public string AvatarUrl { get; private set; } = string.Empty;

        /// <summary>登录凭证就是 <c>MUSIC_U</c> Cookie；扫码确认后由服务端 Set-Cookie 下发。</summary>
        public bool IsLoggedIn => cookies.ContainsKey("MUSIC_U");

        /// <summary>请求时整体回传给网易云的 Cookie 头。</summary>
        public string CookieHeader => string.Join("; ", cookies.Select(p => $"{p.Key}={p.Value}"));

        /// <summary>weapi 的 CSRF 校验值，就是 <c>__csrf</c> Cookie；未登录时为空串。</summary>
        public string CsrfToken => cookies.TryGetValue("__csrf", out string? value) ? value : string.Empty;

        public string? GetCookie(string name) => cookies.TryGetValue(name, out string? value) ? value : null;

        /// <summary>把服务端 Set-Cookie 合并进会话（包括未登录时下发的 NMTID）。</summary>
        public void MergeSetCookies(IEnumerable<string> headers)
        {
            foreach (string header in headers)
            {
                string pair = header.Split(';')[0];
                int equals = pair.IndexOf('=');

                if (equals <= 0)
                    continue;

                string name = pair[..equals].Trim();
                string value = pair[(equals + 1)..].Trim();

                if (name.Length == 0)
                    continue;

                bool deleted = value.Length == 0
                               || header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)
                               || header.Contains("01 Jan 1970", StringComparison.OrdinalIgnoreCase);

                if (deleted)
                {
                    cookies.Remove(name);
                    continue;
                }

                cookies[name] = value;
            }
        }

        /// <summary>登录成功后记录账号 ID 与资料（Cookie 已由请求层合并）。</summary>
        public void ApplyLogin(long userId, string nickname, string avatarUrl)
        {
            if (userId > 0)
                UserId = userId;

            if (!string.IsNullOrEmpty(nickname))
                Nickname = nickname;

            if (!string.IsNullOrEmpty(avatarUrl))
                AvatarUrl = avatarUrl;
        }

        /// <summary>直接从状态文件恢复（Cookie 串 + 账号资料）。</summary>
        public void Restore(string? cookieHeader, long userId, string? nickname, string? avatarUrl)
        {
            cookies.Clear();

            UserId = userId;
            Nickname = nickname ?? string.Empty;
            AvatarUrl = avatarUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cookieHeader))
                return;

            foreach (string part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = part.IndexOf('=');

                if (equals <= 0)
                    continue;

                cookies[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }
        }

        public void Logout()
        {
            cookies.Clear();
            UserId = 0;
            Nickname = string.Empty;
            AvatarUrl = string.Empty;
        }

        public string Describe()
            => IsLoggedIn
                ? $"已登录，用户 ID={UserId.ToString(CultureInfo.InvariantCulture)}"
                : "未登录";

        private static string generateDeviceId()
        {
            const string hex = "0123456789ABCDEF";
            var chars = new char[52];

            for (int i = 0; i < chars.Length; i++)
                chars[i] = hex[Random.Shared.Next(hex.Length)];

            return new string(chars);
        }
    }
}
