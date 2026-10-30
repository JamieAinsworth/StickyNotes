using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StickyNotes;

/// <summary>
/// Shows a note's image over the desktop: scroll to zoom, drag to pan, and click outside to return to the note.
/// One viewer is shared; opening another image reuses it.
/// </summary>
internal sealed class ImageViewerWindow : Window
{
    private const double MinScale = 0.02;
    private const double MaxScale = 32;
    private const double ZoomStep = 1.25;
    private const double ButtonZoomStep = 0.05;
    private const double DragZoomPerPixel = 0.0025;

    private const string Markup = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid.Resources>
                <Style x:Key="ToolButton" TargetType="Button">
                    <Setter Property="MinWidth" Value="34" />
                    <Setter Property="Height" Value="32" />
                    <Setter Property="Margin" Value="2,0" />
                    <Setter Property="Padding" Value="8,0" />
                    <Setter Property="Foreground" Value="#F0F0F0" />
                    <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
                    <Setter Property="FontSize" Value="14" />
                    <Setter Property="Focusable" Value="False" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <Border x:Name="Bg" Background="Transparent" CornerRadius="5" Padding="{TemplateBinding Padding}">
                                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="Bg" Property="Background" Value="#30FFFFFF" />
                                    </Trigger>
                                    <Trigger Property="IsPressed" Value="True">
                                        <Setter TargetName="Bg" Property="Background" Value="#50FFFFFF" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Grid.Resources>
            <Canvas x:Name="ShadowLayer" IsHitTestVisible="False">
                <Border x:Name="ImageShadow" Background="Black">
                    <Border.Effect>
                        <DropShadowEffect Color="Black" BlurRadius="28" ShadowDepth="8" Direction="270" Opacity="0.65" />
                    </Border.Effect>
                </Border>
            </Canvas>
            <Border x:Name="Surface" ClipToBounds="True" Background="Transparent" Cursor="Hand">
                <Grid>
                <Image x:Name="LoadingPreview" Stretch="Fill" HorizontalAlignment="Left" VerticalAlignment="Top"
                       IsHitTestVisible="False" Visibility="Collapsed" />
                <Image x:Name="Picture" Stretch="None" HorizontalAlignment="Left" VerticalAlignment="Top" />
                </Grid>
            </Border>
            <TextBlock x:Name="Missing" Text="This image can't be found." Foreground="#F0F0F0" FontSize="16"
                       Background="#FF2B2B2B" Padding="16" IsHitTestVisible="False"
                       HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed" />
                <Canvas>
                <Border x:Name="ImageBorder" BorderBrush="#E0FFFFFF" BorderThickness="1" IsHitTestVisible="False" />
            <Border x:Name="ViewerToolbar" Background="#F02B2B2B" Padding="8,6" CornerRadius="6"
                    HorizontalAlignment="Left" VerticalAlignment="Top">
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                    <Button x:Name="ZoomOutButton" Style="{StaticResource ToolButton}" Content="&#xE71F;" ToolTip="Zoom out (-)" />
                    <TextBlock x:Name="ZoomText" Width="56" Height="32" Padding="0,7" TextAlignment="Center" VerticalAlignment="Center"
                               Background="Transparent" Cursor="SizeWE" ToolTip="Drag left or right to adjust zoom"
                               AutomationProperties.Name="Zoom percentage"
                               Foreground="#F0F0F0" FontFamily="Segoe UI" FontSize="13" />
                    <Button x:Name="ZoomInButton" Style="{StaticResource ToolButton}" Content="&#xE8A3;" ToolTip="Zoom in (+)" />
                    <Button x:Name="FitButton" Style="{StaticResource ToolButton}" FontFamily="Segoe UI" FontSize="13"
                            Content="Fit" ToolTip="Fit to window (0)" />
                    <Button x:Name="ActualButton" Style="{StaticResource ToolButton}" FontFamily="Segoe UI" FontSize="13"
                            Content="1:1" ToolTip="Actual size (1)" />
                    <Rectangle Width="1" Margin="8,4" Fill="#50FFFFFF" />
                    <Button x:Name="CopyButton" Style="{StaticResource ToolButton}" Content="&#xE8C8;" ToolTip="Copy image (Ctrl+C)" />
                    <Button x:Name="SaveButton" Style="{StaticResource ToolButton}" Content="&#xE74E;" ToolTip="Save a copy… (Ctrl+S)" />
                    <Button x:Name="CloseButton" Style="{StaticResource ToolButton}" Content="&#xE711;" ToolTip="Close (Esc)" />
                </StackPanel>
            </Border>
            </Canvas>
        </Grid>
        """;

    private static ImageViewerWindow? _current;

    private readonly Grid _root;
    private readonly ScaleTransform _transitionScale = new();
    private readonly TranslateTransform _transitionTranslate = new();
    private readonly FrameworkElement _surface;
    private readonly Image _picture;
    private readonly GifPlayback _playback;
    private readonly Image _loadingPreview;
    private readonly FrameworkElement _missing;
    private readonly TextBlock _zoomText;
    private readonly Border _toolbar;
    private readonly Border _imageBorder;
    private readonly Canvas _shadowLayer;
    private readonly Border _imageShadow;
    private readonly ScaleTransform _toolbarScale = new();
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();

    private string? _name;
    private string? _pendingGif;
    private CancellationTokenSource? _gifLoadCancellation;
    private NoteWindow? _returnTo;
    private double _scale = 1;
    private bool _fitted = true;
    private Point? _panFrom;
    private double? _zoomDragStartX;
    private double _zoomDragStartScale;
    private bool _dismissing;
    private int _transitionVersion;
    private readonly DispatcherTimer _transitionTimer = new(DispatcherPriority.Render);
    private Action? _completeTransition;

    internal bool IsTransitioning { get; private set; }

    private ImageViewerWindow()
    {
        _transitionTimer.Tick += (_, _) => _completeTransition?.Invoke();
        Title = "Image - Sticky Notes";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Width = 1000;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = (Grid)XamlReader.Parse(Markup);
        _root = root;
        _root.RenderTransform = new TransformGroup { Children = { _transitionScale, _transitionTranslate } };
        Content = root;
        _surface = (FrameworkElement)root.FindName("Surface");
        _picture = (Image)root.FindName("Picture");
        _playback = new GifPlayback(_picture);
        _loadingPreview = (Image)root.FindName("LoadingPreview");
        _missing = (FrameworkElement)root.FindName("Missing");
        _zoomText = (TextBlock)root.FindName("ZoomText");
        _toolbar = (Border)root.FindName("ViewerToolbar");
        _imageBorder = (Border)root.FindName("ImageBorder");
        _shadowLayer = (Canvas)root.FindName("ShadowLayer");
        _imageShadow = (Border)root.FindName("ImageShadow");
        _toolbar.LayoutTransform = _toolbarScale;
        _picture.RenderTransform = new TransformGroup { Children = { _scaleTransform, _translateTransform } };
        _loadingPreview.RenderTransform = _picture.RenderTransform;
        XamlAnimatedGif.AnimationBehavior.AddLoadedHandler(_picture, (_, _) =>
        {
            if (_picture.Source is BitmapSource frame) UpdatePictureSize(frame);
            if (_fitted) FitToWindow();
            XamlAnimatedGif.AnimationBehavior.SetAutoStart(_picture, !IsTransitioning && !_dismissing);
            if (XamlAnimatedGif.AnimationBehavior.GetAnimator(_picture) is { } animator)
            {
                EventHandler? firstFrame = null;
                firstFrame = (_, _) =>
                {
                    animator.CurrentFrameChanged -= firstFrame;
                    if (ReferenceEquals(animator, XamlAnimatedGif.AnimationBehavior.GetAnimator(_picture)))
                        _loadingPreview.Visibility = Visibility.Collapsed;
                };
                animator.CurrentFrameChanged += firstFrame;
            }
        });
        XamlAnimatedGif.AnimationBehavior.AddErrorHandler(_picture, (_, _) =>
        {
            var preview = _loadingPreview.Source;
            StopGif();
            _picture.Source = preview;
            _loadingPreview.Visibility = Visibility.Collapsed;
        });

        ((Button)root.FindName("ZoomOutButton")).Click += (_, _) => ZoomAt(Centre, _scale - ButtonZoomStep);
        ((Button)root.FindName("ZoomInButton")).Click += (_, _) => ZoomAt(Centre, _scale + ButtonZoomStep);
        _zoomText.MouseLeftButtonDown += (_, e) =>
        {
            BeginZoomDrag(e.GetPosition(this).X);
            e.Handled = true;
        };
        _zoomText.MouseMove += (_, e) =>
        {
            if (_zoomDragStartX == null) return;
            DragZoomTo(e.GetPosition(this).X);
            e.Handled = true;
        };
        _zoomText.MouseLeftButtonUp += (_, e) =>
        {
            if (_zoomDragStartX == null) return;
            EndZoomDrag();
            e.Handled = true;
        };
        _zoomText.LostMouseCapture += (_, _) => EndZoomDrag();
        ((Button)root.FindName("FitButton")).Click += (_, _) => FitToWindow();
        ((Button)root.FindName("ActualButton")).Click += (_, _) => ZoomAt(Centre, 1);
        ((Button)root.FindName("CopyButton")).Click += (_, _) => Copy();
        ((Button)root.FindName("SaveButton")).Click += (_, _) => SaveCopy();
        ((Button)root.FindName("CloseButton")).Click += (_, _) => Dismiss();

        _surface.SizeChanged += (_, _) =>
        {
            if (_fitted) FitToWindow();
        };
        _surface.MouseWheel += Surface_MouseWheel;
        _surface.PreviewMouseDown += (_, e) =>
        {
            if (DismissIfOutsideImage(e.GetPosition(_surface))) e.Handled = true;
        };
        _surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
        _surface.MouseMove += Surface_MouseMove;
        _surface.MouseLeftButtonUp += (_, _) => EndPan();
        _surface.LostMouseCapture += (_, _) => EndPan();
        PreviewKeyDown += Viewer_PreviewKeyDown;
        Closed += (_, _) =>
        {
            _transitionVersion++;
            IsTransitioning = false;
            ClearTransitionAnimations();
            _root.CacheMode = null;
            EndPan();
            EndZoomDrag();
            StopGif();
            if (_current == this) _current = null;
            ReturnToNote();
        };
    }

    /// <summary>The open viewer, if any.</summary>
    internal static ImageViewerWindow? Current => _current;

    internal string? ImageName => _name;

    internal double Scale => _scale;

    internal bool IsFitted => _fitted;

    /// <summary>Opens (or reuses) the viewer on the screen of <paramref name="from"/>, showing <paramref name="name"/>.</summary>
    public static ImageViewerWindow Open(string name, NoteWindow? from)
    {
        var viewer = _current;
        if (viewer == null)
        {
            viewer = _current = new ImageViewerWindow();
        }

        viewer.EndPan();
        viewer.EndZoomDrag();
        viewer._returnTo = from;
        var screen = from != null
            ? System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(from).Handle)
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var bounds = screen.Bounds;
        var handle = new WindowInteropHelper(viewer).EnsureHandle();
        NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE);
        viewer.Load(name);
        if (!viewer.IsVisible) viewer.Show();
        viewer.UpdateLayout();
        viewer.FitToWindow();
        viewer._dismissing = false;
        viewer._root.IsHitTestVisible = true;
        viewer.AnimateTransition(closing: false);
        viewer.Activate();
        return viewer;
    }

    private void Load(string name)
    {
        StopGif();
        _name = name;
        bool isGif = name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        var bitmap = isGif ? ImageStore.Load(name) : ImageStore.LoadFull(name);
        _picture.Source = bitmap;
        _loadingPreview.Source = bitmap;
        _loadingPreview.Visibility = Visibility.Collapsed;
        _missing.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
        if (bitmap != null)
        {
            UpdatePictureSize(bitmap);
        }
        else
        {
            Title = "Image - Sticky Notes";
        }
        _pendingGif = bitmap != null && isGif ? name : null;
        UpdateLayout();
        FitToWindow();
    }

    private void UpdatePictureSize(BitmapSource bitmap)
    {
        _loadingPreview.Width = _picture.Width = bitmap.Width;
        _loadingPreview.Height = _picture.Height = bitmap.Height;
        Title = $"Image ({bitmap.PixelWidth} × {bitmap.PixelHeight}) - Sticky Notes";
    }

    private Point Centre => new(_surface.ActualWidth / 2, _surface.ActualHeight / 2);

    /// <summary>Fits the image within 80% of the display, never enlarging it past its actual size.</summary>
    internal void FitToWindow()
    {
        if ((_picture.Source ?? _loadingPreview.Source) is not BitmapSource bitmap || bitmap.Width <= 0 || bitmap.Height <= 0) return;
        double width = Math.Max(1, _surface.ActualWidth * 0.8);
        double height = Math.Max(1, _surface.ActualHeight * 0.8);
        _scale = Math.Min(1, Math.Min(width / bitmap.Width, height / bitmap.Height));
        _translateTransform.X = (_surface.ActualWidth - bitmap.Width * _scale) / 2;
        _translateTransform.Y = (_surface.ActualHeight - bitmap.Height * _scale) / 2;
        _fitted = true;
        ApplyScale();
    }

    /// <summary>Zooms to <paramref name="scale"/>, keeping the point under <paramref name="anchor"/> where it is.</summary>
    internal void ZoomAt(Point anchor, double scale)
    {
        if (_picture.Source == null && _loadingPreview.Source == null) return;
        scale = Math.Clamp(scale, MinScale, MaxScale);
        double ratio = scale / _scale;
        _translateTransform.X = anchor.X - (anchor.X - _translateTransform.X) * ratio;
        _translateTransform.Y = anchor.Y - (anchor.Y - _translateTransform.Y) * ratio;
        _scale = scale;
        _fitted = false;
        ApplyScale();
    }

    private void ApplyScale()
    {
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = _scale;
        // Smooth when shrinking, crisp pixels when zoomed right in.
        RenderOptions.SetBitmapScalingMode(_picture, _scale > 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _zoomText.Text = $"{_scale * 100:0}%";
        UpdateImageChrome();
    }

    private async Task StartGifAsync(string name, int version)
    {
        var cancellation = new CancellationTokenSource();
        _gifLoadCancellation = cancellation;
        try
        {
            var data = await ImageStore.LoadGifAsync(name, cancellation.Token);
            if (data == null || cancellation.IsCancellationRequested || version != _transitionVersion || _dismissing || !IsVisible) return;
            _loadingPreview.Visibility = Visibility.Visible;
            _playback.Start(data);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            if (ReferenceEquals(_gifLoadCancellation, cancellation)) _gifLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void StopGif()
    {
        _pendingGif = null;
        _gifLoadCancellation?.Cancel();
        _playback.Stop();
    }

    internal void BeginZoomDrag(double pointerX)
    {
        if (_dismissing || !_zoomText.CaptureMouse()) return;
        _zoomDragStartX = pointerX;
        _zoomDragStartScale = _scale;
    }

    internal void DragZoomTo(double pointerX)
    {
        if (_zoomDragStartX is not { } startX) return;
        ZoomAt(Centre, _zoomDragStartScale + (pointerX - startX) * DragZoomPerPixel);
    }

    internal void EndZoomDrag()
    {
        _zoomDragStartX = null;
        if (_zoomText.IsMouseCaptured) _zoomText.ReleaseMouseCapture();
    }

    private void UpdateImageChrome()
    {
        if (_picture.Source == null && _loadingPreview.Source == null)
        {
            _shadowLayer.Visibility = _imageBorder.Visibility = _toolbar.Visibility = Visibility.Collapsed;
            return;
        }
        _shadowLayer.Visibility = _imageBorder.Visibility = _toolbar.Visibility = Visibility.Visible;
        var bounds = _picture.RenderTransform.TransformBounds(new Rect(0, 0, _picture.Width, _picture.Height));
        _imageBorder.Width = bounds.Width + 2;
        _imageBorder.Height = bounds.Height + 2;
        Canvas.SetLeft(_imageBorder, bounds.Left - 1);
        Canvas.SetTop(_imageBorder, bounds.Top - 1);

        var frame = new Rect(bounds.Left - 1, bounds.Top - 1, bounds.Width + 2, bounds.Height + 2);
        _imageShadow.Width = frame.Width;
        _imageShadow.Height = frame.Height;
        Canvas.SetLeft(_imageShadow, frame.Left);
        Canvas.SetTop(_imageShadow, frame.Top);
        var shadowArea = frame;
        shadowArea.Inflate(48, 48);
        _shadowLayer.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(shadowArea), new RectangleGeometry(frame));

        _toolbar.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = _toolbar.Child.DesiredSize.Width + _toolbar.Padding.Left + _toolbar.Padding.Right;
        double height = _toolbar.Child.DesiredSize.Height + _toolbar.Padding.Top + _toolbar.Padding.Bottom;
        double scale = Math.Min(1, Math.Max(0.6, bounds.Width / width));
        scale = Math.Min(scale, Math.Max(0.01, (_surface.ActualWidth - 24) / width));
        _toolbarScale.ScaleX = _toolbarScale.ScaleY = scale;
        _toolbar.Width = width;
        _toolbar.Height = height;
        Canvas.SetLeft(_toolbar, Math.Clamp(bounds.Left + (bounds.Width - width * scale) / 2,
            12, Math.Max(12, _surface.ActualWidth - width * scale - 12)));
        Canvas.SetTop(_toolbar, Math.Min(bounds.Bottom + 10, Math.Max(12, _surface.ActualHeight - height * scale - 12)));
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(_surface), _scale * Math.Pow(ZoomStep, e.Delta / 120.0));
        e.Handled = true;
    }

    internal bool DismissIfOutsideImage(Point point)
    {
        var bounds = _picture.RenderTransform.TransformBounds(new Rect(_picture.RenderSize));
        if ((_picture.Source != null || _loadingPreview.Source != null) && bounds.Contains(point)) return false;
        Dismiss();
        return true;
    }

    private void Dismiss()
    {
        if (_dismissing) return;
        _dismissing = true;
        EndPan();
        EndZoomDrag();
        _gifLoadCancellation?.Cancel();
        XamlAnimatedGif.AnimationBehavior.GetAnimator(_picture)?.Pause();
        _root.IsHitTestVisible = false;
        AnimateTransition(closing: true);
    }

    private void AnimateTransition(bool closing)
    {
        int version = ++_transitionVersion;
        _root.CacheMode = new BitmapCache();
        var imageBounds = _picture.RenderTransform.TransformBounds(new Rect(_picture.RenderSize));
        double originScaleX = 0.94;
        double originScaleY = 0.94;
        double originX = _root.ActualWidth * 0.03;
        double originY = _root.ActualHeight * 0.03;
        if (_name != null && _returnTo is { IsDeleted: false, IsVisible: true } note
            && note.ImageBoundsOnScreen(_name) is { } source && imageBounds.Width > 0 && imageBounds.Height > 0)
        {
            var start = PointFromScreen(source.TopLeft);
            var end = PointFromScreen(source.BottomRight);
            originScaleX = (end.X - start.X) / imageBounds.Width;
            originScaleY = (end.Y - start.Y) / imageBounds.Height;
            originX = start.X - imageBounds.Left * originScaleX;
            originY = start.Y - imageBounds.Top * originScaleY;
        }
        double fromOpacity = closing ? _root.Opacity : 0;
        double fromScaleX = closing ? _transitionScale.ScaleX : originScaleX;
        double fromScaleY = closing ? _transitionScale.ScaleY : originScaleY;
        double fromX = closing ? _transitionTranslate.X : originX;
        double fromY = closing ? _transitionTranslate.Y : originY;
        double toOpacity = closing ? 0 : 1;
        double toScaleX = closing ? originScaleX : 1;
        double toScaleY = closing ? originScaleY : 1;
        double toX = closing ? originX : 0;
        double toY = closing ? originY : 0;
        ClearTransitionAnimations();
        _root.Opacity = fromOpacity;
        _transitionScale.ScaleX = fromScaleX;
        _transitionScale.ScaleY = fromScaleY;
        _transitionTranslate.X = fromX;
        _transitionTranslate.Y = fromY;
        var transitionTime = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 280 : 0);
        var duration = new Duration(transitionTime);
        var easing = new CubicEase { EasingMode = closing ? EasingMode.EaseIn : EasingMode.EaseOut };
        var opacity = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        Action complete = () =>
        {
            if (version != _transitionVersion || !IsTransitioning) return;
            _root.Opacity = toOpacity;
            _transitionScale.ScaleX = toScaleX;
            _transitionScale.ScaleY = toScaleY;
            _transitionTranslate.X = toX;
            _transitionTranslate.Y = toY;
            ClearTransitionAnimations();
            _root.CacheMode = null;
            IsTransitioning = false;
            if (closing) Close();
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (version != _transitionVersion || _dismissing || !IsVisible || _pendingGif is not { } gif) return;
                _pendingGif = null;
                _ = StartGifAsync(gif, version);
            }));
        };
        opacity.Completed += (_, _) => complete();
        _completeTransition = complete;
        IsTransitioning = true;
        _root.BeginAnimation(OpacityProperty, opacity);
        _transitionScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScaleX, toScaleX, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        _transitionScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScaleY, toScaleY, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        _transitionTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        _transitionTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, toY, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        _transitionTimer.Interval = transitionTime > TimeSpan.Zero ? transitionTime : TimeSpan.FromMilliseconds(1);
        _transitionTimer.Start();
    }

    private void ClearTransitionAnimations()
    {
        _transitionTimer.Stop();
        _completeTransition = null;
        _root.BeginAnimation(OpacityProperty, null);
        _transitionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _transitionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _transitionTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _transitionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_fitted) ZoomAt(e.GetPosition(_surface), 1);
            else FitToWindow();
            e.Handled = true;
            return;
        }
        _panFrom = e.GetPosition(_surface);
        _surface.CaptureMouse();
        _surface.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from) return;
        var point = e.GetPosition(_surface);
        _translateTransform.X += point.X - from.X;
        _translateTransform.Y += point.Y - from.Y;
        _panFrom = point;
        _fitted = false;
        UpdateImageChrome();
    }

    private void EndPan()
    {
        if (_panFrom == null) return;
        _panFrom = null;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        _surface.Cursor = Cursors.Hand;
    }

    private void Viewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                break;
            case Key.OemPlus or Key.Add:
                ZoomAt(Centre, _scale + ButtonZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAt(Centre, _scale - ButtonZoomStep);
                break;
            case Key.D0 or Key.NumPad0:
                FitToWindow();
                break;
            case Key.D1 or Key.NumPad1:
                ZoomAt(Centre, 1);
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.S when ctrl:
                SaveCopy();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Copy()
    {
        if (_name != null) ImageStore.CopyToClipboard(_name);
    }

    private void SaveCopy()
    {
        if (_name == null) return;
        var source = Path.Combine(ImageStore.Folder, _name);
        if (!File.Exists(source)) return;
        string extension = Path.GetExtension(_name);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "Image" + extension,
            DefaultExt = extension,
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} image|*{extension}",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The image couldn't be saved.\n\n{ex.Message}", "Sticky Notes",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Puts focus back on the note the image came from, without bringing the other notes forward.</summary>
    private void ReturnToNote()
    {
        var note = _returnTo;
        _returnTo = null;
        if (note == null || note.IsDeleted || !note.IsVisible || new WindowInteropHelper(note).Handle == IntPtr.Zero) return;
        note.ActivateNote();
    }
}
