namespace GinkgoArticleParser;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        TemplateMAUI.TemplateMAUI.Init();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window;
#if WINDOWS
        window =  new Window(new AppWindowsShell());
#else
        window = new Window(new AppShell());
#endif

#if WINDOWS
        window.Activated += Window_Activated;
#endif

        return window;
    }

#if WINDOWS
    private async void Window_Activated(object sender, EventArgs e)
    {
        try
        {
            await PasteText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard error: {ex}");
        }
    }
#endif

    public async void OnAppResumed()
    {
        try
        {
            await PasteText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard error: {ex}");
        }
    }

    //全局共享的MainViewModel
    private MainViewModel MainVM =>
        Current?.Handler?.MauiContext?.Services?.GetService<MainViewModel>();

    private async Task PasteText()
    {
        await Task.Delay(300); // 等待系统稳定
        var text = await Clipboard.GetTextAsync();
        if (!string.IsNullOrEmpty(text) && Windows[0].Page is Shell shell)
        {
            MainVM.Url = text;
        }
    }

}
