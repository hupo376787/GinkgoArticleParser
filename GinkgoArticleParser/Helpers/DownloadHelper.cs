using System.Net;

namespace GinkgoArticleParser.Helpers
{
    public static class DownloadHelper
    {
        // 共享 HttpClient，启用解压缩和合理超时
        private static readonly HttpClient Http = CreateClient();

        public static async Task<bool> TryDownloadToFileAsync(string url, string filePath, CancellationToken ct = default)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                // 最多重试 2 次
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var req = BuildRequest(url);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // 403/404 基本就是 Referer/Cookie 问题，重试也大概率无效，但给一次机会
                        if (attempt == 0) continue;
                        return false;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var file = File.Create(filePath);
                    await stream.CopyToAsync(file, ct).ConfigureAwait(false);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DownloadHelper.TryDownloadToFileAsync error: {ex.Message}");
                return false;
            }
        }

        public static async Task<byte[]?> TryDownloadBytesAsync(string url, CancellationToken ct = default)
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var req = BuildRequest(url);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if (attempt == 0) continue;
                        return null;
                    }

                    return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DownloadHelper.TryDownloadBytesAsync error: {ex.Message}");
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true
            };
            var c = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            // 放宽 Accept，兼容视频
            c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            return c;
        }

        private static HttpRequestMessage BuildRequest(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);

            // 根据域名设置 Referer（微博/公众号等常见防盗链）
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                if (host.Contains("sinaimg.cn") || host.Contains("weibo.com") || host.Contains("weibocdn.com"))
                {
                    req.Headers.Referrer = new Uri("https://weibo.com/");
                }
                else if (host.Contains("mmbiz.qpic.cn") || host.EndsWith("qpic.cn"))
                {
                    req.Headers.Referrer = new Uri("https://mp.weixin.qq.com/");
                }
            }
            catch
            {
                // url 不合法时忽略
            }

            return req;
        }
    }
}