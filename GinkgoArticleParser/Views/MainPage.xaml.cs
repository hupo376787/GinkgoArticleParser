using Microsoft.Maui.Controls;
using System.Threading.Tasks;

namespace GinkgoArticleParser.Views;

public partial class MainPage : ContentPage
{
    double _fabStartTx, _fabStartTy;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();

        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is MainViewModel vm)
            await vm.LoadAsync(); // fire-and-forget 加载
    }

    void FloatingActionButton_PanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (FloatingActionButton?.Parent is not View parent)
            return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _fabStartTx = FloatingActionButton.TranslationX;
                _fabStartTy = FloatingActionButton.TranslationY;
                break;

            case GestureStatus.Running:
                UpdateFabTranslation(
                    _fabStartTx + e.TotalX,
                    _fabStartTy + e.TotalY);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                //SnapFabToNearestHorizontalEdge();
                break;
        }
    }

    void UpdateFabTranslation(double targetTx, double targetTy)
    {
        if (FloatingActionButton?.Parent is not View parent)
            return;

        // 以布局后的绝对位置为基准，限制 Translation 在父容器内
        var minTx = -FloatingActionButton.X;
        var maxTx = parent.Width - FloatingActionButton.Width - FloatingActionButton.X;
        var minTy = -FloatingActionButton.Y;
        var maxTy = parent.Height - FloatingActionButton.Height - FloatingActionButton.Y;

        targetTx = Math.Clamp(targetTx, minTx, maxTx);
        targetTy = Math.Clamp(targetTy, minTy, maxTy);

        FloatingActionButton.TranslationX = targetTx;
        FloatingActionButton.TranslationY = targetTy;
    }

    void SnapFabToNearestHorizontalEdge()
    {
        if (FloatingActionButton?.Parent is not View parent || parent.Width <= 0)
            return;

        var currentAbsX = FloatingActionButton.X + FloatingActionButton.TranslationX;
        var leftDist = currentAbsX;
        var rightDist = parent.Width - currentAbsX - FloatingActionButton.Width;

        var targetTx = (leftDist <= rightDist)
            ? -FloatingActionButton.X // 吸到左侧
            : parent.Width - FloatingActionButton.Width - FloatingActionButton.X; // 吸到右侧

        var targetTy = FloatingActionButton.TranslationY;

        // 平滑吸附动画
        _ = FloatingActionButton.TranslateTo(targetTx, targetTy, 120u, Easing.CubicOut);
    }
}
