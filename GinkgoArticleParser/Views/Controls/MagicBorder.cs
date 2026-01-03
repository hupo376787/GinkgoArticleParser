using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace GinkgoArticleParser.Views.Controls;

public class MagicBorder : Grid
{
    private readonly SKCanvasView _overlay;
    private readonly IDispatcherTimer _timer;

    // 防止递归重排产生 StackOverflow 的标志
    private bool _reordering;

    // 连续旋转进度（0~1）
    private double _progress;
    private DateTime _lastFrame = DateTime.UtcNow;

    public static readonly BindableProperty StrokeWidthProperty =
        BindableProperty.Create(nameof(StrokeWidth), typeof(float), typeof(MagicBorder), 2f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 0 = 自动胶囊（高度/2）
    public static readonly BindableProperty CornerRadiusProperty =
        BindableProperty.Create(nameof(CornerRadius), typeof(float), typeof(MagicBorder), 0f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 度/秒 -> 旋转圈速 = AnimationSpeed/360
    public static readonly BindableProperty AnimationSpeedProperty =
        BindableProperty.Create(nameof(AnimationSpeed), typeof(float), typeof(MagicBorder), 180f);

    public static readonly BindableProperty IsAnimatingProperty =
        BindableProperty.Create(nameof(IsAnimating), typeof(bool), typeof(MagicBorder), true,
            propertyChanged: (b, _, __) => ((MagicBorder)b).UpdateTimer());

    // 是否按子内容边界对齐（建议 true）
    public static readonly BindableProperty UseChildBoundsProperty =
        BindableProperty.Create(nameof(UseChildBounds), typeof(bool), typeof(MagicBorder), true,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 轨道的额外内缩（dp），最终内缩 = Stroke/2 + TrackInset
    public static readonly BindableProperty TrackInsetProperty =
        BindableProperty.Create(nameof(TrackInset), typeof(float), typeof(MagicBorder), 1f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 雪花尺寸（dp，头部会放大 1.5x）
    public static readonly BindableProperty SnowflakeSizeProperty =
        BindableProperty.Create(nameof(SnowflakeSize), typeof(float), typeof(MagicBorder), 4f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 拖尾点数量（包括头部）
    public static readonly BindableProperty TrailPointsProperty =
        BindableProperty.Create(nameof(TrailPoints), typeof(int), typeof(MagicBorder), 40,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 拖尾采样间距（相对于一圈周长的比例，0~1，越大拖尾越稀疏）
    public static readonly BindableProperty TrailSpacingRatioProperty =
        BindableProperty.Create(nameof(TrailSpacingRatio), typeof(double), typeof(MagicBorder), 0.0125,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 轨道淡淡的参考线（便于观感，0 关闭）
    public static readonly BindableProperty TrackOpacityProperty =
        BindableProperty.Create(nameof(TrackOpacity), typeof(double), typeof(MagicBorder), 0.15,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // —— 头部“星点簇/泪滴”参数 ——
    public static readonly BindableProperty HeadClusterCountProperty =
        BindableProperty.Create(nameof(HeadClusterCount), typeof(int), typeof(MagicBorder), 6,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // dp
    public static readonly BindableProperty HeadClusterRadiusProperty =
        BindableProperty.Create(nameof(HeadClusterRadius), typeof(float), typeof(MagicBorder), 6f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    // 泪滴拉伸因子（越大越长）
    public static readonly BindableProperty HeadStretchProperty =
        BindableProperty.Create(nameof(HeadStretch), typeof(float), typeof(MagicBorder), 3f,
            propertyChanged: (b, _, __) => ((MagicBorder)b).InvalidateCanvas());

    public float StrokeWidth { get => (float)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public float CornerRadius { get => (float)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public float AnimationSpeed { get => (float)GetValue(AnimationSpeedProperty); set => SetValue(AnimationSpeedProperty, value); }
    public bool IsAnimating { get => (bool)GetValue(IsAnimatingProperty); set => SetValue(IsAnimatingProperty, value); }
    public bool UseChildBounds { get => (bool)GetValue(UseChildBoundsProperty); set => SetValue(UseChildBoundsProperty, value); }
    public float TrackInset { get => (float)GetValue(TrackInsetProperty); set => SetValue(TrackInsetProperty, value); }
    public float SnowflakeSize { get => (float)GetValue(SnowflakeSizeProperty); set => SetValue(SnowflakeSizeProperty, value); }
    public int TrailPoints { get => (int)GetValue(TrailPointsProperty); set => SetValue(TrailPointsProperty, value); }
    public double TrailSpacingRatio { get => (double)GetValue(TrailSpacingRatioProperty); set => SetValue(TrailSpacingRatioProperty, value); }
    public double TrackOpacity { get => (double)GetValue(TrackOpacityProperty); set => SetValue(TrackOpacityProperty, value); }

    public int HeadClusterCount { get => (int)GetValue(HeadClusterCountProperty); set => SetValue(HeadClusterCountProperty, value); }
    public float HeadClusterRadius { get => (float)GetValue(HeadClusterRadiusProperty); set => SetValue(HeadClusterRadiusProperty, value); }
    public float HeadStretch { get => (float)GetValue(HeadStretchProperty); set => SetValue(HeadStretchProperty, value); }

    public MagicBorder()
    {
        _overlay = new SKCanvasView
        {
            IgnorePixelScaling = false,
            InputTransparent = true
        };
        _overlay.PaintSurface += OnPaintSurface;

        Children.Add(_overlay);
        _overlay.ZIndex = int.MaxValue;

        ChildAdded += (_, __) => EnsureOverlayOnTop();

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, __) => OnTick();
        _timer.Start();

        SizeChanged += (_, __) => InvalidateCanvas();

        // —— 新增：Loaded 时恢复订阅与动画 ——
        Loaded += (_, __) =>
        {
            // 防重复：先移再订
            _overlay.PaintSurface -= OnPaintSurface;
            _overlay.PaintSurface += OnPaintSurface;

            EnsureOverlayOnTop();
            _lastFrame = DateTime.UtcNow;
            UpdateTimer();           // 如果 IsAnimating=true，会重新 Start
            InvalidateCanvas();
        };

        Unloaded += (_, __) =>
        {
            _timer.Stop();
            _overlay.PaintSurface -= OnPaintSurface;
        };
    }

    private void EnsureOverlayOnTop()
    {
        if (_reordering) return;
        if (Children.Count == 0) return;
        if (Children[^1] == _overlay) return;

        _reordering = true;
        try
        {
            Children.Remove(_overlay);
            Children.Add(_overlay);
            _overlay.ZIndex = int.MaxValue;
        }
        finally
        {
            _reordering = false;
        }
    }

    private void OnTick()
    {
        if (!IsAnimating) return;

        var now = DateTime.UtcNow;
        var delta = now - _lastFrame;
        _lastFrame = now;

        // 度/秒 -> 圈/秒
        var revPerSec = AnimationSpeed / 360.0;
        _progress = (_progress + revPerSec * delta.TotalSeconds) % 1.0;

        _overlay.InvalidateSurface();
    }

    private void UpdateTimer()
    {
        if (IsAnimating)
        {
            if (!_timer.IsRunning)
            {
                _lastFrame = DateTime.UtcNow; // —— 新增：避免恢复后首帧突跳
                _timer.Start();
            }
        }
        else
        {
            if (_timer.IsRunning) _timer.Stop();
        }
    }

    private void InvalidateCanvas() => _overlay.InvalidateSurface();

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var w = e.Info.Width;
        var h = e.Info.Height;
        if (w <= 4 || h <= 4) return;

        var density = (float)DeviceDisplay.MainDisplayInfo.Density;
        float strokePx = Math.Max(1f, StrokeWidth * density);
        float trackInsetPx = Math.Max(0f, TrackInset * density);
        float snowPx = Math.Max(1f, SnowflakeSize * density);

        // 对齐到子内容的边界（如果有）
        var content = Children.FirstOrDefault(c => c != _overlay) as VisualElement;
        float originXpx = 0, originYpx = 0, contentWpx = w, contentHpx = h;

        if (UseChildBounds && content != null && content.Width > 0 && content.Height > 0)
        {
            originXpx = (float)content.X * density;
            originYpx = (float)content.Y * density;
            contentWpx = (float)content.Width * density;
            contentHpx = (float)content.Height * density;
        }

        // 轨道绘制矩形：内缩 = Stroke/2 + 额外内缩（保证贴边不被裁切）
        float inset = strokePx / 2f + trackInsetPx;
        var rect = SKRect.Create(
            originXpx + inset,
            originYpx + inset,
            Math.Max(0, contentWpx - inset * 2),
            Math.Max(0, contentHpx - inset * 2));

        if (rect.Width <= 0 || rect.Height <= 0) return;

        // 自动胶囊圆角
        float cornerPx = CornerRadius <= 0
            ? rect.Height / 2f
            : MathF.Max(0f, CornerRadius * density - trackInsetPx);

        // 仅支持胶囊/大圆角的流畅路径（cornerPx 不超过半高）
        cornerPx = Math.Min(cornerPx, rect.Height / 2f);

        // 轨道长度（胶囊：两段直线 + 两个半圆）
        float straightLen = Math.Max(0f, rect.Width - 2f * cornerPx);
        float arcLen = MathF.PI * cornerPx; // 半圆
        float totalLen = 2f * straightLen + 2f * arcLen;

        if (totalLen <= 0.01f) return;

        // 可选：画一条很淡的轨道参考线（便于观感）
        if (TrackOpacity > 0)
        {
            using var trackPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokePx,
                IsAntialias = true,
                Color = SKColors.White.WithAlpha((byte)(TrackOpacity * 255)),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var path = CreateCapsulePath(rect, cornerPx);
            canvas.DrawPath(path, trackPaint);
        }

        // 头部参数（沿路径的弧长位置）
        float headS = (float)(_progress * totalLen);
        var headPt = SampleCapsule(rect, cornerPx, straightLen, arcLen, totalLen, headS, out var headTangent);

        // 绘制拖尾（从 i=1 开始，头部单独绘制）
        int points = Math.Max(1, TrailPoints);
        double spacingRatio = Math.Clamp(TrailSpacingRatio, 0.001, 0.2);
        float ds = (float)(spacingRatio * totalLen);

        for (int i = 1; i < points; i++)
        {
            float si = headS - i * ds;
            while (si < 0) si += totalLen;

            var pt = SampleCapsule(rect, cornerPx, straightLen, arcLen, totalLen, si, out var _);

            // 尾部递减
            float t = i / (float)(points - 1 == 0 ? 1 : points - 1);
            float alpha = 1f - t;
            float size = snowPx * (1.1f - 0.8f * t);
            float glow = 5f * (1f - 0.6f * t) * density;

            DrawSnowflake(canvas, pt, size, alpha, glow);
        }

        // “彗星头/泪滴 + 星点簇”
        DrawCometHead(canvas, headPt, headTangent, snowPx * 1.5f, density, HeadClusterCount, HeadClusterRadius * density, HeadStretch);
    }

    // 生成胶囊的路径（用于淡轨道）
    private static SKPath CreateCapsulePath(SKRect rect, float r)
    {
        var path = new SKPath();

        if (r <= 0.01f)
        {
            path.AddRect(rect);
            return path;
        }

        r = Math.Min(r, Math.Min(rect.Height / 2f, rect.Width / 2f));

        var topLeft = new SKPoint(rect.Left + r, rect.Top);
        var topRight = new SKPoint(rect.Right - r, rect.Top);
        var bottomRight = new SKPoint(rect.Right - r, rect.Bottom);
        var bottomLeft = new SKPoint(rect.Left + r, rect.Bottom);

        path.MoveTo(topLeft);
        path.LineTo(topRight);
        path.ArcTo(r, r, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, bottomRight.X, bottomRight.Y);
        path.LineTo(bottomLeft);
        path.ArcTo(r, r, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, topLeft.X, topLeft.Y);
        path.Close();
        return path;
    }

    // 在胶囊路径上取点（si 为从 top line 起点按顺时针的弧长）
    private static SKPoint SampleCapsule(SKRect rect, float r, float straightLen, float arcLen, float totalLen, float si, out SKPoint tangent)
    {
        float L1 = straightLen;
        float L2 = L1 + arcLen;
        float L3 = L2 + straightLen;

        if (si < L1)
        {
            float x = rect.Left + r + si;
            float y = rect.Top;
            tangent = new SKPoint(1, 0);
            return new SKPoint(x, y);
        }
        else if (si < L2)
        {
            float s = si - L1;
            float theta = -90f + (s / arcLen) * 180f;
            float rad = theta * (float)Math.PI / 180f;
            float cx = rect.Right - r, cy = rect.Top + r;
            float x = cx + r * MathF.Cos(rad);
            float y = cy + r * MathF.Sin(rad);
            tangent = new SKPoint(-MathF.Sin(rad), MathF.Cos(rad));
            return new SKPoint(x, y);
        }
        else if (si < L3)
        {
            float s = si - L2;
            float x = rect.Right - r - s;
            float y = rect.Bottom;
            tangent = new SKPoint(-1, 0);
            return new SKPoint(x, y);
        }
        else
        {
            float s = si - L3;
            float theta = 90f + (s / arcLen) * 180f;
            float rad = theta * (float)Math.PI / 180f;
            float cx = rect.Left + r, cy = rect.Top + r;
            float x = cx + r * MathF.Cos(rad);
            float y = cy + r * MathF.Sin(rad);
            tangent = new SKPoint(-MathF.Sin(rad), MathF.Cos(rad));
            return new SKPoint(x, y);
        }
    }

    // “彗星头/泪滴 + 星点簇”
    private void DrawCometHead(SKCanvas canvas, SKPoint center, SKPoint tangent, float baseSizePx, float density, int clusterCount, float clusterRadiusPx, float stretchFactor)
    {
        float tanLen = MathF.Max(1e-3f, MathF.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y));
        float nx = tangent.X / tanLen;
        float ny = tangent.Y / tanLen;
        float angleDeg = (float)(Math.Atan2(ny, nx) * 180.0 / Math.PI);

        float bodyLen = MathF.Max(baseSizePx * stretchFactor, baseSizePx * 1.5f);
        float bodyThick = baseSizePx * 1.0f;
        var bodyRect = SKRect.Create(-bodyLen * 0.7f, -bodyThick * 0.5f, bodyLen, bodyThick);
        float bodyRadius = bodyThick * 0.5f;

        using var bodyGlow = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 140),
            ImageFilter = SKImageFilter.CreateBlur(8f * density, 8f * density, SKShaderTileMode.Clamp),
            BlendMode = SKBlendMode.Screen
        };

        using var bodyCore = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 220),
            ImageFilter = SKImageFilter.CreateBlur(2f * density, 2f * density, SKShaderTileMode.Clamp),
            BlendMode = SKBlendMode.Screen
        };

        canvas.Save();
        canvas.Translate(center);
        canvas.RotateDegrees(angleDeg);
        canvas.DrawRoundRect(bodyRect, bodyRadius, bodyRadius, bodyGlow);
        canvas.DrawRoundRect(bodyRect, bodyRadius, bodyRadius, bodyCore);
        canvas.DrawCircle(0, 0, baseSizePx * 0.7f, bodyCore);
        canvas.Restore();

        int n = Math.Max(0, clusterCount);
        for (int i = 0; i < n; i++)
        {
            float a = (i / (float)Math.Max(1, n)) * 2f * (float)Math.PI;
            float jitter = 0.5f + 0.5f * MathF.Sin((float)(_progress * 6.28318 * 2.0) + i * 1.7f);
            float r = clusterRadiusPx * (0.4f + 0.6f * jitter);
            var p = new SKPoint(center.X + r * MathF.Cos(a), center.Y + r * MathF.Sin(a));

            float alpha = 0.85f;
            float size = baseSizePx * (0.5f + 0.25f * (1f - i / MathF.Max(1f, n - 1f)));
            float glow = 4f * density;

            DrawSnowflake(canvas, p, size, alpha, glow);
        }
    }

    // 六向星 + 光晕
    private static void DrawSnowflake(SKCanvas canvas, SKPoint center, float size, float alpha01, float glowBlurPx)
    {
        byte a = (byte)(Math.Clamp(alpha01, 0f, 1f) * 255);

        using var glow = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, size * 0.9f),
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)(a * 0.8f)),
            ImageFilter = SKImageFilter.CreateBlur(glowBlurPx, glowBlurPx, SKShaderTileMode.Clamp),
            StrokeCap = SKStrokeCap.Round,
            BlendMode = SKBlendMode.Screen
        };

        using var pen = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, size * 0.45f),
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, a),
            StrokeCap = SKStrokeCap.Round,
            BlendMode = SKBlendMode.Screen
        };

        canvas.DrawPoint(center, glow);
        DrawStarLine(canvas, center, size, 0f, pen);
        DrawStarLine(canvas, center, size, 60f, pen);
        DrawStarLine(canvas, center, size, 120f, pen);
    }

    private static void DrawStarLine(SKCanvas canvas, SKPoint c, float size, float degrees, SKPaint pen)
    {
        float rad = (float)(Math.PI / 180.0 * degrees);
        float dx = size * MathF.Cos(rad);
        float dy = size * MathF.Sin(rad);
        canvas.DrawLine(c.X - dx, c.Y - dy, c.X + dx, c.Y + dy, pen);
    }
}