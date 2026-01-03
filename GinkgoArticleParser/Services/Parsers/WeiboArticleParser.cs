using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using HtmlAgilityPack;
using System.Net;
using System.Text.RegularExpressions;

namespace GinkgoArticleParser.Services.Parsers;

public sealed class WeiboArticleParser : IArticleParser
{
    private static readonly CookieContainer CookieJar = new();
    private static readonly HttpClient httpClient = CreateClient();

    public bool CanHandle(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Contains("weibo.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Contains("weibo.cn", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default)
    {
        var result = new ArticleParseResult
        {
            Title = "微博内容",
            Author = string.Empty,
            PublishDateTime = FormatDate(DateTime.Now),
            DownloadDateTime = FormatDate(DateTime.Now),
            Platform = Enums.PlatformsEnum.Weibo
        };

        var midCode = ExtractMidCode(url);
        if (string.IsNullOrEmpty(midCode))
            return result;

        // 1) 移动接口（匿名可用，优先）
        if (await TryParseFromMobileApiAsync(midCode!, mode, result, ct))
            return result;

        // 2) 备用移动接口（也常匿名可用）
        if (await TryParseFromMobileAltAsync(midCode!, mode, result, ct))
            return result;

        // 3) 若用户提供 Cookie，再尝试 PC Ajax
        EnsureCookiesLoaded(cookie);
        if (await TryParseFromAjaxAsync(midCode!, mode, result, ct))
            return result;

        // 4) 兜底 OG
        try
        {
            var html = await httpClient.GetStringAsync(url, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var ogTitleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
            var ogTitle = ogTitleNode?.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(ogTitle)) result.Title = ogTitle!;

            var ogImageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            var ogImage = ogImageNode?.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(ogImage)) result.ImageUrls.Add(ogImage!);
        }
        catch { }

        return result;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = CookieJar
        };
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static void EnsureCookiesLoaded(string cookie)
    {
        // 从 Preferences 取浏览器 Cookie 文本（整段，如 "SUB=...; SUBP=...; SSOLoginState=...; WBPSESS=...; ..."）
        var cookieRaw = cookie;
        if (string.IsNullOrWhiteSpace(cookieRaw))
            return;

        // 写入 weibo.com / m.weibo.cn / weibo.cn 三个域（尽量覆盖）
        var domains = new[]
        {
            new Uri("https://weibo.com"),
            new Uri("https://m.weibo.cn"),
            new Uri("https://weibo.cn")
        };

        foreach (var part in cookieRaw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var name = kv[0].Trim();
            var value = kv[1].Trim();

            if (string.IsNullOrEmpty(name)) continue;

            foreach (var d in domains)
            {
                try
                {
                    CookieJar.Add(d, new Cookie(name, value, "/", d.Host));
                }
                catch { /* 忽略重复等异常 */ }
            }
        }
    }

    private static string? ExtractMidCode(string url)
    {
        // 1) weibo.com/<uid>/<midCode>
        var m1 = Regex.Match(url, @"weibo\.com/\d+/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (m1.Success) return m1.Groups[1].Value;

        // 2) weibo.com/<midCode>
        var m2 = Regex.Match(url, @"weibo\.com/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (m2.Success) return m2.Groups[1].Value;

        // 3) m.weibo.cn/detail|status|statuses/<midCode>
        var m3 = Regex.Match(url, @"m\.weibo\.cn/(detail|status|statuses)/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (m3.Success) return m3.Groups[^1].Value;

        // 4) weibo.cn/\d+/<midCode>
        var m4 = Regex.Match(url, @"weibo\.cn/\d+/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (m4.Success) return m4.Groups[1].Value;

        return null;
    }

    private async Task<bool> TryParseFromAjaxAsync(string midCode, ParseMode mode, ArticleParseResult result, CancellationToken ct)
    {
        var apiUrl = $"https://weibo.com/ajax/statuses/show?id={midCode}&locale=zh-CN&isGetLongText=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Referrer = new Uri($"https://weibo.com/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        // 如果 Cookie 里有 XSRF-TOKEN，带上 X-XSRF-TOKEN 头
        var xsrf = GetCookieValue(".weibo.com", "XSRF-TOKEN") ?? GetCookieValue("weibo.com", "XSRF-TOKEN");
        if (!string.IsNullOrEmpty(xsrf))
            req.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", xsrf);

        using var resp = await httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"Weibo ajax status: {(int)resp.StatusCode}");
            return false;
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return FillFromAjaxJson(doc.RootElement, mode, result);
    }

    private async Task<bool> TryParseFromMobileApiAsync(string midCode, ParseMode mode, ArticleParseResult result, CancellationToken ct)
    {
        var apiUrl = $"https://m.weibo.cn/statuses/show?id={midCode}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Referrer = new Uri($"https://m.weibo.cn/detail/{midCode}");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var resp = await httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"Weibo mobile status: {(int)resp.StatusCode}");
            return false;
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return FillFromMobileJson(doc.RootElement, mode, result);
    }

    private async Task<bool> TryParseFromMobileAltAsync(string midCode, ParseMode mode, ArticleParseResult result, CancellationToken ct)
    {
        // 备用移动端接口路径（匿名常可用）
        var apiUrl = $"https://m.weibo.cn/api/statuses/show?id={midCode}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Referrer = new Uri($"https://m.weibo.cn/detail/{midCode}");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("mweibo-pwa", "1");

        using var resp = await httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        // 结构与 TryParseFromMobileApiAsync 基本一致，复用填充逻辑
        if (FillFromMobileJson(root, mode, result)) return true;

        // 个别返回直接是状态对象，兼容一次
        return FillFromAjaxJson(root, mode, result);
    }

    private static bool FillFromAjaxJson(JsonElement root, ParseMode mode, ArticleParseResult result)
    {
        Debug.WriteLine(root.ToString());

        // 文章时间
        if (root.TryGetProperty("created_at", out var created))
        {
            var s = created.GetString() ?? string.Empty;
            if (TryParseWeiboCreatedAt(s, out var dt))
                result.PublishDateTime = FormatDate(dt);
        }

        // 取正文：优先 longText.longTextContent -> text_raw -> text（去标签）
        if (root.TryGetProperty("longText", out var longText) && longText.TryGetProperty("longTextContent", out var longTextContent))
        {
            var plain = StripHtml(longTextContent.GetString() ?? "");
            if (!string.IsNullOrWhiteSpace(plain))
                result.Title = StringHelper.SanitizeTitle(plain);
        }
        else if (root.TryGetProperty("text_raw", out var textRaw))
        {
            var plain = textRaw.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(plain))
                result.Title = StringHelper.SanitizeTitle(plain);
        }
        else if (root.TryGetProperty("text", out var textHtml))
        {
            var plain = StripHtml(textHtml.GetString() ?? "");
            if (!string.IsNullOrWhiteSpace(plain))
                result.Title = StringHelper.SanitizeTitle(plain);
        }

        // 作者昵称：user.nickname
        if (root.TryGetProperty("user", out var user) &&
            user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("screen_name", out var nn) &&
            nn.ValueKind == JsonValueKind.String)
        {
            var nick = nn.GetString();
            if (!string.IsNullOrWhiteSpace(nick))
                result.Author = StringHelper.SanitizeTitle(nick!);
        }


        // 图片/LivePhoto：优先 pic_ids + pic_infos[pid]
        var images = new List<string>();
        if (root.TryGetProperty("pic_ids", out var picIds) && root.TryGetProperty("pic_infos", out var picInfos))
        {
            foreach (var id in picIds.EnumerateArray())
            {
                var pid = id.GetString() ?? "";
                if (string.IsNullOrEmpty(pid)) continue;
                if (!picInfos.TryGetProperty(pid, out var info)) continue;

                // 先判断是否为 livephoto，并提取视频直链
                var typeStr = info.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "").ToLowerInvariant() : "";
                if (typeStr == "livephoto" && info.TryGetProperty("video", out var vProp))
                {
                    var vUrl = vProp.GetString();
                    if (!string.IsNullOrWhiteSpace(vUrl))
                    {
                        if (!result.VideoUrls.Contains(vUrl!))
                            result.VideoUrls.Add(vUrl!);

                        // mov 优先标记为 Mov，其它回退 Mp4
                        result.MediaType = vUrl!.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                            ? Enums.MediaTypeEnum.Mov
                            : Enums.MediaTypeEnum.Mp4;

                        if (mode == ParseMode.CoverImage)
                            return true; // Cover 只取首个媒体
                    }
                }

                // 再提取对应图片 URL（largest/large/original/bmiddle/url）
                string? url = null;
                if (info.TryGetProperty("largest", out var largest) && largest.TryGetProperty("url", out var largestUrl))
                    url = largestUrl.GetString();
                if (string.IsNullOrWhiteSpace(url) && info.TryGetProperty("large", out var large) && large.TryGetProperty("url", out var largeUrl))
                    url = largeUrl.GetString();
                if (string.IsNullOrWhiteSpace(url) && info.TryGetProperty("original", out var original) && original.TryGetProperty("url", out var originalUrl))
                    url = originalUrl.GetString();
                if (string.IsNullOrWhiteSpace(url) && info.TryGetProperty("bmiddle", out var bmiddle) && bmiddle.TryGetProperty("url", out var bmiddleUrl))
                    url = bmiddleUrl.GetString();
                if (string.IsNullOrWhiteSpace(url) && info.TryGetProperty("url", out var simpleUrl))
                    url = simpleUrl.GetString();

                if (!string.IsNullOrWhiteSpace(url))
                {
                    images.Add(url!);
                    if (mode == ParseMode.CoverImage) break;
                }
            }
        }

        if (images.Count > 0)
            result.ImageUrls.AddRange(images.Distinct()); // 去重

        // 视频：page_info.media_info 或 page_info.playback_list
        TryFillVideoFromPageInfo(root, result);

        // 混合媒体 mix_media_info（图片 + 视频）
        TryFillFromMixMediaInfo(root, mode, result);

        return result.VideoUrls.Count > 0 || result.ImageUrls.Count > 0 || !string.IsNullOrWhiteSpace(result.Title);
    }

    private static bool FillFromMobileJson(JsonElement root, ParseMode mode, ArticleParseResult result)
    {
        if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.Number && okProp.GetInt32() != 1)
            return false;

        if (root.TryGetProperty("data", out var data))
        {
            // 文章时间
            if (data.TryGetProperty("created_at", out var created))
            {
                var s = created.GetString() ?? string.Empty;
                if (TryParseWeiboCreatedAt(s, out var dt))
                    result.PublishDateTime = FormatDate(dt);
            }


            // 作者昵称：user.nickname
            if (data.TryGetProperty("user", out var user) &&
                user.ValueKind == JsonValueKind.Object &&
                user.TryGetProperty("screen_name", out var nn) &&
                nn.ValueKind == JsonValueKind.String)
            {
                var nick = nn.GetString();
                if (!string.IsNullOrWhiteSpace(nick))
                    result.Author = StringHelper.SanitizeTitle(nick!);
            }

            if (data.TryGetProperty("text", out var textHtml))
            {
                var plain = StripHtml(textHtml.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(plain))
                    result.Title = StringHelper.SanitizeTitle(plain);
            }

            if (data.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pics.EnumerateArray())
                {
                    string? url = null;
                    if (p.TryGetProperty("large", out var large) && large.TryGetProperty("url", out var largeUrl))
                        url = largeUrl.GetString();
                    if (string.IsNullOrWhiteSpace(url) && p.TryGetProperty("url", out var smallUrl))
                        url = smallUrl.GetString();

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        if (!result.ImageUrls.Contains(url!))
                            result.ImageUrls.Add(url!);
                        if (mode == ParseMode.CoverImage) return true;
                    }
                }
            }

            // 视频：data.page_info.media_info / playback_list
            TryFillVideoFromPageInfo(data, result);

            // 新增：混合媒体 mix_media_info（图片 + 视频）
            TryFillFromMixMediaInfo(data, mode, result);

            return result.VideoUrls.Count > 0 || result.ImageUrls.Count > 0 || !string.IsNullOrWhiteSpace(result.Title);
        }

        return false;
    }

    // 从 page_info 中提取视频直链（优先 playback_list，再回退 media_info）
    private static void TryFillVideoFromPageInfo(JsonElement container, ArticleParseResult result)
    {
        if (!container.TryGetProperty("page_info", out var pageInfo)) return;

        string? videoUrl = null;

        // 兼容两种位置：
        // 1) page_info.playback_list
        // 2) page_info.media_info.playback_list
        JsonElement playback;
        bool hasPlayback =
            (pageInfo.TryGetProperty("playback_list", out playback) &&
             playback.ValueKind == JsonValueKind.Array &&
             playback.GetArrayLength() > 0)
            ||
            (pageInfo.TryGetProperty("media_info", out var mediaInfoWithPlayback) &&
             mediaInfoWithPlayback.TryGetProperty("playback_list", out playback) &&
             playback.ValueKind == JsonValueKind.Array &&
             playback.GetArrayLength() > 0);

        // 1) 优先：playback_list（分辨率/质量最全）
        if (hasPlayback)
        {
            var bestUrl = PickBestMp4FromPlayback(playback);
            if (!string.IsNullOrWhiteSpace(bestUrl))
                videoUrl = bestUrl;
        }

        // 2) 回退：media_info 的若干直链字段
        if (string.IsNullOrWhiteSpace(videoUrl) && pageInfo.TryGetProperty("media_info", out var mediaInfo))
        {
            videoUrl = PickVideoUrlFromMediaInfo(mediaInfo);
        }

        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            if (!result.VideoUrls.Contains(videoUrl!))
                result.VideoUrls.Add(videoUrl!);
            result.MediaType = Enums.MediaTypeEnum.Mp4;
        }
    }

    // 新增：从 mix_media_info 中提取图片/视频
    // 新增/替换：从 mix_media_info 中提取图片/视频，同时在 pic 分支识别 livephoto
    private static void TryFillFromMixMediaInfo(JsonElement container, ParseMode mode, ArticleParseResult result)
    {
        if (!container.TryGetProperty("mix_media_info", out var mix)) return;

        // mix_media_info 可能是数组，或 { items: [] }
        JsonElement items;
        if (mix.ValueKind == JsonValueKind.Array)
        {
            items = mix;
        }
        else if (mix.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            items = arr;
        }
        else
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var tp) ? (tp.GetString() ?? "").ToLowerInvariant() : "";
            var data = item.TryGetProperty("data", out var d) ? d : item;

            var isPicItem = type.Contains("pic", StringComparison.Ordinal);
            var isVideoItem = type.Contains("video", StringComparison.Ordinal);

            if (isPicItem)
            {
                // 1) 先判断子类型是否为 livephoto（data.type 或 data.sub_type）
                var subType =
                    (data.TryGetProperty("type", out var st) ? st.GetString() : null) ??
                    (data.TryGetProperty("sub_type", out var sst) ? sst.GetString() : null);
                var isLivePhoto = string.Equals(subType, "livephoto", StringComparison.OrdinalIgnoreCase);

                if (isLivePhoto)
                {
                    // livephoto 常见字段：data.video（.mov），也可能有 media_info / playback_list
                    string? v = null;

                    if (data.TryGetProperty("video", out var vProp))
                    {
                        var s = vProp.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            v = s;
                    }

                    if (string.IsNullOrWhiteSpace(v) && data.TryGetProperty("playback_list", out var pb1) && pb1.ValueKind == JsonValueKind.Array && pb1.GetArrayLength() > 0)
                    {
                        v = PickBestMp4FromPlayback(pb1);
                    }

                    if (string.IsNullOrWhiteSpace(v) && data.TryGetProperty("media_info", out var mediaInfo1))
                    {
                        if (mediaInfo1.TryGetProperty("playback_list", out var pb2) && pb2.ValueKind == JsonValueKind.Array && pb2.GetArrayLength() > 0)
                            v = PickBestMp4FromPlayback(pb2);
                        if (string.IsNullOrWhiteSpace(v))
                            v = PickVideoUrlFromMediaInfo(mediaInfo1);
                    }

                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        if (!result.VideoUrls.Contains(v!))
                            result.VideoUrls.Add(v!);
                        // 根据后缀标注媒体类型
                        result.MediaType = v!.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                            ? Enums.MediaTypeEnum.Mov
                            : Enums.MediaTypeEnum.Mp4;

                        if (mode == ParseMode.CoverImage) return;
                    }
                }

                // 2) 继续提取该项的图片 URL（largest/large/original/bmiddle/url）
                var img = PickBestImageUrlFromNode(data);
                if (!string.IsNullOrWhiteSpace(img))
                {
                    if (!result.ImageUrls.Contains(img!))
                        result.ImageUrls.Add(img!);
                    if (mode == ParseMode.CoverImage) return;
                }
            }
            else if (isVideoItem)
            {
                // 与原逻辑一致：优先 playback_list，再回退 media_info
                string? v = null;
                if (data.TryGetProperty("playback_list", out var pb) && pb.ValueKind == JsonValueKind.Array && pb.GetArrayLength() > 0)
                {
                    v = PickBestMp4FromPlayback(pb);
                }
                if (string.IsNullOrWhiteSpace(v) && data.TryGetProperty("media_info", out var mediaInfo))
                {
                    if (mediaInfo.TryGetProperty("playback_list", out var pb2) && pb2.ValueKind == JsonValueKind.Array && pb2.GetArrayLength() > 0)
                        v = PickBestMp4FromPlayback(pb2);
                    if (string.IsNullOrWhiteSpace(v))
                        v = PickVideoUrlFromMediaInfo(mediaInfo);
                }

                if (!string.IsNullOrWhiteSpace(v))
                {
                    if (!result.VideoUrls.Contains(v!))
                        result.VideoUrls.Add(v!);
                    result.MediaType = Enums.MediaTypeEnum.Mp4;
                    if (mode == ParseMode.CoverImage) return;
                }
            }
        }
    }
    private static string? PickBestImageUrlFromNode(JsonElement node)
    {
        // 常见字段：largest.url / large.url / original.url / bmiddle.url / url
        string? url = null;
        if (node.TryGetProperty("largest", out var largest) && largest.TryGetProperty("url", out var u0))
            url = u0.GetString();
        if (string.IsNullOrWhiteSpace(url) && node.TryGetProperty("large", out var large) && large.TryGetProperty("url", out var u1))
            url = u1.GetString();
        if (string.IsNullOrWhiteSpace(url) && node.TryGetProperty("original", out var original) && original.TryGetProperty("url", out var u2))
            url = u2.GetString();
        if (string.IsNullOrWhiteSpace(url) && node.TryGetProperty("bmiddle", out var bmiddle) && bmiddle.TryGetProperty("url", out var u3))
            url = u3.GetString();
        if (string.IsNullOrWhiteSpace(url) && node.TryGetProperty("url", out var u4))
            url = u4.GetString();
        return url;
    }

    private static string? PickBestMp4FromPlayback(JsonElement playback)
    {
        long bestScore = long.MinValue;
        string? bestUrl = null;

        foreach (var item in playback.EnumerateArray())
        {
            if (!item.TryGetProperty("play_info", out var pi)) continue;

            var mime = pi.TryGetProperty("mime", out var mimeProp) ? (mimeProp.GetString() ?? "") : "";
            var url = pi.TryGetProperty("url", out var urlProp) ? (urlProp.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(url)) continue;

            var isMp4 = (string.IsNullOrWhiteSpace(mime) || mime.Contains("video/mp4", StringComparison.OrdinalIgnoreCase))
                        && url.Contains(".mp4", StringComparison.OrdinalIgnoreCase);
            if (!isMp4) continue;

            int qualityIndex = 0;
            if (item.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("quality_index", out var qi) &&
                qi.ValueKind == JsonValueKind.Number)
            {
                qualityIndex = qi.GetInt32();
            }

            int width = 0, height = 0, bitrate = 0;
            if (pi.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) width = w.GetInt32();
            if (pi.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt32();
            if (pi.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number) bitrate = b.GetInt32();

            long score =
                (long)qualityIndex * 1_000_000_000L +
                (long)Math.Clamp(width, 0, 10000) * Math.Clamp(height, 0, 10000) * 10L +
                (long)Math.Clamp(bitrate, 0, 100_000_000);

            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    private static string? PickVideoUrlFromMediaInfo(JsonElement mediaInfo)
    {
        var preferKeys = new[]
        {
            "mp4_4k_mp4",
            "mp4_1080p_mp4",
            "mp4_1080p",
            "mp4_hd_url",
            "mp4_720p_mp4",
            "stream_url_hd",
            "mp4_sd_url",
            "stream_url"
        };

        foreach (var k in preferKeys)
        {
            if (mediaInfo.TryGetProperty(k, out var v))
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    return s!;
            }
        }
        return null;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var noTags = Regex.Replace(html, "<.*?>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(noTags).Trim();
    }


    private static string? GetCookieValue(string domain, string name)
    {
        try
        {
            var cookies = CookieJar.GetCookies(new Uri($"https://{domain.TrimStart('.')}"));
            foreach (Cookie c in cookies)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c.Value;
        }
        catch { }
        return null;
    }

    // 解析微博 created_at，多种可能格式
    private static bool TryParseWeiboCreatedAt(string s, out DateTime dt)
    {
        dt = default;

        if (string.IsNullOrWhiteSpace(s)) return false;

        // 常见 PC 接口格式: "Wed Oct 16 14:03:36 +0800 2024"
        var formats = new[]
        {
            "ddd MMM dd HH:mm:ss K yyyy",
            "ddd MMM d HH:mm:ss K yyyy",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm"
        };

        // 尝试严格解析（英文月份/星期）
        foreach (var f in formats)
        {
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out dt) ||
                DateTime.TryParseExact(s, f, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                dt = dt.ToLocalTime();
                return true;
            }
        }

        // 尝试一般解析
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;

        // 处理 "MM-dd HH:mm"（缺少年份）这种
        var mdhm = Regex.Match(s, @"^\s*(\d{1,2})-(\d{1,2})\s+(\d{1,2}):(\d{2})\s*$");
        if (mdhm.Success)
        {
            var now = DateTime.Now;
            var month = int.Parse(mdhm.Groups[1].Value);
            var day = int.Parse(mdhm.Groups[2].Value);
            var hour = int.Parse(mdhm.Groups[3].Value);
            var minute = int.Parse(mdhm.Groups[4].Value);
            try
            {
                dt = new DateTime(now.Year, month, day, hour, minute, 0, DateTimeKind.Local);
                return true;
            }
            catch { }
        }

        return false;
    }

    private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMddHHmmss");
}