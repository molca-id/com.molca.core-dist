using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace Molca.Editor.Addons
{
    /// <summary>
    /// Minimal hardened UPM <c>.tgz</c> extractor. It requires the standard <c>package/</c> archive root,
    /// rejects links/special files/path traversal, validates tar checksums, and caps files/expanded bytes.
    /// </summary>
    internal static class AddonTarGzExtractor
    {
        private const int TarBlockSize = 512;

        internal static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            string destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long expandedBytes = 0;
            int entryCount = 0;
            string pendingLongPath = null;

            using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress, false);
            var header = new byte[TarBlockSize];
            while (ReadBlock(gzip, header))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsZeroBlock(header)) break;
                ValidateChecksum(header);

                string name = ReadText(header, 0, 100);
                string prefix = ReadText(header, 345, 155);
                if (!string.IsNullOrEmpty(prefix)) name = prefix + "/" + name;
                long size = ReadOctal(header, 124, 12);
                byte type = header[156];

                if (++entryCount > AddonDistributionConfig.MaxExtractedFiles)
                    throw new InvalidDataException("Add-on contains too many archive entries.");
                expandedBytes = checked(expandedBytes + size);
                if (expandedBytes > AddonDistributionConfig.MaxExpandedBytes)
                    throw new InvalidDataException("Add-on expanded size exceeds the client policy.");

                if (type == (byte)'x' || type == (byte)'g')
                {
                    byte[] pax = ReadEntryBytes(gzip, size, 1024 * 1024);
                    SkipPadding(gzip, size);
                    string paxPath = ParsePaxPath(pax);
                    if (type == (byte)'x' && !string.IsNullOrEmpty(paxPath)) pendingLongPath = paxPath;
                    continue;
                }
                if (type == (byte)'L')
                {
                    pendingLongPath = Encoding.UTF8.GetString(ReadEntryBytes(gzip, size, 1024 * 1024)).TrimEnd('\0', '\r', '\n');
                    SkipPadding(gzip, size);
                    continue;
                }
                if (!string.IsNullOrEmpty(pendingLongPath))
                {
                    name = pendingLongPath;
                    pendingLongPath = null;
                }

                string relative = NormalizePackagePath(name);
                if (relative == null) throw new InvalidDataException($"Tar entry is outside the required package/ root: '{name}'.");
                if (relative.Length == 0)
                {
                    SkipEntry(gzip, size);
                    continue;
                }

                string output = SafeOutputPath(destinationRoot, relative);
                if (type == (byte)'5')
                {
                    if (size != 0) throw new InvalidDataException($"Directory entry '{name}' has data.");
                    Directory.CreateDirectory(output);
                    continue;
                }
                if (type != 0 && type != (byte)'0')
                    throw new InvalidDataException($"Tar entry '{name}' uses forbidden type '{(char)type}'. Links and special files are not allowed.");

                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? destinationRoot);
                using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    CopyExactly(gzip, target, size, cancellationToken);
                SkipPadding(gzip, size);
            }
        }

        private static string NormalizePackagePath(string name)
        {
            string normalized = (name ?? string.Empty).Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)) return null;
            if (normalized == "package" || normalized == "package/") return string.Empty;
            if (!normalized.StartsWith("package/", StringComparison.Ordinal)) return null;
            return normalized.Substring("package/".Length);
        }

        private static string SafeOutputPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\0') >= 0 || Path.IsPathRooted(relative))
                throw new InvalidDataException("Tar entry has an invalid path.");
            string output = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Tar path traversal was refused: '{relative}'.");
            return output;
        }

        private static bool ReadBlock(Stream stream, byte[] block)
        {
            int offset = 0;
            while (offset < block.Length)
            {
                int read = stream.Read(block, offset, block.Length - offset);
                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new EndOfStreamException("Truncated tar header.");
                }
                offset += read;
            }
            return true;
        }

        private static void ValidateChecksum(byte[] header)
        {
            long expected = ReadOctal(header, 148, 8);
            long actual = 0;
            for (int i = 0; i < header.Length; i++)
                actual += i >= 148 && i < 156 ? (byte)' ' : header[i];
            if (actual != expected) throw new InvalidDataException("Tar header checksum mismatch.");
        }

        private static long ReadOctal(byte[] buffer, int offset, int length)
        {
            string text = ReadText(buffer, offset, length).Trim();
            if (text.Length == 0) return 0;
            if (text[0] == '\u0080') throw new InvalidDataException("Base-256 tar sizes are not supported.");
            try { return Convert.ToInt64(text, 8); }
            catch (Exception exception) { throw new InvalidDataException($"Invalid tar octal value '{text}'.", exception); }
        }

        private static string ReadText(byte[] buffer, int offset, int length)
        {
            int count = 0;
            while (count < length && buffer[offset + count] != 0) count++;
            return Encoding.UTF8.GetString(buffer, offset, count);
        }

        private static bool IsZeroBlock(byte[] block)
        {
            foreach (byte value in block) if (value != 0) return false;
            return true;
        }

        private static byte[] ReadEntryBytes(Stream stream, long size, int limit)
        {
            if (size < 0 || size > limit) throw new InvalidDataException("Tar metadata entry is too large.");
            var bytes = new byte[(int)size];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException("Truncated tar entry.");
                offset += read;
            }
            return bytes;
        }

        private static string ParsePaxPath(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);
            int offset = 0;
            string path = null;
            while (offset < text.Length)
            {
                int space = text.IndexOf(' ', offset);
                if (space < 0 || !int.TryParse(text.Substring(offset, space - offset), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int length) || length <= 0 || offset + length > text.Length)
                    throw new InvalidDataException("Malformed PAX tar header.");
                string record = text.Substring(space + 1, offset + length - space - 2);
                int equals = record.IndexOf('=');
                if (equals > 0 && record.Substring(0, equals) == "path") path = record.Substring(equals + 1);
                if (equals > 0 && record.Substring(0, equals) == "linkpath")
                    throw new InvalidDataException("PAX link entries are forbidden.");
                offset += length;
            }
            return path;
        }

        private static void CopyExactly(Stream source, Stream target, long size, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            long remaining = size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new EndOfStreamException("Truncated tar file entry.");
                target.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void SkipEntry(Stream stream, long size)
        {
            SkipExactly(stream, size);
            SkipPadding(stream, size);
        }

        private static void SkipPadding(Stream stream, long size)
        {
            long padding = (TarBlockSize - (size % TarBlockSize)) % TarBlockSize;
            SkipExactly(stream, padding);
        }

        private static void SkipExactly(Stream stream, long bytes)
        {
            var buffer = new byte[4096];
            while (bytes > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
                if (read == 0) throw new EndOfStreamException("Truncated tar padding.");
                bytes -= read;
            }
        }
    }
}
