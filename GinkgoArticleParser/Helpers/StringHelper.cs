using System.Text.RegularExpressions;

namespace GinkgoArticleParser.Helpers
{
    public static class StringHelper
    {
        // 从用户混合文本中提取并规范化首个可用 URL（支持中文 Host 与中文路径）
        public static string? ExtractAndNormalizeUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // 去除首尾空白
            var text = raw.Trim();

            // 正则匹配候选 URL（排除明显的结束符号）
            var matches = Regex.Matches(text,
                @"https?://[^\s""'<>（）()]+",
                RegexOptions.IgnoreCase);

            if (matches.Count == 0) return null;

            // 取第一个匹配（可扩展：也可依次尝试解析器支持情况）
            var candidate = matches[0].Value;

            // 去除末尾常见的句号 / 逗号 / 括号等
            candidate = candidate.TrimEnd('.', '。', ',', '，', ';', '；', '！', '!', ')', '）', ']', '】', '”', '"', '\'');

            // 若包含全角分隔符，截断
            var fullWidthIdx = candidate.IndexOf('，');
            if (fullWidthIdx > 0) candidate = candidate[..fullWidthIdx];

            // 标准化（IDN + 路径编码）
            return NormalizeUrl(candidate);
        }

        private static string? NormalizeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            // 处理国际化域名（中文域名转 punycode）
            var host = uri.Host;
            if (host.Any(c => c > 127))
            {
                try
                {
                    var idn = new System.Globalization.IdnMapping();
                    host = idn.GetAscii(host);
                }
                catch { /* 失败则保留原 host */ }
            }

            // 重新编码 path（保留斜杠，逐段 EscapeDataString）
            string EncodePath(string p)
            {
                if (string.IsNullOrEmpty(p) || p == "/") return p;
                var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => Uri.EscapeDataString(s));
                return "/" + string.Join('/', segments);
            }

            // 重新编码 query（key=value&...）
            string EncodeQuery(string q)
            {
                if (string.IsNullOrEmpty(q)) return q;
                if (q.StartsWith("?")) q = q[1..];
                var pairs = q.Split('&', StringSplitOptions.RemoveEmptyEntries);
                var encoded = pairs.Select(pair =>
                {
                    var kv = pair.Split('=', 2);
                    var k = Uri.EscapeDataString(kv[0]);
                    var v = kv.Length == 2 ? Uri.EscapeDataString(kv[1]) : "";
                    return $"{k}={(v ?? "")}";
                });
                return "?" + string.Join('&', encoded);
            }

            var builder = new UriBuilder(uri.Scheme, host, uri.Port == -1 ? (uri.Scheme == "https" ? 443 : 80) : uri.Port)
            {
                Path = EncodePath(uri.AbsolutePath),
                Query = EncodeQuery(uri.Query),
                Fragment = uri.Fragment // Fragment 原样保留
            };

            // 去掉默认端口显示
            if ((builder.Scheme == "https" && builder.Port == 443) ||
                (builder.Scheme == "http" && builder.Port == 80))
            {
                builder.Port = -1;
            }

            return builder.Uri.ToString();
        }

        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        public static string SanitizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";

            // 去除所有空白（空格、制表、换行等），同时过滤非法字符与控制字符
            Span<char> invalid = InvalidFileNameChars;
            var sb = new System.Text.StringBuilder(title.Length);

            foreach (var ch in title)
            {
                if (char.IsWhiteSpace(ch)) continue;                // 去除空白与换行
                if (ch < 32) continue;                              // 控制字符
                if (invalid.Contains(ch)) continue;                 // 路径非法字符
                sb.Append(ch);
            }

            var cleaned = sb.ToString();

            // 再保险：正则剔除常见非法（若前面遗漏）
            cleaned = Regex.Replace(cleaned, @"[<>:""/\\|?*\u0000-\u001F]", "");

            // 去除常见不可作为文件名结尾的标点/空格
            //cleaned = cleaned.Trim().TrimEnd('.', ' ');

            // Windows 设备名避让
            if (Regex.IsMatch(cleaned, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                cleaned = "_" + cleaned;

            // 去除孤立 emoji / 未配对代理项（防止部分文件系统问题）
            cleaned = Regex.Replace(cleaned, @"\p{Cs}", "");

            // 限长
            if (cleaned.Length > 64)
                cleaned = cleaned[..64];

            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "untitled";

            return cleaned;
        }
    }
}
