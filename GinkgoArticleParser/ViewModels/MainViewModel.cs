using CommunityToolkit.Mvvm.Messaging;
using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using GinkgoArticleParser.Services;
using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace GinkgoArticleParser.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    ISqliteService _sqliteService;
    private readonly SemaphoreSlim _dbAccessSemaphore = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, bool> _downloadingUrls = new();
    private static readonly HttpClient httpClient = new HttpClient();

    public RelayCommand OnInfoTappedCommand { get; }

    public MainViewModel(ISqliteService sqliteService)
    {
        _sqliteService = sqliteService;
        OnInfoTappedCommand = new RelayCommand(OnInfoTapped);
        Init();
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

    private async void Init()
    {
        await _sqliteService.InitAsync();

        // 设置 User-Agent
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
        );

        //https://pixabay.com/api/?key=9812959-81284015e28af3ba27773114a&editors_choice=true&orientation=horizontal&category=nature
        //从 Pixabay 获取一张随机图片
        string editorChoiceUrl = "https://pixabay.com/api/?key=9812959-81284015e28af3ba27773114a&editors_choice=true&category=nature";
        //区分Windows\Mac: orientation=horizontal；Android\iOS: orientation=vertical
        if (DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.MacCatalyst)
            editorChoiceUrl += "&orientation=horizontal";
        else
            editorChoiceUrl += "&orientation=vertical";
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
                    ImageUrl = randomImage.GetProperty("largeImageURL").GetString();
                }
            }
        }
        catch (Exception ex) { }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Download()
    {
        var currentUrl = Url;

        if (string.IsNullOrEmpty(currentUrl) || !currentUrl.StartsWith("https://mp.weixin.qq.com/"))
        {
            await ToastHelper.ShowToast("请输入正确的文章链接");
            return;
        }
        if (!_downloadingUrls.TryAdd(currentUrl, true))
        {
            await ToastHelper.ShowToast("该文章正在下载中，请稍候...");
            return;
        }

        var res = await DownloadTask(currentUrl);
        if (res)
            await ToastHelper.ShowToast("下载完成");

        // 下载完成或失败后移除
        _downloadingUrls.TryRemove(currentUrl, out _);
    }

    private async Task<bool> DownloadTask(string currentUrl)
    {
        //检查数据库中是否存在
        await _dbAccessSemaphore.WaitAsync();
        try
        {
            var existingRecords = await _sqliteService.GetByUrlAsync<HistoryModel>(currentUrl.Trim());
            if (existingRecords != null)
            {
                await ToastHelper.ShowToast("已经下载过啦");
                return false;
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

        string html;
        try
        {
            html = await httpClient.GetStringAsync(currentUrl);
        }
        catch (Exception ex)
        {
            await ToastHelper.ShowToast("下载失败：" + ex.Message);
            return false;

        }
        // 用 HtmlAgilityPack 解析
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 获取文章标题
        var ogTitleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
        string ogTitle = ogTitleNode?.GetAttributeValue("content", "") ?? "未找到 og:title";
        // 去除特殊字符，只保留中英文、数字和空格
        ogTitle = Regex.Replace(ogTitle, @"[^\u4e00-\u9fa5a-zA-Z0-9\s]", "");

        var body = doc.Text;
        //下载列表
        List<PicturePageInfoModel> list = new();

        if (IsArticleChecked)
        {
            // 用正则提取 picturePageInfoList 的值
            var match = Regex.Match(html, @"var\s+picturePageInfoList\s*=\s*""(.*?)"";", RegexOptions.Singleline);
            if (!match.Success)
            {
                match = Regex.Match(html, @"window\.picture_page_info_list\s*=\s*(\[.*?\])\.slice", RegexOptions.Singleline);
                if (!match.Success)
                {
                    await ToastHelper.ShowToast("未找到图片列表");
                    return false;
                }
                else
                {
                    var raw = match.Groups[1].Value;
                    // 提取 URL（匹配 http/https）
                    var matches = Regex.Matches(raw, @"https?://[^\s'\""]+");

                    foreach (Match m in matches)
                    {
                        if (m.Success)
                        {
                            list.Add(new PicturePageInfoModel
                            {
                                CdnUrl = m.Value
                            });
                        }
                    }
                }
            }
            else
            {
                var raw = match.Groups[1].Value;
                // 修正 JSON 格式
                raw = raw.Replace(",]", "]")
                         .Replace("'", "\"")
                         .Replace("\\x26amp;amp;", "&")
                         .Replace("\\x26amp;", "&");
                try
                {
                    list = JsonSerializer.Deserialize<List<PicturePageInfoModel>>(raw)!;
                }
                catch (Exception ex)
                {
                    await ToastHelper.ShowToast("图片列表解析失败：" + ex.Message);
                    return false;
                }
            }


        }
        else if (IsCoverChecked)
        {
            var ogImageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            string ogImage = ogImageNode?.GetAttributeValue("content", "") ?? "未找到 og:image";
            list.Add(new PicturePageInfoModel { CdnUrl = ogImage });
        }

        // 下载图片
        for (int i = 0; i < list.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(list[i].CdnUrl)) continue;

            try
            {
                // 用时间戳命名文件
                var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var fileName = $"{ogTitle}-{timestamp}.jpeg";
                var filePath = Path.Combine(downloadPath, fileName);

                using var imgResponse = await httpClient.GetAsync(list[i].CdnUrl);
                imgResponse.EnsureSuccessStatusCode();

                await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await imgResponse.Content.CopyToAsync(fs);

                Debug.WriteLine($"下载成功({i + 1}/{list.Count}): {filePath}");
                DownloadPathHint = $"({i + 1}/{list.Count})已保存到: {filePath}";
                ProgressValue = (i + 1) * 1.0 / list.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"下载失败: {list[i].CdnUrl}，原因: {ex.Message}");
            }
        }

        //插入数据库
        await _dbAccessSemaphore.WaitAsync();
        var history = new HistoryModel
        {
            Url = currentUrl.Trim(),
            Title = ogTitle,
            Timespan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        if (IsArticleChecked)
            await _sqliteService.InsertAsync(history);
        _dbAccessSemaphore.Release(); // 释放锁

        //如果首页下载了新的数据，通知历史页面刷新
        WeakReferenceMessenger.Default.Send("NewArticleDownloaded");

        return true;
    }

    private async void OnInfoTapped()
    {
        if (DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.MacCatalyst)
            await Shell.Current.DisplayAlert("Ginkgo", "背景图片来源Pixabay", "OK");
        else
            await ToastHelper.ShowToast("背景图片来源Pixabay");
    }
}
