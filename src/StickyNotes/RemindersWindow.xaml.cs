using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StickyNotes;

public partial class RemindersWindow : Window
{
    private readonly NoteData _note;
    private readonly Action _onChanged;
    private readonly ObservableCollection<ReminderItemViewModel> _items = new();

    public RemindersWindow(NoteData note, Action onChanged)
    {
        InitializeComponent();
        _note = note;
        _onChanged = onChanged;

        var now = DateTime.Now.AddMinutes(15);
        ReminderDatePicker.SelectedDate = now.Date;
        HourText.Text = now.Hour.ToString("D2");
        MinuteText.Text = now.Minute.ToString("D2");

        GotKeyboardFocus += (_, e) =>
        {
            if (e.OriginalSource is TextBox tb) tb.SelectAll();
        };

        RemindersList.ItemsSource = _items;
        RefreshList();
    }

    private void TimeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[0-9]+$");
    }

    private void HourText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up)
        {
            AdjustHour(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            AdjustHour(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && HourText.CaretIndex == HourText.Text.Length)
        {
            MinuteText.Focus();
            MinuteText.SelectAll();
            e.Handled = true;
        }
    }

    private void MinuteText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up)
        {
            AdjustMinute(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            AdjustMinute(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && MinuteText.CaretIndex == 0)
        {
            HourText.Focus();
            HourText.SelectAll();
            e.Handled = true;
        }
    }

    private void HourText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(HourText.Text, out int h))
        {
            h = Math.Clamp(h, 0, 23);
            HourText.Text = h.ToString("D2");
        }
        else
        {
            HourText.Text = "12";
        }
    }

    private void MinuteText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MinuteText.Text, out int m))
        {
            m = Math.Clamp(m, 0, 59);
            MinuteText.Text = m.ToString("D2");
        }
        else
        {
            MinuteText.Text = "00";
        }
    }

    private void TimeUp_Click(object sender, RoutedEventArgs e)
    {
        if (HourText.IsFocused) AdjustHour(1);
        else AdjustMinute(1);
    }

    private void TimeDown_Click(object sender, RoutedEventArgs e)
    {
        if (HourText.IsFocused) AdjustHour(-1);
        else AdjustMinute(-1);
    }

    private void AdjustHour(int delta)
    {
        int h = int.TryParse(HourText.Text, out int cur) ? cur : 12;
        h = (h + delta) % 24;
        if (h < 0) h += 24;
        HourText.Text = h.ToString("D2");
        HourText.SelectAll();
    }

    private void AdjustMinute(int delta)
    {
        int m = int.TryParse(MinuteText.Text, out int cur) ? cur : 0;
        int next = m + delta;
        if (next >= 60)
        {
            AdjustHour(1);
            next %= 60;
        }
        else if (next < 0)
        {
            AdjustHour(-1);
            next = 60 + (next % 60);
        }
        MinuteText.Text = next.ToString("D2");
        MinuteText.SelectAll();
    }

    private void RecurringCheck_Changed(object sender, RoutedEventArgs e)
    {
        ReminderDatePicker.IsEnabled = RecurringCheck.IsChecked != true;
    }

    private void RefreshList()
    {
        _items.Clear();
        foreach (var reminder in _note.Reminders.OrderBy(r => r.TargetTime.TimeOfDay))
        {
            _items.Add(new ReminderItemViewModel(reminder));
        }
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        bool isRecurring = RecurringCheck.IsChecked == true;
        if (!isRecurring && ReminderDatePicker.SelectedDate == null)
        {
            MessageBox.Show(this, "Please select a date.", "Sticky Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int hour = int.TryParse(HourText.Text, out int hVal) ? Math.Clamp(hVal, 0, 23) : 12;
        int minute = int.TryParse(MinuteText.Text, out int mVal) ? Math.Clamp(mVal, 0, 59) : 0;
        HourText.Text = hour.ToString("D2");
        MinuteText.Text = minute.ToString("D2");

        var selectedDate = ReminderDatePicker.SelectedDate?.Date ?? DateTime.Today;
        var targetTime = new DateTime(selectedDate.Year, selectedDate.Month, selectedDate.Day, hour, minute, 0);

        if (!isRecurring && targetTime <= DateTime.Now)
        {
            MessageBox.Show(this, "Please select a future time for the reminder.", "Sticky Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reminder = new NoteReminder
        {
            TargetTime = targetTime,
            IsRecurring = isRecurring,
            HasFired = false,
        };

        _note.Reminders.Add(reminder);
        _onChanged();
        RefreshList();
    }

    private void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReminderItemViewModel vm })
        {
            _note.Reminders.Remove(vm.Reminder);
            _onChanged();
            RefreshList();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public sealed class ReminderItemViewModel
{
    public NoteReminder Reminder { get; }

    public ReminderItemViewModel(NoteReminder reminder)
    {
        Reminder = reminder;
    }

    public string DisplayText => Reminder.IsRecurring
        ? Reminder.TargetTime.ToString("HH:mm")
        : Reminder.TargetTime.ToString("MMM d, yyyy HH:mm");

    public string RecurrenceText => Reminder.IsRecurring
        ? "Repeats daily"
        : (Reminder.HasFired ? "Fired" : "One-time");
}
