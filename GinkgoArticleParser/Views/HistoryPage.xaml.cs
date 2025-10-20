namespace GinkgoArticleParser.Views;

public partial class HistoryPage : ContentPage
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        viewModel.Navigation = Navigation;
        Loaded += (s, e) =>
        {
            FetchData();
        };
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await FetchData();
    }

    private async Task FetchData()
    {
        if (BindingContext is HistoryViewModel vm)
        {
            if (vm.HistoryItems.Count == 0)
                await vm.Init();
        }
    }
}
