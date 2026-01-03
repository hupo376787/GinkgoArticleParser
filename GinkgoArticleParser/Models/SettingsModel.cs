using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace GinkgoArticleParser.Models;

public partial class SettingsModel : ObservableObject
{
    [ObservableProperty]
    [property: PrimaryKey, AutoIncrement]
    private int id;

    // 下载时震动提示
    [ObservableProperty]
    private bool isDownloadVibrate = true;

    // 下载否是修改文件日期
    [ObservableProperty]
    private bool isModifyFileDate = true;

    // 文件命名日期在前
    [ObservableProperty]
    private bool isDateTimeStart = true;

    // 按平台分类文件夹
    [ObservableProperty]
    private bool isClassifyFolders = true;

    // 显示悬浮下载按钮
    [ObservableProperty]
    private bool isFloatDownloadButtonVisible = true;

    // 完成后显示标题
    [ObservableProperty]
    private bool isFinishedShowTitle = true;

    // weibo.com Cookie
    [ObservableProperty]
    private string? weiboComCookie;

    // weibo.com Cookie
    [ObservableProperty]
    private string? bilibiliCookie;
}