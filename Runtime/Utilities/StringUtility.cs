using System.Linq;
using System.Text;

public static class StringUtility
{
    public static bool IsFilenameSafe(string filename)
    {
        // Check for length restrictions
        if (filename.Length > 255)
        {
            return false;
        }

        // Check for invalid characters
        foreach (char c in filename)
        {
            if (!char.IsLetterOrDigit(c) && !char.IsPunctuation(c) && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        // Check for reserved filenames (adjust as needed)
        string[] reservedFilenames = { "CON", "AUX", "PRN", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reservedFilenames.Contains(filename.ToUpperInvariant()))
        {
            return false;
        }

        return true;
    }

    public static string EnsureFilenameSafe(string filename)
    {
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = 0; i < filename.Length; i++)
        {
            var c = filename[i];
            if (!char.IsLetterOrDigit(c) && !char.IsPunctuation(c) && !char.IsWhiteSpace(c))
            {
                c = '_';
            }

            stringBuilder.Append(c);
        }
        return stringBuilder.ToString();
    }
}
