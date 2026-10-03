using System.Numerics;
using OsuPlayer.Game.KuGou.Signing;

namespace OsuPlayer.Game.KuGou
{
    /// <summary>
    /// Pseudo-hardware identifiers, mirroring how the reference clients derive them:
    /// a stable per-install GUID (md5 of a UUID v4), from which <see cref="Mid"/> is derived as the
    /// decimal form of md5(guid). The gateway/kugouvip/youth endpoints validate this format — sending
    /// the raw 32-hex md5 (or one derived from "-") is rejected as “params invalid” (20017).
    /// </summary>
    public sealed class KuGouDeviceIdentity
    {
        /// <summary>Stable per-install id: md5(UUID v4). Sent as the KUGOU_API_GUID cookie.</summary>
        public string Guid { get; set; }

        /// <summary>Short server device id (10 uppercase chars), sent as the KUGOU_API_DEV cookie.</summary>
        public string Dev { get; set; }

        /// <summary>Device fingerprint id; "-" until a register_dev call succeeds.</summary>
        public string Dfid { get; set; } = "-";

        /// <summary>Device UUID query parameter. The reference clients always send the literal "-".</summary>
        public string Uuid => "-";

        /// <summary>Gateway MID: decimal(bigint) of md5(guid).</summary>
        public string Mid => calculateMid(Guid);

        public KuGouDeviceIdentity(string? persistentGuid = null, string? persistentDev = null)
        {
            Guid = persistentGuid ?? KuGouSigner.Md5Hex(System.Guid.NewGuid().ToString("D"));
            Dev = persistentDev ?? randomDev();
        }

        /// <summary>
        /// md5(hex) re-interpreted as a base-16 big integer and printed in decimal — the MID format the
        /// KuGou clients use (see KuGouMusicApi/util/helper.js calculateMid).
        /// </summary>
        private static string calculateMid(string input)
        {
            string hash = KuGouSigner.Md5Hex(input);
            var big = BigInteger.Zero;

            foreach (char c in hash)
            {
                int digit = c <= '9' ? c - '0' : (c & 0xDF) - 'A' + 10;
                big = big * 16 + digit;
            }

            return big.ToString();
        }

        private static string randomDev()
        {
            const string pool = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            Span<char> chars = stackalloc char[10];

            for (int i = 0; i < chars.Length; i++)
                chars[i] = pool[Random.Shared.Next(pool.Length)];

            return new string(chars);
        }
    }
}
