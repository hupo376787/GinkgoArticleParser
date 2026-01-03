using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;

namespace GinkgoArticleParser.Helpers
{
    public class SimpleToastPopup : Popup
    {
        // 单实例控制，新的弹窗会先关闭旧的，避免叠加导致的残留
        static readonly object _guard = new();
        static WeakReference<SimpleToastPopup>? _current;

        int DefaultWidth = 320;
        int DefaultHeight = 96;
        int DefaultAutoCloseMs = 2000;

        readonly Frame _frame;
        readonly Grid _overlay;

        public SimpleToastPopup(string message, int? autoCloseMilliseconds = null)
        {
            // 关闭已显示的旧实例（避免重叠产生“白框残留”）
            lock (_guard)
            {
                if (_current != null && _current.TryGetTarget(out var prev) && prev != null)
                    prev.ForceClose();
                _current = new WeakReference<SimpleToastPopup>(this);
            }

            this.Closed += (_, __) =>
            {
                lock (_guard)
                {
                    if (_current != null && _current.TryGetTarget(out var me) && ReferenceEquals(me, this))
                        _current = null;
                }
            };

            var closeMs = autoCloseMilliseconds ?? DefaultAutoCloseMs;

            // 左侧 App Logo
            var logo = new Image
            {
                Source = "logo.png",
                WidthRequest = 28,
                HeightRequest = 28,
                Aspect = Aspect.AspectFit,
                Margin = new Thickness(12, 8, 8, 8)
            };

            // 右侧文字（允许多行）
            var label = new Label
            {
                Text = message,
                FontSize = 15,
                LineBreakMode = LineBreakMode.WordWrap,
                HorizontalTextAlignment = TextAlignment.Start,
                VerticalTextAlignment = TextAlignment.Center,
                MaxLines = 6,
                BackgroundColor = Colors.Transparent,
                Margin = new Thickness(0, 8, 12, 8),
                HorizontalOptions = LayoutOptions.Fill
            };
            // 文字颜色跟随系统主题：浅色用深灰，深色用白色
            label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1F2937"), Colors.White);

            // 内容布局：两列（左 Logo，右 文本）
            var contentGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 4,
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition(GridLength.Auto)
                },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            contentGrid.Add(logo, 0, 0);
            contentGrid.Add(label, 1, 0);

            // 容器卡片
            _frame = new Frame
            {
                CornerRadius = 14,
                Padding = 0,
                HasShadow = false, // 关闭阴影以避免不同平台残留
                Content = contentGrid,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            // 背景色跟随系统主题（带 80% 透明度）：浅色=白、深色=黑
            _frame.SetAppThemeColor(VisualElement.BackgroundColorProperty,
                                    Color.FromArgb("#CCFFFFFF"),
                                    Color.FromArgb("#CC000000"));

            // 明确设置透明 Shadow（部分平台会尊重该属性）
            try
            {
                _frame.Shadow = new Shadow
                {
                    Brush = new SolidColorBrush(Colors.Transparent),
                    Offset = new Point(0, 0),
                    Radius = 0,
                    Opacity = 0
                };
            }
            catch { }

            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) => ForceClose();
            _frame.GestureRecognizers.Add(tap);

            _overlay = new Grid
            {
                BackgroundColor = Colors.Transparent,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                InputTransparent = false
            };

            var center = new Grid
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };

            // 固定最大宽度，移除固定高度以便多行文本自然扩展
            _frame.WidthRequest = DefaultWidth;
            _frame.MinimumHeightRequest = 64;
            _frame.MaximumWidthRequest = DefaultWidth;
            _frame.HorizontalOptions = LayoutOptions.Center;

            center.Children.Add(_frame);
            _overlay.Children.Add(center);

            Content = _overlay;

            // 动画起始状态：对 overlay 做淡入淡出，确保整块视图消失
            _overlay.Opacity = 0;
            _frame.Scale = 0.96;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        _overlay.FadeTo(1, 180, Easing.CubicOut),
                        _frame.ScaleTo(1.02, 180, Easing.CubicOut)
                    );
                    await _frame.ScaleTo(1.0, 120, Easing.CubicIn);
                }
                catch { }

                if (closeMs > 0)
                {
                    try
                    {
                        await Task.Delay(closeMs);
                        await Task.WhenAll(
                            _overlay.FadeTo(0, 160, Easing.CubicIn),
                            _frame.ScaleTo(0.96, 160, Easing.CubicIn)
                        );
                    }
                    catch { }
                    finally
                    {
                        ForceClose();
                    }
                }
            });
        }

        // 对外暴露强制关闭（新弹窗出现时先关闭旧弹窗会调用）
        public void ForceClose()
        {
            try
            {
                // 优先尝试真正关闭 Popup（避免只是隐藏内容导致“白框残留”）
                if (TryCloseByReflection()) return;

                // 最后兜底：隐藏可视内容，释放引用，确保视觉上消失
                try
                {
                    if (_frame != null)
                    {
                        _frame.IsVisible = false;
                        _frame.Content = null;
                        _frame.Opacity = 0;
                    }
                    if (_overlay != null)
                    {
                        _overlay.IsVisible = false;
                        _overlay.InputTransparent = true;
                        _overlay.Opacity = 0;
                    }
                    this.Content = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SimpleToastPopup fallback hide error: {ex}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SimpleToastPopup ForceClose error: {ex}");
            }
        }

        // 兼容不同版本的 CommunityToolkit.Maui 的 Popup 关闭方法
        private bool TryCloseByReflection()
        {
            try
            {
                var type = this.GetType();
                while (type != null)
                {
                    var methodNames = new[] { "Close", "Dismiss", "Hide", "CloseAsync", "DismissAsync", "HideAsync" };
                    foreach (var name in methodNames)
                    {
                        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (method == null) continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length == 0)
                        {
                            var result = method.Invoke(this, null);
                            if (result is Task t) t.ContinueWith(_ => { });
                            return true;
                        }
                        else
                        {
                            method.Invoke(this, new object[] { null });
                            return true;
                        }
                    }
                    type = type.BaseType;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SimpleToastPopup reflection close error: {ex}");
            }
            return false;
        }
    }
}