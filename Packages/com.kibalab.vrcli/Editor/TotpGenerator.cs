using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;

namespace KibaLab.WorldDeployment.Editor
{
    public static class TotpGenerator
    {
        public static string GenerateCode(string base32Secret, DateTimeOffset timestamp, int digits = 6)
        {
            if (digits < 6 || digits > 8)
                throw new ArgumentOutOfRangeException(nameof(digits), "TOTP digits must be from 6 to 8.");

            byte[] key = DecodeBase32(base32Secret);
            byte[] counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(timestamp.ToUnixTimeSeconds() / 30));
            byte[] hash = Array.Empty<byte>();
            try
            {
                using (HMACSHA1 hmac = new HMACSHA1(key))
                {
                    hash = hmac.ComputeHash(counter);
                }

                int offset = hash[hash.Length - 1] & 0x0f;
                int binary = ((hash[offset] & 0x7f) << 24) |
                             ((hash[offset + 1] & 0xff) << 16) |
                             ((hash[offset + 2] & 0xff) << 8) |
                             (hash[offset + 3] & 0xff);
                int modulus = digits == 8 ? 100000000 : digits == 7 ? 10000000 : 1000000;
                return (binary % modulus).ToString(new string('0', digits), CultureInfo.InvariantCulture);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
                Array.Clear(counter, 0, counter.Length);
                Array.Clear(hash, 0, hash.Length);
            }
        }

        private static byte[] DecodeBase32(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("The TOTP secret is empty.", nameof(value));

            string normalized = new string(value
                .Where(character => !char.IsWhiteSpace(character) && character != '-')
                .ToArray())
                .TrimEnd('=')
                .ToUpperInvariant();
            if (normalized.Length == 0)
                throw new ArgumentException("The TOTP secret is empty.", nameof(value));

            List<byte> bytes = new List<byte>(normalized.Length * 5 / 8);
            int buffer = 0;
            int bitsInBuffer = 0;
            foreach (char character in normalized)
            {
                int decoded = character >= 'A' && character <= 'Z'
                    ? character - 'A'
                    : character >= '2' && character <= '7'
                        ? character - '2' + 26
                        : -1;
                if (decoded < 0)
                    throw new ArgumentException("The TOTP secret is not valid Base32.", nameof(value));

                buffer = (buffer << 5) | decoded;
                bitsInBuffer += 5;
                if (bitsInBuffer >= 8)
                {
                    bitsInBuffer -= 8;
                    bytes.Add((byte)(buffer >> bitsInBuffer));
                    buffer &= (1 << bitsInBuffer) - 1;
                }
            }

            if (bytes.Count == 0)
                throw new ArgumentException("The TOTP secret is too short.", nameof(value));
            return bytes.ToArray();
        }
    }
}
