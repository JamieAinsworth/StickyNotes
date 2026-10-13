using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;

namespace StickyNotes;

/// <summary>Registers global hotkeys, described as text such as "Ctrl+Alt+N".</summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    public const int ToggleNotesId = 1;
    public const int NewNoteId = 2;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, Action> _actions = new();
    private readonly HashSet<int> _registered = new();

    public HotkeyManager()
    {
        // Message-only window to receive WM_HOTKEY.
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
    }

    /// <summary>
    /// Registers or replaces a hotkey with the specified ID.
    /// Returns false if the text is invalid or the combination is already in use by another app.
    /// </summary>
    public bool Register(int id, string? text, Action action)
    {
        Unregister(id);
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out uint modifiers, out uint vk)) return false;

        bool success = NativeMethods.RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, vk);
        if (success)
        {
            _registered.Add(id);
            _actions[id] = action;
        }
        return success;
    }

    public void Unregister(int id)
    {
        if (!_registered.Contains(id)) return;
        NativeMethods.UnregisterHotKey(Handle, id);
        _registered.Remove(id);
        _actions.Remove(id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                action();
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _registered.ToList())
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
        _registered.Clear();
        _actions.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    /// <summary>True when both texts describe the same key combination (blank texts never conflict).</summary>
    public static bool AreSame(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;

        if (TryParse(first, out uint firstModifiers, out uint firstKey) &&
            TryParse(second, out uint secondModifiers, out uint secondKey))
            return firstModifiers == secondModifiers && firstKey == secondKey;

        return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var text = string.Empty;
        if (modifiers.HasFlag(ModifierKeys.Control)) text += "Ctrl+";
        if (modifiers.HasFlag(ModifierKeys.Alt)) text += "Alt+";
        if (modifiers.HasFlag(ModifierKeys.Shift)) text += "Shift+";
        if (modifiers.HasFlag(ModifierKeys.Windows)) text += "Win+";
        return text + key;
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key) || key == Key.None || !Enum.IsDefined(key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }
}
