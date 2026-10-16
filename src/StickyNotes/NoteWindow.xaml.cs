using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace StickyNotes;

public partial class NoteWindow : Window
{
    private const double ResizeBorder = 6;
    private const double CollapsedHeight = 32;

    private readonly NoteManager _manager;
    private IntPtr _hwnd;
    private bool _formatUpdateQueued;
    private double _uncollapsedHeight = NoteData.DefaultSize;
    private bool _isHoverExpanded;
    private bool _isManagerExpanded;
    private bool _isAlertFlashing;
    private bool _alertKeepOnTop;
    private AlertGlowWindow? _alertGlow;
    private readonly DispatcherTimer _hoverTimer;

    public NoteData Data { get; }

    /// <summary>True when the user deleted the note (as opposed to the window being torn down externally).</summary>
    public bool IsDeleted { get; set; }

    public NoteWindow(NoteManager manager, NoteData data)
    {
        InitializeComponent();
        _manager = manager;
        Data = data;

        Left = data.Left;
        Top = data.Top;
        Width = data.Width;

        _uncollapsedHeight = data.Height >= 120 ? data.Height : NoteData.DefaultSize;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += (_, _) => CollapseIfCursorLeft();

        LoadContent();
        ChecklistEditor.RefreshGlyphs(Editor.Document);
        ImageEditor.Hydrate(Editor);
        LinkEditor.Relink(Editor, includeCaret: true);
        // Loading isn't something to undo; without this Ctrl+Z could strip the pictures back out.
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
        ApplyColor(NoteColors.Get(data.Color));
        ApplyPinned(data.Pinned);
        ApplyAlwaysOnTop(data.AlwaysOnTop);
        ApplyCollapsed(data.IsCollapsed, saveSize: false);
        UpdateReminderButton();

        DataObject.AddPastingHandler(Editor, OnPaste);
        // A pasted address ends at the caret; link it straight away rather than waiting for the next space.
        DataObject.AddPastingHandler(Editor, (_, _) => _relinkIncludeCaret = true);
        Editor.AddHandler(PreviewDragOverEvent, new DragEventHandler(Editor_PreviewDragOver), true);
        Editor.AddHandler(PreviewDropEvent, new DragEventHandler(Editor_PreviewDrop), true);
        Editor.SizeChanged += (_, _) => ImageEditor.FitToWidth(Editor);
        Editor.MouseLeave += (_, _) =>
        {
            if (_imageOverlay is { IsResizing: false }) HideImageOverlay();
        };
        Editor.TextChanged += Editor_TextChanged;
        Editor.SelectionChanged += (_, _) => UpdateFormatButtons();
        // Formatting commands (buttons or Ctrl+B etc.) with no selection change nothing visible yet, so refresh after them too.
        Editor.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler((_, _) => QueueFormatButtonsUpdate()), true);

        LocationChanged += (_, _) =>
        {
            _alertGlow?.Follow();
            _manager.RequestSave();
        };
        SizeChanged += (_, _) =>
        {
            if (!Data.IsCollapsed && !_isHoverExpanded && !_isManagerExpanded)
            {
                if (ActualHeight >= 120)
                {
                    _uncollapsedHeight = ActualHeight;
                }
            }
            _alertGlow?.Follow();
            _manager.RequestSave();
        };
        Activated += (_, _) =>
        {
            if (!IsEffectivelyCollapsed) Toolbar.Visibility = Visibility.Visible;
            _alertGlow?.Follow();
        };
        Deactivated += (_, _) =>
        {
            Toolbar.Visibility = Visibility.Collapsed;
            RelinkNow(includeCaret: true);
            _alertKeepOnTop = false;
            ApplyZOrder();
            _manager.NoteDeactivated();
        };
        StateChanged += (_, _) =>
        {
            // Notes live on the desktop; never minimise/maximise them.
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };

        PreviewMouseDown += (_, _) => DismissReminderAlert();

        MouseLeave += NoteWindow_MouseLeave;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: no taskbar button and not listed in Alt+Tab.
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // Owning the note by the desktop window keeps it on the desktop layer and visible after "Show desktop" (Win+D).
        var progman = NativeMethods.GetDesktopProgman();
        if (progman != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, progman);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        ApplyZOrder();
    }

    /// <summary>Activates this note without bringing the other notes forward with it.</summary>
    public void ActivateNote()
    {
        _manager.BeginActivating(this);
        Activate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            _manager.BeginActivating(this);
        }
        else if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 && ShouldHoldZOrder(pos.hwndInsertAfter))
            {
                pos.flags |= NativeMethods.SWP_NOZORDER;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// True when this note is only being moved up because a sibling note is activating. Notes share the desktop
    /// window as owner, so Windows would otherwise raise all of them whenever one is clicked or created.
    /// </summary>
    internal bool ShouldHoldZOrder(IntPtr insertAfter)
    {
        var activating = _manager.ActivatingNote;
        if (activating == null || activating == this || IsActive || _manager.Raised || Topmost) return false;
        // Sending to the bottom or changing the topmost band is our own doing; only an upward pull is held back.
        return insertAfter != NativeMethods.HWND_BOTTOM
            && insertAfter != NativeMethods.HWND_TOPMOST
            && insertAfter != NativeMethods.HWND_NOTOPMOST;
    }

    /// <summary>Notes sit on the desktop layer unless raised by the tray/hotkey, set to stay on top, or alerting.</summary>
    public void ApplyZOrder()
    {
        bool top = _manager.Raised || Data.AlwaysOnTop || _isAlertFlashing || _alertKeepOnTop;

        // A note owned by the desktop is raised together with its siblings when clicked, so notes that
        // stay on top or alerting are left unowned to keep that from dragging the other notes forward.
        if (_hwnd != IntPtr.Zero)
        {
            var owner = (Data.AlwaysOnTop || _isAlertFlashing || _alertKeepOnTop) ? IntPtr.Zero : NativeMethods.GetDesktopProgman();
            if (NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT) != owner)
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, owner);
        }
        if (Topmost != top) Topmost = top;
        if (!top) SendToBottom();
    }

    private void SendToBottom()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// If the note's header is no longer on any connected monitor (a screen was unplugged or the display layout
    /// changed), moves it onto the main monitor. Notes are owned by the desktop window, so Windows doesn't do
    /// this for them. Works in physical pixels so it's correct on mixed-DPI setups.
    /// </summary>
    /// <param name="slot">Which rescued note this is, so several don't land on top of each other.</param>
    /// <returns>True if the note was moved.</returns>
    internal bool EnsureReachable(IReadOnlyList<PixelRect> monitors, PixelRect mainWorkArea, int slot)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;

        int headerHeight = (int)Math.Ceiling(CollapsedHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var header = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Top + headerHeight);
        if (ScreenPlacement.IsReachable(header, monitors)) return false;

        var (x, y) = ScreenPlacement.Rescue(rect.Right - rect.Left, rect.Bottom - rect.Top, mainWorkArea, slot);
        return NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Ignore Alt+F4 etc.; notes are removed with the delete button or hidden by exiting from the tray.
        if (!IsDeleted && !_manager.AllowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>The note's current content.</summary>
    internal FlowDocument EditorDocument => Editor.Document;

    public void FocusEditor()
    {
        Editor.Focus();
        Keyboard.Focus(Editor);
        Editor.CaretPosition = Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    public NoteData CaptureData()
    {
        if (!double.IsNaN(Left)) Data.Left = Left;
        if (!double.IsNaN(Top)) Data.Top = Top;
        if (!double.IsNaN(Width)) Data.Width = Width;
        Data.Height = _uncollapsedHeight >= 120 ? _uncollapsedHeight : NoteData.DefaultSize;

        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        Data.PlainText = range.Text.TrimEnd('\r', '\n');
        try
        {
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.Xaml);
            Data.Content = Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            Data.Content = string.Empty;
        }
        return Data;
    }

    private void LoadContent()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        if (!string.IsNullOrEmpty(Data.Content))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Data.Content));
                range.Load(ms, DataFormats.Xaml);
                return;
            }
            catch
            {
                // Fall back to plain text below.
            }
        }
        if (!string.IsNullOrEmpty(Data.PlainText)) range.Text = Data.PlainText;
    }

    // ---- Header actions ----

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Data.Pinned || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => _manager.CreateNote(this);

    private void Delete_Click(object sender, RoutedEventArgs e) => _manager.DeleteNote(this);

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        ApplyPinned(!Data.Pinned);
        _manager.RequestSave();
    }

    private void OnTop_Click(object sender, RoutedEventArgs e)
    {
        ApplyAlwaysOnTop(!Data.AlwaysOnTop);
        _manager.RequestSave();
    }

    private void ApplyAlwaysOnTop(bool onTop)
    {
        Data.AlwaysOnTop = onTop;
        OnTopButton.Tag = onTop ? "Active" : null;
        OnTopButton.ToolTip = onTop ? "Stop keeping above other windows" : "Keep above all windows";
        ApplyZOrder();
    }

    private void ApplyPinned(bool pinned)
    {
        Data.Pinned = pinned;
        PinButton.Content = pinned ? "\uE841" : "\uE718";
        PinButton.ToolTip = pinned ? "Unpin (allow moving and resizing)" : "Pin in place";
        Header.Cursor = pinned ? Cursors.Arrow : Cursors.SizeAll;
        ResizeMode = pinned ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            ResizeBorderThickness = new Thickness(pinned ? 0 : ResizeBorder),
        });
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColorButton, Placement = PlacementMode.Bottom };
        foreach (var color in NoteColors.All)
        {
            var item = new MenuItem
            {
                Header = color.Name,
                IsChecked = color.Name == Data.Color,
                Icon = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    Background = color.BodyBrush,
                    BorderBrush = color.HeaderBrush,
                    BorderThickness = new Thickness(2),
                },
            };
            item.Click += (_, _) =>
            {
                ApplyColor(color);
                _manager.RequestSave();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ApplyColor(NoteColor color)
    {
        Data.Color = color.Name;
        Background = color.BodyBrush;
        Header.Background = color.HeaderBrush;
    }

    // ---- Collapse & Reminders ----

    public bool IsEffectivelyCollapsed => Data.IsCollapsed && !_isHoverExpanded && !_isManagerExpanded;

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        ApplyCollapsed(!Data.IsCollapsed, saveSize: true);
        _manager.RequestSave();
    }

    private void ApplyCollapsed(bool collapsed, bool saveSize)
    {
        if (saveSize && !IsEffectivelyCollapsed)
        {
            _uncollapsedHeight = ActualHeight >= 120 ? ActualHeight : (Height >= 120 ? Height : NoteData.DefaultSize);
        }

        Data.IsCollapsed = collapsed;
        _isHoverExpanded = false;
        UpdateVisualCollapseState();
    }

    private void UpdateVisualCollapseState()
    {
        bool collapsed = IsEffectivelyCollapsed;

        CollapseButton.Content = Data.IsCollapsed ? "\uE70D" : "\uE70E";
        CollapseButton.ToolTip = Data.IsCollapsed ? "Expand note" : "Collapse note";

        if (collapsed)
        {
            Editor.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            MinHeight = CollapsedHeight;
            MaxHeight = CollapsedHeight;
            Height = CollapsedHeight;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            Editor.Visibility = Visibility.Visible;
            if (IsActive) Toolbar.Visibility = Visibility.Visible;
            MinHeight = 140;
            MaxHeight = double.PositiveInfinity;
            Height = _uncollapsedHeight >= 140 ? _uncollapsedHeight : NoteData.DefaultSize;
            ResizeMode = Data.Pinned ? ResizeMode.NoResize : ResizeMode.CanResize;
        }
    }

    private void Header_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Data.IsCollapsed && !_isHoverExpanded && !_isManagerExpanded)
        {
            _isHoverExpanded = true;
            UpdateVisualCollapseState();
            _hoverTimer.Start();
        }
    }

    private bool IsCursorOutside()
    {
        var pos = Mouse.GetPosition(this);
        return pos.X < 0 || pos.Y < 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight;
    }

    private void CollapseIfCursorLeft()
    {
        if (!_isHoverExpanded) { _hoverTimer.Stop(); return; }
        if (!Data.IsCollapsed || _isManagerExpanded || IsCursorOutside())
        {
            _hoverTimer.Stop();
            _isHoverExpanded = false;
            UpdateVisualCollapseState();
        }
    }

    // MouseLeave isn't raised reliably (e.g. when leaving through the resize border), so the timer is the safety net.
    private void NoteWindow_MouseLeave(object sender, MouseEventArgs e) => CollapseIfCursorLeft();

    public void OnManagerRaised()
    {
        if (Data.IsCollapsed)
        {
            _isManagerExpanded = true;
            UpdateVisualCollapseState();
        }
    }

    public void OnManagerLowered()
    {
        if (Data.IsCollapsed)
        {
            _isManagerExpanded = false;
            _isHoverExpanded = false;
            UpdateVisualCollapseState();
        }
    }

    private void Reminder_Click(object sender, RoutedEventArgs e)
    {
        var window = new RemindersWindow(Data, () =>
        {
            UpdateReminderButton();
            _manager.RequestSave();
        })
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void UpdateReminderButton()
    {
        bool hasActiveReminders = Data.Reminders.Any(r => r.IsRecurring || !r.HasFired);
        ReminderBellOutline.Visibility = hasActiveReminders ? Visibility.Collapsed : Visibility.Visible;
        ReminderBellFilled.Visibility = hasActiveReminders ? Visibility.Visible : Visibility.Collapsed;
        ReminderButton.ToolTip = hasActiveReminders ? "Reminders set" : "Set reminders";
    }

    public void TriggerReminderAlert()
    {
        _isAlertFlashing = true;
        _alertKeepOnTop = false;

        // A one-off reminder has just fired, so the bell may no longer have anything active.
        UpdateReminderButton();

        // Bring this specific note to the front, then place the glow just behind it
        ApplyZOrder();
        StartAlertGlow();
        ActivateNote();
        Focus();

        // Play chime sound
        try
        {
            SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    private void StartAlertGlow()
    {
        StopAlertGlow();
        _alertGlow = new AlertGlowWindow(this, NoteColors.Get(Data.Color).Header);
        _alertGlow.Show();
        _alertGlow.Follow();
    }

    private void StopAlertGlow()
    {
        _alertGlow?.Close();
        _alertGlow = null;
    }

    /// <summary>
    /// Clicking an alerting note stops the glow but keeps the note above other windows (like "keep above all
    /// windows") until it loses focus, after which it returns to the desktop.
    /// </summary>
    public void DismissReminderAlert()
    {
        if (_isAlertFlashing)
        {
            _isAlertFlashing = false;
            _alertKeepOnTop = true;
            StopAlertGlow();
            ApplyZOrder();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverTimer.Stop();
        StopAlertGlow();
        base.OnClosed(e);
    }

    // ---- Editor behaviour ----

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        _manager.RequestSave();
        if (_imageOverlay is { IsResizing: false }) HideImageOverlay();
        if (_relinking) return;
        if (e.UndoAction is UndoAction.Undo or UndoAction.Redo)
        {
            // Changing the document now would add a new undo step, wiping the redo history and trapping Ctrl+Z
            // (each undo would be redone straight away), so only refill images and reapply their sizes, which
            // aren't undoable changes.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                ImageEditor.Hydrate(Editor, repairOnly: true);
                ImageEditor.FitToWidth(Editor);
            });
            return;
        }
        _hydrateQueued = true;
        QueueRelink();

        // Text added to a ticked item should look done like the rest of it. Applied after this change completes.
        var edited = Editor.Selection.End.Paragraph;
        if (edited != null && ChecklistEditor.IsDone(edited))
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ChecklistEditor.EnsureDoneStyle(Editor, edited));
        QueueFormatButtonsUpdate();

        // Text was only inserted (not deleted) and the paragraph is now exactly a list marker + space.
        if (e.Changes.Count != 1) return;
        var change = e.Changes.First();
        if (change.AddedLength < 1 || change.RemovedLength != 0) return;

        var paragraph = Editor.Selection.End.Paragraph;
        if (paragraph == null) return;

        var text = ChecklistEditor.ParagraphText(paragraph);
        if (text is "[] " or "[ ] ")
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => StartChecklistFromShortcut(paragraph, text));
            return;
        }
        if (paragraph.Parent is ListItem) return;

        RoutedUICommand? command = text switch
        {
            "- " or "* " => EditingCommands.ToggleBullets,
            "1. " => EditingCommands.ToggleNumbering,
            _ => null,
        };
        if (command == null) return;

        // Don't modify the document from inside its own change notification.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => StartListFromShortcut(paragraph, text, command));
    }

    /// <summary>
    /// WPF drops formatting toggled on (e.g. Ctrl+B) before the first character typed into an empty paragraph,
    /// so insert that first character ourselves and apply the pending formatting to it.
    /// </summary>
    private void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (!Editor.Selection.IsEmpty) return;
        var paragraph = Editor.Selection.Start.GetInsertionPosition(LogicalDirection.Forward).Paragraph;
        bool isEmpty = paragraph != null
            ? paragraph.Inlines.All(i => i is Run r && r.Text.Length == 0)
            : new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Trim('\r', '\n').Length == 0;
        if (!isEmpty) return;

        var selection = Editor.Selection;
        var weight = selection.GetPropertyValue(TextElement.FontWeightProperty);
        var style = selection.GetPropertyValue(TextElement.FontStyleProperty);
        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        bool hasFormatting =
            (weight is FontWeight w && w != FontWeights.Normal) ||
            (style is FontStyle s && s != FontStyles.Normal) ||
            (decorations is TextDecorationCollection d && d.Count > 0);
        if (!hasFormatting) return;

        Editor.BeginChange();
        try
        {
            selection.Text = e.Text;
            if (weight != DependencyProperty.UnsetValue) selection.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
            if (style != DependencyProperty.UnsetValue) selection.ApplyPropertyValue(TextElement.FontStyleProperty, style);
            if (decorations != DependencyProperty.UnsetValue) selection.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations);
            selection.Select(selection.End, selection.End);
        }
        finally
        {
            Editor.EndChange();
        }
        e.Handled = true;
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        if (e.Key == Key.T && modifiers == ModifierKeys.Control)
        {
            ToggleStrikethrough();
            e.Handled = true;
        }
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ChecklistEditor.Toggle(Editor);
            UpdateFormatButtons();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && modifiers == ModifierKeys.None)
        {
            e.Handled = ChecklistEditor.TryBackspaceAtCheckbox(Editor);
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.None)
        {
            if (ChecklistEditor.TryExitEmptyItem(Editor) || TryExitListOnEmptyItem())
            {
                e.Handled = true;
            }
            else if (ChecklistEditor.CaretInChecklistItem(Editor))
            {
                // Let Enter split the item first, then give the new item its own checkbox.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ChecklistEditor.PrepareNewItem(Editor));
            }
        }
    }

    private void Checklist_Click(object sender, RoutedEventArgs e)
    {
        ChecklistEditor.Toggle(Editor);
        Editor.Focus();
        UpdateFormatButtons();
    }

    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the image toolbar or resize grip belong to them.
        if (_imageOverlay?.IsMouseOverControls == true) return;

        if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control
            && ImageEditor.ImageAt(Editor, e.GetPosition(Editor)) is { } image)
        {
            OpenImageViewer(image);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control
            && LinkEditor.LinkAt(Editor, e.GetPosition(Editor)) is { } link
            && LinkEditor.UriOf(link) is { } uri)
        {
            LinkEditor.Open(uri);
            e.Handled = true;
            return;
        }

        var paragraph = ChecklistEditor.ParagraphAtCheckbox(Editor, e.GetPosition(Editor));
        if (paragraph == null) return;

        ChecklistEditor.ToggleDone(Editor, paragraph);
        Editor.Focus();
        e.Handled = true;
    }

    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Editor);
        UpdateImageOverlay(point);
        if (_imageOverlay?.IsMouseOverControls == true)
        {
            Editor.ClearValue(CursorProperty);
            return;
        }
        bool overCheckbox = ChecklistEditor.ParagraphAtCheckbox(Editor, point) != null;
        bool overImage = _imageOverlay != null && ImageEditor.ImageAt(Editor, point) != null;
        bool overLink = Keyboard.Modifiers == ModifierKeys.Control && LinkEditor.LinkAt(Editor, point) != null;
        if (overCheckbox || overImage || overLink) Editor.Cursor = Cursors.Hand;
        else Editor.ClearValue(CursorProperty);
    }

    // ---- Images ----

    private ImageOverlay? _imageOverlay;

    internal ImageOverlay? ImageOverlay => _imageOverlay;

    /// <summary>Shows the outline, toolbar and resize grip on the image under the mouse, and hides them once it leaves.</summary>
    internal void UpdateImageOverlay(Point point)
    {
        if (_imageOverlay is { } current && (current.IsMouseOverControls || current.IsResizing)) return;

        var hit = ImageEditor.HitAt(Editor, point);
        if (hit != null && _imageOverlay != null && _imageOverlay.AdornedElement == hit.Content && _imageOverlay.Link == hit.Link)
            return;

        HideImageOverlay();
        if (hit == null || AdornerLayer.GetAdornerLayer(hit.Content) is not { } layer) return;

        var overlay = new ImageOverlay(hit.Content, hit.Link, hit.Name)
        {
            MaxImageWidth = MaxWidthFor(hit.Content),
        };
        overlay.PreviewRequested += () => OpenImageViewer(overlay.ImageName);
        overlay.CopyRequested += () => ImageStore.CopyToClipboard(overlay.ImageName);
        overlay.DeleteRequested += () =>
        {
            HideImageOverlay();
            ImageEditor.Remove(Editor, overlay.Link);
            Editor.Focus();
        };
        overlay.Resizing += width => ((FrameworkElement)overlay.AdornedElement).Width = width;
        overlay.Resized += width => CommitImageWidth(overlay, width);
        layer.Add(overlay);
        _imageOverlay = overlay;
    }

    internal void HideImageOverlay()
    {
        if (_imageOverlay == null) return;
        var overlay = _imageOverlay;
        _imageOverlay = null;
        AdornerLayer.GetAdornerLayer(overlay.AdornedElement)?.Remove(overlay);
    }

    /// <summary>The widest an image can be shown: its own size, and never wider than the note.</summary>
    private static double MaxWidthFor(FrameworkElement content)
    {
        double natural = ImageEditor.BitmapOf(content)?.Width ?? double.PositiveInfinity;
        double note = double.IsInfinity(content.MaxWidth) ? double.PositiveInfinity : content.MaxWidth;
        return Math.Min(natural, note);
    }

    /// <summary>Saves the width an image was dragged to; dragging it back to full size makes it fit the note again.</summary>
    private void CommitImageWidth(ImageOverlay overlay, double width)
    {
        double? chosen = width >= overlay.MaxImageWidth - 1 ? null : width;
        ImageEditor.SetWidth(Editor, overlay.Link, chosen);
        ImageEditor.FitToWidth(Editor);
    }

    internal void OpenImageViewer(string name)
    {
        HideImageOverlay();
        ImageViewerWindow.Open(name, this);
    }

    private bool _relinking;
    private bool _relinkQueued;
    private bool _relinkIncludeCaret;
    private bool _hydrateQueued;

    /// <summary>Updates the note's links and images once the current edit has finished.</summary>
    private void QueueRelink()
    {
        if (_relinkQueued) return;
        _relinkQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RelinkNow(_relinkIncludeCaret));
    }

    internal void RelinkNow(bool includeCaret)
    {
        _relinkQueued = false;
        _relinkIncludeCaret = false;
        bool hydrate = _hydrateQueued;
        _hydrateQueued = false;
        if (_relinking) return;
        _relinking = true;
        try
        {
            if (hydrate) ImageEditor.Hydrate(Editor);
            LinkEditor.Relink(Editor, includeCaret);
        }
        finally
        {
            _relinking = false;
        }
    }

    /// <summary>Puts images at the caret (replacing any selection) as one undoable change.</summary>
    internal void InsertImages(IEnumerable<string> names)
    {
        Editor.BeginChange();
        try
        {
            foreach (var name in names) ImageEditor.Insert(Editor, name);
        }
        finally
        {
            Editor.EndChange();
        }
        Editor.Focus();
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (ImageEditor.ImageFilesIn(e.Data).Count == 0) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = ImageEditor.ImageFilesIn(e.Data);
        if (files.Count == 0) return;
        e.Handled = true;

        var names = files.Select(ImageStore.Import).OfType<string>().ToList();
        if (names.Count == 0) return;
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: true);
        if (position != null) Editor.Selection.Select(position, position);
        Activate();
        InsertImages(names);
    }

    /// <summary>Typing "[] " turns the line into a checklist item, or adds a checkbox to a bullet or numbered item.</summary>
    private void StartChecklistFromShortcut(Paragraph paragraph, string expectedText)
    {
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        if (ChecklistEditor.ParagraphText(paragraph) != expectedText) return;

        Editor.BeginChange();
        try
        {
            range.Text = string.Empty;
            Editor.CaretPosition = paragraph.ContentStart;
            ChecklistEditor.Toggle(Editor);
        }
        finally
        {
            Editor.EndChange();
        }
    }

    /// <summary>Typing "- " or "* " starts a bulleted list, "1. " starts a numbered list.</summary>
    private void StartListFromShortcut(Paragraph paragraph, string expectedText, RoutedUICommand command)
    {
        if (paragraph.Parent is ListItem) return;
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        if (range.Text != expectedText) return;

        Editor.BeginChange();
        try
        {
            range.Text = string.Empty;
            Editor.CaretPosition = paragraph.ContentStart;
            command.Execute(null, Editor);
        }
        finally
        {
            Editor.EndChange();
        }
    }

    /// <summary>Pressing Enter on an empty list item ends the list, like most editors.</summary>
    private bool TryExitListOnEmptyItem()
    {
        if (!Editor.Selection.IsEmpty) return false;
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph?.Parent is not ListItem { Parent: List list }) return false;
        if (ChecklistEditor.ParagraphText(paragraph).Length != 0) return false;

        if (ChecklistEditor.IsChecklistList(list))
        {
            ChecklistEditor.ExitList(paragraph);
            Editor.CaretPosition = paragraph.ContentStart;
            return true;
        }

        var command = list.MarkerStyle == TextMarkerStyle.Decimal
            ? EditingCommands.ToggleNumbering
            : EditingCommands.ToggleBullets;
        command.Execute(null, Editor);
        return true;
    }

    private void Strikethrough_Click(object sender, RoutedEventArgs e) => ToggleStrikethrough();

    private void ToggleStrikethrough()
    {
        var selection = Editor.Selection;

        if (selection.IsEmpty)
        {
            // Nothing selected: switch strikethrough on or off for the text typed next.
            var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            bool hasStrike = HasDecoration(current, TextDecorationLocation.Strikethrough);
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, TextFormatting.WithStrikethrough(current, !hasStrike));
        }
        else
        {
            // Adjust each run separately so unrelated decorations (e.g. an underlined word) are kept.
            Editor.BeginChange();
            try
            {
                TextFormatting.SetStrikethrough(selection, !TextFormatting.IsFullyStruck(selection));
            }
            finally
            {
                Editor.EndChange();
            }
        }

        Editor.Focus();
        UpdateFormatButtons();
    }

    private void QueueFormatButtonsUpdate()
    {
        if (_formatUpdateQueued) return;
        _formatUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _formatUpdateQueued = false;
            UpdateFormatButtons();
        });
    }

    /// <summary>Highlights each formatting button whose style applies to the caret or the whole selection.</summary>
    private void UpdateFormatButtons()
    {
        var selection = Editor.Selection;

        SetActive(BoldButton, selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight &&
                              weight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight());
        SetActive(ItalicButton, selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style &&
                                style != FontStyles.Normal);

        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        SetActive(UnderlineButton, HasDecoration(decorations, TextDecorationLocation.Underline));
        SetActive(StrikeButton, HasDecoration(decorations, TextDecorationLocation.Strikethrough));

        var list = (selection.Start.Paragraph?.Parent as ListItem)?.List;
        bool numbered = list != null && list.MarkerStyle == TextMarkerStyle.Decimal;
        SetActive(BulletsButton, list != null && !numbered && !ChecklistEditor.IsChecklistList(list));
        SetActive(NumberingButton, numbered);
        SetActive(ChecklistButton, ChecklistEditor.SelectionHasCheckbox(Editor));
    }

    private static bool HasDecoration(TextDecorationCollection? decorations, TextDecorationLocation location) =>
        decorations != null && decorations.Any(d => d.Location == location);

    private static void SetActive(Button button, bool active) => button.Tag = active ? "Active" : null;

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;

        // Content copied from a note. If it has images, paste it ourselves so they come back in the same undo step.
        if (data.GetDataPresent(DataFormats.Xaml) || data.GetDataPresent(DataFormats.XamlPackage))
        {
            if (!e.IsDragDrop && data.GetData(DataFormats.Xaml) is string xaml && ImageEditor.UriPattern.IsMatch(xaml))
            {
                e.CancelCommand();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PasteNoteContent(xaml));
            }
            else if (data.GetDataPresent(DataFormats.Xaml))
            {
                // WPF's own image format would bring in a copy of the picture rather than the note's file.
                e.FormatToApply = DataFormats.Xaml;
            }
            return;
        }

        var files = ImageEditor.ImageFilesIn(data);
        if (files.Count > 0)
        {
            e.CancelCommand();
            var names = files.Select(ImageStore.Import).OfType<string>().ToList();
            if (names.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => InsertImages(names));
            return;
        }

        // Paste anything else as plain text so the note stays tidy.
        if (data.GetData(DataFormats.UnicodeText) is string text)
        {
            var plain = new DataObject();
            plain.SetData(DataFormats.UnicodeText, text);
            e.DataObject = plain;
            return;
        }

        e.CancelCommand();
        if (ImageEditor.ImageFrom(data) is { } bitmap)
        {
            string name;
            try
            {
                name = ImageStore.Save(bitmap);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => InsertImages(new[] { name }));
        }
    }

    private void PasteNoteContent(string xaml)
    {
        Editor.BeginChange();
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
            Editor.Selection.Load(stream, DataFormats.Xaml);
            ImageEditor.Hydrate(Editor);
            ChecklistEditor.RefreshGlyphs(Editor.Document);
            Editor.Selection.Select(Editor.Selection.End, Editor.Selection.End);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException
                                       or System.Windows.Markup.XamlParseException)
        {
            // Unreadable clipboard content; leave the note as it was.
        }
        finally
        {
            Editor.EndChange();
        }
    }
}
