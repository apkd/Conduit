#nullable enable

using System.Security.Cryptography;

namespace Conduit.Runtime
{
    static class RuntimeHash
    {
        public static string Sha256(byte[] bytes)
        {
            using var algorithm = SHA256.Create();
            var hash = algorithm.ComputeHash(bytes);
            return ToHex(hash);
        }

        static string ToHex(byte[] bytes)
        {
            var characters = new char[bytes.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = alphabet[bytes[index] >> 4];
                characters[index * 2 + 1] = alphabet[bytes[index] & 0xf];
            }

            return new string(characters);
        }
    }
}
