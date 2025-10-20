using CommunityToolkit.Mvvm.Messaging;
using GinkgoArticleParser.Models;
using GinkgoArticleParser.Services;
using System.Windows.Input;

namespace GinkgoArticleParser.ViewModels;

public partial class HistoryViewModel : BaseViewModel
{
    ISqliteService _sqliteService;
    public INavigation? Navigation { get; set; }

    [ObservableProperty]
    private ObservableCollection<HistoryModel> historyItems = new();
    [ObservableProperty]
    private bool isLoading = false;

    private int _currentPage = 1;
    private const int PageSize = 20;
    public ICommand LoadMoreAsyncCommand { get; }
    public ICommand HistoryItemTappedCommand { get; }

    public HistoryViewModel(ISqliteService sqliteService)
    {
        _sqliteService = sqliteService;

        LoadMoreAsyncCommand = new Command(async () => await LoadMoreAsync());
        HistoryItemTappedCommand = new Command<HistoryModel>(HistoryItemTapped);

        //注册消息接收，当有新文章下载完成时，刷新列表
        WeakReferenceMessenger.Default.Register<string>("NewArticleDownloaded", async (r, m) =>
        {
            await Init();
        });
    }

    public async Task Init()
    {
        _currentPage = 1;
        HistoryItems.Clear();

        //var list = await _sqliteService.GetAllAsync<HistoryModel>();
        //Debug.WriteLine($"数据库中共有 {list.Count} 条记录");

        await LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        if (IsLoading) return;

        // 避免重复加载第一页
        if (_currentPage > 1 && HistoryItems.Count == 0)
        {
            return;
        }

        Debug.WriteLine($"正在加载第{_currentPage}页");
        IsLoading = true;

        // 调用示例：
        var items = await _sqliteService.GetPageAsync<HistoryModel>(_currentPage, PageSize, x => x.Id, descending: true);
        if (items == null || items.Count == 0)
        {
            Debug.WriteLine("没有更多数据了");
            IsLoading = false;
            return;
        }
        foreach (var item in items)
            HistoryItems.Add(item);

        _currentPage++;
        IsLoading = false;
    }

    private async void HistoryItemTapped(HistoryModel item)
    {
        if (item == null) return;
        Debug.WriteLine($"点击了文章: {item.Title}, URL: {item.Url}");

        //跳转到WebView页面
        await Navigation!.PushAsync(new WebViewPage(new WebViewViewModel(item.Url)));
    }
}
