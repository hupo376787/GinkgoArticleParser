using CommunityToolkit.Maui.Alerts;

namespace GinkgoArticleParser.Helpers
{
    class ToastHelper
    {
        public static async Task ShowToast(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var toast = Toast.Make(message, CommunityToolkit.Maui.Core.ToastDuration.Short);
                await toast.Show();
            });
        }
    }
}
