using System;

public class ByteSizeFormatter
{
    /*public enum ByteUnit
    {
        Byte,
        KB,
        MB,
        GB,
        TB,
        PB
    }

    public static string Format(long bytes, ByteUnit unit = ByteUnit.Byte, int decimalPlaces = 1, bool useBaseTen = false)
    {
        string[] decimalSuffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        string[] binarySuffixes = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };

        decimal number = bytes;
        int divisor = useBaseTen ? 1000 : 1024;
        string[] suffixes = useBaseTen ? decimalSuffixes : binarySuffixes;

        int targetUnit = (int)unit;

        // Convert to target unit
        for (int i = 0; i < targetUnit && number >= divisor; i++)
        {
            number /= divisor;
        }

        string format = $"{{0:n{decimalPlaces}}} {{1}}";
        return string.Format(format, number, suffixes[targetUnit]);
    }*/

    public static string Format(long byteSize)
    {
        long KB = 1024;
        long MB = KB * 1024;
        long GB = MB * 1024;
        long TB = GB * 1024;
        double size = byteSize;
        if (byteSize >= TB)
        {
            size = Math.Round((double)byteSize / TB, 2);
            return $"{size} TB";
        }
        else if (byteSize >= GB)
        {
            size = Math.Round((double)byteSize / GB, 2);
            return $"{size} GB";
        }
        else if (byteSize >= MB)
        {
            size = Math.Round((double)byteSize / MB, 2);
            return $"{size} MB";
        }
        else if (byteSize >= KB)
        {
            size = Math.Round((double)byteSize / KB, 2);
            return $"{size} KB";
        }
        else
        {
            return $"{size} Bytes";
        }
    }
}