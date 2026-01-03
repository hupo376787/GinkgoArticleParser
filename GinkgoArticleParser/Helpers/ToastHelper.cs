using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Extensions;

namespace GinkgoArticleParser.Helpers
{
    class ToastHelper
    {
        public static async Task ShowToast(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                // Windows 下原生 Toast 需要打包（或正确的 AppUserModelID/快捷方式），
                // 因此在 WinUI 下使用 Snackbar 作为回退（已在 MauiProgram 中启用）。
                if (DeviceInfo.Platform == DevicePlatform.WinUI)
                {
                    var page = Application.Current?.MainPage;
                    if (page != null)
                    {
                        var popup = new SimpleToastPopup(message);
                        page.ShowPopup(popup);
                        return;
                    }
                }
                else
                {
                    var toast = Toast.Make(message, ToastDuration.Short);
                    await toast.Show();
                }
            });
        }
    }
}
