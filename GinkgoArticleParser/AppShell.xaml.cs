namespace GinkgoArticleParser;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // 监听导航事件
        //Navigated += OnShellNavigated;
    }

    private void OnShellNavigated(object sender, ShellNavigatedEventArgs e)
    {
        // 获取当前的页面 route
        var current = Shell.Current?.CurrentState?.Location?.ToString();
        current = current.Substring(2, current.Length - 2);

        // 判断当前是否是顶级页面（显示 TabBar）
        bool showTabBar = current.Split('/').Length == 1;

#if ANDROID || IOS
        // 动态隐藏 TabBar（通过修改 Shell 的 TabBar Handler）
        var shell = Shell.Current;
        var shellItems = shell?.Items;
        if (shellItems is not null)
        {
            foreach (var item in shellItems)
            {
                Shell.SetTabBarIsVisible(item, showTabBar);
            }
        }
#endif
    }
}
