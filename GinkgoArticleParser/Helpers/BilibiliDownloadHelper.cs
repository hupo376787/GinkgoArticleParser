using System.Net;
using System.Net.Http.Headers;

namespace GinkgoArticleParser.Helpers;

public static class BilibiliDownloadHelper
{
    private static readonly string[] BiliDomains = { "bilivideo.com", "bilivideo.cn", "bilibili.com", "upos-", "mcdn.bilivideo.cn" };

    /// <summary>
    /// 下载直链到文件：
    /// 1. 全量 GET
    /// 2. 若失败：记录状态码，改用 Range 探测长度
    /// 3. 若仍失败：分块下载（多段 Range）
    /// 4. 最后回退：降级 qn=80 fnval=0 重新获取一次新的 URL（由外层调用）
    /// </summary>
    public static async Task<BiliDownloadResult> DownloadToFileAsync(
        string url,
        string filePath,
        string? cookie = null,
        int maxRetry = 3,
        bool enableChunk = true,
        long chunkSize = 4 * 1024 * 1024,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var attemptsLog = new List<string>();

        for (int attempt = 1; attempt <= maxRetry; attempt++)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                using var http = new HttpClient(handler);
                PrepareHeaders(http, url, cookie);

                var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)resp.StatusCode;
                attemptsLog.Add($"GET#{attempt}:{code}");

                if (resp.IsSuccessStatusCode)
                {
                    long? totalLen = resp.Content.Headers.ContentLength;
                    // 大文件流式复制
                    using var input = await resp.Content.ReadAsStreamAsync(ct);
                    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64);
                    await input.CopyToAsync(fs, ct);
                    return BiliDownloadResult.Success(filePath, attemptsLog);
                }

                // 状态码可重试
                if (IsRetryableStatus(resp.StatusCode))
                {
                    // 尝试探测长度并分块
                    if (enableChunk)
                    {
                        long? len = await ProbeLengthAsync(http, url, ct);
                        attemptsLog.Add($"ProbeLen:{len}");
                        if (len is > 0)
                        {
                            var okChunk = await DownloadInChunksAsync(http, url, filePath, len.Value, chunkSize, attemptsLog, ct);
                            if (okChunk) return BiliDownloadResult.Success(filePath, attemptsLog);
                        }
                    }
                    // 等待后重试
                    await Task.Delay(250 + attempt * 200, ct);
                    continue;
                }

                // 不可重试错误直接退出
                if (attempt == maxRetry)
                    return BiliDownloadResult.Fail(attemptsLog);
            }
            catch (Exception ex) when (attempt < maxRetry)
            {
                attemptsLog.Add($"EX#{attempt}:{ex.GetType().Name}:{ex.Message}");
                await Task.Delay(250 + attempt * 200, ct);
            }
            catch (Exception ex)
            {
                attemptsLog.Add($"EX_LAST:{ex.GetType().Name}:{ex.Message}");
                return BiliDownloadResult.Fail(attemptsLog);
            }
        }

        return BiliDownloadResult.Fail(attemptsLog);
    }

    private static async Task<long?> ProbeLengthAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var cr = resp.Content.Headers.ContentRange;
            return cr?.Length;
        }
        catch { return null; }
    }

    private static async Task<bool> DownloadInChunksAsync(
        HttpClient http,
        string url,
        string filePath,
        long totalLength,
        long chunkSize,
        List<string> log,
        CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64);
            long offset = 0;
            while (offset < totalLength)
            {
                ct.ThrowIfCancellationRequested();
                var end = Math.Min(offset + chunkSize - 1, totalLength - 1);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(offset, end);
                var resp = await http.SendAsync(req, ct);
                var code = (int)resp.StatusCode;
                log.Add($"RANGE {offset}-{end}:{code}");
                if (!resp.IsSuccessStatusCode) return false;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                await fs.WriteAsync(bytes, 0, bytes.Length, ct);
                offset = end + 1;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"ChunkEX:{ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    private static void PrepareHeaders(HttpClient http, string url, string? cookie)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        http.DefaultRequestHeaders.Connection.ParseAdd("keep-alive");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

        if (!string.IsNullOrWhiteSpace(cookie))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);

        // 一些线路需要明确 Range 可接受
        if (BiliDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase)))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "video");
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode code) =>
        code is HttpStatusCode.Forbidden
        or HttpStatusCode.NotFound
        or HttpStatusCode.Gone
        or HttpStatusCode.PreconditionFailed
        or HttpStatusCode.Unauthorized
        or HttpStatusCode.BadRequest; // 某些签名过期返回 400

    public sealed class BiliDownloadResult
    {
        public bool ISuccess { get; }
        public string? Path { get; }
        public IReadOnlyList<string> Log { get; }

        private BiliDownloadResult(bool success, string? path, IReadOnlyList<string> log)
        {
            ISuccess = success;
            Path = path;
            Log = log;
        }

        public static BiliDownloadResult Success(string path, IReadOnlyList<string> log) =>
            new(true, path, log);

        public static BiliDownloadResult Fail(IReadOnlyList<string> log) =>
            new(false, null, log);
    }
}