using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StickyNotes;

/// <summary>
/// Shows a note's image full size: scroll to zoom (around the mouse), drag to pan, double-click to switch between
/// fitting the window and actual size. One viewer is shared; opening another image reuses it.
/// </summary>
internal sealed class ImageViewerWindow : Window
{
    private const double MinScale = 0.02;
    private const double MaxScale = 32;
    private const double ZoomStep = 1.25;

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
            <Grid.RowDefinitions>
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <Border x:Name="Surface" ClipToBounds="True" Background="Transparent" Cursor="Hand">
                <Image x:Name="Picture" Stretch="None" HorizontalAlignment="Left" VerticalAlignment="Top" />
            </Border>
            <TextBlock x:Name="Missing" Text="This image can't be found." Foreground="#C0C0C0" FontSize="16"
                       HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed" />
            <Border Grid.Row="1" Background="#FF2B2B2B" Padding="8,6">
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                    <Button x:Name="ZoomOutButton" Style="{StaticResource ToolButton}" Content="&#xE71F;" ToolTip="Zoom out (-)" />
                    <TextBlock x:Name="ZoomText" Width="56" TextAlignment="Center" VerticalAlignment="Center"
                               Foreground="#F0F0F0" FontFamily="Segoe UI" FontSize="13" />
                    <Button x:Name="ZoomInButton" Style="{StaticResource ToolButton}" Content="&#xE8A3;" ToolTip="Zoom in (+)" />
                    <Button x:Name="FitButton" Style="{StaticResource ToolButton}" FontFamily="Segoe UI" FontSize="13"
                            Content="Fit" ToolTip="Fit to window (0)" />
                    <Button x:Name="ActualButton" Style="{StaticResource ToolButton}" FontFamily="Segoe UI" FontSize="13"
                            Content="1:1" ToolTip="Actual size (1)" />
                    <Rectangle Width="1" Margin="8,4" Fill="#50FFFFFF" />
                    <Button x:Name="CopyButton" Style="{StaticResource ToolButton}" Content="&#xE8C8;" ToolTip="Copy image (Ctrl+C)" />
                    <Button x:Name="SaveButton" Style="{StaticResource ToolButton}" Content="&#xE74E;" ToolTip="Save a copy… (Ctrl+S)" />
                </StackPanel>
            </Border>
        </Grid>
        """;

    private static ImageViewerWindow? _current;

    private readonly FrameworkElement _surface;
    private readonly Image _picture;
    private readonly FrameworkElement _missing;
    private readonly TextBlock _zoomText;
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();

    private string? _name;
    private NoteWindow? _returnTo;
    private double _scale = 1;
    private bool _fitted = true;
    private Point? _panFrom;

    private ImageViewerWindow()
    {
        Title = "Image - Sticky Notes";
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Width = 1000;
        Height = 700;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = (Grid)XamlReader.Parse(Markup);
        Content = root;
        _surface = (FrameworkElement)root.FindName("Surface");
        _picture = (Image)root.FindName("Picture");
        _missing = (FrameworkElement)root.FindName("Missing");
        _zoomText = (TextBlock)root.FindName("ZoomText");
        _picture.RenderTransform = new TransformGroup { Children = { _scaleTransform, _translateTransform } };

        ((Button)root.FindName("ZoomOutButton")).Click += (_, _) => ZoomAt(Centre, _scale / ZoomStep);
        ((Button)root.FindName("ZoomInButton")).Click += (_, _) => ZoomAt(Centre, _scale * ZoomStep);
        ((Button)root.FindName("FitButton")).Click += (_, _) => FitToWindow();
        ((Button)root.FindName("ActualButton")).Click += (_, _) => ZoomAt(Centre, 1);
        ((Button)root.FindName("CopyButton")).Click += (_, _) => Copy();
        ((Button)root.FindName("SaveButton")).Click += (_, _) => SaveCopy();

        _surface.SizeChanged += (_, _) =>
        {
            if (_fitted) FitToWindow();
        };
        _surface.MouseWheel += Surface_MouseWheel;
        _surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
        _surface.MouseMove += Surface_MouseMove;
        _surface.MouseLeftButtonUp += (_, _) => EndPan();
        _surface.LostMouseCapture += (_, _) => EndPan();
        PreviewKeyDown += Viewer_PreviewKeyDown;
        Closing += (_, _) => ReturnToNote();
        Closed += (_, _) =>
        {
            if (_current == this) _current = null;
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
            if (from != null)
            {
                // Start on the note's screen; maximising then fills that screen.
                viewer.Left = from.Left + from.ActualWidth / 2 - viewer.Width / 2;
                viewer.Top = from.Top + from.ActualHeight / 2 - viewer.Height / 2;
                var area = SystemParameters.WorkArea;
                if (double.IsNaN(viewer.Left) || double.IsNaN(viewer.Top))
                {
                    viewer.Left = area.Left + (area.Width - viewer.Width) / 2;
                    viewer.Top = area.Top + (area.Height - viewer.Height) / 2;
                }
            }
            else
            {
                viewer.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            viewer.WindowState = WindowState.Maximized;
        }

        viewer._returnTo = from;
        viewer.Load(name);
        if (!viewer.IsVisible) viewer.Show();
        if (viewer.WindowState == WindowState.Minimized) viewer.WindowState = WindowState.Maximized;
        viewer.Activate();
        return viewer;
    }

    private void Load(string name)
    {
        _name = name;
        var bitmap = ImageStore.LoadFull(name);
        _picture.Source = bitmap;
        _missing.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
        if (bitmap != null)
        {
            _picture.Width = bitmap.Width;
            _picture.Height = bitmap.Height;
            Title = $"Image ({bitmap.PixelWidth} × {bitmap.PixelHeight}) - Sticky Notes";
        }
        else
        {
            Title = "Image - Sticky Notes";
        }
        UpdateLayout();
        FitToWindow();
    }

    private Point Centre => new(_surface.ActualWidth / 2, _surface.ActualHeight / 2);

    /// <summary>Shows the whole image, shrinking it if needed but never enlarging it past its actual size.</summary>
    internal void FitToWindow()
    {
        if (_picture.Source is not BitmapSource bitmap || bitmap.Width <= 0 || bitmap.Height <= 0) return;
        double width = Math.Max(1, _surface.ActualWidth - 24);
        double height = Math.Max(1, _surface.ActualHeight - 24);
        _scale = Math.Min(1, Math.Min(width / bitmap.Width, height / bitmap.Height));
        _translateTransform.X = (_surface.ActualWidth - bitmap.Width * _scale) / 2;
        _translateTransform.Y = (_surface.ActualHeight - bitmap.Height * _scale) / 2;
        _fitted = true;
        ApplyScale();
    }

    /// <summary>Zooms to <paramref name="scale"/>, keeping the point under <paramref name="anchor"/> where it is.</summary>
    internal void ZoomAt(Point anchor, double scale)
    {
        if (_picture.Source == null) return;
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
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(_surface), _scale * Math.Pow(ZoomStep, e.Delta / 120.0));
        e.Handled = true;
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
                Close();
                break;
            case Key.OemPlus or Key.Add:
                ZoomAt(Centre, _scale * ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAt(Centre, _scale / ZoomStep);
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
