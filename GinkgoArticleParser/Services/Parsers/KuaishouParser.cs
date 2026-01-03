using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GinkgoArticleParser.Services.Parsers
{
    /// <summary>
    /// 快手短视频/图集解析（支持短链 v.kuaishou.com）
    /// </summary>
    public sealed class KuaishouParser : IArticleParser
    {
        private static readonly CookieContainer CookieJar = new();
        private static readonly HttpClient http = CreateClient();

        public bool CanHandle(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            return uri.Host.Contains("kuaishou.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("kwai.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default)
        {
            var result = new ArticleParseResult
            {
                Title = "快手内容",
                Author = string.Empty,
                PublishDateTime = FormatDate(DateTime.Now),
                DownloadDateTime = FormatDate(DateTime.Now),
                Platform = Enums.PlatformsEnum.Kuaishou
            };

            var finalUrl = await ResolveFinalUrlAsync(url, ct);

            string html;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                req.Headers.Referrer = new Uri(finalUrl);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1");
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                return result;
            }

            TryFillOgTitle(html, result);

            JsonElement root;
            if (
                TryExtractInitState(html, out root) ||
                TryExtractApollo(html, out root) ||
                TryExtractInitial(html, out root) ||
                TryExtractNextData(html, out root) ||
                TryExtractPageData(html, out root))
            {
                // 优先针对顶层每个键的 photo 结构提取
                ExtractPhotoBlocks(root, mode, result);
                // 再做通用递归提取（补充遗漏）
                //TryFillFromApollo(root, mode, result);

                if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                    return result;
            }

            if (result.VideoUrls.Count == 0 || result.ImageUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"manifest\s*:\s*(\{[^<]+?\})", RegexOptions.IgnoreCase))
                {
                    var json = m.Groups[1].Value;
                    if (TryParseJson(json, out var manifest))
                    {
                        if (TryPickMp4(manifest, out var v))
                        {
                            if (!string.IsNullOrWhiteSpace(v) && !result.VideoUrls.Contains(v!))
                            {
                                result.VideoUrls.Add(v!);
                                result.MediaType = Enums.MediaTypeEnum.Mp4;
                                if (mode == ParseMode.CoverImage) return result;
                            }
                        }
                        if (TryPickImage(manifest, out var img))
                        {
                            if (!string.IsNullOrWhiteSpace(img) && !result.ImageUrls.Contains(img!))
                            {
                                result.ImageUrls.Add(img!);
                                if (mode == ParseMode.CoverImage) return result;
                            }
                        }
                    }
                }
            }

            if (result.VideoUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?\.mp4(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var v = m.Value;
                    if (!result.VideoUrls.Contains(v))
                    {
                        result.VideoUrls.Add(v);
                        result.MediaType = Enums.MediaTypeEnum.Mp4;
                        if (mode == ParseMode.CoverImage) return result;
                    }
                }
            }
            if (result.ImageUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?\.(jpg|jpeg|png|webp)(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var u = m.Value;
                    if (!result.ImageUrls.Contains(u))
                    {
                        result.ImageUrls.Add(u);
                        if (mode == ParseMode.CoverImage) return result;
                    }
                }
            }

            return result;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                CookieContainer = CookieJar,
                UseCookies = true
            };
            var c = new HttpClient(handler);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
            c.Timeout = TimeSpan.FromSeconds(15);
            return c;
        }

        private static async Task<string> ResolveFinalUrlAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            if (uri.Host.Contains("v.kuaishou.com", StringComparison.OrdinalIgnoreCase))
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
                using var c = new HttpClient(handler);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1");
                using var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode is >= 300 and < 400)
                {
                    var loc = resp.Headers.Location;
                    if (loc != null)
                    {
                        var abs = loc.IsAbsoluteUri ? loc.ToString() : new Uri(uri, loc).ToString();
                        if (abs.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return abs;
                    }
                }
            }

            try
            {
                using var resp = await http.GetAsync(url, ct);
                return resp.RequestMessage?.RequestUri?.ToString() ?? url;
            }
            catch
            {
                return url;
            }
        }

        private static bool TryExtractApollo(string html, out JsonElement root)
        {
            root = default;
            return TryExtractObjectAfterVar(html, "window.__APOLLO_STATE__", out root);
        }

        private static bool TryExtractInitial(string html, out JsonElement root)
        {
            root = default;
            return TryExtractObjectAfterVar(html, "window.INIT_STATE", out root);
        }

        private static bool TryExtractInitState(string html, out JsonElement root)
        {
            root = default;
            return TryExtractObjectAfterVar(html, "window.INIT_STATE", out root);
        }

        private static bool TryExtractNextData(string html, out JsonElement root)
        {
            root = default;
            var m = Regex.Match(html, @"id=[""']__NEXT_DATA__[""'][^>]*> (?<j>\{[\s\S]*?\}) </script>", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
            if (!m.Success) return false;
            var json = m.Groups["j"].Value;
            return TryParseJson(json, out root);
        }

        private static bool TryExtractPageData(string html, out JsonElement root)
        {
            root = default;
            return TryExtractObjectAfterVar(html, "window.pageData", out root);
        }

        private static bool TryExtractObjectAfterVar(string html, string varName, out JsonElement root)
        {
            root = default;
            if (!TryExtractRawObject(html, varName, out var json)) return false;
            json = json.Replace("\\u002F", "/");
            return TryParseJson(json, out root);
        }

        private static bool TryParseJson(string json, out JsonElement root)
        {
            root = default;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                root = doc.RootElement.Clone();
                return true;
            }
            catch { return false; }
        }

        private static bool TryExtractRawObject(string html, string varName, out string json)
        {
            json = string.Empty;
            var idx = html.IndexOf(varName, StringComparison.Ordinal);
            if (idx < 0) return false;
            var start = html.IndexOf('{', idx);
            if (start < 0) return false;

            int depth = 0;
            bool inStr = false; char strCh = '\0';
            for (int i = start; i < html.Length; i++)
            {
                var ch = html[i];
                if (inStr)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == strCh) inStr = false;
                }
                else
                {
                    if (ch == '"' || ch == '\'') { inStr = true; strCh = ch; }
                    else if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            json = html[start..(i + 1)];
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static void ExtractPhotoBlocks(JsonElement root, ParseMode mode, ArticleParseResult result)
        {
            if (root.ValueKind != JsonValueKind.Object) return;

            foreach (var kv in root.EnumerateObject())
            {
                var obj = kv.Value;
                if (obj.ValueKind != JsonValueKind.Object) continue;
                if (!obj.TryGetProperty("photo", out var photo) || photo.ValueKind != JsonValueKind.Object) continue;

                // caption
                if (photo.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.String)
                {
                    var s = cap.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Title = StringHelper.SanitizeTitle(s);
                }

                //nickname
                if (photo.TryGetProperty("userName", out var nickname) && cap.ValueKind == JsonValueKind.String)
                {
                    var s = nickname.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Author = StringHelper.SanitizeTitle(s);
                }

                // timestamp
                if (photo.TryGetProperty("timestamp", out var ts) && ts.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    if (TryNormalizeTimestamp(ts, out var dt))
                        result.PublishDateTime = FormatDate(dt);
                }

                // 视频：mainMvUrls / main_mv_urls
                if (TryPickMp4(photo, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    if (!result.VideoUrls.Contains(v!))
                        result.VideoUrls.Add(v!);
                    result.MediaType = Enums.MediaTypeEnum.Mp4;
                    if (mode == ParseMode.CoverImage) continue;
                }

                // 封面：webpCoverUrls / coverUrls / coverUrl
                if (TryPickImage(photo, out var img) && !string.IsNullOrWhiteSpace(img))
                {
                    if (!result.ImageUrls.Contains(img!))
                        result.ImageUrls.Add(img!);
                }

                // 图集：同级的 atlas
                if (obj.TryGetProperty("atlas", out var atlas) && atlas.ValueKind == JsonValueKind.Object)
                {
                    // 选一个 CDN
                    string? cdnHost = null;

                    if (atlas.TryGetProperty("cdn", out var cdnProp))
                    {
                        if (cdnProp.ValueKind == JsonValueKind.String)
                        {
                            cdnHost = cdnProp.GetString();
                        }
                        else if (cdnProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var it in cdnProp.EnumerateArray())
                            {
                                if (it.ValueKind == JsonValueKind.String)
                                {
                                    var h = it.GetString();
                                    if (!string.IsNullOrWhiteSpace(h)) { cdnHost = h; break; }
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(cdnHost) && atlas.TryGetProperty("cdnList", out var cdnList) && cdnList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in cdnList.EnumerateArray())
                        {
                            if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("cdn", out var c) && c.ValueKind == JsonValueKind.String)
                            {
                                var h = c.GetString();
                                if (!string.IsNullOrWhiteSpace(h)) { cdnHost = h; break; }
                            }
                            else if (it.ValueKind == JsonValueKind.String)
                            {
                                var h = it.GetString();
                                if (!string.IsNullOrWhiteSpace(h)) { cdnHost = h; break; }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(cdnHost) && atlas.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        var baseUrl = cdnHost!;
                        if (!baseUrl.Contains("://", StringComparison.Ordinal)) baseUrl = "https://" + baseUrl;
                        baseUrl = baseUrl.TrimEnd('/');

                        foreach (var it in list.EnumerateArray())
                        {
                            if (it.ValueKind != JsonValueKind.String) continue;
                            var path = it.GetString();
                            if (string.IsNullOrWhiteSpace(path)) continue;

                            string u;
                            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                u = path;
                            else if (path.StartsWith("/", StringComparison.Ordinal))
                                u = baseUrl + path;
                            else
                                u = baseUrl + "/" + path;

                            if (IsImageUrl(u) && !result.ImageUrls.Contains(u))
                                result.ImageUrls.Add(u);
                        }
                    }
                }
            }
        }

        private static void TryFillFromApollo(JsonElement root, ParseMode mode, ArticleParseResult result)
        {
            void Walk(JsonElement n)
            {
                switch (n.ValueKind)
                {
                    case JsonValueKind.Object:
                        {
                            if (n.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.String)
                            {
                                var s = cap.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) result.Title = s.Length > 64 ? s[..64] : s;
                            }
                            else if (n.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                            {
                                var s = title.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) result.Title = s.Length > 64 ? s[..64] : s;
                            }
                            else if (n.TryGetProperty("photo", out var photoObj) && photoObj.ValueKind == JsonValueKind.Object)
                            {
                                if (photoObj.TryGetProperty("caption", out var pc) && pc.ValueKind == JsonValueKind.String)
                                {
                                    var s = pc.GetString();
                                    if (!string.IsNullOrWhiteSpace(s)) result.Title = s.Length > 64 ? s[..64] : s;
                                }
                                if (photoObj.TryGetProperty("timestamp", out var ts) && ts.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                                {
                                    if (TryNormalizeTimestamp(ts, out var dt))
                                        result.PublishDateTime = FormatDate(dt);
                                }
                            }

                            if (n.TryGetProperty("timestamp", out var tsTop) && tsTop.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                            {
                                if (TryNormalizeTimestamp(tsTop, out var dt))
                                    result.PublishDateTime = FormatDate(dt);
                            }

                            if (TryPickMp4(n, out var v))
                            {
                                if (!string.IsNullOrWhiteSpace(v) && !result.VideoUrls.Contains(v!))
                                    result.VideoUrls.Add(v!);
                                result.MediaType = Enums.MediaTypeEnum.Mp4;
                                if (mode == ParseMode.CoverImage) return;
                            }

                            if (TryPickImage(n, out var img))
                            {
                                if (!string.IsNullOrWhiteSpace(img) && !result.ImageUrls.Contains(img!))
                                    result.ImageUrls.Add(img!);
                                if (mode == ParseMode.CoverImage) return;
                            }

                            foreach (var p in n.EnumerateObject())
                                Walk(p.Value);
                            break;
                        }
                    case JsonValueKind.Array:
                        foreach (var it in n.EnumerateArray())
                            Walk(it);
                        break;
                }
            }

            Walk(root);
        }

        private static bool TryPickMp4(JsonElement node, out string? url)
        {
            url = null;

            if (node.TryGetProperty("main_mv_urls", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (it.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        var s = u.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            url = s; return true;
                        }
                    }
                }
            }

            if (node.TryGetProperty("mainMvUrls", out var mvArr) && mvArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in mvArr.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        var s = it.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            url = s; return true;
                        }
                    }
                    else if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        var s = u.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            url = s; return true;
                        }
                    }
                }
            }

            foreach (var k in new[] { "videoUrl", "playUrl", "mediaUrl", "mainUrl", "url" })
            {
                if (node.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        url = s; return true;
                    }
                }
            }

            if (node.TryGetProperty("urlList", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in list.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        var s = it.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            url = s; return true;
                        }
                    }
                }
            }

            if (node.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                if (TryPickMp4(media, out var s)) { url = s; return true; }
            }

            if (node.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object)
            {
                if (TryPickMp4(photo, out var s)) { url = s; return true; }
            }

            return false;
        }

        private static bool TryPickImage(JsonElement node, out string? url)
        {
            url = null;

            if (node.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (TryPickImage(it, out var u)) { url = u; return true; }
                }
            }

            foreach (var k in new[] { "url", "imageUrl", "coverUrl" })
            {
                if (node.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (IsImageUrl(s)) { url = s!; return true; }
                }
            }

            if (node.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in urls.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        var s = it.GetString();
                        if (IsImageUrl(s)) { url = s!; return true; }
                    }
                }
            }

            if (node.TryGetProperty("urlList", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in list.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                    {
                        var s = it.GetString();
                        if (IsImageUrl(s)) { url = s!; return true; }
                    }
                }
            }

            foreach (var coverKey in new[] { "webpCoverUrls", "coverUrls" })
            {
                if (node.TryGetProperty(coverKey, out var covers) && covers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in covers.EnumerateArray())
                    {
                        if (it.ValueKind == JsonValueKind.String)
                        {
                            var s = it.GetString();
                            if (IsImageUrl(s)) { url = s!; return true; }
                        }
                        else if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            var s = u.GetString();
                            if (IsImageUrl(s)) { url = s!; return true; }
                        }
                    }
                }
            }

            if (node.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                if (TryPickImage(media, out var s)) { url = s; return true; }
            }
            if (node.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object)
            {
                if (TryPickImage(photo, out var s)) { url = s; return true; }
            }

            return false;
        }

        private static void TryFillOgTitle(string html, ArticleParseResult result)
        {
            var m = Regex.Match(html, @"<meta\s+property=[""']og:title[""']\s+content=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var t = WebUtility.HtmlDecode(m.Groups["t"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    result.Title = t.Length > 64 ? t[..64] : t;
            }
        }

        private static bool IsImageUrl(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return Regex.IsMatch(s, @"\.(jpg|jpeg|png|webp)(\?|$)", RegexOptions.IgnoreCase);
        }

        private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMddHHmmss");

        private static bool TryNormalizeTimestamp(JsonElement ts, out DateTime dt)
        {
            dt = DateTime.Now;
            try
            {
                if (ts.ValueKind == JsonValueKind.Number)
                {
                    var v = ts.GetInt64();
                    if (v >= 1_000_000_000_000) dt = DateTimeOffset.FromUnixTimeMilliseconds(v).LocalDateTime;
                    else dt = DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime;
                    return true;
                }
                if (ts.ValueKind == JsonValueKind.String)
                {
                    var s = ts.GetString();
                    if (long.TryParse(s, out var v))
                    {
                        if (v >= 1_000_000_000_000) dt = DateTimeOffset.FromUnixTimeMilliseconds(v).LocalDateTime;
                        else dt = DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}