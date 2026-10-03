using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace OsuPlayer.Game.KuGou.Signing
{
    /// <summary>
    /// Ports the MD5 based signature algorithms from KuGouMusicApi/util/helper.js.
    /// </summary>
    internal static class KuGouSigner
    {
        public static string Md5Hex(string input)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Android gateway signature: MD5(salt + sorted "key=value" pairs + body + salt).
        /// </summary>
        public static string SignatureAndroid(IReadOnlyDictionary<string, object?> parameters, string body, KuGouPlatform platform)
        {
            string joined = canonicalPairs(parameters);
            return Md5Hex($"{KuGouConstants.AndroidSalt(platform)}{joined}{body}{KuGouConstants.AndroidSalt(platform)}");
        }

        /// <summary>
        /// Web/H5 signature: uppercase MD5(salt + sorted "key=value" pairs + body + salt).
        /// The login endpoints require an UPPERCASE hex digest (verified live).
        /// </summary>
        public static string SignatureWeb(IReadOnlyDictionary<string, object?> parameters, string body = "")
        {
            string joined = canonicalPairs(parameters);
            return Md5Hex($"{KuGouConstants.web_salt}{joined}{body}{KuGouConstants.web_salt}").ToUpperInvariant();
        }

        /// <summary>
        /// signKey used by the track URL API: MD5(hash + salt + appid + mid + userid).
        /// </summary>
        public static string SignKey(string hash, string mid, long userId, int appId, KuGouPlatform platform)
            => Md5Hex($"{hash}{KuGouConstants.SignKeySalt(platform)}{appId}{mid}{(userId == 0 ? "0" : userId.ToString(CultureInfo.InvariantCulture))}");

        /// <summary>
        /// key= parameter for private FM: MD5(appid + salt + clientver + data).
        /// </summary>
        public static string SignParamsKey(string data, KuGouPlatform platform)
            => Md5Hex($"{KuGouConstants.AppId(platform)}{KuGouConstants.AndroidSalt(platform)}{KuGouConstants.ClientVer(platform)}{data}");

        private static string canonicalPairs(IReadOnlyDictionary<string, object?> parameters)
        {
            // JS: Object.keys(params).sort().map(k => `${k}=${params[k]}`).join('')
            var sb = new StringBuilder();
            foreach (string key in parameters.Keys.Order(StringComparer.Ordinal))
                sb.Append(key).Append('=').Append(canonicalValue(parameters[key]));
            return sb.ToString();
        }

        private static string canonicalValue(object? value) => value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonConvert.SerializeObject(value) // nested objects/arrays are JSON-encoded into the signature
        };
    }
}