namespace Molca.ContentPackage.Utilities
{
    /// <summary>
    /// Utility class for formatting file sizes in a human-readable format.
    /// </summary>
    public static class SizeFormatter
    {
        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>
        /// Formats a size in bytes to a human-readable string.
        /// </summary>
        /// <param name="bytes">The size in bytes.</param>
        /// <param name="decimals">Number of decimal places (default: 2).</param>
        /// <returns>Formatted size string (e.g., "1.5 MB").</returns>
        public static string Format(long bytes, int decimals = 2)
        {
            if (bytes <= 0)
                return "0 B";

            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < SizeUnits.Length - 1)
            {
                order++;
                size /= 1024;
            }

            string format = decimals > 0 ? $"F{decimals}" : "F0";
            return $"{size.ToString(format)} {SizeUnits[order]}";
        }

        /// <summary>
        /// Formats a size with automatic precision based on magnitude.
        /// </summary>
        public static string FormatAuto(long bytes)
        {
            if (bytes < 1024)
                return Format(bytes, 0); // Bytes: no decimals
            if (bytes < 1024 * 1024)
                return Format(bytes, 1); // KB: 1 decimal
            return Format(bytes, 2); // MB and above: 2 decimals
        }

        /// <summary>
        /// Parses a formatted size string back to bytes (e.g., "1.5 MB" -> 1572864).
        /// </summary>
        public static bool TryParse(string sizeString, out long bytes)
        {
            bytes = 0;

            if (string.IsNullOrWhiteSpace(sizeString))
                return false;

            var parts = sizeString.Trim().Split(' ');
            if (parts.Length != 2)
                return false;

            if (!double.TryParse(parts[0], out double value))
                return false;

            var unit = parts[1].ToUpperInvariant();
            int multiplier = System.Array.IndexOf(SizeUnits, unit);

            if (multiplier < 0)
                return false;

            bytes = (long)(value * System.Math.Pow(1024, multiplier));
            return true;
        }
    }
}

