using GinkgoArticleParser.Enums;

namespace GinkgoArticleParser.Helpers
{
    public class PlatformHelper
    {
        /// <summary>
        /// 根据输入的url，返回对应的平台枚举
        /// 支持常见主域及常用短链域名的识别；无法识别则返回 Unknown
        /// </summary>
        public static PlatformsEnum GetPlatform(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return PlatformsEnum.Unknown;

            // 预处理：可能用户直接粘贴了不带协议的域名或前后包含文字
            url = url.Trim();

            // 若不含协议，补全 https://
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            // 尝试解析 URI
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return PlatformsEnum.Unknown;

            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            // 微信公众号
            if (host.Contains("mp.weixin.qq.com") || host.Contains("weixin.qq.com"))
                return PlatformsEnum.Weixin;

            // 微博（含 weibo.com / weibo.cn / m.weibo.cn）
            if (host.Contains("weibo.com") || host.Contains("weibo.cn") || host.Contains("m.weibo.cn"))
                return PlatformsEnum.Weibo;

            // 小红书（含正式域与短链域）
            if (host.Contains("xiaohongshu.com") || host.Contains("xhslink.com"))
                return PlatformsEnum.Xiaohongshu;

            // 快手（含 kuaishou / kwai）
            if (host.Contains("kuaishou.com") || host.Contains("kwai.com") || host.Contains("v.kuaishou.com"))
                return PlatformsEnum.Kuaishou;

            // 抖音（含主域/备用域/短链）
            if (host.Contains("douyin.com") ||
                host.Contains("iesdouyin.com") ||
                host.Contains("amemv.com") ||
                host.Contains("v.douyin.com") ||
                host.Contains("is.douyin.com"))
                return PlatformsEnum.Douyin;

            // 腾讯微视（含主域与可能的短链 w.url.cn；t.cn 常用于微博短链，这里优先判定为微博，微视只在明确 weishi.qq.com 或 w.url.cn 时返回）
            if (host.Contains("weishi.qq.com") || host.Contains("w.url.cn"))
                return PlatformsEnum.Weishi;

            // Twitter / X
            if (host.Contains("twitter.com") || host.Contains("x.com"))
                return PlatformsEnum.Twitter;

            // Bilibili（需路径中包含 /video/BV）
            if (host.Contains("bilibili.com") && path.Contains("/video/bv"))
                return PlatformsEnum.Bilibili;

            return PlatformsEnum.Unknown;
        }
    }
}