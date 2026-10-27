using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace StickyNotes;

/// <summary>
/// Shown over an image while the mouse is on it: an outline, a toolbar (preview, copy, delete) and a corner grip
/// that resizes the image. Pictures inside an editable note can't take the mouse themselves, so this sits in the
/// editor's adorner layer instead.
/// </summary>
internal sealed class ImageOverlay : Adorner
{
    public const double MinImageWidth = 32;

    private const string Markup = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid.Resources>
                <Style x:Key="ToolButton" TargetType="Button">
                    <Setter Property="Width" Value="26" />
                    <Setter Property="Height" Value="26" />
                    <Setter Property="Margin" Value="1,0" />
                    <Setter Property="Foreground" Value="White" />
                    <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets" />
                    <Setter Property="FontSize" Value="13" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="Focusable" Value="False" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <Border x:Name="Bg" Background="Transparent" CornerRadius="4">
                                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="Bg" Property="Background" Value="#40FFFFFF" />
                                    </Trigger>
                                    <Trigger Property="IsPressed" Value="True">
                                        <Setter TargetName="Bg" Property="Background" Value="#70FFFFFF" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Grid.Resources>
            <Rectangle Stroke="#C0000000" StrokeThickness="1" IsHitTestVisible="False" />
            <Rectangle Stroke="#E0FFFFFF" StrokeThickness="1" Margin="1" StrokeDashArray="3 2" IsHitTestVisible="False" />
            <Border x:Name="Toolbar" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="4"
                    Background="#B0202020" CornerRadius="5" Padding="2">
                <StackPanel Orientation="Horizontal">
                    <Button x:Name="PreviewButton" Style="{StaticResource ToolButton}" Content="&#xE8A3;" ToolTip="Preview" />
                    <Button x:Name="CopyButton" Style="{StaticResource ToolButton}" Content="&#xE8C8;" ToolTip="Copy image" />
                    <Button x:Name="DeleteButton" Style="{StaticResource ToolButton}" Content="&#xE74D;" ToolTip="Remove image" />
                </StackPanel>
            </Border>
            <Border x:Name="Grip" Width="14" Height="14" HorizontalAlignment="Right" VerticalAlignment="Bottom"
                    Margin="0,0,-5,-5" Background="White" BorderBrush="#C0000000" BorderThickness="1" CornerRadius="3"
                    Cursor="SizeNWSE" ToolTip="Drag to resize" />
        </Grid>
        """;

    private readonly Grid _root;
    private readonly FrameworkElement _toolbar;
    private readonly FrameworkElement _grip;
    private double _dragStartX;
    private double _dragStartWidth;
    private double _lastWidth;

    public ImageOverlay(FrameworkElement image, Hyperlink link, string imageName) : base(image)
    {
        Link = link;
        ImageName = imageName;
        _root = (Grid)XamlReader.Parse(Markup);
        _toolbar = (FrameworkElement)_root.FindName("Toolbar");
        _grip = (FrameworkElement)_root.FindName("Grip");
        ((Button)_root.FindName("PreviewButton")).Click += (_, _) => PreviewRequested?.Invoke();
        ((Button)_root.FindName("CopyButton")).Click += (_, _) => CopyRequested?.Invoke();
        ((Button)_root.FindName("DeleteButton")).Click += (_, _) => DeleteRequested?.Invoke();
        _grip.MouseLeftButtonDown += Grip_MouseLeftButtonDown;
        _grip.MouseMove += Grip_MouseMove;
        _grip.MouseLeftButtonUp += Grip_MouseLeftButtonUp;
        _grip.LostMouseCapture += (_, _) => EndResize();
        AddVisualChild(_root);
        AddLogicalChild(_root);
    }

    public Hyperlink Link { get; }

    public string ImageName { get; }

    /// <summary>The widest the image may be made within the note.</summary>
    public double MaxImageWidth { get; set; } = double.PositiveInfinity;

    public bool IsResizing { get; private set; }

    public event Action? PreviewRequested;
    public event Action? CopyRequested;
    public event Action? DeleteRequested;

    /// <summary>The width while the grip is dragged.</summary>
    public event Action<double>? Resizing;

    /// <summary>The final width once the grip is released.</summary>
    public event Action<double>? Resized;

    /// <summary>True while the mouse is over the toolbar or grip, where clicks belong to the overlay.</summary>
    public bool IsMouseOverControls => _toolbar.IsMouseOver || _grip.IsMouseOver || IsResizing;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _root;

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(AdornedElement.RenderSize);
        // Hide the toolbar on tiny images rather than covering all of them.
        _toolbar.Visibility = AdornedElement.RenderSize.Width >= 100 && AdornedElement.RenderSize.Height >= 40
            ? Visibility.Visible
            : Visibility.Collapsed;
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(AdornedElement.RenderSize));
        return finalSize;
    }

    private double PointerX(MouseEventArgs e) => e.GetPosition((IInputElement?)VisualParent ?? this).X;

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartX = PointerX(e);
        _dragStartWidth = ((FrameworkElement)AdornedElement).ActualWidth;
        _lastWidth = _dragStartWidth;
        IsResizing = true;
        _grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!IsResizing) return;
        _lastWidth = WidthFor(e);
        Resizing?.Invoke(_lastWidth);
        e.Handled = true;
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsResizing) return;
        _lastWidth = WidthFor(e);
        EndResize();
        e.Handled = true;
    }

    private double WidthFor(MouseEventArgs e) =>
        Math.Clamp(_dragStartWidth + PointerX(e) - _dragStartX, MinImageWidth, Math.Max(MinImageWidth, MaxImageWidth));

    /// <summary>Finishes a resize, also when capture is lost (e.g. Alt+Tab mid-drag), so the size shown is kept.</summary>
    private void EndResize()
    {
        if (!IsResizing) return;
        IsResizing = false;
        if (_grip.IsMouseCaptured) _grip.ReleaseMouseCapture();
        Resized?.Invoke(_lastWidth);
    }
}
