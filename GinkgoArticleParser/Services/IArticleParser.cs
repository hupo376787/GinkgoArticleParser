using GinkgoArticleParser.Models;

namespace GinkgoArticleParser.Services;

public interface IArticleParser
{
    // 该解析器是否能处理此 URL（用于路由）
    bool CanHandle(string url);

    // 根据模式解析，返回标题与图片地址列表
    Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default);
}