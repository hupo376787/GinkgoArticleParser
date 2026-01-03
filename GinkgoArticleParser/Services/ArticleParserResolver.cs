using GinkgoArticleParser.Enums;

namespace GinkgoArticleParser.Services;

public interface IArticleParserResolver
{
    IArticleParser? Resolve(string url);
    ParserResolveInfo ResolveInfo(string url);
}

public sealed record ParserResolveInfo(
    bool Supported,
    IArticleParser? Parser,
    PlatformsEnum? Platform,
    string? ResourceId,
    bool RequiresCookie,
    string? Reason
);

public sealed class ArticleParserResolver : IArticleParserResolver
{
    private readonly IEnumerable<IArticleParser> _parsers;

    public ArticleParserResolver(IEnumerable<IArticleParser> parsers)
        => _parsers = parsers;

    public IArticleParser? Resolve(string url) => _parsers.FirstOrDefault(p => p.CanHandle(url));

    // 增强：返回结构化支持信息
    public ParserResolveInfo ResolveInfo(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ParserResolveInfo(false, null, null, null, false, "URL为空");

        var parser = _parsers.FirstOrDefault(p => p.CanHandle(url));
        if (parser is null)
            return new ParserResolveInfo(false, null, null, null, false, "没有匹配的解析器");

        var platform = MapPlatform(parser);
        var (resourceId, idReason) = TryExtractId(parser, url);
        var requiresCookie = platform is PlatformsEnum.Weibo; // 示例：微博更完整内容经常需要 Cookie

        string reason = "匹配成功";
        if (!string.IsNullOrEmpty(idReason))
            reason = idReason;

        return new ParserResolveInfo(true, parser, platform, resourceId, requiresCookie, reason);
    }

    private static PlatformsEnum? MapPlatform(IArticleParser parser)
    {
        var type = parser.GetType().Name;
        return type switch
        {
            "WeiboArticleParser" => PlatformsEnum.Weibo,
            "WeChatArticleParser" => PlatformsEnum.Weixin,
            "XiaohongshuParser" => PlatformsEnum.Xiaohongshu,
            _ => null
        };
    }

    // 仅示例：按不同平台做简单的 ID 提取
    private static (string? id, string? reason) TryExtractId(IArticleParser parser, string url)
    {
        var name = parser.GetType().Name;

        if (name == "WeiboArticleParser")
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"weibo\.com/(?:\d+/)?([A-Za-z0-9]+)");
            if (m.Success) return (m.Groups[1].Value, "已提取微博MID");
        }
        else if (name == "WeChatArticleParser")
        {
            // 微信文章通常不需要 ID（直接用 URL），返回 null
            return (null, "微信文章无需资源ID");
        }
        else if (name == "XiaohongshuParser")
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"xiaohongshu\.com/explore/([a-f0-9]{18,32})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return (m.Groups[1].Value, "已提取小红书笔记ID");
        }

        return (null, "未识别资源ID");
    }
}