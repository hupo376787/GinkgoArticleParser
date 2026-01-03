using CommunityToolkit.Mvvm.Messaging;
using GinkgoArticleParser.Helpers;
using GinkgoArticleParser.Models;
using GinkgoArticleParser.Services;

namespace GinkgoArticleParser.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private readonly ISqliteService _sqliteService;

    private CancellationTokenSource? _autoSaveCts;
    private INotifyPropertyChanged? _wiredModel; // 当前已订阅的 Settings

    public SettingsViewModel(ISqliteService sqliteService)
    {
        _sqliteService = sqliteService;
        // 确保绑定时不为 null，避免子属性路径首次绑定失败
        Settings = new SettingsModel();
        EnsureAutoSaveHook(); // 初始对象即重绑一次
    }

    [ObservableProperty]
    private SettingsModel settings;

    // 当 Settings 引用变化时（由 MVVM Toolkit 生成的局部方法）
    partial void OnSettingsChanged(SettingsModel value)
    {
        EnsureAutoSaveHook();
    }

    private void EnsureAutoSaveHook()
    {
        // 若已绑定同一实例则忽略
        if (ReferenceEquals(Settings, _wiredModel))
            return;

        // 解绑旧实例
        if (_wiredModel != null)
            _wiredModel.PropertyChanged -= OnSettingPropertyChanged;

        // 绑定新实例（要求 SettingsModel 实现 INotifyPropertyChanged）
        if (Settings is INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += OnSettingPropertyChanged;
            _wiredModel = npc;
        }
        else
        {
            _wiredModel = null;
        }
    }

    private void OnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Debug.WriteLine($"{e.PropertyName}值改变了");
        QueueSaveDebounced();
    }

    private void QueueSaveDebounced(int delayMs = 600)
    {
        _autoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await SaveAsync();
            }
            catch (TaskCanceledException) { }
        });
    }

    // 进入设置页时加载（若没有记录则用默认值）
    public async Task LoadAsync()
    {
        try
        {
            var all = await _sqliteService.GetAllAsync<SettingsModel>();
            Settings = all?.FirstOrDefault() ?? new SettingsModel();
        }
        catch
        {
            Settings = new SettingsModel();
        }

        EnsureAutoSaveHook();
    }

    // 离开设置页时保存（存在则更新，不存在则插入）
    public async Task SaveAsync()
    {
        try
        {
            if (Settings is null)
                return;

            if (Settings.Id == 0)
                await _sqliteService.InsertAsync(Settings);
            else
                await _sqliteService.UpdateAsync(Settings);

            // 通知其它页面（主页）更新
            WeakReferenceMessenger.Default.Send(Settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save settings failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        await _sqliteService.CloseConnectionAsync();

        bool success = await DatabaseHelper.ImportDatabaseAsync(async () => await _sqliteService.CloseConnectionAsync());
        //string res = success ? "导入成功" : "导入失败";
        //await ToastHelper.ShowToast($"{res}");
        await _sqliteService.InitAsync();

        //通知历史页面刷新
        WeakReferenceMessenger.Default.Send("NewArticleDownloaded");
    }

    [RelayCommand]
    private async Task Export()
    {
        bool success = await DatabaseHelper.ExportDatabaseAsync();
        //string res = success ? "导出成功" : "导出失败";
        //await ToastHelper.ShowToast($"{res}");
    }

    [RelayCommand]
    private async Task OpenDownloadFolder()
    {
        // 打开下载文件夹：Windows -> 资源管理器；Android -> 文件应用定位到 Pictures/GinkgoArticleParser
        var root = FolderHelper.GetDownloadFolder();
        var target = Path.Combine(root, "GinkgoArticleParser");
        try
        {
            if (!Directory.Exists(target))
                Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ensure folder failed: {ex.Message}");
        }

#if WINDOWS
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open Explorer failed: {ex.Message}");
        }
#endif
    }
}
