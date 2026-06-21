using System.Linq;
using System;

public static class RandomStringGenerator
{
    // Random and a character array
    private static readonly Random random = new Random();
    private const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate(int length)
    {
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)])
            .ToArray());
    }

    // Cryptographically secure random
    public static string GenerateSecure(int length)
    {
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            var bytes = new byte[length];
            rng.GetBytes(bytes);

            // Convert bytes to string, ensuring only allowed characters
            return Convert.ToBase64String(bytes).Remove(length);
        }
    }

    // Custom character sets
    public static string GenerateCustom(int length, bool includeUppercase = true,
        bool includeLowercase = true, bool includeNumbers = true, bool includeSpecial = false)
    {
        var charSet = "";
        if (includeUppercase) charSet += "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (includeLowercase) charSet += "abcdefghijklmnopqrstuvwxyz";
        if (includeNumbers) charSet += "0123456789";
        if (includeSpecial) charSet += "!@#$%^&*()_+-=[]{}|;:,.<>?";

        return new string(Enumerable.Repeat(charSet, length)
            .Select(s => s[random.Next(s.Length)])
            .ToArray());
    }

    // Guid (for unique identifiers)
    public static string GenerateGuid()
    {
        return Guid.NewGuid().ToString();
    }
}