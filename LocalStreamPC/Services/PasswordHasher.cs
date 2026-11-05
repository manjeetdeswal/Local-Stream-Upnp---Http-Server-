using System;
using System.Security.Cryptography;
using System.Text;

namespace LocalStreamPC.Services
{
    /// <summary>PBKDF2-SHA256 password hashes, stored as "pbkdf2$iterations$salt$hash".</summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltBytes = 16;
        private const int KeyBytes = 32;

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
            return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }

        public static bool Verify(string password, string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            try
            {
                var parts = stored.Split('$');
                if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
                int iterations = int.Parse(parts[1]);
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A random password that is easy to type: no 0/O, 1/l/I.</summary>
        public static string GeneratePassword(int length = 12)
        {
            const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }
    }
}
