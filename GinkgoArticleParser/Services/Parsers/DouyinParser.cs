using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GinkgoArticleParser.Services.Parsers
{
    /// <summary>
    /// 抖音视频/图集解析（v.douyin.com / is.douyin.com 短链支持）
    /// 优先 iteminfo，回退页面 RouterData / 页面 JSON 与直链扫描。
    /// 增强：作者 / 音乐 / 封面 / 去水印视频直链。
    /// 图集：每张 images 元素仅选其 url_list 中质量最高的一条；评分近似时用 HEAD 比较体积。
    /// </summary>
    public sealed class DouyinParser : IArticleParser
    {
        private static readonly CookieContainer CookieJar = new();
        private static readonly HttpClient http = CreateClient();
        private const bool EnableHeadCheck = true;          // 是否启用 HEAD 文件大小比较
        private const int HeadCandidateLimit = 3;           // HEAD 比较的最多候选数量
        private const double HeadPixelToleranceRatio = 0.05;// 像素接近阈值（±5%）

        public bool CanHandle(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var h = uri.Host.ToLowerInvariant();
            return h.Contains("douyin.com") || h.Contains("iesdouyin.com") || h.Contains("amemv.com");
        }

        public async Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default)
        {
            var result = new ArticleParseResult
            {
                Title = "抖音内容",
                Author = string.Empty,
                PublishDateTime = FormatDate(DateTime.Now),
                DownloadDateTime = FormatDate(DateTime.Now),
                Platform = Enums.PlatformsEnum.Douyin
            };

            var finalUrl = await ResolveFinalUrlAsync(url, ct);
            var awemeId = ExtractAwemeId(finalUrl);

            if (!string.IsNullOrEmpty(awemeId) &&
                await TryParseFromItemInfoAsync(awemeId!, mode, result, cookie, ct))
            {
                DeduplicateImages(result);
                return result;
            }

            string html;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                req.Headers.Referrer = new Uri("https://www.iesdouyin.com/");
                using var resp = await http.SendAsync(req, ct);
                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch { DeduplicateImages(result); return result; }

            TryFillOgTitle(html, result);

            if (TryExtractJson(html, out var root))
            {
                bool routerOk = false;
                if (!string.IsNullOrEmpty(awemeId))
                {
                    routerOk = await TryFillFromRouterDataAsync(root, awemeId!, mode, result, ct);
                    if (routerOk && mode == ParseMode.CoverImage &&
                        (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                    {
                        DeduplicateImages(result);
                        return result;
                    }
                }

                if (!routerOk)
                {
                    TryFillFromJson(root, mode, result); // 回退遍历
                    if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                    {
                        DeduplicateImages(result);
                        return result;
                    }
                }
            }

            if (TryScanForPlayerUrlInHtml(html, result, mode))
            {
                if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                {
                    DeduplicateImages(result);
                    return result;
                }
            }

            // 极限回退：正则直链
            if (result.VideoUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?\.mp4(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var v = m.Value;
                    if (!result.VideoUrls.Contains(v))
                    {
                        result.VideoUrls.Add(v);
                        result.MediaType = Enums.MediaTypeEnum.Mp4;
                        if (mode == ParseMode.CoverImage)
                        {
                            DeduplicateImages(result);
                            return result;
                        }
                    }
                }
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?\.m3u8(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var u = m.Value;
                    if (!result.VideoUrls.Contains(u))
                    {
                        result.VideoUrls.Add(u);
                        result.MediaType = Enums.MediaTypeEnum.Mp4;
                        if (mode == ParseMode.CoverImage)
                        {
                            DeduplicateImages(result);
                            return result;
                        }
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
                        if (mode == ParseMode.CoverImage)
                        {
                            DeduplicateImages(result);
                            return result;
                        }
                    }
                }
            }

            DeduplicateImages(result);
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
                "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1");
            c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
            c.Timeout = TimeSpan.FromSeconds(15);
            return c;
        }

        private static async Task<string> ResolveFinalUrlAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
            var host = uri.Host.ToLowerInvariant();

            if (host.Contains("v.douyin.com") || host.Contains("is.douyin.com"))
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
                using var c = new HttpClient(handler);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Linux; Android 13; Pixel 6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
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
            catch { return url; }
        }

        private static string? ExtractAwemeId(string url)
        {
            var m1 = Regex.Match(url, @"(?:douyin|iesdouyin)\.com/.*/(?:video|note)/(\d+)", RegexOptions.IgnoreCase);
            if (m1.Success) return m1.Groups[1].Value;
            var m2 = Regex.Match(url, @"(?:douyin|iesdouyin)\.com/.*/share/(?:video|note)/(\d+)", RegexOptions.IgnoreCase);
            if (m2.Success) return m2.Groups[1].Value;
            var m3 = Regex.Match(url, @"[?&](?:item_ids|aweme_id)=(\d+)", RegexOptions.IgnoreCase);
            if (m3.Success) return m3.Groups[1].Value;
            return null;
        }

        private static async Task<bool> TryParseFromItemInfoAsync(string awemeId, ParseMode mode, ArticleParseResult result, string? cookie, CancellationToken ct)
        {
            var api = $"https://www.iesdouyin.com/web/api/v2/aweme/iteminfo/?item_ids={awemeId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, api);
            req.Headers.Referrer = new Uri($"https://www.iesdouyin.com/share/video/{awemeId}/");
            req.Headers.Accept.ParseAdd("application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            if (!string.IsNullOrWhiteSpace(cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            string raw;
            try { raw = await resp.Content.ReadAsStringAsync(ct); }
            catch { return false; }
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var firstNonWs = raw.FirstOrDefault(ch => !char.IsWhiteSpace(ch));
            if (firstNonWs != '{' && firstNonWs != '[') return false;

            JsonDocument? doc;
            try { doc = JsonDocument.Parse(raw); }
            catch { return false; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("item_list", out var list) ||
                    list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                    return false;

                var item = list[0];
                if (item.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.String)
                {
                    var t = desc.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(t))
                        result.Title = StringHelper.SanitizeTitle(t);
                }
                if (item.TryGetProperty("create_time", out var ctProp) && ctProp.ValueKind == JsonValueKind.Number)
                {
                    try
                    {
                        var ts = ctProp.GetInt64();
                        var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                        result.PublishDateTime = FormatDate(dt);
                    }
                    catch { }
                }

                TryFillAuthor(item, result);
                TryFillCover(item, result, mode);

                bool gotAny = false;
                if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                    gotAny |= await CollectBestImagesAsync(images, result, mode, ct);
                if (!gotAny && item.TryGetProperty("image_post_info", out var ipi) &&
                    ipi.TryGetProperty("images", out var ipiImgs) && ipiImgs.ValueKind == JsonValueKind.Array)
                    gotAny |= await CollectBestImagesAsync(ipiImgs, result, mode, ct);

                if (item.TryGetProperty("video", out var video))
                {
                    var best = PickBestMp4FromVideo(video);
                    var noWatermark = BuildNoWatermarkVideoUrl(video) ?? best;
                    if (!string.IsNullOrWhiteSpace(noWatermark))
                    {
                        if (!result.VideoUrls.Contains(noWatermark))
                            result.VideoUrls.Insert(0, noWatermark);
                        result.MediaType = Enums.MediaTypeEnum.Mp4;
                        gotAny = true;
                    }
                    if (!string.IsNullOrWhiteSpace(best) && best != noWatermark && !result.VideoUrls.Contains(best))
                        result.VideoUrls.Add(best);
                    if (mode == ParseMode.CoverImage && result.VideoUrls.Count > 0) return true;
                }

                TryFillMusic(item, result);
                return gotAny || result.VideoUrls.Count > 0;
            }
        }

        private static void TryFillAuthor(JsonElement item, ArticleParseResult result)
        {
            if (!item.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object) return;
            if (author.TryGetProperty("nickname", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name) &&
                    !result.Title.Contains(name, StringComparison.OrdinalIgnoreCase))
                    result.Title = $"{result.Title}-{name}";
            }
        }

        private static void TryFillCover(JsonElement item, ArticleParseResult result, ParseMode mode)
        {
            if (item.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            {
                string? cover = TryPickCoverFromVideo(video);
                if (!string.IsNullOrWhiteSpace(cover) && !result.ImageUrls.Contains(cover))
                {
                    result.ImageUrls.Insert(0, cover);
                    if (mode == ParseMode.CoverImage) return;
                }
            }
        }

        private static string? TryPickCoverFromVideo(JsonElement video)
        {
            foreach (var key in new[] { "origin_cover", "cover", "dynamic_cover" })
            {
                if (video.TryGetProperty(key, out var obj) && obj.ValueKind == JsonValueKind.Object)
                {
                    if (obj.TryGetProperty("url_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var urls = arr.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .Where(IsImageUrl)
                            .Distinct()
                            .ToList();
                        var best = SelectBestImageUrl(urls);
                        if (!string.IsNullOrWhiteSpace(best)) return best;
                    }
                    else if (obj.TryGetProperty("url", out var single) && single.ValueKind == JsonValueKind.String)
                    {
                        var u = single.GetString();
                        if (!string.IsNullOrWhiteSpace(u)) return u;
                    }
                }
            }
            return null;
        }

        private static void TryFillMusic(JsonElement item, ArticleParseResult result)
        {
            if (!item.TryGetProperty("music", out var music) || music.ValueKind != JsonValueKind.Object) return;
            string? musicUrl = null;
            if (music.TryGetProperty("play_url", out var play) && play.ValueKind == JsonValueKind.Object)
            {
                if (play.TryGetProperty("url_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            var u = s.GetString();
                            if (!string.IsNullOrWhiteSpace(u)) { musicUrl = u; break; }
                        }
                    }
                }
                if (musicUrl == null && play.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
                {
                    var raw = uriEl.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                        musicUrl = raw;
                }
            }
            if (!string.IsNullOrWhiteSpace(musicUrl) && !result.VideoUrls.Contains(musicUrl))
                result.VideoUrls.Add(musicUrl);
        }

        // 图集：对每个 image 选出最佳单张（异步支持 HEAD）
        private static async Task<bool> CollectBestImagesAsync(JsonElement imagesArray, ArticleParseResult result, ParseMode mode, CancellationToken ct)
        {
            bool any = false;
            foreach (var img in imagesArray.EnumerateArray())
            {
                string? best = null;

                if (img.TryGetProperty("url_list", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var urls = list.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(IsImageUrl)
                        .Distinct()
                        .ToList();
                    best = await SelectBestImageUrlAsync(urls, ct);
                }

                if (best == null && img.TryGetProperty("display_image", out var di) && di.ValueKind == JsonValueKind.Object)
                {
                    if (di.TryGetProperty("url_list", out var dl) && dl.ValueKind == JsonValueKind.Array)
                    {
                        var urls = dl.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .Where(IsImageUrl)
                            .Distinct()
                            .ToList();
                        best = await SelectBestImageUrlAsync(urls, ct);
                    }
                    if (best == null && di.TryGetProperty("url", out var single) && single.ValueKind == JsonValueKind.String)
                        best = single.GetString();
                }

                if (!string.IsNullOrWhiteSpace(best) && !result.ImageUrls.Contains(best))
                {
                    result.ImageUrls.Add(best);
                    any = true;
                    if (mode == ParseMode.CoverImage) return true;
                }
            }
            return any;
        }

        private static string? PickBestMp4FromVideo(JsonElement video)
        {
            if (video.TryGetProperty("bit_rate", out var brArr) && brArr.ValueKind == JsonValueKind.Array)
            {
                long bestScore = long.MinValue; string? best = null;
                foreach (var br in brArr.EnumerateArray())
                {
                    if (!br.TryGetProperty("play_addr", out var pa)) continue;
                    var urls = new List<string>();
                    if (pa.TryGetProperty("url_list", out var ulist) && ulist.ValueKind == JsonValueKind.Array)
                        foreach (var s in ulist.EnumerateArray())
                            if (s.ValueKind == JsonValueKind.String) urls.Add(s.GetString()!);
                    if (urls.Count == 0 && pa.TryGetProperty("url", out var single) && single.ValueKind == JsonValueKind.String)
                        urls.Add(single.GetString()!);
                    if (urls.Count == 0) continue;

                    int width = 0, height = 0, bitrate = 0;
                    if (video.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) width = w.GetInt32();
                    if (video.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt32();
                    if (br.TryGetProperty("bit_rate", out var b) && b.ValueKind == JsonValueKind.Number) bitrate = b.GetInt32();

                    foreach (var u in urls)
                    {
                        if (u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            long score = (long)Math.Max(1, width) * Math.Max(1, height) * 10L + Math.Max(1, bitrate);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                best = u.Contains("playwm", StringComparison.OrdinalIgnoreCase) ? u.Replace("playwm", "play") : u;
                            }
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(best)) return best;
            }

            if (video.TryGetProperty("play_addr", out var play))
            {
                if (play.TryGetProperty("url_list", out var lst) && lst.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in lst.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.String) continue;
                        var u = s.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(u)) continue;
                        if (u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            if (u.Contains("playwm", StringComparison.OrdinalIgnoreCase))
                                u = u.Replace("playwm", "play");
                            return u;
                        }
                    }
                }
            }
            return null;
        }

        private static string? BuildNoWatermarkVideoUrl(JsonElement video)
        {
            string? vid = null;
            if (video.TryGetProperty("play_addr", out var play) && play.ValueKind == JsonValueKind.Object)
                if (play.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
                    vid = uriEl.GetString();
            if (string.IsNullOrWhiteSpace(vid) && video.TryGetProperty("vid", out var vidEl) && vidEl.ValueKind == JsonValueKind.String)
                vid = vidEl.GetString();
            if (string.IsNullOrWhiteSpace(vid)) return null;
            return $"https://aweme.snssdk.com/aweme/v1/play/?video_id={vid}&ratio=1080p&line=0";
        }

        private static void TryFillOgTitle(string html, ArticleParseResult result)
        {
            var m = Regex.Match(html, @"<meta\s+property=[""']og:title[""']\s+content=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var t = WebUtility.HtmlDecode(m.Groups["t"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(t)) result.Title = StringHelper.SanitizeTitle(t);
            }
        }

        private static bool TryExtractJson(string html, out JsonElement root)
        {
            root = default;
            var keys = new[]
            {
                "window.__INIT_PROPS__",
                "window.__INITIAL_STATE__",
                "window.REDUX_STATE",
                "window.SIGI_STATE",
                "window._ROUTER_DATA"
            };

            foreach (var key in keys)
            {
                if (TryExtractObjectAfterVar(html, key, out var json))
                {
                    json = json.Replace("\\u002F", "/");
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
                    catch { }
                }
            }
            return false;
        }

        private static bool TryExtractObjectAfterVar(string html, string varName, out string json)
        {
            json = string.Empty;
            var idx = html.IndexOf(varName, StringComparison.Ordinal);
            if (idx < 0) return false;
            var start = html.IndexOf('{', idx);
            if (start < 0) return false;

            int depth = 0; bool inStr = false; char strCh = '\0';
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
                        if (depth == 0) { json = html[start..(i + 1)]; return true; }
                    }
                }
            }
            return false;
        }

        private static async Task<bool> TryFillFromRouterDataAsync(JsonElement root, string awemeId, ParseMode mode, ArticleParseResult result, CancellationToken ct)
        {
            if (!root.TryGetProperty("loaderData", out var loader) || loader.ValueKind != JsonValueKind.Object)
                return false;

            string keyEscaped = $"video_(id)/page";
            string keyRawEscaped = $"video_(id)\\u002Fpage";
            JsonElement target = default; bool found = false;

            foreach (var prop in loader.EnumerateObject())
            {
                if (prop.Name.Equals(keyEscaped, StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals(keyRawEscaped, StringComparison.OrdinalIgnoreCase) ||
                    (prop.Name.Contains($"video_(id)", StringComparison.OrdinalIgnoreCase) && prop.Name.EndsWith("/page", StringComparison.OrdinalIgnoreCase)) ||
                    (prop.Name.Contains($"note_(id)", StringComparison.OrdinalIgnoreCase) && prop.Name.EndsWith("/page", StringComparison.OrdinalIgnoreCase))
                    )
                {
                    target = prop.Value; found = true; break;
                }
            }
            if (!found) return false;

            if (!target.TryGetProperty("videoInfoRes", out var info) || info.ValueKind != JsonValueKind.Object) return false;
            if (!info.TryGetProperty("item_list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return false;

            bool any = false;
            foreach (var item in list.EnumerateArray())
            {
                string? desc = null;
                if (item.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String)
                    desc = d.GetString();
                string? nickname = null;
                if (item.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object &&
                    au.TryGetProperty("nickname", out var nn) && nn.ValueKind == JsonValueKind.String)
                    nickname = nn.GetString();

                if (!string.IsNullOrWhiteSpace(desc))
                {
                    var baseTitle = desc;
                    if (!string.IsNullOrWhiteSpace(nickname) &&
                        !baseTitle.Contains(nickname, StringComparison.OrdinalIgnoreCase))
                        baseTitle = $"{baseTitle}-{nickname}";
                    result.Title = StringHelper.SanitizeTitle(baseTitle);
                }
                else if (!string.IsNullOrWhiteSpace(nickname) &&
                         !result.Author.Contains(nickname, StringComparison.OrdinalIgnoreCase))
                {
                    result.Author = StringHelper.SanitizeTitle(nickname);
                }

                if (item.TryGetProperty("create_time", out var ctProp) && ctProp.ValueKind == JsonValueKind.Number)
                {
                    try
                    {
                        var ts = ctProp.GetInt64();
                        var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                        result.PublishDateTime = FormatDate(dt);
                    }
                    catch { }
                }

                int awemeType = -1;
                if (item.TryGetProperty("aweme_type", out var typeEl) && typeEl.ValueKind == JsonValueKind.Number)
                    awemeType = typeEl.GetInt32();

                if (awemeType == 2)
                {
                    if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                        any |= await CollectBestImagesAsync(images, result, mode, ct);
                }
                else if (awemeType == 4)
                {
                    if (item.TryGetProperty("video", out var video))
                    {
                        var best = PickBestMp4FromVideo(video);
                        var noWatermark = BuildNoWatermarkVideoUrl(video) ?? best;
                        if (!string.IsNullOrWhiteSpace(noWatermark) && !result.VideoUrls.Contains(noWatermark))
                        {
                            result.VideoUrls.Insert(0, noWatermark);
                            result.MediaType = Enums.MediaTypeEnum.Mp4;
                            any = true;
                            if (mode == ParseMode.CoverImage) return true;
                        }
                        if (!string.IsNullOrWhiteSpace(best) && best != noWatermark && !result.VideoUrls.Contains(best))
                        {
                            result.VideoUrls.Add(best);
                            result.MediaType = Enums.MediaTypeEnum.Mp4;
                            any = true;
                            if (mode == ParseMode.CoverImage) return true;
                        }
                        TryFillCover(item, result, mode);
                    }
                }
                else
                {
                    if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                        any |= await CollectBestImagesAsync(images, result, mode, ct);
                    if (item.TryGetProperty("video", out var video))
                    {
                        var best = PickBestMp4FromVideo(video);
                        if (!string.IsNullOrWhiteSpace(best) && !result.VideoUrls.Contains(best))
                        {
                            result.VideoUrls.Add(best);
                            result.MediaType = Enums.MediaTypeEnum.Mp4;
                            any = true;
                            if (mode == ParseMode.CoverImage) return true;
                        }
                    }
                }

                TryFillMusic(item, result);
            }

            return any || result.VideoUrls.Count > 0 || result.ImageUrls.Count > 0;
        }

        private static void TryFillFromJson(JsonElement root, ParseMode mode, ArticleParseResult result)
        {
            void Walk(JsonElement n)
            {
                switch (n.ValueKind)
                {
                    case JsonValueKind.Object:
                        {
                            if (n.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String)
                            {
                                var s = d.GetString();
                                if (!string.IsNullOrWhiteSpace(s))
                                    result.Title = StringHelper.SanitizeTitle(s);
                            }
                            if (TryPickImage(n, out var img))
                            {
                                if (!result.ImageUrls.Contains(img)) result.ImageUrls.Add(img);
                                if (mode == ParseMode.CoverImage) return;
                            }
                            if (TryPickMp4(n, out var v))
                            {
                                if (!result.VideoUrls.Contains(v)) result.VideoUrls.Add(v);
                                result.MediaType = Enums.MediaTypeEnum.Mp4;
                                if (mode == ParseMode.CoverImage) return;
                            }
                            foreach (var p in n.EnumerateObject()) Walk(p.Value);
                            break;
                        }
                    case JsonValueKind.Array:
                        foreach (var it in n.EnumerateArray()) Walk(it);
                        break;
                }
            }
            Walk(root);
        }

        private static bool TryPickImage(JsonElement node, out string? url)
        {
            url = null;
            foreach (var k in new[] { "url", "img_url", "cover", "display_image", "images" })
            {
                if (node.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (IsImageUrl(s)) { url = s; return true; }
                }
            }
            if (node.TryGetProperty("url_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var urls = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(IsImageUrl)
                    .Distinct()
                    .ToList();
                var best = SelectBestImageUrl(urls);
                if (!string.IsNullOrWhiteSpace(best)) { url = best; return true; }
            }
            return false;
        }

        private static bool TryPickMp4(JsonElement node, out string? url)
        {
            url = null;
            foreach (var k in new[] { "playAddr", "playApi", "play_addr", "url" })
            {
                if (!node.TryGetProperty(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    { url = s.Contains("playwm", StringComparison.OrdinalIgnoreCase) ? s.Replace("playwm", "play") : s; return true; }
                }
                else if (v.ValueKind == JsonValueKind.Object &&
                         v.TryGetProperty("url_list", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.String) continue;
                        var u = s.GetString();
                        if (!string.IsNullOrWhiteSpace(u) && u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        {
                            if (u.Contains("playwm", StringComparison.OrdinalIgnoreCase)) u = u.Replace("playwm", "play");
                            url = u; return true;
                        }
                    }
                }
            }
            if (node.TryGetProperty("url_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in list.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.String)
                    {
                        var u = s.GetString();
                        if (!string.IsNullOrWhiteSpace(u) && u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                        { url = u; return true; }
                    }
                }
            }
            return false;
        }

        private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMddHHmmss");

        private static bool TryScanForPlayerUrlInHtml(string html, ArticleParseResult result, ParseMode mode)
        {
            bool added = false;
            var mVid = Regex.Match(html, @"<video\b[^>]*\bsrc=[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (mVid.Success)
            {
                var u = WebUtility.HtmlDecode(mVid.Groups["u"].Value);
                if (!string.IsNullOrWhiteSpace(u) && !result.VideoUrls.Contains(u))
                {
                    result.VideoUrls.Add(u); result.MediaType = Enums.MediaTypeEnum.Mp4;
                    added = true; if (mode == ParseMode.CoverImage) return true;
                }
            }
            foreach (Match m in Regex.Matches(html, @"<source\b[^>]*\bsrc=[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase))
            {
                var u = WebUtility.HtmlDecode(m.Groups["u"].Value);
                if (!string.IsNullOrWhiteSpace(u) && !result.VideoUrls.Contains(u))
                {
                    result.VideoUrls.Add(u); result.MediaType = Enums.MediaTypeEnum.Mp4;
                    added = true; if (mode == ParseMode.CoverImage) return true;
                }
            }
            foreach (Match m in Regex.Matches(html, @"https?://[^\s'""<>]+?\.(mp4|m3u8)(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
            {
                var u = m.Value;
                if (!result.VideoUrls.Contains(u))
                {
                    result.VideoUrls.Add(u); result.MediaType = Enums.MediaTypeEnum.Mp4;
                    added = true; if (mode == ParseMode.CoverImage) return true;
                }
            }
            var mUri = Regex.Match(html, @"""uri""\s*:\s*""(?<u>v[^""]+)""", RegexOptions.IgnoreCase);
            if (!mUri.Success)
                mUri = Regex.Match(html, @"""vid""\s*:\s*""(?<u>[^""]+)""", RegexOptions.IgnoreCase);
            if (mUri.Success)
            {
                var vid = mUri.Groups["u"].Value;
                if (!string.IsNullOrWhiteSpace(vid))
                {
                    var play = $"https://aweme.snssdk.com/aweme/v1/play/?video_id={vid}&ratio=1080p&line=0";
                    if (!result.VideoUrls.Contains(play))
                    {
                        result.VideoUrls.Insert(0, play); result.MediaType = Enums.MediaTypeEnum.Mp4;
                        added = true; if (mode == ParseMode.CoverImage) return true;
                    }
                }
            }
            for (int i = 0; i < result.VideoUrls.Count; i++)
            {
                var u = result.VideoUrls[i];
                if (u.Contains("playwm", StringComparison.OrdinalIgnoreCase))
                {
                    var noW = u.Replace("playwm", "play");
                    if (!result.VideoUrls.Contains(noW))
                    {
                        result.VideoUrls.Insert(0, noW);
                        result.MediaType = Enums.MediaTypeEnum.Mp4;
                        added = true; if (mode == ParseMode.CoverImage) return true;
                    }
                }
            }
            return added;
        }

        // =============== 图片选择逻辑 ===============

        private static bool IsImageUrl(string? u) =>
            !string.IsNullOrWhiteSpace(u) &&
            Regex.IsMatch(u, @"\.(jpg|jpeg|png|webp)(\?|$)", RegexOptions.IgnoreCase);

        private static (int w, int h) ExtractResolution(string url)
        {
            // 支持 shrink:W:H | sh=W_H | w= & h= | 纯路径中 WxH
            var m1 = Regex.Match(url, @"shrink:(\d+):(\d+)", RegexOptions.IgnoreCase);
            if (m1.Success) return (int.Parse(m1.Groups[1].Value), int.Parse(m1.Groups[2].Value));

            var m2 = Regex.Match(url, @"[?&]sh=(\d+)_(\d+)", RegexOptions.IgnoreCase);
            if (m2.Success) return (int.Parse(m2.Groups[1].Value), int.Parse(m2.Groups[2].Value));

            var m3 = Regex.Match(url, @"[?&]w=(\d+)[&].*?[?&]h=(\d+)", RegexOptions.IgnoreCase);
            if (m3.Success) return (int.Parse(m3.Groups[1].Value), int.Parse(m3.Groups[2].Value));

            var m4 = Regex.Match(url, @"(\d{3,4})x(\d{3,4})", RegexOptions.IgnoreCase);
            if (m4.Success) return (int.Parse(m4.Groups[1].Value), int.Parse(m4.Groups[2].Value));

            var m5 = Regex.Match(url, @"[?&]sh=(\d+)_(\d+)", RegexOptions.IgnoreCase);
            if (m5.Success) return (int.Parse(m5.Groups[1].Value), int.Parse(m5.Groups[2].Value));

            // query 中 sh=1440_1920
            var m6 = Regex.Match(url, @"[?&]sh=(\d+)_(\d+)", RegexOptions.IgnoreCase);
            if (m6.Success) return (int.Parse(m6.Groups[1].Value), int.Parse(m6.Groups[2].Value));

            // fallback：biz_tag 没有分辨率时返回 (0,0)
            return (0, 0);
        }

        private static int HeuristicImageScore(string url)
        {
            var (w, h) = ExtractResolution(url);
            long pixels = (long)w * h;
            int score = 0;

            // 分辨率权重
            if (pixels > 0) score += (int)Math.Min(pixels / 1000, 1500); // 上限避免爆炸

            // 压缩标记
            if (url.Contains("q75", StringComparison.OrdinalIgnoreCase)) score -= 80;
            if (url.Contains("shrink:", StringComparison.OrdinalIgnoreCase)) score += 120;

            // 格式
            var u = url.ToLowerInvariant();
            if (u.EndsWith(".jpeg") || u.EndsWith(".jpg")) score += 40;
            else if (u.EndsWith(".png")) score += 35;
            else if (u.EndsWith(".webp")) score += 10; // webp 可能压缩

            // 可能的原图关键词
            if (u.Contains("origin") || u.Contains("original") || u.Contains("raw")) score += 200;

            // 水印/压缩惩罚
            if (u.Contains("watermark") || u.Contains("wm_")) score -= 150;
            if (u.Contains("compress") || u.Contains("thumb") || u.Contains("small")) score -= 100;

            return score;
        }

        private static string? SelectBestImageUrl(List<string> urls)
        {
            if (urls == null || urls.Count == 0) return null;
            return urls
                .Select(u => (Url: u, Score: HeuristicImageScore(u), Pixels: (long)ExtractResolution(u).w * ExtractResolution(u).h))
                .OrderByDescending(t => t.Score)
                .ThenByDescending(t => t.Pixels)
                .ThenByDescending(t => t.Url.Length)
                .Select(t => t.Url)
                .FirstOrDefault();
        }

        private static async Task<string?> SelectBestImageUrlAsync(List<string> urls, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return null;

            var ranked = urls
                .Select(u =>
                {
                    var (w, h) = ExtractResolution(u);
                    return new
                    {
                        Url = u,
                        Score = HeuristicImageScore(u),
                        Pixels = (long)w * h
                    };
                })
                .OrderByDescending(t => t.Score)
                .ThenByDescending(t => t.Pixels)
                .ThenByDescending(t => t.Url.Length)
                .ToList();

            var top = ranked.First();
            if (!EnableHeadCheck || ranked.Count == 1) return top.Url;

            // 找出像素接近的前 N 个（与首个像素差比 ≤5%）
            long basePixels = Math.Max(1, top.Pixels);
            var headCandidates = ranked
                .Where(t => basePixels > 0 && Math.Abs(t.Pixels - basePixels) / (double)basePixels <= HeadPixelToleranceRatio)
                .Take(HeadCandidateLimit)
                .Select(t => t.Url)
                .ToList();

            if (headCandidates.Count <= 1) return top.Url;

            long bestSize = -1;
            string? bestUrl = null;

            foreach (var u in headCandidates)
            {
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, u);
                    using var headResp = await http.SendAsync(headReq, ct);
                    if (headResp.Content.Headers.ContentLength is long len && len > bestSize)
                    {
                        bestSize = len;
                        bestUrl = u;
                    }
                }
                catch
                {
                    // HEAD 失败可尝试 GET Range 0-0（轻量探测）
                    try
                    {
                        using var getReq = new HttpRequestMessage(HttpMethod.Get, u);
                        getReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                        using var getResp = await http.SendAsync(getReq, ct);
                        if (getResp.Content.Headers.ContentLength is long len2 && len2 > bestSize)
                        {
                            bestSize = len2;
                            bestUrl = u;
                        }
                    }
                    catch { /* 忽略 */ }
                }
            }

            // 如果 HEAD + Range 都没成功，回退启发式最佳
            return bestUrl ?? top.Url;
        }

        /// <summary>
        /// 规范化图片 Key：用于识别同一原图的不同 CDN / 查询参数版本
        /// </summary>
        private static string NormalizeImageKey(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            // 去掉查询参数
            var noQuery = url.Split('?', '#')[0];

            // 统一主机后缀 (p*.douyinpic.com => douyinpic.com)
            try
            {
                var uri = new Uri(noQuery);
                var host = uri.Host;
                var hostNorm = Regex.Replace(host, @"^p\d+\.", "", RegexOptions.IgnoreCase);
                // 取路径
                var path = uri.AbsolutePath;

                // 若存在 aweme-image / tos 路径，截取自其开始
                var idxAweme = path.IndexOf("/aweme-image/", StringComparison.OrdinalIgnoreCase);
                if (idxAweme >= 0) path = path[idxAweme..];
                var idxTos = path.IndexOf("/tos-", StringComparison.OrdinalIgnoreCase);
                if (idxTos >= 0) path = path[idxTos..];

                // 取最后两级（避免过长）
                var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length >= 2)
                    path = $"{segs[^2]}/{segs[^1]}";
                else if (segs.Length == 1)
                    path = segs[0];

                return (hostNorm + "/" + path).ToLowerInvariant();
            }
            catch
            {
                return noQuery.ToLowerInvariant();
            }
        }

        /// <summary>
        /// 对 result.ImageUrls 去重：同一规范化 Key 保留评分最高的那一项
        /// </summary>
        private static void DeduplicateImages(ArticleParseResult result)
        {
            if (result.ImageUrls.Count <= 1) return;

            var groups = result.ImageUrls
                .GroupBy(NormalizeImageKey)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            var picked = new List<string>();
            foreach (var g in groups)
            {
                // 按启发式评分 + 分辨率像素排序
                var best = g
                    .OrderByDescending(u => HeuristicImageScore(u))
                    .ThenByDescending(u =>
                    {
                        var (w, h) = ExtractResolution(u);
                        return (long)w * h;
                    })
                    .First();
                picked.Add(best);
            }

            // 保持原始出现顺序（以首次加入顺序排序）
            var orderMap = result.ImageUrls
                .Select((u, idx) => new { Url = u, Idx = idx })
                .ToDictionary(x => x.Url, x => x.Idx);

            picked = picked
                .OrderBy(u => orderMap.TryGetValue(u, out var i) ? i : int.MaxValue)
                .ToList();

            result.ImageUrls.Clear();
            result.ImageUrls.AddRange(picked);
        }
    }
}