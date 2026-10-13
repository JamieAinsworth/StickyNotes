using System.Windows.Input;
using Xunit;

namespace StickyNotes.Tests;

public class HotkeyManagerTests
{
    // Unlikely to be taken by anything else on a developer machine or CI runner.
    private const string First = "Ctrl+Alt+Shift+F11";
    private const string Second = "Ctrl+Alt+Shift+F12";

    [Theory]
    [InlineData("Ctrl+Alt+N", true)]
    [InlineData("ctrl + alt + n", true)]
    [InlineData("Ctrl+Shift+F5", true)]
    [InlineData("Win+Z", true)]
    [InlineData("N", false)]
    [InlineData("", false)]
    [InlineData("Ctrl+", false)]
    [InlineData("Hyper+N", false)]
    [InlineData("Ctrl+NotAKey", false)]
    [InlineData("Ctrl+999", false)]
    public void Shortcut_text_is_parsed(string text, bool valid)
    {
        Assert.Equal(valid, HotkeyManager.TryParse(text, out _, out _));
    }

    [Fact]
    public void Formatted_shortcuts_parse_back()
    {
        var text = HotkeyManager.Format(ModifierKeys.Control | ModifierKeys.Alt, Key.N);

        Assert.Equal("Ctrl+Alt+N", text);
        Assert.True(HotkeyManager.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData("Ctrl+Alt+N", "Ctrl+Alt+N", true)]
    [InlineData("Ctrl+Alt+N", "ctrl+alt+n", true)]
    [InlineData("Ctrl+Alt+N", " Ctrl + Alt + N ", true)]
    [InlineData("Ctrl+Alt+N", "Alt+Ctrl+N", true)]
    [InlineData("Ctrl+Alt+N", "Ctrl+Alt+M", false)]
    [InlineData("Ctrl+Alt+N", "Ctrl+Shift+N", false)]
    [InlineData("Ctrl+Alt+N", "", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    [InlineData("Ctrl+Alt+N", null, false)]
    public void Two_texts_for_the_same_combination_are_recognised(string? first, string? second, bool expected)
    {
        Assert.Equal(expected, HotkeyManager.AreSame(first, second));
    }

    [Fact]
    public void Releasing_both_shortcuts_first_lets_the_two_be_swapped()
    {
        UiThread.Run(() =>
        {
            using var hotkeys = new HotkeyManager();
            Assert.True(hotkeys.Register(HotkeyManager.ToggleNotesId, First, () => { }));
            Assert.True(hotkeys.Register(HotkeyManager.NewNoteId, Second, () => { }));

            // Replacing one at a time collides with the other shortcut's old registration (the reported bug).
            Assert.False(hotkeys.Register(HotkeyManager.ToggleNotesId, Second, () => { }));

            // What the settings window now does: release both, then register the swapped pair.
            hotkeys.Unregister(HotkeyManager.ToggleNotesId);
            hotkeys.Unregister(HotkeyManager.NewNoteId);
            Assert.True(hotkeys.Register(HotkeyManager.ToggleNotesId, Second, () => { }));
            Assert.True(hotkeys.Register(HotkeyManager.NewNoteId, First, () => { }));
        });
    }

    [Fact]
    public void An_invalid_or_blank_shortcut_is_handled()
    {
        UiThread.Run(() =>
        {
            using var hotkeys = new HotkeyManager();

            Assert.True(hotkeys.Register(HotkeyManager.ToggleNotesId, null, () => { }));
            Assert.True(hotkeys.Register(HotkeyManager.ToggleNotesId, "  ", () => { }));
            Assert.False(hotkeys.Register(HotkeyManager.ToggleNotesId, "nonsense", () => { }));
        });
    }
}
