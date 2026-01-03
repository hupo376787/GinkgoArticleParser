using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GinkgoArticleParser.Services.Parsers
{
    /// <summary>
    /// 解析小红书笔记图集/视频（支持短链 xhslink.com 与正式 explore/discovery 链接）
    /// </summary>
    public sealed class XiaohongshuParser : IArticleParser
    {
        private static readonly CookieContainer CookieJar = new();
        private static readonly HttpClient httpClient = CreateClient();

        public bool CanHandle(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Host.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("xiaohongshu.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<ArticleParseResult> ParseAsync(string url, ParseMode mode, string? cookie = null, CancellationToken ct = default)
        {
            var result = new ArticleParseResult
            {
                Title = "小红书内容",
                Author = string.Empty,
                PublishDateTime = FormatDate(DateTime.Now),
                DownloadDateTime = FormatDate(DateTime.Now),
                Platform = Enums.PlatformsEnum.Xiaohongshu
            };

            EnsureCookiesLoaded(cookie);

            // 1) 短链解析（禁自动跳转，读取 Location）
            var finalUrl = await ResolveFinalUrlAsync(url, ct);

            // 2) 提取 noteId
            var noteId = ExtractNoteId(finalUrl);
            // 不强制依赖 URL 的 noteId，后面也会从 __INITIAL_STATE__ 中尝试 firstNoteId/currentNoteId
            // 这里如果拿不到就先继续

            // 3) 获取笔记页 HTML
            string html;
            try
            {
                html = await httpClient.GetStringAsync(finalUrl, ct);
            }
            catch
            {
                return result;
            }

            // 4) 从 __INITIAL_STATE__ 解析媒体
            if (TryExtractInitialStateJson(html, out var json))
            {
                // 先将 \u002F -> /，再做 JSON 解析（并允许尾逗号、注释）
                var normalized = FixInitialStateJsonEscapes(json);

                var options = new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                };

                try
                {
                    using var doc = JsonDocument.Parse(normalized, options);
                    FillFromInitialState(doc.RootElement, noteId, mode, result);

                    if (result.ImageUrls.Count == 0)
                        TryFillImagesFromJson(doc.RootElement, mode, result);

                    if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                        return result;
                }
                catch (JsonException)
                {
                    // 兜底：进一步清洗后再试一次
                    if (TryFixInitialState(normalized, out var fixedJson))
                    {
                        try
                        {
                            using var doc2 = JsonDocument.Parse(fixedJson, options);
                            FillFromInitialState(doc2.RootElement, noteId, mode, result);

                            if (result.ImageUrls.Count == 0)
                                TryFillImagesFromJson(doc2.RootElement, mode, result);

                            if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                                return result;
                        }
                        catch { /* 仍失败则走兜底直链扫描 */ }
                    }
                }
            }

            // 5) 兜底：扫描直链
            if (result.ImageUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?(\.jpg|\.jpeg|\.png|\.webp)(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var u = m.Value;
                    if (!result.ImageUrls.Contains(u))
                    {
                        result.ImageUrls.Add(u);
                        if (mode == ParseMode.CoverImage) return result;
                    }
                }
            }

            if (result.VideoUrls.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"https://[^\s'""<>]+?(\.mp4|\.mov)(\?[^\s'""<>]*)?", RegexOptions.IgnoreCase))
                {
                    var v = m.Value;
                    if (!result.VideoUrls.Contains(v))
                    {
                        result.VideoUrls.Add(v);
                        result.MediaType = v.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                            ? Enums.MediaTypeEnum.Mov
                            : Enums.MediaTypeEnum.Mp4;
                        if (mode == ParseMode.CoverImage) return result;
                    }
                }
            }

            // 6) 标题兜底
            if (string.IsNullOrWhiteSpace(result.Title) || result.Title == "小红书内容")
            {
                var titleMatch = Regex.Match(html, @"<meta\s+property=[""']og:title[""']\s+content=[""'](?<t>[^""']+)[""']",
                    RegexOptions.IgnoreCase);
                if (titleMatch.Success)
                {
                    var t = StringHelper.SanitizeTitle(titleMatch.Groups["t"].Value);
                    if (!string.IsNullOrWhiteSpace(t))
                        result.Title = t;
                }
            }

            if (result.VideoUrls.Count > 0 && result.MediaType == Enums.MediaTypeEnum.Jpeg)
                result.MediaType = Enums.MediaTypeEnum.Mp4;

            return result;
        }

        #region 核心解析

        private static void FillFromInitialState(JsonElement root, string? urlNoteId, ParseMode mode, ArticleParseResult result)
        {
            if (!root.TryGetProperty("note", out var section))
                return;

            // 1) 优先：从 noteDetailMap 中按 noteId 定位
            if (FillFromNoteDetailMap(section, urlNoteId, mode, result))
                return;

            // 2) 兼容旧路径：note.note / note.imageList 等
            var noteObj = section.TryGetProperty("note", out var nested) ? nested : section;

            // 标题
            if (noteObj.TryGetProperty("title", out var titleProp))
            {
                var t = StringHelper.SanitizeTitle(titleProp.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(t))
                    result.Title = t;
            }
            else if (noteObj.TryGetProperty("desc", out var descProp))
            {
                var t = StringHelper.SanitizeTitle(descProp.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(t))
                    result.Title = t;
            }

            // 发布时间（多字段兼容，若 noteDetailMap 未命中，这里作为回退）
            DateTime? publish = null;
            foreach (var k in new[] { "lastUpdateTime", "time", "createTime", "publishTime", "postTime" })
            {
                if (noteObj.TryGetProperty(k, out var tp))
                {
                    if (tp.ValueKind == JsonValueKind.Number)
                    {
                        var num = tp.GetInt64();
                        publish = num > 1_000_000_000_000
                            ? DateTimeOffset.FromUnixTimeMilliseconds(num).LocalDateTime
                            : DateTimeOffset.FromUnixTimeSeconds(num).LocalDateTime;
                    }
                    else
                    {
                        var s = tp.GetString();
                        if (TryParseFlexibleDateTime(s, out var dt))
                            publish = dt;
                    }
                }
                if (publish.HasValue) break;
            }
            if (publish.HasValue)
                result.PublishDateTime = FormatDate(publish.Value);

            // 图集：多路径尝试；失败则递归扫描
            if (!TryFillImagesFromKnownPaths(section, noteObj, mode, result))
                TryFillImagesFromJson(section, mode, result);

            // 视频
            if (section.TryGetProperty("video", out var videoObj))
            {
                var vUrl = PickVideoUrl(videoObj);
                if (!string.IsNullOrWhiteSpace(vUrl))
                {
                    if (!result.VideoUrls.Contains(vUrl!))
                        result.VideoUrls.Add(vUrl!);

                    result.MediaType = vUrl!.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                        ? Enums.MediaTypeEnum.Mov
                        : Enums.MediaTypeEnum.Mp4;

                    if (mode == ParseMode.CoverImage) return;
                }
            }
        }

        // 新增：优先从 note.noteDetailMap/<id>/note 读取 title、imageList、lastUpdateTime
        private static bool FillFromNoteDetailMap(JsonElement section, string? urlNoteId, ParseMode mode, ArticleParseResult result)
        {
            if (!section.TryGetProperty("noteDetailMap", out var map) || map.ValueKind != JsonValueKind.Object)
                return false;

            // 选择 notes 的主键：优先 URL 中的 noteId，其次 firstNoteId / currentNoteId，最后取第一个键
            string? targetId = urlNoteId;

            if (string.IsNullOrWhiteSpace(targetId))
            {
                if (section.TryGetProperty("firstNoteId", out var f) && f.ValueKind == JsonValueKind.String)
                    targetId = f.GetString();
                else if (section.TryGetProperty("currentNoteId", out var c) && c.ValueKind == JsonValueKind.String)
                    targetId = c.GetString();
            }

            JsonElement targetEntry = default;
            bool found = false;

            if (!string.IsNullOrWhiteSpace(targetId) && map.TryGetProperty(targetId!, out var direct))
            {
                targetEntry = direct;
                found = true;
            }
            else
            {
                // 取第一个键
                foreach (var prop in map.EnumerateObject())
                {
                    targetEntry = prop.Value;
                    targetId = prop.Name;
                    found = true;
                    break;
                }
            }

            if (!found) return false;

            // 目标 note 节点
            var node = targetEntry.TryGetProperty("note", out var noteNode) ? noteNode : targetEntry;

            // 标题
            if (node.TryGetProperty("title", out var tProp))
            {
                node.TryGetProperty("desc", out var tDesc);
                var t = StringHelper.SanitizeTitle(tProp.GetString() + tDesc.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(t))
                    result.Title = t;
            }

            // 作者昵称：user.nickname
            if (node.TryGetProperty("user", out var user) &&
                user.ValueKind == JsonValueKind.Object &&
                user.TryGetProperty("nickname", out var nn) &&
                nn.ValueKind == JsonValueKind.String)
            {
                var nick = nn.GetString();
                if (!string.IsNullOrWhiteSpace(nick))
                    result.Author = StringHelper.SanitizeTitle(nick!);
            }

            // lastUpdateTime 优先作为 PublishDateTime
            if (node.TryGetProperty("lastUpdateTime", out var lut))
            {
                if (lut.ValueKind == JsonValueKind.Number)
                {
                    var num = lut.GetInt64();
                    var dt = num > 1_000_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(num).LocalDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(num).LocalDateTime;
                    result.PublishDateTime = FormatDate(dt);
                }
                else
                {
                    var s = lut.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && TryParseFlexibleDateTime(s, out var dt))
                        result.PublishDateTime = FormatDate(dt);
                }
            }

            // 图集
            if (node.TryGetProperty("imageList", out var imageList) && imageList.ValueKind == JsonValueKind.Array)
            {
                CollectImagesFromArray(imageList, result, mode);
                if (mode == ParseMode.CoverImage && (result.ImageUrls.Count > 0 || result.VideoUrls.Count > 0))
                    return true;
            }

            return result.ImageUrls.Count > 0 || !string.IsNullOrWhiteSpace(result.Title);
        }

        private static string? PickVideoUrl(JsonElement videoObj)
        {
            foreach (var k in new[] { "playUrl", "url", "mediaUrl", "mainUrl" })
            {
                if (videoObj.TryGetProperty(k, out var p))
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && (s.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || s.Contains(".mov", StringComparison.OrdinalIgnoreCase)))
                        return s;
                }
            }

            if (videoObj.TryGetProperty("media", out var media) &&
                media.TryGetProperty("stream", out var stream))
            {
                foreach (var prop in stream.EnumerateObject())
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(v) && (v.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || v.Contains(".mov", StringComparison.OrdinalIgnoreCase)))
                        return v;
                }
            }

            return null;
        }

        #endregion

        #region 工具方法

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
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
            c.Timeout = TimeSpan.FromSeconds(15);
            return c;
        }

        private static async Task<string> ResolveFinalUrlAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            // 专门处理 xhslink 短链：禁自动跳转，抓 Location
            if (uri.Host.Contains("xhslink.com", StringComparison.OrdinalIgnoreCase))
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = true,
                    CookieContainer = CookieJar
                };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1");
                client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");

                try
                {
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
                    {
                        var loc = resp.Headers.Location;
                        if (loc != null)
                        {
                            var abs = loc.IsAbsoluteUri ? loc.ToString() : new Uri(uri, loc).ToString();

                            // 若是包了 target= 的跳转，先解包
                            var mTarget = Regex.Match(abs, @"[?&]target=([^&]+)", RegexOptions.IgnoreCase);
                            if (mTarget.Success)
                                abs = Uri.UnescapeDataString(mTarget.Groups[1].Value);

                            // 自定义协议 xiaohongshu://note/<id>
                            var mNote = Regex.Match(abs, @"note/([0-9a-zA-Z]{16,40})", RegexOptions.IgnoreCase);
                            if (abs.StartsWith("xiaohongshu://", StringComparison.OrdinalIgnoreCase) && mNote.Success)
                                return $"https://www.xiaohongshu.com/explore/{mNote.Groups[1].Value}";

                            if (abs.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                return abs;
                        }

                        var body = await resp.Content.ReadAsStringAsync(ct);
                        var m = Regex.Match(body, @"https?://(?:www\.)?xiaohongshu\.com/(?:explore|discovery/item)/([0-9a-zA-Z]{16,40})", RegexOptions.IgnoreCase);
                        if (m.Success) return $"https://www.xiaohongshu.com/explore/{m.Groups[1].Value}";
                    }
                }
                catch
                {
                    // 忽略，走通用逻辑
                }
            }

            // 其它：用默认客户端（自动跳转）尝试得到最终 URL
            try
            {
                using var resp = await httpClient.GetAsync(url, ct);
                return resp.RequestMessage?.RequestUri?.ToString() ?? url;
            }
            catch
            {
                return url;
            }
        }

        private static string? ExtractNoteId(string url)
        {
            var m1 = Regex.Match(url, @"xiaohongshu\.com/(?:explore)/([0-9a-zA-Z]{16,40})", RegexOptions.IgnoreCase);
            if (m1.Success) return m1.Groups[1].Value;

            var m2 = Regex.Match(url, @"xiaohongshu\.com/(?:discovery|discover)/item/([0-9a-zA-Z]{16,40})", RegexOptions.IgnoreCase);
            if (m2.Success) return m2.Groups[1].Value;

            return null;
        }

        private static bool TryExtractInitialStateJson(string html, out string json)
        {
            json = string.Empty;
            var match = Regex.Match(html, @"window\.__INITIAL_STATE__\s*=\s*(\{.*?\})\s*;</script>", RegexOptions.Singleline);
            if (match.Success)
            {
                json = match.Groups[1].Value;
                return true;
            }

            var idx = html.IndexOf("window.__INITIAL_STATE__=", StringComparison.Ordinal);
            if (idx < 0) return false;
            var start = html.IndexOf('{', idx);
            if (start < 0) return false;

            int depth = 0;
            for (int i = start; i < html.Length; i++)
            {
                var ch = html[i];
                if (ch == '{') depth++;
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
            return false;
        }

        // 关键：将 \u002F -> /，并预处理常见非 JSON 值/转义，提升可解析性
        private static string FixInitialStateJsonEscapes(string raw)
        {
            var s = raw;

            // 1) 先把 \u002F 变为 /（URL 中很常见）
            s = s.Replace("\\u002F", "/");

            // 2) \xNN -> \u00NN
            s = Regex.Replace(s, @"\\x([0-9A-Fa-f]{2})", m => "\\u00" + m.Groups[1].Value);

            // 3) 去掉尾随逗号
            s = Regex.Replace(s, @",\s*(?=[}\]])", string.Empty);

            return s;
        }

        // 进一步容错：undefined / NaN / Infinity -> null
        private static bool TryFixInitialState(string raw, out string fixedJson)
        {
            var s = raw;

            s = Regex.Replace(s, @"(?<=[:\[,]\s*)undefined(?=\s*[,}\]])", "null", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"(?<=[:\[,]\s*)(NaN|Infinity|-Infinity)(?=\s*[,}\]])", "null", RegexOptions.IgnoreCase);

            // 再次去尾随逗号，以免替换产生逗号悬挂
            s = Regex.Replace(s, @",\s*(?=[}\]])", string.Empty);

            fixedJson = s;
            return true;
        }

        private static void EnsureCookiesLoaded(string cookie)
        {
            var raw = cookie;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var domain = new Uri("https://www.xiaohongshu.com");
            foreach (var part in raw.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var name = kv[0].Trim();
                var value = kv[1].Trim();
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    CookieJar.Add(domain, new Cookie(name, value, "/", domain.Host));
                }
                catch { }
            }
        }

        private static bool TryParseFlexibleDateTime(string? s, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            if (Regex.IsMatch(s, @"^\d{10,13}$") && long.TryParse(s, out var num))
            {
                try
                {
                    dt = s.Length == 13
                        ? DateTimeOffset.FromUnixTimeMilliseconds(num).LocalDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(num).LocalDateTime;
                    return true;
                }
                catch { }
            }

            foreach (var f in new[]
            {
                "yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm","yyyy/MM/dd HH:mm:ss","yyyy/MM/dd HH:mm",
                "yyyy-MM-dd","yyyy/MM/dd","yyyyMMddHHmmss","yyyyMMdd"
            })
            {
                if (DateTime.TryParseExact(s, f, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                    return true;
            }

            return DateTime.TryParse(s, out dt);
        }

        private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMddHHmmss");

        // 多路径提取图集
        private static bool TryFillImagesFromKnownPaths(JsonElement section, JsonElement noteObj, ParseMode mode, ArticleParseResult result)
        {
            if (TryReadImageArray(noteObj, "imageList", mode, result)) return true;
            if (TryReadImageArray(section, "imageList", mode, result)) return true;

            if (TryReadImageArray(noteObj, "images", mode, result)) return true;
            if (TryReadImageArray(section, "images", mode, result)) return true;

            if (TryReadImageArray(noteObj, "image_infos", mode, result)) return true;
            if (TryReadImageArray(section, "image_infos", mode, result)) return true;

            if (TryReadImageArray(noteObj, "image_info_list", mode, result)) return true;
            if (TryReadImageArray(section, "image_info_list", mode, result)) return true;

            return result.ImageUrls.Count > 0;
        }

        // 递归扫描 JSON，寻找任何包含图片直链的数组
        private static void TryFillImagesFromJson(JsonElement root, ParseMode mode, ArticleParseResult result, int depth = 0)
        {
            if (depth > 8) return;

            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in root.EnumerateArray())
                        TryFillImagesFromJson(item, mode, result, depth + 1);
                    break;

                case JsonValueKind.Object:
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array &&
                            (prop.NameEquals("imageList") || prop.NameEquals("images") || prop.NameEquals("image_infos") || prop.NameEquals("image_info_list")))
                        {
                            CollectImagesFromArray(prop.Value, result, mode);
                            if (mode == ParseMode.CoverImage && result.ImageUrls.Count > 0) return;
                        }
                        else
                        {
                            TryFillImagesFromJson(prop.Value, mode, result, depth + 1);
                        }
                    }
                    break;
            }
        }

        private static bool TryReadImageArray(JsonElement node, string propName, ParseMode mode, ArticleParseResult result)
        {
            if (!node.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
            CollectImagesFromArray(arr, result, mode);
            return result.ImageUrls.Count > 0;
        }

        private static void CollectImagesFromArray(JsonElement arr, ArticleParseResult result, ParseMode mode)
        {
            foreach (var img in arr.EnumerateArray())
            {
                string? url = null;

                // 1) 优先 urlDefault
                if (img.TryGetProperty("urlDefault", out var ud) && ud.ValueKind == JsonValueKind.String)
                    url = ud.GetString();

                // 2) infoList 中优先 WB_DFT，再回退 WB_PRV
                if (string.IsNullOrWhiteSpace(url) &&
                    img.TryGetProperty("infoList", out var infoList) && infoList.ValueKind == JsonValueKind.Array)
                {
                    string? dft = null;
                    string? prv = null;
                    foreach (var info in infoList.EnumerateArray())
                    {
                        var scene = info.TryGetProperty("imageScene", out var sc) ? sc.GetString() : null;
                        var iu = info.TryGetProperty("url", out var uprop) ? uprop.GetString() : null;
                        if (string.IsNullOrWhiteSpace(iu)) continue;
                        if (string.Equals(scene, "WB_DFT", StringComparison.OrdinalIgnoreCase)) dft = iu;
                        if (string.Equals(scene, "WB_PRV", StringComparison.OrdinalIgnoreCase)) prv = iu;
                    }
                    url = dft ?? prv ?? url;
                }

                // 3) 回退 urlPre
                if (string.IsNullOrWhiteSpace(url) &&
                    img.TryGetProperty("urlPre", out var up) && up.ValueKind == JsonValueKind.String)
                    url = up.GetString();

                // 4) 再回退 url
                if (string.IsNullOrWhiteSpace(url) &&
                    img.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    url = u.GetString();

                // 5) 最后尝试 urls[]（若存在）
                if (string.IsNullOrWhiteSpace(url) &&
                    img.TryGetProperty("urls", out var urlsArr) &&
                    urlsArr.ValueKind == JsonValueKind.Array && urlsArr.GetArrayLength() > 0)
                {
                    foreach (var s in urlsArr.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            var candidate = s.GetString();
                            if (!string.IsNullOrWhiteSpace(candidate)) url = candidate;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(url) && IsImageUrl(url!))
                {
                    if (!result.ImageUrls.Contains(url!))
                        result.ImageUrls.Add(url!);

                    if (mode == ParseMode.CoverImage) return;
                }
            }
        }

        private static bool IsImageUrl(string s)
        {
            if (Regex.IsMatch(s, @"\.(jpg|jpeg|png|webp)(\?|$)", RegexOptions.IgnoreCase))
                return true;

            // 兼容 xhscdn 的图片 URL（常见为 ...jpg_3 / !nd_xxx_jpg_3 这类无"."扩展形式）
            if (s.Contains("xhscdn.com", StringComparison.OrdinalIgnoreCase) &&
                s.Contains("jpg", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        #endregion
    }
}