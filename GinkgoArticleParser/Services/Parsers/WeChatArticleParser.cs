using System.Text.RegularExpressions;
using System.Globalization;
using HtmlAgilityPack;
using GinkgoArticleParser.Models;
using GinkgoArticleParser.Helpers;

namespace GinkgoArticleParser.Services.Parsers;

public sealed class WeChatArticleParser : IArticleParser
{
    private static readonly HttpClient httpClient = CreateClient();

    public bool CanHandle(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Contains("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default)
    {
        var html = await httpClient.GetStringAsync(url, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = GetTitle(doc);

        // 默认当前时间，若能解析到文章发布时间则覆盖
        var result = new ArticleParseResult
        {
            Title = StringHelper.SanitizeTitle(title),
            Author = string.Empty,
            PublishDateTime = FormatDate(DateTime.Now),
            DownloadDateTime = FormatDate(DateTime.Now),
            Platform = Enums.PlatformsEnum.Weixin
        };

        if (TryExtractPublishTime(doc, html, out var publishDt))
            result.PublishDateTime = FormatDate(publishDt);
        // 解析作者昵称（JsDecode(...) / htmlDecode(...) / nickname 变量等）
        if (TryExtractAuthor(doc, html, out var author))
            result.Author = author;

        if (mode == ParseMode.CoverImage)
        {
            var ogImageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            var ogImage = ogImageNode?.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(ogImage))
                result.ImageUrls.Add(ogImage!);
            return result;
        }

        // 文章图片解析：兼容两种脚本形式
        // 1) var picturePageInfoList = "....";
        var m1 = Regex.Match(html, @"var\s+picturePageInfoList\s*=\s*""(.*?)"";", RegexOptions.Singleline);
        if (m1.Success)
        {
            var raw = m1.Groups[1].Value;
            raw = raw.Replace(",]", "]")
                     .Replace("'", "\"")
                     .Replace("\\x26amp;amp;", "&")
                     .Replace("\\x26amp;", "&");

            // 提取 URL（避免引入额外模型依赖）
            foreach (Match mm in Regex.Matches(raw, @"https?://[^\s'\""]+", RegexOptions.IgnoreCase))
            {
                if (mm.Success) result.ImageUrls.Add(mm.Value);
            }
            return result;
        }

        // 2) window.picture_page_info_list = [...].slice(...)
        var m2 = Regex.Match(html, @"window\.picture_page_info_list\s*=\s*(\[.*?\])\.slice", RegexOptions.Singleline);
        if (m2.Success)
        {
            var raw = m2.Groups[1].Value;
            foreach (Match mm in Regex.Matches(raw, @"https?://[^\s'\""]+", RegexOptions.IgnoreCase))
            {
                if (mm.Success) result.ImageUrls.Add(mm.Value);
            }
            return result;
        }

        return result; // 未找到返回空列表
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        return c;
    }

    private static string GetTitle(HtmlDocument doc)
    {
        var ogTitleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
        return ogTitleNode?.GetAttributeValue("content", "") ?? string.Empty;
    }

    private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMddHHmmss");

    private static bool TryExtractPublishTime(HtmlDocument doc, string html, out DateTime dt)
    {
        dt = default;

        // 1) 页面上常见节点：<em id="publish_time">2024-10-21 12:34</em>
        var node = doc.DocumentNode.SelectSingleNode("//em[@id='publish_time']") ??
                   doc.DocumentNode.SelectSingleNode("//span[@id='publish_time']");
        if (node != null)
        {
            var text = (node.InnerText ?? string.Empty).Trim();
            if (TryParseCommonDate(text, out dt)) return true;
        }

        // 2) 常见 meta：article:published_time / og:updated_time / itemprop=datePublished
        var metaCandidates = new[]
        {
        "//meta[@property='article:published_time']",
        "//meta[@property='og:updated_time']",
        "//meta[@itemprop='datePublished']",
        "//meta[@name='pubdate']"
    };
        foreach (var xpath in metaCandidates)
        {
            var meta = doc.DocumentNode.SelectSingleNode(xpath);
            var content = meta?.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(content) && TryParseIsoOrCommon(content!, out dt))
                return true;
        }

        // 3) create_time 通过 JsDecode('...')（可能是日期字符串或 \uXXXX 形式）
        var mCreateJs = Regex.Match(
            html,
            @"\bcreate_time\s*[:=]\s*JsDecode\(\s*(['""])(?<t>.*?)\1\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );
        if (mCreateJs.Success)
        {
            var raw = mCreateJs.Groups["t"].Value;
            var text = CleanDecode(raw).Trim(); // HTML 实体 + \uXXXX 反解码
                                                // 优先按常见日期解析
            if (TryParseIsoOrCommon(text, out dt)) return true;
            // 若是纯数字时间戳，按 Unix 解析
            if (Regex.IsMatch(text, @"^\d{10,13}$") && TryParseUnix(text, out dt)) return true;
        }

        // 4) 页面脚本变量：var ct = "1696845005"; 或 publish_time: "1696845005"（秒/毫秒）
        var tsMatch = Regex.Match(html, @"\b(?:var\s+ct|publish_time|create_time)\s*[:=]\s*""(?<ts>\d{10,13})""", RegexOptions.IgnoreCase);
        if (tsMatch.Success)
        {
            var tsStr = tsMatch.Groups["ts"].Value;
            if (TryParseUnix(tsStr, out dt)) return true;
        }

        // 5) 其它可能：publish_time: "2024-10-21 12:34:56" 或 create_time: "..."
        var textMatch = Regex.Match(html, @"\b(?:publish_time|create_time)\s*[:=]\s*""(?<t>[^""]{8,})""", RegexOptions.IgnoreCase);
        if (textMatch.Success)
        {
            var t = textMatch.Groups["t"].Value.Trim();
            if (TryParseIsoOrCommon(t, out dt)) return true;
        }

        return false;
    }

    private static bool TryParseCommonDate(string s, out DateTime dt)
        => TryParseIsoOrCommon(s, out dt);

    private static bool TryParseIsoOrCommon(string s, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        // 常见格式与 ISO-8601
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd",
            "yyyyMMddHHmmss",
            "yyyyMMdd"
        };

        // 先试标准解析（ISO 等）
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;

        foreach (var f in formats)
        {
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                return true;
        }

        return false;
    }

    private static bool TryParseUnix(string ts, out DateTime dt)
    {
        dt = default;
        if (!long.TryParse(ts, out var num)) return false;

        try
        {
            if (ts.Length == 13)
                dt = DateTimeOffset.FromUnixTimeMilliseconds(num).LocalDateTime;
            else if (ts.Length == 10)
                dt = DateTimeOffset.FromUnixTimeSeconds(num).LocalDateTime;
            else
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractAuthor(HtmlDocument doc, string html, out string author)
    {
        author = string.Empty;

        // 1) JsDecode('xxx') 兼容 nick_name: JsDecode(...) / nickname: JsDecode(...) / var nickname = JsDecode(...)
        var mJs = Regex.Match(
            html,
            @"\b(?:nick_name|nickname)\s*[:=]\s*JsDecode\(\s*(['""])(?<name>.*?)\1\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );
        if (mJs.Success)
        {
            var raw = mJs.Groups["name"].Value;
            author = CleanDecode(raw);
            if (!string.IsNullOrWhiteSpace(author)) return true;
        }

        // 2) var nickname = htmlDecode("xxx") 或 nickname: htmlDecode("xxx")
        var mHtmlDec = Regex.Match(
            html,
            @"\b(?:var\s+)?nickname\s*[:=]\s*htmlDecode\(\s*(['""])(?<name>.*?)\1\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );
        if (mHtmlDec.Success)
        {
            var raw = mHtmlDec.Groups["name"].Value;
            author = CleanDecode(raw);
            if (!string.IsNullOrWhiteSpace(author)) return true;
        }

        // 3) 兜底：nick_name / nickname 直接赋值
        var mNickVar = Regex.Match(
            html,
            @"\b(?:var\s+)?(?:nick_name|nickname)\s*[:=]\s*(['""])(?<name>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );
        if (mNickVar.Success)
        {
            var raw = mNickVar.Groups["name"].Value;
            author = CleanDecode(raw);
            if (!string.IsNullOrWhiteSpace(author)) return true;
        }

        // 4) DOM 属性兜底
        var attrNode = doc.DocumentNode.SelectSingleNode("//*[@data-nickname]");
        var attrVal = attrNode?.GetAttributeValue("data-nickname", null);
        if (!string.IsNullOrWhiteSpace(attrVal))
        {
            author = CleanDecode(attrVal);
            if (!string.IsNullOrWhiteSpace(author)) return true;
        }

        var spanNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'nickname') or contains(@id,'nickname')]");
        var spanText = spanNode?.InnerText?.Trim();
        if (!string.IsNullOrWhiteSpace(spanText))
        {
            author = CleanDecode(spanText);
            if (!string.IsNullOrWhiteSpace(author)) return true;
        }

        return false;
    }

    // 统一清理与解码：HTML 实体 + JS Unicode \uXXXX
    private static string CleanDecode(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var htmlDecoded = HtmlEntity.DeEntitize(s).Trim();
        return JsUnicodeUnescape(htmlDecoded).Trim();
    }

    // 将字符串中的 \uXXXX 反解码为 Unicode 字符
    private static string JsUnicodeUnescape(string s)
    {
        return Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
        {
            var code = Convert.ToInt32(m.Groups[1].Value, 16);
            return char.ConvertFromUtf32(code);
        });
    }
}