using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StickyNotes;

/// <summary>Notification-area icon (lives in the "hidden icons" overflow) with the app menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private Icon _icon;
    private string _iconColour;

    public TrayIcon(NoteManager manager, Action openSettings, Action exit)
    {
        var colour = NoteColors.Get(AppSettings.Current.DefaultColor);
        _iconColour = colour.Name;
        _icon = CreateIcon(colour);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("New note", null, (_, _) => manager.CreateNote());

        var undoDelete = new ToolStripMenuItem("Undo delete note") { Enabled = false };
        undoDelete.Click += (_, _) => manager.UndoDelete();
        _menu.Items.Add(undoDelete);

        var recentlyDeleted = new ToolStripMenuItem("Recently deleted") { Enabled = false };
        _menu.Items.Add(recentlyDeleted);
        _menu.Opening += (_, _) =>
        {
            undoDelete.Enabled = manager.CanUndoDelete;

            recentlyDeleted.DropDownItems.Clear();
            var deleted = manager.RecentlyDeleted();
            recentlyDeleted.Enabled = deleted.Count > 0;
            foreach (var note in deleted)
            {
                // '&' would otherwise be read as a menu access key.
                var item = new ToolStripMenuItem($"{note.Title.Replace("&", "&&")}  ·  {note.AgeText}");
                item.Click += (_, _) => manager.UndoDelete(note.Data);
                recentlyDeleted.DropDownItems.Add(item);
            }
        };
        _menu.Items.Add("Show desktop", null, (_, _) => ShowDesktop());
        _menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows");
        try { startup.Checked = StartupManager.IsEnabled; } catch { }
        startup.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(!startup.Checked);
                startup.Checked = StartupManager.IsEnabled;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't update the startup setting:\n" + ex.Message, "Sticky Notes",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _menu.Items.Add(startup);
        _menu.Items.Add("Settings…", null, (_, _) => openSettings());

        var updates = new ToolStripMenuItem("Check for updates…");
        updates.Click += async (_, _) => await CheckForUpdatesAsync(updates);
        _menu.Items.Add(updates);
        _menu.Opening += (_, _) =>
        {
            try { startup.Checked = StartupManager.IsEnabled; } catch { }
        };

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Sticky Notes",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) manager.BringForward();
        };
    }

    private static async Task CheckForUpdatesAsync(ToolStripMenuItem item)
    {
        item.Enabled = false;
        try
        {
            var info = await UpdateChecker.CheckAsync();

            if (info.IsNewer)
            {
                var open = MessageBox.Show(
                    $"Sticky Notes {info.Latest} is available (you have {info.Current}).\n\nOpen the download page?",
                    "Sticky Notes", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (open == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
            }
            else if (info.Latest == null)
            {
                MessageBox.Show("No published releases were found.", "Sticky Notes",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"You're up to date (version {info.Current}).", "Sticky Notes",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't check for updates. Check your internet connection and try again.\n\n" + ex.Message,
                "Sticky Notes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            item.Enabled = true;
        }
    }

    private static void ShowDesktop()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            shell?.ToggleDesktop();
        }
        catch
        {
            // Not critical; ignore if the shell isn't available.
        }
    }

    /// <summary>Redraws the icon in the current default note colour, if it changed.</summary>
    public void RefreshColour()
    {
        var colour = NoteColors.Get(AppSettings.Current.DefaultColor);
        if (colour.Name == _iconColour) return;

        var old = _icon;
        _icon = CreateIcon(colour);
        _iconColour = colour.Name;
        _notifyIcon.Icon = _icon;
        old.Dispose();
    }

    private static Icon CreateIcon(NoteColor colour)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        // The note's header colour is used for the body so small icons stay visible on the taskbar;
        // the strip and outline are darker shades of it.
        var baseColour = Color.FromArgb(colour.Header.R, colour.Header.G, colour.Header.B);
        var strip = Shade(baseColour, 0.80f);
        var edge = Shade(baseColour, 0.50f);
        var line = Color.FromArgb(0x90, Shade(baseColour, 0.40f));

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float s = size / 16f;
            var body = new RectangleF(1 * s, 1 * s, 14 * s, 14 * s);
            using var bodyBrush = new SolidBrush(baseColour);
            using var headerBrush = new SolidBrush(strip);
            using var outline = new Pen(edge, Math.Max(1f, s));
            using var linePen = new Pen(line, Math.Max(1f, s));

            g.FillRectangle(bodyBrush, body);
            g.FillRectangle(headerBrush, body.X, body.Y, body.Width, 3.5f * s);
            g.DrawLine(linePen, 3.5f * s, 7.5f * s, 12.5f * s, 7.5f * s);
            g.DrawLine(linePen, 3.5f * s, 10f * s, 12.5f * s, 10f * s);
            g.DrawLine(linePen, 3.5f * s, 12.5f * s, 9.5f * s, 12.5f * s);
            g.DrawRectangle(outline, body.X, body.Y, body.Width - 0.5f, body.Height - 0.5f);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static Color Shade(Color c, float factor) =>
        Color.FromArgb((int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
