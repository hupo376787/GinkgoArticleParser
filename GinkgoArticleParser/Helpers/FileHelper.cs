using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GinkgoArticleParser.Helpers
{
    public class FileHelper
    {
        static readonly string[] ReservedFileNames =
        {
            "CON","PRN","AUX","NUL","CLOCK$",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        public static string SanitizeFileName(string? name, int maxLength = 64, string fallback = "image")
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;

            var cleaned = new string(name.Where(ch => !char.IsControl(ch)).ToArray());

            var invalid = Path.GetInvalidFileNameChars();
            var chars = cleaned.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (invalid.Contains(chars[i])) chars[i] = '_';
            cleaned = new string(chars);

            cleaned = cleaned.Trim().Trim('.', ' ');
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = fallback;

            if (cleaned.Length > maxLength)
                cleaned = cleaned.Substring(0, maxLength).Trim('.', ' ');

            // 仅在 Windows 下规避保留名（基名比较，不区分大小写）
            if (OperatingSystem.IsWindows())
            {
                var baseName = Path.GetFileNameWithoutExtension(cleaned);
                if (ReservedFileNames.Any(r => string.Equals(r, baseName, StringComparison.OrdinalIgnoreCase)))
                    cleaned = $"_{cleaned}";
            }

            return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
        }
    }
}
