using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class NoteWindowTests
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Call(NoteWindow note, string method, params object?[] args) =>
        typeof(NoteWindow).GetMethod(method, NonPublic)!.Invoke(note, args);

    private static T Field<T>(NoteWindow note, string name) =>
        (T)typeof(NoteWindow).GetField(name, NonPublic)!.GetValue(note)!;

    private static void CollapseAndHover(NoteWindow note)
    {
        Call(note, "ApplyCollapsed", true, true);
        UiThread.Wait(50);
        Call(note, "Header_MouseEnter", null, null);
    }

    [Fact]
    public void Formatting_toolbar_is_outside_the_note_and_keeps_commands_targeted_at_the_editor()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.ActivateNote();
            note.FocusEditor();
            UiThread.Pump();
            note.UpdateLayout();

            var editor = ManagerScope.EditorOf(note);
            var popup = (Popup)note.FindName("FormattingPopup");
            var toolbar = (Border)note.FindName("Toolbar");
            Assert.True(popup.IsOpen);
            Assert.NotSame(PresentationSource.FromVisual(note), PresentationSource.FromVisual(toolbar));
            var popupSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(toolbar));
            Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(popupSource.Handle, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_NOACTIVATE);
            Assert.InRange(editor.ActualHeight, note.ActualHeight - 33, note.ActualHeight - 31);
            var bold = (Button)note.FindName("BoldButton");
            Assert.Same(editor, bold.CommandTarget);
            Assert.False(bold.Focusable);
            editor.SelectAll();
            ((RoutedCommand)bold.Command).Execute(null, bold.CommandTarget);
            Assert.Equal(FontWeights.Bold, editor.Selection.GetPropertyValue(TextElement.FontWeightProperty));
            Assert.True(note.IsActive);
            Assert.True(editor.IsKeyboardFocusWithin);

            var other = scope.CreateNote("other");
            other.ActivateNote();
            UiThread.Pump();
            Assert.False(popup.IsOpen);
            note.ActivateNote();
            UiThread.Pump();
            Assert.True(popup.IsOpen);

            Call(note, "ApplyCollapsed", true, true);
            Assert.False(popup.IsOpen);
            Call(note, "ApplyCollapsed", false, false);
            UiThread.Pump();
            Assert.True(popup.IsOpen);
            scope.Manager.DeleteNote(note);
            Assert.False(popup.IsOpen);
        });
    }

    [Theory]
    [InlineData("below", false, false)]
    [InlineData("right", true, false)]
    [InlineData("left", true, false)]
    [InlineData("above", false, false)]
    [InlineData("below", false, true)]
    [InlineData("right", true, true)]
    [InlineData("left", true, true)]
    [InlineData("above", false, true)]
    public void Formatting_toolbar_follows_the_note_and_fits_outside_the_visible_screen_edges(string side, bool vertical, bool reload)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.ActivateNote();
            note.FocusEditor();
            UiThread.Pump();
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(note));
            var area = PixelRect.From(System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea);
            var dpi = VisualTreeHelper.GetDpi(note);
            note.Width = side == "above" ? area.Width / dpi.DpiScaleX : 280;
            note.Height = 280;
            note.Left = side == "left" ? area.Right / dpi.DpiScaleX - note.Width : area.Left / dpi.DpiScaleX + (side == "above" ? 0 : 80);
            note.Top = side == "below" ? area.Top / dpi.DpiScaleY + 40 : area.Bottom / dpi.DpiScaleY - note.Height;
            UiThread.Pump();
            note.UpdateLayout();

            if (reload)
            {
                scope.Manager.SaveNow();
                scope.Manager.Reload();
                note = Assert.Single(scope.OpenNotes());
                note.ActivateNote();
                note.FocusEditor();
                UiThread.Pump();
                note.UpdateLayout();
                source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(note));
            }

            var popup = (Popup)note.FindName("FormattingPopup");
            var toolbar = (Border)note.FindName("Toolbar");
            PixelRect Bounds()
            {
                var origin = toolbar.PointToScreen(new Point());
                var toolbarDpi = VisualTreeHelper.GetDpi(toolbar);
                return new PixelRect((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                    (int)Math.Round(origin.X + toolbar.ActualWidth * toolbarDpi.DpiScaleX),
                    (int)Math.Round(origin.Y + toolbar.ActualHeight * toolbarDpi.DpiScaleY));
            }

            try
            {
                UiThread.WaitUntil(() =>
                {
                    if (!note.IsActive)
                    {
                        note.ActivateNote();
                        return false;
                    }
                    if (!popup.IsOpen || PresentationSource.FromVisual(toolbar) == null || toolbar.ActualWidth <= 0) return false;
                    var bounds = Bounds();
                    if (!NativeMethods.GetWindowRect(source.Handle, out var rect)) return false;
                    bool outside = side switch
                    {
                        "below" => bounds.Top >= rect.Bottom,
                        "right" => bounds.Left >= rect.Right,
                        "left" => bounds.Right <= rect.Left,
                        _ => bounds.Bottom <= rect.Top,
                    };
                    return outside && bounds.Left >= area.Left && bounds.Top >= area.Top
                        && bounds.Right <= area.Right && bounds.Bottom <= area.Bottom;
                });
            }
            catch (TimeoutException)
            {
                NativeMethods.GetWindowRect(source.Handle, out var rect);
                string popupState = PresentationSource.FromVisual(toolbar) == null ? "closed" : Bounds().ToString();
                Assert.Fail($"Expected {side}; popup {popupState}; active {note.IsActive}; note {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; work area {area}; orientation {((StackPanel)note.FindName("FormattingButtons")).Orientation}");
            }

            Assert.Equal(vertical ? Orientation.Vertical : Orientation.Horizontal,
                ((StackPanel)note.FindName("FormattingButtons")).Orientation);
            var buttons = ((StackPanel)note.FindName("FormattingButtons")).Children.OfType<Button>().ToList();
            Assert.Equal(7, buttons.Count);
            var popupSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(toolbar));
            Assert.True(NativeMethods.GetWindowRect(popupSource.Handle, out var popupBounds));
            foreach (var button in buttons)
            {
                Assert.True(button.IsVisible);
                var bounds = button.TransformToAncestor(toolbar).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0
                    && bounds.Right <= toolbar.ActualWidth && bounds.Bottom <= toolbar.ActualHeight,
                    $"{side}: {button.Name} bounds {bounds} outside toolbar {toolbar.ActualWidth} x {toolbar.ActualHeight}");
                var buttonTopLeft = button.PointToScreen(new Point());
                var buttonBottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                Assert.True(buttonTopLeft.X >= popupBounds.Left && buttonTopLeft.Y >= popupBounds.Top
                    && buttonBottomRight.X <= popupBounds.Right && buttonBottomRight.Y <= popupBounds.Bottom,
                    $"{side}: {button.Name} screen bounds {buttonTopLeft} to {buttonBottomRight} outside popup {popupBounds.Left},{popupBounds.Top},{popupBounds.Right},{popupBounds.Bottom}");
            }
            Assert.Equal(vertical, toolbar.ActualHeight > toolbar.ActualWidth);
            Assert.True(note.IsActive);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Formatting_toolbar_tracks_each_note_move_without_waiting_for_another_render(bool sideMounted)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(note));
            var area = PixelRect.From(System.Windows.Forms.Screen.FromHandle(source.Handle).WorkingArea);
            var dpi = VisualTreeHelper.GetDpi(note);
            int top = sideMounted ? area.Bottom - (int)Math.Ceiling(note.ActualHeight * dpi.DpiScaleY) - 4 : area.Top + 100;
            NativeMethods.SetWindowPos(source.Handle, IntPtr.Zero, area.Left + 100, top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            note.ActivateNote();
            note.FocusEditor();
            UiThread.Pump();
            var toolbar = (Border)note.FindName("Toolbar");
            UiThread.WaitUntil(() => PresentationSource.FromVisual(toolbar) != null);
            var popupSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(toolbar));
            Assert.True(NativeMethods.GetWindowRect(source.Handle, out var originalNote));
            Assert.True(NativeMethods.GetWindowRect(popupSource.Handle, out var originalToolbar));
            Assert.Equal(sideMounted ? Orientation.Vertical : Orientation.Horizontal,
                ((StackPanel)note.FindName("FormattingButtons")).Orientation);
            if (!sideMounted) Assert.Equal(originalNote.Left, originalToolbar.Left);

            foreach (var offset in new[] { (12, 8), (24, 16), (36, 24), (18, 12), (0, 0) })
            {
                Assert.True(NativeMethods.SetWindowPos(source.Handle, IntPtr.Zero,
                    originalNote.Left + offset.Item1, originalNote.Top + offset.Item2, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE));

                Assert.True(NativeMethods.GetWindowRect(popupSource.Handle, out var currentToolbar));
                Assert.Equal(originalToolbar.Left + offset.Item1, currentToolbar.Left);
                Assert.Equal(originalToolbar.Top + offset.Item2, currentToolbar.Top);
                Assert.True(note.IsActive);

                UiThread.Pump();
                Assert.True(NativeMethods.GetWindowRect(popupSource.Handle, out var settledToolbar));
                Assert.Equal(currentToolbar.Left, settledToolbar.Left);
                Assert.Equal(currentToolbar.Top, settledToolbar.Top);
            }
        });
    }

    [Fact]
    public void Tab_nests_a_bullet_under_the_previous_item()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var editor = ManagerScope.EditorOf(note);
            var parent = new Paragraph(new Run("Parent"));
            var child = new Paragraph(new Run("Child"));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc };
            list.ListItems.Add(new ListItem(parent));
            list.ListItems.Add(new ListItem(child));
            editor.Document.Blocks.Clear();
            editor.Document.Blocks.Add(list);
            editor.CaretPosition = child.ContentStart;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            editor.RaiseEvent(key);

            Assert.True(key.Handled);
            var nested = Assert.IsType<List>(Assert.IsType<ListItem>(child.Parent).Parent);
            Assert.NotSame(list, nested);
            Assert.Same(parent.Parent, nested.Parent);

            scope.Manager.SaveNow();
            scope.Manager.Reload();
            var reloaded = ManagerScope.EditorOf(Assert.Single(scope.OpenNotes()));
            var savedList = Assert.IsType<List>(Assert.Single(reloaded.Document.Blocks));
            var savedParent = Assert.Single(savedList.ListItems);
            var savedChildList = Assert.Single(savedParent.Blocks.OfType<List>());
            var savedChild = Assert.Single(Assert.Single(savedChildList.ListItems).Blocks.OfType<Paragraph>());
            Assert.Equal("Child", ChecklistEditor.ParagraphText(savedChild));
        });
    }

    [Fact]
    public void Enter_on_an_empty_sub_bullet_returns_to_the_parent_level()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var editor = ManagerScope.EditorOf(note);
            var parent = new Paragraph(new Run("Parent"));
            var child = new Paragraph();
            var list = new List(new ListItem(parent));
            list.ListItems.Add(new ListItem(child));
            editor.Document.Blocks.Clear();
            editor.Document.Blocks.Add(list);
            editor.CaretPosition = child.ContentStart;
            Assert.True(TextFormatting.ChangeListIndentation(editor, outdent: false));
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            editor.RaiseEvent(key);

            Assert.True(key.Handled);
            var item = Assert.IsType<ListItem>(editor.CaretPosition.Paragraph!.Parent);
            Assert.Same(list, item.Parent);
        });
    }

    [Theory]
    [InlineData(180, false)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    [InlineData(221, false)]
    [InlineData(280, false)]
    [InlineData(480, false)]
    public void Header_controls_fit_without_overlapping_at_every_note_width(double width, bool collapsed)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote(new string('W', 200));
            note.Width = width;
            Call(note, "ApplyCollapsed", collapsed, false);
            UiThread.Pump();
            note.UpdateLayout();

            var header = (FrameworkElement)note.FindName("Header");
            var collapse = (Button)note.FindName("CollapseButton");
            var title = (TextBlock)note.FindName("HeaderTitle");
            var menu = (Button)note.FindName("MenuButton");
            var collapsePosition = collapse.TranslatePoint(new Point(), header);
            var titlePosition = title.TranslatePoint(new Point(), header);
            var menuPosition = menu.TranslatePoint(new Point(), header);

            Assert.True(title.ActualWidth > 0);
            Assert.True(collapsePosition.X >= 0);
            Assert.True(collapsePosition.X + collapse.ActualWidth <= titlePosition.X);
            Assert.True(titlePosition.X + title.ActualWidth <= menuPosition.X);
            Assert.True(menuPosition.X + menu.ActualWidth <= header.ActualWidth);
            Assert.True(menuPosition.Y >= 0);
            Assert.True(menuPosition.Y + menu.ActualHeight <= header.ActualHeight);
            Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.Equal(collapsed, note.IsEffectivelyCollapsed);

            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var actions = (Border)note.FindName("HeaderActions");
            UiThread.WaitUntil(() => !actions.HasAnimatedProperties);
            note.UpdateLayout();
            var panel = Assert.IsType<StackPanel>(actions.Child);
            var buttons = panel.Children.OfType<Button>().ToList();
            Assert.Equal(6, buttons.Count);
            foreach (var button in buttons)
            {
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.ActualWidth > 0);
                var position = button.TranslatePoint(new Point(), header);
                Assert.True(position.X >= collapsePosition.X + collapse.ActualWidth,
                    $"{button.Name}: left {position.X}, collapse right {collapsePosition.X + collapse.ActualWidth}, title area {((FrameworkElement)note.FindName("HeaderTitleArea")).ActualWidth}");
                Assert.True(position.X + button.ActualWidth <= menuPosition.X,
                    $"{button.Name}: right {position.X + button.ActualWidth}, menu left {menuPosition.X}");
                Assert.InRange(button.ActualHeight, 25, 27);
            }
            Assert.Equal(0, title.Opacity);
        });
    }

    [Fact]
    public void Header_title_tracks_the_first_nonempty_line_and_is_blank_for_empty_notes()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var title = (TextBlock)note.FindName("HeaderTitle");
            Assert.Empty(title.Text);
            Assert.Empty(note.Title);

            ManagerScope.SetText(note, "\n   \n  Shopping list  \nMilk");
            Assert.Equal("Shopping list", title.Text);
            Assert.Equal(title.Text, note.Title);

            ManagerScope.SetText(note, "Updated title\nMore text");
            Assert.Equal("Updated title", title.Text);

            ManagerScope.SetText(note, "   \n");
            Assert.Empty(title.Text);
            Assert.Empty(note.Title);
        });
    }

    [Fact]
    public void Hovering_the_burger_reveals_header_actions_and_leaving_restores_the_title()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("Title underneath");
            note.Left = -5000;
            var menu = (Button)note.FindName("MenuButton");
            var actions = (Border)note.FindName("HeaderActions");
            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.Equal(SystemParameters.ClientAreaAnimation, actions.HasAnimatedProperties);
            UiThread.WaitUntil(() => !actions.HasAnimatedProperties);

            var title = (TextBlock)note.FindName("HeaderTitle");
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Equal(1, actions.Opacity);
            Assert.Equal(0, title.Opacity);
            Assert.Equal(0, ((TranslateTransform)actions.RenderTransform).X);
            Assert.False(actions.HasAnimatedProperties);
            Assert.Null(menu.ContextMenu);

            ((Border)note.FindName("Header")).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.MouseLeaveEvent,
            });
            Assert.Equal(SystemParameters.ClientAreaAnimation, actions.HasAnimatedProperties);
            UiThread.WaitUntil(() => !actions.HasAnimatedProperties);

            Assert.Equal(Visibility.Collapsed, actions.Visibility);
            Assert.Equal(1, title.Opacity);
            Assert.Equal(16, ((TranslateTransform)actions.RenderTransform).X);
            Assert.False(actions.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Header_action_animation_can_reverse_without_losing_the_title_or_buttons()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("Title");
            note.Left = -5000;
            var menu = (Button)note.FindName("MenuButton");
            var header = (Border)note.FindName("Header");
            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            UiThread.Wait(40);
            header.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            UiThread.Wait(40);
            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var actions = (Border)note.FindName("HeaderActions");
            UiThread.WaitUntil(() => !actions.HasAnimatedProperties);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Equal(1, actions.Opacity);
            Assert.Equal(0, ((TranslateTransform)actions.RenderTransform).X);
            Assert.Equal(0, ((TextBlock)note.FindName("HeaderTitle")).Opacity);
        });
    }

    [Fact]
    public void Inline_header_actions_update_state_and_allow_colour_popup_selection()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("Title");
            note.Left = -5000;
            var menu = (Button)note.FindName("MenuButton");
            menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var actions = (Border)note.FindName("HeaderActions");
            UiThread.WaitUntil(() => !actions.HasAnimatedProperties);
            var pin = (Button)note.FindName("HeaderPinButton");
            pin.Focus();
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(note.Data.Pinned);
            Assert.Equal("Active", pin.Tag);
            ((Button)note.FindName("HeaderOnTopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(note.Data.AlwaysOnTop);
            Assert.Equal("Active", ((Button)note.FindName("HeaderOnTopButton")).Tag);

            ((Button)note.FindName("HeaderColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var colours = (ContextMenu)note.FindName("HeaderColorMenu");
            Assert.True(colours.IsOpen);
            var header = (Border)note.FindName("Header");
            header.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            UiThread.Wait(250);
            Assert.Equal(Visibility.Visible, ((Border)note.FindName("HeaderActions")).Visibility);

            var green = colours.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Green"));
            green.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("Green", note.Data.Color);
            Assert.True(green.IsChecked);
            colours.IsOpen = false;
            UiThread.WaitUntil(() => actions.Visibility == Visibility.Collapsed);

            Assert.Equal(Visibility.Collapsed, ((Border)note.FindName("HeaderActions")).Visibility);
        });
    }

    [Fact]
    public void Clicking_the_burger_does_not_open_a_dropdown()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("Title");
            var menu = (Button)note.FindName("MenuButton");

            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Null(menu.ContextMenu);
            Assert.Null(note.FindName("NoteMenu"));
        });
    }

    [Fact]
    public void Inline_header_preserves_pin_topmost_colour_new_and_delete_actions()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");

            var pin = (Button)note.FindName("HeaderPinButton");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(note.Data.Pinned);
            Assert.Equal("Active", pin.Tag);
            Assert.Equal(ResizeMode.NoResize, note.ResizeMode);
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(note.Data.Pinned);
            Assert.Null(pin.Tag);

            var onTop = (Button)note.FindName("HeaderOnTopButton");
            onTop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(note.Data.AlwaysOnTop);
            Assert.Equal("Active", onTop.Tag);
            onTop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(note.Data.AlwaysOnTop);
            Assert.Null(onTop.Tag);

            ((Button)note.FindName("HeaderColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var colourMenu = (ContextMenu)note.FindName("HeaderColorMenu");
            Assert.True(colourMenu.IsOpen);
            var colours = colourMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(NoteColors.All.Length, colours.Count);
            var green = colours.Single(item => Equals(item.Header, "Green"));
            green.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("Green", note.Data.Color);
            Assert.Same(green, Assert.Single(colours, item => item.IsChecked));

            colourMenu.IsOpen = false;
            ((Button)note.FindName("HeaderNewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, scope.OpenNotes().Count);
            ((Button)note.FindName("HeaderDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(scope.OpenNotes());
            Assert.True(note.IsDeleted);
        });
    }

    [Fact]
    public void A_hover_preview_stays_expanded_until_its_colour_picker_closes()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            CollapseAndHover(note);
            ((Button)note.FindName("HeaderColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            note.Left = -5000;

            Call(note, "CollapseIfCursorLeft");
            Assert.False(note.IsEffectivelyCollapsed);
            Assert.True(note.Topmost);

            ((ContextMenu)note.FindName("HeaderColorMenu")).IsOpen = false;
            UiThread.Wait(450);
            Assert.True(note.IsEffectivelyCollapsed);
            Assert.False(note.Topmost);
        });
    }

    [Fact]
    public void Undo_delete_restores_the_most_recently_deleted_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            scope.CreateNote("survivor");
            scope.Manager.DeleteNote(scope.CreateNote("older"));
            scope.Manager.DeleteNote(scope.CreateNote("newest"));
            UiThread.Pump();

            Assert.NotNull(scope.Manager.UndoDelete());
            UiThread.Pump();

            Assert.Contains(scope.OpenNotes(), n => ManagerScope.TextOf(n) == "newest");
            Assert.Single(scope.Manager.RecentlyDeleted());
            Assert.Equal("older", scope.Manager.RecentlyDeleted()[0].Title);
        });
    }

    [Fact]
    public void Hovering_a_collapsed_note_restores_the_height_it_had_before_collapsing()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Height = 340;
            UiThread.Pump();
            note.UpdateLayout();

            Call(note, "ApplyCollapsed", true, true);
            UiThread.Pump();
            Assert.InRange(note.Height, 31, 33);

            Call(note, "Header_MouseEnter", null, null);
            Field<DispatcherTimer>(note, "_hoverTimer").Stop();
            UiThread.Wait(300);
            Assert.InRange(note.Height, 339, 341);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanding_a_collapsed_note_preserves_its_saved_size(bool hoverFirst)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Width = 360;
            note.Height = 340;
            UiThread.Pump();
            note.UpdateLayout();

            Call(note, "Collapse_Click", null, new RoutedEventArgs());
            UiThread.Pump();
            Assert.True(note.IsEffectivelyCollapsed);
            if (hoverFirst)
            {
                Call(note, "Header_MouseEnter", null, null);
                UiThread.Wait(60);
            }

            Call(note, "Collapse_Click", null, new RoutedEventArgs());
            UiThread.Pump();
            note.UpdateLayout();

            Assert.False(note.Data.IsCollapsed);
            Assert.False(note.HasAnimatedProperties);
            Assert.InRange(note.ActualWidth, 359, 361);
            Assert.InRange(note.ActualHeight, 339, 341);
            Assert.InRange(note.CaptureData().Height, 339, 341);
        });
    }

    [Fact]
    public void Hover_preview_animates_to_the_saved_height_without_changing_it()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Height = 340;
            UiThread.Pump();
            Call(note, "ApplyCollapsed", true, true);
            UiThread.Pump();

            Call(note, "Header_MouseEnter", null, null);
            Field<DispatcherTimer>(note, "_hoverTimer").Stop();

            Assert.Equal(SystemParameters.ClientAreaAnimation, note.HasAnimatedProperties);
            Assert.InRange(note.CaptureData().Height, 339, 341);
            UiThread.Wait(300);

            Assert.False(note.HasAnimatedProperties);
            Assert.InRange(note.ActualHeight, 339, 341);
            Assert.InRange(note.CaptureData().Height, 339, 341);
            Assert.Equal(ResizeMode.CanResize, note.ResizeMode);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaving_a_hover_preview_animates_back_to_the_header_and_preserves_the_saved_height(bool duringExpansion)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Height = 340;
            note.Left = -5000;
            UiThread.Pump();
            Call(note, "ApplyCollapsed", true, true);
            UiThread.Pump();
            Call(note, "Header_MouseEnter", null, null);
            Field<DispatcherTimer>(note, "_hoverTimer").Stop();
            UiThread.Wait(duringExpansion ? 60 : 300);

            Call(note, "CollapseIfCursorLeft");
            Assert.Equal(SystemParameters.ClientAreaAnimation, note.HasAnimatedProperties);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(Visibility.Visible, ManagerScope.EditorOf(note).Visibility);
                Assert.True(note.Topmost);
            }
            Assert.InRange(note.CaptureData().Height, 339, 341);
            UiThread.Wait(300);

            Assert.True(note.IsEffectivelyCollapsed);
            Assert.False(note.HasAnimatedProperties);
            Assert.False(note.Topmost);
            Assert.Equal(Visibility.Collapsed, ManagerScope.EditorOf(note).Visibility);
            Assert.InRange(note.ActualHeight, 31, 33);
            Assert.InRange(note.CaptureData().Height, 339, 341);
            Assert.False(Field<DispatcherTimer>(note, "_hoverTimer").IsEnabled);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reentering_or_expanding_during_retraction_restores_the_saved_height(bool clickToExpand)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Height = 340;
            note.Left = -5000;
            UiThread.Pump();
            CollapseAndHover(note);
            Field<DispatcherTimer>(note, "_hoverTimer").Stop();
            UiThread.Wait(300);
            Call(note, "CollapseIfCursorLeft");
            UiThread.Wait(60);

            double retractingHeight = note.ActualHeight;
            if (clickToExpand)
                Call(note, "Collapse_Click", null, new RoutedEventArgs());
            else
            {
                Call(note, "Header_MouseEnter", null, null);
                Field<DispatcherTimer>(note, "_hoverTimer").Stop();
                if (SystemParameters.ClientAreaAnimation)
                    Assert.InRange(note.Height, retractingHeight - 1, retractingHeight + 1);
            }
            UiThread.Wait(300);

            Assert.False(note.IsEffectivelyCollapsed);
            Assert.Equal(!clickToExpand, note.Data.IsCollapsed);
            Assert.False(note.HasAnimatedProperties);
            Assert.InRange(note.ActualHeight, 339, 341);
            Assert.InRange(note.CaptureData().Height, 339, 341);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deleting_a_note_while_hover_expanded_stops_its_timer_and_raises_no_errors(bool retractFirst)
    {
        UiThread.Run(() =>
        {
            while (UiThread.Unhandled.TryDequeue(out _)) { }
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            CollapseAndHover(note);
            var timer = Field<DispatcherTimer>(note, "_hoverTimer");
            Assert.True(timer.IsEnabled);
            if (retractFirst)
            {
                note.Left = -5000;
                Call(note, "CollapseIfCursorLeft");
            }

            scope.Manager.DeleteNote(note);
            UiThread.Wait(500);

            Assert.False(timer.IsEnabled);
            Assert.False(note.HasAnimatedProperties);
            Assert.Empty(UiThread.Unhandled);
        });
    }

    [Fact]
    public void Reloading_with_a_hover_expanded_note_stops_its_timer()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            CollapseAndHover(note);
            var timer = Field<DispatcherTimer>(note, "_hoverTimer");

            scope.Manager.Reload();

            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void The_bell_goes_back_to_an_outline_once_a_one_off_reminder_has_fired()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var filled = (FrameworkElement)note.FindName("ReminderBellFilled");
            var outline = (FrameworkElement)note.FindName("ReminderBellOutline");

            Assert.Equal(Visibility.Collapsed, filled.Visibility);

            var reminder = NoteReminder.Create(DateTime.Now.AddHours(1), recurring: false, now: DateTime.Now);
            note.Data.Reminders.Add(reminder);
            Call(note, "UpdateReminderButton");
            Assert.Equal(Visibility.Visible, filled.Visibility);
            Assert.Equal(Visibility.Collapsed, outline.Visibility);

            reminder.MarkFired(DateTime.Now);
            note.TriggerReminderAlert();
            UiThread.Pump();

            Assert.Equal(Visibility.Collapsed, filled.Visibility);
            Assert.Equal(Visibility.Visible, outline.Visibility);
            note.DismissReminderAlert();
        });
    }

    [Fact]
    public void The_bell_stays_filled_while_a_daily_reminder_remains()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var filled = (FrameworkElement)note.FindName("ReminderBellFilled");
            var oneOff = NoteReminder.Create(DateTime.Now.AddHours(1), recurring: false, now: DateTime.Now);
            note.Data.Reminders.Add(oneOff);
            note.Data.Reminders.Add(NoteReminder.Create(DateTime.Now.AddHours(1), recurring: true, now: DateTime.Now));

            oneOff.MarkFired(DateTime.Now);
            note.TriggerReminderAlert();
            UiThread.Pump();

            Assert.Equal(Visibility.Visible, filled.Visibility);
            note.DismissReminderAlert();
        });
    }

    [Fact]
    public void A_due_reminder_alerts_its_note_and_clicking_it_dismisses_the_alert()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.Data.Reminders.Add(new NoteReminder { TargetTime = DateTime.Now.AddMinutes(-1) });

            typeof(NoteManager).GetMethod("CheckReminders", NonPublic)!.Invoke(scope.Manager, null);
            UiThread.Pump();

            Assert.True(Field<bool>(note, "_isAlertFlashing"));
            Assert.True(note.Data.Reminders[0].HasFired);

            note.DismissReminderAlert();
            Assert.False(Field<bool>(note, "_isAlertFlashing"));
            Assert.True(Field<bool>(note, "_alertKeepOnTop"));
        });
    }

    [Fact]
    public void A_reminder_only_alerts_once()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.Data.Reminders.Add(new NoteReminder { TargetTime = DateTime.Now.AddMinutes(-1) });
            var check = typeof(NoteManager).GetMethod("CheckReminders", NonPublic)!;

            check.Invoke(scope.Manager, null);
            note.DismissReminderAlert();
            check.Invoke(scope.Manager, null);

            Assert.False(Field<bool>(note, "_isAlertFlashing"));
        });
    }

    private static ScrollBar? FindVerticalScrollBar(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical) return bar;
            var found = FindVerticalScrollBar(child);
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public void The_note_scrollbar_is_a_vertical_pill()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote(string.Join("\n", Enumerable.Range(0, 80).Select(i => "line " + i)));
            note.UpdateLayout();
            UiThread.Pump();
            note.UpdateLayout();

            var bar = FindVerticalScrollBar(ManagerScope.EditorOf(note));
            Assert.NotNull(bar);
            var track = (Track)bar!.Template.FindName("PART_Track", bar);

            Assert.Equal(Orientation.Vertical, track.Orientation);
            Assert.True(track.Thumb.ActualWidth > 0);
            Assert.True(track.Thumb.ActualHeight > track.Thumb.ActualWidth * 2);
        });
    }

    [Fact]
    public void The_scrollbar_template_also_works_horizontally()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var bar = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Style = (Style)note.FindResource("PillScrollBar"),
                Width = 100,
                Height = 10,
                Maximum = 100,
                ViewportSize = 30,
            };
            var host = new Window
            {
                Content = bar,
                Left = -5000,
                Top = 0,
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            host.Show();
            UiThread.Pump();

            var track = (Track)bar.Template.FindName("PART_Track", bar);

            Assert.Equal(Orientation.Horizontal, track.Orientation);
            Assert.False(track.IsDirectionReversed);
            host.Close();
        });
    }
}
