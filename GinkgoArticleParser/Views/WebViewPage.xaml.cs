namespace GinkgoArticleParser.Views;

public partial class WebViewPage : ContentPage
{
    public WebViewPage(WebViewViewModel viewModel)
    {
        InitializeComponent();
#if WINDOWS
        webView.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
#elif ANDROID
        webView.UserAgent = "Mozilla/5.0 (Linux; Android 13; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
#elif IOS || MACCATALYST
        webView.UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
#endif
        BindingContext = viewModel;
    }
}