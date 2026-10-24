using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OsuPlayer.Game.KuGou.Signing
{
    /// <summary>
    /// 安全验证（SSA）所需的密码学工具，逐函数移植自 KuGouMusicApi：
    /// <c>util/generate_simulate.js</c>（行为指纹 sid/edt）与 <c>util/crypto.js</c>
    /// （cryptoAesEncrypt / cryptoRSAEncrypt）。
    ///
    /// 两条链路：
    ///  - 行为指纹：明文 → AES-128-CBC（固定 IV "kugousecurity123"）→ EDT(Base64)；
    ///    AES 密钥 → RSA-OAEP(SHA-256) → SID(Base64)。
    ///  - 验证上报：AES-256-CBC 得到 params(hex)，AES 密钥再用「裸 RSA」得到 pk(hex)。
    /// </summary>
    internal static class KuGouVerificationCrypto
    {
        /// <summary>randomString 的字符表（util/util.js，36 个字符，无小写）。</summary>
        private const string random_alphabet = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public sealed record AesEncrypted(string Key, string Hex);

        public static string RandomString(int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(random_alphabet[Random.Shared.Next(random_alphabet.Length)]);
            return sb.ToString();
        }

        /// <summary>
        /// cryptoAesEncrypt({}) ：随机 16 位小写串 → MD5 前 32 位作密钥、其后 16 位作 IV，
        /// AES-256-CBC + PKCS7 加密 "{}"，输出 hex；密钥原文随响应回传（提交时用 <c>Key</c> 再 RSA）。
        /// </summary>
        public static AesEncrypted AesEncryptEmptyObject() => AesEncryptJson("{}");

        /// <summary>
        /// cryptoAesEncrypt(对象)：JSON 文本 → AES-256-CBC + PKCS7 → 小写 hex。
        /// 密钥同样由随机 16 位小写串派生（MD5 前 32 位作 key、其后 16 位作 IV），
        /// 原文随结果返回 —— 登录接口提交的 <c>params</c>/<c>pk</c> 就是「密文 + 被 RSA 的密钥」这一对。
        /// </summary>
        public static AesEncrypted AesEncryptJson(string plaintext)
        {
            string tempKey = RandomString(16).ToLowerInvariant();
            string key = KuGouSigner.Md5Hex(tempKey);
            return new AesEncrypted(tempKey, AesCbcHex(key, key[16..], plaintext));
        }

        /// <summary>
        /// cryptoAesDecrypt ：与 <see cref="AesEncryptJson"/> 对称 —— 密钥 MD5 取前 32 位、
        /// IV 取其末 16 位，把 hex 密文解成 UTF-8 明文（登录响应里的 <c>secu_params</c> 走这里）。
        /// </summary>
        public static string AesDecryptHex(string hex, string tempKey)
        {
            string key = KuGouSigner.Md5Hex(tempKey);

            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(key[16..]);

            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] input = Convert.FromHexString(hex);
            return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(input, 0, input.Length));
        }

        /// <summary>
        /// 用固定 key/iv 的 AES-256-CBC（cryptoAesEncrypt(data, {key, iv}) 的重载）：
        /// 概念版登录请求里的 t1/t2 就是用它算出来的，输出小写 hex。
        /// </summary>
        public static string AesEncryptWithFixedKey(string plaintext, string key, string iv)
            => AesCbcHex(key, iv, plaintext);

        /// <summary>
        /// cryptoRSAEncrypt ：用「裸 RSA」（无填充，c = m^e mod n）加密明文，输出定长 hex。
        /// 明文不足密钥长度时右侧补零（与参考实现一致）。
        /// </summary>
        public static string RawRsaEncryptHex(string plaintext, KuGouPlatform platform)
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(KuGouConstants.VerifyRsaKey(platform));

            RSAParameters parameters = rsa.ExportParameters(false);
            byte[] modulus = parameters.Modulus!;
            int keyLength = modulus.Length;

            byte[] buffer = Encoding.UTF8.GetBytes(plaintext);

            if (buffer.Length > keyLength)
                throw new InvalidOperationException("Data length exceeds key size");

            var padded = new byte[keyLength];
            Array.Copy(buffer, padded, buffer.Length);

            var m = new BigInteger(padded, isUnsigned: true, isBigEndian: true);
            var e = new BigInteger(parameters.Exponent!, isUnsigned: true, isBigEndian: true);
            var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);

            return BigInteger.ModPow(m, e, n).ToString("x").PadLeft(keyLength * 2, '0');
        }

        /// <summary>
        /// generateSimulate：生成服务端模拟的行为指纹。明文形如
        /// <c>mid=..;userid=..;dfid=..;webgl=..;webdriver=0;ts=..;data=..</c>。
        /// </summary>
        public static (string Sid, string Edt) GenerateSimulate(string mid, long userId, string dfid,
            string? webglHash = null)
        {
            // 哨兵值每次重新生成，增加指纹随机性。
            int sentinel = unchecked((int)(0xffffffffu - (uint)Random.Shared.Next(20)));

            string key = KuGouSigner.Md5Hex(RandomString(16))[..16];
            int points = randomInt(30, 60);

            string webgl = string.IsNullOrEmpty(webglHash) ? webglHashValue() : webglHash;
            string data = buildFingerprint(sentinel, randomInt(200, 600), randomInt(200, 500),
                randomInt(500, 700), randomInt(80, 150), points);

            string plaintext = $"mid={orZero(mid)};userid={userId};dfid={orZero(dfid)};"
                               + $"webgl={webgl};webdriver=0;ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};data={data}";

            string edt = AesCbcBase64(key, KuGouConstants.simulate_iv, plaintext);
            string sid = OaepSha256Base64(key);

            return (sid, edt);
        }

        private static string orZero(string value) => string.IsNullOrEmpty(value) ? "0" : value;

        /// <summary>Node 侧 WebGL 指纹回退：随机 uint64 的十进制串。</summary>
        private static string webglHashValue()
        {
            ulong hi = (ulong)Random.Shared.NextInt64(0, 0x100000000L);
            ulong lo = (ulong)Random.Shared.NextInt64(0, 0x100000000L);
            return ((hi << 32) | lo).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 模拟鼠标/滚动/窗口事件序列（三阶贝塞尔轨迹 + 抖动 + 哨兵记录），
        /// 条目用 ':' 分隔、字段用 ',' 分隔。
        /// </summary>
        private static string buildFingerprint(int sentinel, int startX, int startY, int endX, int endY, int mousePoints)
        {
            var entries = new List<string>();
            int ts = 0;
            int eventIndex = 0;

            entries.Add(f5(0, 0));
            entries.Add(fs5(sentinel, 0));
            entries.Add(f5(0, 0));
            entries.Add(fs5(sentinel, 0));

            ts += randomInt(5, 20);
            entries.Add(f6(ts, eventIndex, 750, 500));
            entries.Add(fs6(sentinel, eventIndex, 750, 500));
            eventIndex++;

            for (int i = 0; i < 3; i++)
            {
                ts += randomInt(80, 600);
                entries.Add(f5(ts, eventIndex));
                entries.Add(fs5(sentinel, eventIndex));
                eventIndex++;
            }

            IReadOnlyList<(double X, double Y)> path = bezierPath(startX, startY, endX, endY, mousePoints);
            int subIndex = 0;

            for (int i = 0; i < path.Count; i++)
            {
                int x = (int)Math.Round(path[i].X);
                int y = (int)Math.Round(path[i].Y);

                ts += randomInt(8, 50);
                entries.Add(f3(ts, subIndex, x, y));
                entries.Add(fs3(sentinel, subIndex, x, y));

                if (i > 0 && i % 12 == 0)
                {
                    ts += randomInt(20, 60);
                    entries.Add(f5(ts, eventIndex));
                    entries.Add(fs5(sentinel, eventIndex));
                    eventIndex++;
                }

                subIndex = (subIndex + 1) % 2;
            }

            ts += randomInt(5, 30);
            entries.Add(f3(ts, 1, endX + randomInt(-5, 5), endY + randomInt(-5, 5)));
            entries.Add(fs3(sentinel, 1, endX, endY));

            return string.Join(':', entries);
        }

        private static IReadOnlyList<(double X, double Y)> bezierPath(int sx, int sy, int ex, int ey, int n)
        {
            double c1x = sx + (ex - sx) * 0.3 + randomInt(-80, 80);
            double c1y = sy + (ey - sy) * 0.2 + randomInt(-60, 60);
            double c2x = sx + (ex - sx) * 0.7 + randomInt(-60, 60);
            double c2y = sy + (ey - sy) * 0.8 + randomInt(-40, 40);

            var points = new List<(double, double)>(n + 1);

            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double u = 1 - t;

                double x = u * u * u * sx + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * ex;
                double y = u * u * u * sy + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * ey;

                double jitter = Math.Max(0.5, 3 - t * 2.5);
                points.Add((x + (Random.Shared.NextDouble() - 0.5) * jitter,
                    y + (Random.Shared.NextDouble() - 0.5) * jitter));
            }

            return points;
        }

        private static string f3(int t, int i, int x, int y) => $"3,{t},{i},{x},{y}";
        private static string f5(int t, int i) => $"5,{t},{i}";
        private static string f6(int t, int i, int x, int y) => $"6,{t},{i},{x},{y}";
        private static string fs3(int sentinel, int i, int x, int y) => $"3,{sentinel},{i},{x},{y}";
        private static string fs5(int sentinel, int i) => $"5,{sentinel},{i}";
        private static string fs6(int sentinel, int i, int x, int y) => $"6,{sentinel},{i},{x},{y}";

        /// <summary>[min, max] 闭区间随机整数。</summary>
        private static int randomInt(int min, int max) => Random.Shared.Next(min, max + 1);

        private static string AesCbcHex(string key, string iv, string plaintext)
            => Convert.ToHexString(aesCbc(key, iv, plaintext)).ToLowerInvariant();

        private static string AesCbcBase64(string key, string iv, string plaintext)
            => Convert.ToBase64String(aesCbc(key, iv, plaintext));

        private static byte[] aesCbc(string key, string iv, string plaintext)
        {
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);

            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] input = Encoding.UTF8.GetBytes(plaintext);
            return encryptor.TransformFinalBlock(input, 0, input.Length);
        }

        private static string OaepSha256Base64(string plaintext)
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(KuGouConstants.simulate_rsa_key);
            return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.OaepSHA256));
        }
    }
}
