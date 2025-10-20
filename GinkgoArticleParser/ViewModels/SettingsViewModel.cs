using CommunityToolkit.Mvvm.Messaging;
using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Services;
using System.Threading.Tasks;

namespace GinkgoArticleParser.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    ISqliteService _sqliteService;
    public SettingsViewModel(ISqliteService sqliteService)
    {
        _sqliteService = sqliteService;
    }

    [RelayCommand]
    private async Task Import()
    {
        await _sqliteService.CloseConnectionAsync();

        bool success = await FileHelper.ImportDatabaseAsync(async () => await _sqliteService.CloseConnectionAsync());
        string res = success ? "导入成功" : "导入失败";
        await ToastHelper.ShowToast($"{res}");
        await _sqliteService.InitAsync();

        //通知历史页面刷新
        WeakReferenceMessenger.Default.Send("NewArticleDownloaded");
    }

    [RelayCommand]
    private async Task Export()
    {
        bool success = await FileHelper.ExportDatabaseAsync();
        string res = success ? "导出成功" : "导出失败";
        await ToastHelper.ShowToast($"{res}");
    }
}
