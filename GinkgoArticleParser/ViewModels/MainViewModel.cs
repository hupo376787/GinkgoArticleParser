using CommunityToolkit.Mvvm.Messaging;
using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using GinkgoArticleParser.Services;
using System.Collections.Concurrent;
using System.IO; // 新增：显式使用 System.IO
using GinkgoArticleParser.Enums;


#if ANDROID
using Android.Content;
using Android.Provider;
#endif

namespace GinkgoArticleParser.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    ISqliteService _sqliteService;
    IArticleParserResolver _parserResolver;

    private readonly SemaphoreSlim _dbAccessSemaphore = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, bool> _downloadingUrls = new();
    private static readonly HttpClient httpClient = new HttpClient();
    private SettingsModel settings;

    public RelayCommand OnInfoTappedCommand { get; }

    public MainViewModel(ISqliteService sqliteService, IArticleParserResolver parserResolver, SettingsViewModel settingsVm)
    {
        _sqliteService = sqliteService;
        _parserResolver = parserResolver;

        OnInfoTappedCommand = new RelayCommand(OnInfoTapped);
        Init();

        // 订阅设置更新消息（设置页保存后即时同步）
        WeakReferenceMessenger.Default.Register<SettingsModel>(this, (_, msg) =>
        {
            if (msg != null && msg is SettingsModel)
            {
                settings = msg;
                Debug.WriteLine("Settings updated via messenger.");
            }
        });
    }

    [ObservableProperty]
    private string imageUrl;
    [ObservableProperty]
    private string url;
    [ObservableProperty]
    private bool isArticleChecked = true;
    [ObservableProperty]
    private bool isCoverChecked;
    [ObservableProperty]
    private double progressValue;
    [ObservableProperty]
    private string downloadPathHint;
    [ObservableProperty]
    private bool floatDownloadButtonVisible;

    private async void Init()
    {
        await _sqliteService.InitAsync();

        // 设置 User-Agent
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
        );

        //https://pixabay.com/api/?key=9812959-81284015e28af3ba27773114a&orientation=horizontal&category=nature&image_type=photo
        //从 Pixabay 获取一张随机图片
        string editorChoiceUrl = "https://pixabay.com/api/?key=9812959-81284015e28af3ba27773114a&category=nature&image_type=photo";
        //区分Windows\Mac: orientation=horizontal；Android\iOS: orientation=vertical
        //if (DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.MacCatalyst)
        editorChoiceUrl += "&orientation=horizontal";
        //else
        //    editorChoiceUrl += "&orientation=vertical";
        try
        {
            var response = await httpClient.GetStringAsync(editorChoiceUrl);
            if (response != null)
            {
                var jsonDoc = JsonDocument.Parse(response);
                var hits = jsonDoc.RootElement.GetProperty("hits");
                if (hits.GetArrayLength() > 0)
                {
                    //从hits数组中随机选择一张图片
                    var random = new Random();
                    var randomImage = hits[random.Next(hits.GetArrayLength())];
                    ImageUrl = randomImage.GetProperty("largeImageURL").GetString()!;
                }
            }
        }
        catch (Exception ex) { }
    }

    public async Task LoadAsync()
    {
        //软件启动的时候，读取数据库。
        //如果从设置页面回来，那么此时立马读取数据库，可能是旧值，因为sqlite是异步写入
        settings = (await _sqliteService.GetAllAsync<SettingsModel>()).FirstOrDefault()!;

        FloatDownloadButtonVisible = settings.IsFloatDownloadButtonVisible;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Download()
    {
        // 移动端启动下载时震动提示（仅 Android / iOS）
        if (settings.IsDownloadVibrate)
        {
            try
            {
                if (DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS)
                {
                    // 使用 Microsoft.Maui.Devices.Vibration
                    try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100)); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Vibration check error: {ex.Message}");
            }
        }

        // 简化：使用解析器的 CanHandle / Resolve 来判断来源
        // 预处理用户输入（可能包含中文或其它文字，需从混合文本中抽取 URL 并标准化）
        var currentUrl = StringHelper.ExtractAndNormalizeUrl(Url);
        if (string.IsNullOrEmpty(currentUrl))
        {
            await ToastHelper.ShowToast("请输入正确的文章链接");
            return;
        }
        // 使用解析器决定是否支持该链接（增强：拿到平台/ID/是否需要Cookie）
        var info = _parserResolver.ResolveInfo(currentUrl);
        if (!info.Supported || info.Parser is null)
        {
            await ToastHelper.ShowToast(info.Reason ?? "暂不支持该链接来源");
            return;
        }

        if (info.RequiresCookie && string.IsNullOrWhiteSpace(settings.WeiboComCookie) && info.Platform == Enums.PlatformsEnum.Weibo)
        {
            await ToastHelper.ShowToast("微博内容较完整需要设置 Cookie（设置页填写浏览器 Cookie）");
        }

        System.Diagnostics.Debug.WriteLine($"Parser: {info.Parser.GetType().Name}, Platform: {info.Platform}, ResourceId: {info.ResourceId}, Reason: {info.Reason}");
        var parser = info.Parser;

        // 若需要调试可记录解析器类型
        System.Diagnostics.Debug.WriteLine($"Using parser: {parser.GetType().FullName}");

        if (!_downloadingUrls.TryAdd(currentUrl, true))
        {
            await ToastHelper.ShowToast("该文章正在下载中，请稍候...");
            return;
        }

        var res = await DownloadTask(currentUrl);
        if (res.Item1)
        {
            if (settings.IsFinishedShowTitle)
                await ToastHelper.ShowToast($"下载完成({(res.Item2!.Length <= 12 ? res.Item2! : (res.Item2!.Substring(0, 8) + "..."))})");
            else
                await ToastHelper.ShowToast("下载完成");
        }

        // 下载完成或失败后移除
        _downloadingUrls.TryRemove(currentUrl, out _);
    }

    private async Task<(bool, string?)> DownloadTask(string currentUrl)
    {
        //检查数据库中是否存在
        await _dbAccessSemaphore.WaitAsync();
        try
        {
            var existingRecords = await _sqliteService.GetByUrlAsync<HistoryModel>(currentUrl.Trim());
            if (existingRecords != null)
            {
                await ToastHelper.ShowToast("已经下载过啦");
                return (false, null);
            }
        }
        finally
        {
            _dbAccessSemaphore.Release();
        }

        await ToastHelper.ShowToast("开始下载");
        ProgressValue = 0;
        //获取下载目录，Windows上是下载目录，安卓是Download目录
        var downloadPath = FolderHelper.GetDownloadFolder();

        var parser = _parserResolver.Resolve(currentUrl);
        if (parser is null)
        {
            await ToastHelper.ShowToast("暂不支持该链接来源");
            return (false, null);
        }

        var mode = IsArticleChecked ? ParseMode.ArticleImages : ParseMode.CoverImage;
        var parseResult = await parser.ParseAsync(currentUrl, mode, GetCookie(PlatformHelper.GetPlatform(currentUrl)));

        // 标题与图片/视频
        var ogTitle = parseResult.Title;
        var fTitle = parseResult.Title + "-" + parseResult.Author;
        var ogAuthor = parseResult.Author;
        var publishDateTime = parseResult.PublishDateTime;
        var pDateTime = DateTime.ParseExact(publishDateTime, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var imageUrls = parseResult.ImageUrls;
        var videoUrls = parseResult.VideoUrls;
        var platform = parseResult.Platform;
        if (imageUrls.Count == 0 && videoUrls.Count == 0)
        {
            await ToastHelper.ShowToast("未找到可下载的图片/视频");
            return (false, null);
        }

        //确认下载目录
        var folder = Path.Combine(downloadPath, "GinkgoArticleParser");
        if (settings.IsClassifyFolders)
            folder = Path.Combine(folder, platform.ToString());
        Directory.CreateDirectory(folder);

        // 统一进度
        var totalCount = imageUrls.Count + videoUrls.Count;
        var processed = 0;

        string safeTitle = "GinkgoArticleParser";

        //下载图片
        for (int i = 0; i < imageUrls.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(imageUrls[i])) continue;

            try
            {
                // 基础文件名（不含扩展名）
                safeTitle = FileHelper.SanitizeFileName(fTitle, maxLength: 200, fallback: "image");
                var baseName = settings.IsDateTimeStart
                    ? $"{publishDateTime}-{safeTitle}-{i + 1:#00}"
                    : $"{safeTitle}-{publishDateTime}-{i + 1:#00}";
                const string ext = ".jpeg";

#if ANDROID
                // MediaStore 目标：相对子目录（不要绝对路径）
                var relativeSubDir = Path.Combine("GinkgoArticleParser", platform.ToString()).Replace('\\', '/');

                // 生成“Windows 风格”的唯一文件名：xxx.jpeg / xxx(1).jpeg / xxx(2).jpeg ...
                var uniqueDisplayName = await GetUniqueMediaStoreFileNameAsync(relativeSubDir, baseName, ext);

                var bytes = await DownloadHelper.TryDownloadBytesAsync(imageUrls[i]);
                if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        var saved = await GinkgoArticleParser.Platforms.Android.MediaSaver
                            .SaveImageAsync(bytes, uniqueDisplayName, relativeSubDir, "image/jpeg");
                        if (!string.IsNullOrEmpty(saved))
                        {
                            Debug.WriteLine($"图片已保存至: {saved}");
                            DownloadPathHint = $"({processed + 1}/{totalCount})图片已保存至: Pictures/{relativeSubDir}/{uniqueDisplayName}";
                            // 设置保存后的媒体时间戳
                            TrySetSavedTimestamps(saved, pDateTime);
                        }
                        else
                        {
                            // 回退：应用目录，仍然使用唯一文件名
                            var uniquePath = GetUniqueFilePath(folder, baseName, ext);
                            await File.WriteAllBytesAsync(uniquePath, bytes);
                            Debug.WriteLine($"回退保存到: {uniquePath}");
                            DownloadPathHint = $"({processed + 1}/{totalCount})图片已保存至: {uniquePath}";
                            TrySetSavedTimestamps(uniquePath, pDateTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Android 保存失败，回退本地文件: {ex.Message}");
                        var uniquePath = GetUniqueFilePath(folder, baseName, ext);
                        await File.WriteAllBytesAsync(uniquePath, bytes);
                        DownloadPathHint = $"({processed + 1}/{totalCount})图片已保存至: {uniquePath}";
                        TrySetSavedTimestamps(uniquePath, pDateTime);
                    }
                }
                else
                {
                    // 内存下载失败，直接落盘应用目录，按唯一文件名
                    var uniquePath = GetUniqueFilePath(folder, baseName, ext);
                    var ok = await DownloadHelper.TryDownloadToFileAsync(imageUrls[i], uniquePath);
                    if (!ok) throw new IOException("下载失败");
                    DownloadPathHint = $"({processed + 1}/{totalCount})图片已保存至: {uniquePath}";
                    TrySetSavedTimestamps(uniquePath, pDateTime);
                }
#else
                // 非 Android：直接计算唯一路径后写入
                var uniquePath = GetUniqueFilePath(folder, baseName, ext);

                var ok = await DownloadHelper.TryDownloadToFileAsync(imageUrls[i], uniquePath);
                if (!ok) throw new IOException("下载失败");

                Debug.WriteLine($"图片已保存至: ({processed + 1}/{totalCount}): {uniquePath}");
                DownloadPathHint = $"({processed + 1}/{totalCount})图片已保存至: {uniquePath}";
                TrySetSavedTimestamps(uniquePath, pDateTime);
#endif
                processed++;
                ProgressValue = totalCount == 0 ? 1.0 : processed * 1.0 / totalCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"下载失败: {imageUrls[i]}，原因: {ex.Message}");
            }
        }

        // 下载视频（mp4）
        for (int i = 0; i < videoUrls.Count; i++)
        {
            var raw = videoUrls[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            bool isAudioTrack = raw.EndsWith("|audio", StringComparison.OrdinalIgnoreCase);
            var vurl = isAudioTrack ? raw[..^("|audio".Length)] : raw;

            try
            {
                safeTitle = FileHelper.SanitizeFileName(fTitle, maxLength: 120, fallback: isAudioTrack ? "audio" : "video");
                var baseName = settings.IsDateTimeStart
                    ? $"{publishDateTime}-{safeTitle}-{(isAudioTrack ? "A" : "V")}{i + 1:#00}"
                    : $"{safeTitle}-{publishDateTime}-{(isAudioTrack ? "A" : "V")}{i + 1:#00}";

                var ext = isAudioTrack ? ".m4a" :
                          vurl.Contains(".flv", StringComparison.OrdinalIgnoreCase) ? ".flv" :
                          vurl.Contains(".m4s", StringComparison.OrdinalIgnoreCase) ? ".m4s" : ".mp4";

                var uniquePath = GetUniqueFilePath(folder, baseName, ext);

                bool biliDomain = vurl.Contains("bilivideo", StringComparison.OrdinalIgnoreCase)
                                  || vurl.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)
                                  || vurl.Contains("upos-", StringComparison.OrdinalIgnoreCase);

                bool ok;
                List<string>? dlLog = null;

                if (biliDomain)
                {
                    var result = await BilibiliDownloadHelper.DownloadToFileAsync(
                        vurl,
                        uniquePath,
                        GetCookie(PlatformsEnum.Bilibili),
                        maxRetry: 3,
                        enableChunk: true,
                        chunkSize: 4 * 1024 * 1024);

                    ok = result.ISuccess;
                    dlLog = result.Log.ToList();

                    if (!ok && i == 0)
                    {
                        // 第一个视频失败：尝试重新解析一次（降级 qn 或重新签名）
                        Debug.WriteLine("[BiliRetry] First video failed, re-parse for fallback qn...");
                        var fallbackParse = await _parserResolver.Resolve(currentUrl)?.ParseAsync(currentUrl, mode, GetCookie(PlatformsEnum.Bilibili));
                        if (fallbackParse != null && fallbackParse.VideoUrls.Count > 0 && fallbackParse.VideoUrls[0] != raw)
                        {
                            // 简单再试一次新 URL
                            var newRaw = fallbackParse.VideoUrls.First(u => !u.EndsWith("|audio"));
                            var newExt = newRaw.Contains(".m4s") ? ".m4s" : ".mp4";
                            var newPath = GetUniqueFilePath(folder, baseName + "_retry", newExt);
                            var retryResult = await BilibiliDownloadHelper.DownloadToFileAsync(newRaw, newPath, GetCookie(PlatformsEnum.Bilibili));
                            ok = retryResult.ISuccess;
                            dlLog?.AddRange(retryResult.Log);
                            if (ok) uniquePath = newPath;
                        }
                    }
                }
                else
                {
                    ok = await DownloadHelper.TryDownloadToFileAsync(vurl, uniquePath);
                }

                if (!ok)
                {
                    Debug.WriteLine($"[BiliDownloadFail] url={vurl}");
                    if (dlLog != null) Debug.WriteLine(string.Join(" | ", dlLog));
                    continue;
                }

                if (dlLog != null)
                    Debug.WriteLine($"[BiliDownloadLog] {string.Join(" | ", dlLog)}");

                DownloadPathHint = $"({processed + 1}/{totalCount}){(isAudioTrack ? "音频" : "视频")}已保存: {uniquePath}";
                TrySetSavedTimestamps(uniquePath, pDateTime);
                processed++;
                ProgressValue = totalCount == 0 ? 1.0 : processed * 1.0 / totalCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"视频/音频下载异常: {vurl} => {ex.Message}");
            }
        }

        //插入数据库
        await _dbAccessSemaphore.WaitAsync();
        var history = new HistoryModel
        {
            Platform = platform,
            Url = currentUrl.Trim(),
            Title = ogTitle,
            Author = ogAuthor,
            Timespan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DowloadDateTime = DateTime.Now,
            PublishDateTime = pDateTime
        };
        if (IsArticleChecked)
            await _sqliteService.InsertAsync(history);
        _dbAccessSemaphore.Release(); // 释放锁

        //如果首页下载了新的数据，通知历史页面刷新
        WeakReferenceMessenger.Default.Send("NewArticleDownloaded");

        return (true, safeTitle);
    }

    private string GetCookie(PlatformsEnum plaform)
    {
        switch (plaform)
        {
            case PlatformsEnum.Weibo:
                return settings.WeiboComCookie;
            case PlatformsEnum.Bilibili:
                return settings.BilibiliCookie;

            default:
                return null;
        }
    }

    private async void OnInfoTapped()
    {
        //if (DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.MacCatalyst)
        //    await Shell.Current.DisplayAlert("Ginkgo", "背景图片来源Pixabay", "OK");
        //else
        await ToastHelper.ShowToast("背景图片来源Pixabay");
    }

    // 生成唯一文件路径：xxx.ext / xxx(1).ext / xxx(2).ext ...
    private static string GetUniqueFilePath(string directory, string baseName, string extension)
    {
        string Make(string name, int? index) =>
            Path.Combine(directory, index is null ? $"{name}{extension}" : $"{name}({index}){extension}");

        var candidate = Make(baseName, null);
        if (!File.Exists(candidate)) return candidate;

        int i = 1;
        while (true)
        {
            candidate = Make(baseName, i);
            if (!File.Exists(candidate)) return candidate;
            i++;
        }
    }

#if ANDROID
    // 查询 MediaStore，确保在 Pictures/relativeSubDir 下 DISPLAY_NAME 唂
    private static async Task<string> GetUniqueMediaStoreFileNameAsync(string relativeSubDir, string baseName, string extension, CancellationToken ct = default)
    {
        string Make(string name, int? index) =>
            index is null ? $"{name}{extension}" : $"{name}({index}){extension}";

        var resolver = Android.App.Application.Context.ContentResolver!;
        var collection = MediaStore.Images.Media.ExternalContentUri;

        // MediaStore 中的 RelativePath 需要以目录名（带斜杠）匹配，例如 "Pictures/GinkgoArticleParser/Weixin/"
        var targetRelative = System.IO.Path.Combine(Android.OS.Environment.DirectoryPictures, relativeSubDir).Replace('\\', '/') + "/@";

        bool Exists(string displayName)
        {
            using var cursor = resolver.Query(
                collection,
                new[] { MediaStore.IMediaColumns.DisplayName },
                $"{MediaStore.IMediaColumns.DisplayName}=? AND {MediaStore.IMediaColumns.RelativePath}=?",
                new[] { displayName, targetRelative },
                null
            );
            return cursor != null && cursor.MoveToFirst();
        }

        var candidate = Make(baseName, null);
        if (!Exists(candidate)) return candidate;

        int i = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            candidate = Make(baseName, i);
            if (!Exists(candidate)) return candidate;
            i++;
        }
    }

    // 查询 MediaStore，确保在 Movies/relativeSubDir 下视频 DISPLAY_NAME 唯一
    private static async Task<string> GetUniqueMediaStoreVideoFileNameAsync(string relativeSubDir, string baseName, string extension, CancellationToken ct = default)
    {
        string Make(string name, int? index) =>
            index is null ? $"{name}{extension}" : $"{name}({index}){extension}";

        var resolver = Android.App.Application.Context.ContentResolver!;
        var collection = MediaStore.Video.Media.ExternalContentUri;

        var targetRelative = System.IO.Path.Combine(Android.OS.Environment.DirectoryMovies, relativeSubDir).Replace('\\', '/') + "/@";

        bool Exists(string displayName)
        {
            using var cursor = resolver.Query(
                collection,
                new[] { MediaStore.IMediaColumns.DisplayName },
                $"{MediaStore.IMediaColumns.DisplayName}=? AND {MediaStore.IMediaColumns.RelativePath}=?",
                new[] { displayName, targetRelative },
                null
            );
            return cursor != null && cursor.MoveToFirst();
        }

        var candidate = Make(baseName, null);
        if (!Exists(candidate)) return candidate;

        int i = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            candidate = Make(baseName, i);
            if (!Exists(candidate)) return candidate;
            i++;
        }
    }
#endif

    // 新增：统一设置保存后的时间戳（创建/修改/访问）为文章发布时间
    private void TrySetSavedTimestamps(string location, DateTime when)
    {
        if (!settings.IsModifyFileDate)
            return;

        try
        {
#if ANDROID
            if (!string.IsNullOrWhiteSpace(location) &&
                location.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            {
                // MediaStore 条目：更新 DATE_MODIFIED（秒）和 DATE_TAKEN（毫秒）
                var ctx = Android.App.Application.Context;
                var resolver = ctx.ContentResolver!;
                var uri = Android.Net.Uri.Parse(location);

                var dto = new DateTimeOffset(when);
                var values = new Android.Content.ContentValues();
                // DateModified 单位：秒
                values.Put(Android.Provider.MediaStore.IMediaColumns.DateModified, dto.ToUnixTimeSeconds());
                try
                {
                    // 根据媒体类型选择列
                    if (uri.ToString().Contains("/video/", StringComparison.OrdinalIgnoreCase))
                        values.Put(Android.Provider.MediaStore.Video.VideoColumns.DateTaken, dto.ToUnixTimeMilliseconds());
                    else
                        values.Put(Android.Provider.MediaStore.Images.ImageColumns.DateTaken, dto.ToUnixTimeMilliseconds());
                }
                catch { /* 某些设备可能不支持该列 */ }

                resolver.Update(uri, values, null, null);
            }
            else
#endif
            {
                if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                {
                    File.SetCreationTime(location, when);
                    File.SetLastWriteTime(location, when);
                    File.SetLastAccessTime(location, when);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"设置文件时间失败: {location}, {ex.Message}");
        }
    }
}
