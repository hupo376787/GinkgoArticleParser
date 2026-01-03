using GinkgoArticleParser.Enums;

namespace GinkgoArticleParser.Models;

public sealed class ArticleParseResult
{
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string PublishDateTime { get; set; } = string.Empty;
    public string DownloadDateTime { get; set; } = string.Empty;
    public PlatformsEnum Platform { get; set; } = PlatformsEnum.Unknown;

    // 图片直链（旧有字段，保留）
    public List<string> ImageUrls { get; init; } = new();

    // 新增：视频直链列表（当是视频内容时填充）
    public List<string> VideoUrls { get; init; } = new();

    // 新增：媒体类型（图片默认 Jpeg；当解析出视频时为 Mp4）
    public MediaTypeEnum MediaType { get; set; } = MediaTypeEnum.Jpeg;
}