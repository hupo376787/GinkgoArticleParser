using GinkgoArticleParser.ViewModels;

namespace GinkgoArticleParser.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is SettingsViewModel vm)
            _ = vm.LoadAsync(); // fire-and-forget 加载
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is SettingsViewModel vm)
            _ = vm.SaveAsync(); // fire-and-forget 保存
    }
}
