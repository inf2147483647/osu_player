using System.IO.Compression;
using System.Text;

namespace OsuPlayer.Game.KuGou
{
    /// <summary>
    /// Decodes KuGou KRC lyrics: skip 4-byte header, XOR with a 16-byte rolling key, zlib inflate.
    /// Port of KuGouMusicApi/util/util.js decodeLyrics.
    /// </summary>
    public static class KrcDecoder
    {
        private static readonly byte[] xor_key =
        {
            64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105
        };

        /// <param name="base64Content">Base64 encoded encrypted KRC payload returned by the lyrics API.</param>
        /// <returns>Decoded UTF-8 lyric text, or an empty string on failure.</returns>
        public static string DecodeBase64(string base64Content)
        {
            byte[] bytes = Convert.FromBase64String(base64Content);
            return Decode(bytes);
        }

        public static string Decode(byte[] encrypted)
        {
            if (encrypted.Length <= 4)
                return string.Empty;

            byte[] body = encrypted[4..];
            for (int i = 0; i < body.Length; i++)
                body[i] ^= xor_key[i % xor_key.Length];

            try
            {
                using var input = new MemoryStream(body);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}