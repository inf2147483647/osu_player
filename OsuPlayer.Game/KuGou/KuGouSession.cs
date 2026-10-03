using OsuPlayer.Game.KuGou.Signing;

namespace OsuPlayer.Game.KuGou
{
    /// <summary>
    /// Mutable login/device state attached to every request. Populated after QR login (token/userid).
    /// Mirrors the "cookie" bag used by the reference projects.
    /// </summary>
    public sealed class KuGouSession
    {
        public KuGouDeviceIdentity Device { get; }

        public string Token { get; set; } = string.Empty;

        public long UserId { get; set; }

        public int VipType { get; set; }

        /// <summary>Member token that accompanies <see cref="VipType"/>; required by the play-URL gateway.</summary>
        public string VipToken { get; set; } = string.Empty;

        /// <summary>Device fingerprint id; "-" until a register_dev call succeeds.</summary>
        public string Dfid
        {
            get => Device.Dfid;
            set => Device.Dfid = value;
        }

        /// <summary>Device MID (decimal form of md5(guid)); see <see cref="KuGouDeviceIdentity.Mid"/>.</summary>
        public string Mid => Device.Mid;

        /// <summary>Device UUID query parameter; always "-" for the current clients.</summary>
        public string Uuid => Device.Uuid;

        public bool IsLoggedIn => !string.IsNullOrEmpty(Token) && UserId != 0;

        /// <summary>
        /// Client flavour presented to the gateway. This player adopts the 酷狗概念版 (lite) identity:
        /// appid 3116, clientver 11440 and the lite MD5 salts, i.e. the free listening product rather
        /// than the standard 酷狗音乐 client. Note that login tokens are issued per platform and are
        /// NOT interchangeable, so switching this value invalidates a persisted session.
        /// </summary>
        public KuGouPlatform Platform { get; set; } = KuGouPlatform.Lite;

        public KuGouSession(KuGouDeviceIdentity? device = null)
        {
            Device = device ?? new KuGouDeviceIdentity();
        }

        public void SetLogin(string token, long userId, int vipType = 0, string vipToken = "")
        {
            Token = token;
            UserId = userId;
            VipType = vipType;
            VipToken = vipToken;
        }

        public void Logout()
        {
            Token = string.Empty;
            UserId = 0;
            VipType = 0;
            VipToken = string.Empty;
        }
    }
}
