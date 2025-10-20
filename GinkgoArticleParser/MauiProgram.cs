using GinkgoArticleParser.Services;
using Microsoft.Maui.LifecycleEvents;
using UraniumUI;

namespace GinkgoArticleParser;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureSyncfusionToolkit()
            .UseSentry(options =>
            {
                // TODO: Set the Sentry Dsn
                options.Dsn = "https://examplePublicKey@o0.ingest.sentry.io/0";
            })
#if DEBUG
            .UseDebugRainbows(new DebugRainbowsOptions { })
#endif
            .UseSkiaSharp()
            .UseMauiCommunityToolkit(options =>
            {
                options.SetShouldEnableSnackbarOnWindows(true);
            })
            .UseUraniumUI()
            .UseUraniumUIMaterial()
            .ConfigureLifecycleEvents(events =>
            {
#if ANDROID
                events.AddAndroid(android => android
                    .OnResume(activity =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            (Application.Current as App)?.OnAppResumed();
                        });
                    }));
#elif IOS
                events.AddiOS(ios => ios
                    .OnActivated(app =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            (Application.Current as App)?.OnAppResumed();
                        });
                    }));
#endif
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("FontAwesome6FreeBrands.otf", "FontAwesomeBrands");
                fonts.AddFont("FontAwesome6FreeRegular.otf", "FontAwesomeRegular");
                fonts.AddFont("FontAwesome6FreeSolid.otf", "FontAwesomeSolid");
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFontAwesomeIconFonts();
                fonts.AddMaterialSymbolsFonts();
            });
#if DEBUG
        builder.Logging.AddDebug();
#endif
        builder.Services.AddSingleton<MainViewModel>();

        builder.Services.AddSingleton<HistoryViewModel>();

        builder.Services.AddSingleton<SettingsViewModel>();

        builder.Services.AddSingleton<WebViewViewModel>();

        builder.Services.AddSingleton<IAccessibilityInfo>(AccessibilityInfo.Default);

        builder.Services.AddSingleton<ISqliteService, SqliteService>();

        builder.Services.AddSingleton(CalendarStore.Default);

        builder.Services.UsePageResolver();

        return builder.Build();
    }
}
