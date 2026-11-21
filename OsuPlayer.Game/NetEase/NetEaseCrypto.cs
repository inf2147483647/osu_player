using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OsuPlayer.Game.NetEase
{
    /// <summary>
    /// 网易云音乐的 weapi 请求签名。
    ///
    /// 官方 Web 客户端把 JSON 做两轮 AES-128-CBC（第一轮用固定密钥，第二轮用随机 16 位密钥），
    /// 再把随机密钥倒序后用 RSA 公钥做「无填充」加密得到 encSecKey。服务端按同样流程反解。
    /// 这里逐字节对齐 NeteaseCloudMusicApi 的 util/crypto.js，已用真实接口验证
    /// （搜索 / 扫码 key / 歌词 / 歌曲详情 / 播放地址均可正常返回）。
    /// </summary>
    internal static class NetEaseCrypto
    {
        private const string iv = "0102030405060708";
        private const string preset_key = "0CoJUm6Qyw8W8jud";
        private const string base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        /// <summary>eapi（App 通道）的 AES-128-ECB 密钥。扫码登录确认必须走这条通道，weapi 会被风控拒绝。</summary>
        private const string eapi_key = "e82ckenh8dichen8";

        private const string public_key_pem =
            "-----BEGIN PUBLIC KEY-----\n"
            + "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB\n"
            + "-----END PUBLIC KEY-----";

        private static readonly Lazy<(byte[] Modulus, byte[] Exponent)> rsa_parameters = new(() =>
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(public_key_pem);
            RSAParameters parameters = rsa.ExportParameters(false);
            return (parameters.Modulus!, parameters.Exponent!);
        });

        /// <summary>把请求体加密成 weapi 需要的 <c>params</c> / <c>encSecKey</c> 两个表单字段。</summary>
        public static (string Params, string EncSecKey) Encrypt(object data)
        {
            string text = data is JToken token
                ? token.ToString(Newtonsoft.Json.Formatting.None)
                : Newtonsoft.Json.JsonConvert.SerializeObject(data);

            string secret = randomSecret();

            string first = Convert.ToBase64String(AesEncrypt(Encoding.UTF8.GetBytes(text), preset_key));
            string second = Convert.ToBase64String(AesEncrypt(Encoding.UTF8.GetBytes(first), secret));

            string reversed = new string(secret.Reverse().ToArray());

            return (second, RsaEncryptNoPadding(reversed));
        }

        /// <summary>
        /// eapi 签名：把 <c>nobody{url}use{json}md5forencrypt</c> 的 MD5 拼进请求体，
        /// 再用 AES-128-ECB 加密并输出大写 hex（服务端要求的 <c>params</c> 格式）。
        /// </summary>
        public static string EapiEncrypt(string url, JObject data)
        {
            string text = data.ToString(Newtonsoft.Json.Formatting.None);
            string digest = Md5Hex($"nobody{url}use{text}md5forencrypt");
            string payload = $"{url}-36cd479b6b5-{text}-36cd479b6b5-{digest}";

            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(eapi_key);
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            byte[] input = Encoding.UTF8.GetBytes(payload);

            using var encryptor = aes.CreateEncryptor();
            byte[] encrypted = encryptor.TransformFinalBlock(input, 0, input.Length);

            return Convert.ToHexString(encrypted);
        }

        private static string Md5Hex(string text)
            => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static string randomSecret()
        {
            var builder = new StringBuilder(16);

            for (int i = 0; i < 16; i++)
                builder.Append(base62[RandomNumberGenerator.GetInt32(base62.Length)]);

            return builder.ToString();
        }

        private static byte[] AesEncrypt(byte[] input, string key)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(input, 0, input.Length);
        }

        /// <summary>
        /// RSA「无填充」加密：把 16 字节的倒序密钥当成大整数做模幂，再左补零到模长。
        /// 不能用 <see cref="RSA.Encrypt(byte[], RSAEncryptionPadding)"/>，它要求输入正好一个模长并且会做 PKCS#1 填充。
        /// </summary>
        private static string RsaEncryptNoPadding(string reversedSecret)
        {
            (byte[] modulusBytes, byte[] exponentBytes) = rsa_parameters.Value;

            var message = new BigInteger(Encoding.ASCII.GetBytes(reversedSecret), isUnsigned: true, isBigEndian: true);
            var modulus = new BigInteger(modulusBytes, isUnsigned: true, isBigEndian: true);
            var exponent = new BigInteger(exponentBytes, isUnsigned: true, isBigEndian: true);

            var cipher = BigInteger.ModPow(message, exponent, modulus);
            byte[] bytes = cipher.ToByteArray(isUnsigned: true, isBigEndian: true);

            var padded = new byte[modulusBytes.Length];
            Buffer.BlockCopy(bytes, 0, padded, padded.Length - bytes.Length, bytes.Length);

            return Convert.ToHexString(padded).ToLowerInvariant();
        }
    }
}
