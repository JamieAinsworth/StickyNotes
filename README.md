# Sticky Notes
Sticky notes that dont annoy me

A lightweight sticky note app for Windows 10/11 built with WPF (.NET 10).

## Features

- Notes live on the desktop: they sit behind your other windows, stay visible on "Show desktop" (Win+D), and have no taskbar button or Alt+Tab entry.
- **Multiple monitors**: if a monitor is disconnected (undocking a laptop, Win+P to "PC screen only", and so on), any note that was on it moves to the main screen automatically, cascaded so they don't stack. The app also checks at startup. Notes stay where they were moved if the monitor comes back.
- Drag a note by its header; resize from any edge or corner.
- **Pin** a note to lock its position and size.
- **Collapse** a note down to its top bar. Hovering over the top bar expands the note for full interaction until the mouse leaves the note. When bringing notes forward with the global hotkey, collapsed notes expand until returned to the desktop.
- **Reminders** per note: schedule one-time or daily recurring reminders (with support for multiple reminders on the same note). When triggered, the note comes forward with an audible chime and a pulsing white glow around the note, tinted with the note's colour, that persists until you click the note. Once clicked, the glow stops and the note stays above other windows until you click away, then returns to the desktop.
- **Colour** picker (yellow, orange, pink, purple, blue, green, grey).
- Text wraps to the note width and scrolls when it overflows.
- Formatting bar (shown while a note is focused): bold, italic, underline, strikethrough, bulleted and numbered lists, and checkboxes. A button is highlighted while its style is active at the cursor or selection, whether you turned it on with the button or a shortcut.
- **Checkboxes**: the checkbox button turns a line into a checklist item (a tick box instead of a bullet), or adds a tick box beside the bullet or number on an existing list item. Click a box to tick it; ticked items are struck through and greyed. Enter starts a new unticked item, Enter on an empty one (or Backspace right after the box) ends it. Typing `[] ` at the start of a line does the same as the button.
- **Undo delete**: deleting a note is immediate, with no confirmation, because it can always be undone. Press **Ctrl+Shift+Z** (the delete button's tooltip reminds you) or use **Undo delete note** in the tray menu to bring the most recently deleted note back with its text, position, colour and reminders. **Recently deleted** in the tray menu lists the last 10 deleted notes (first line of text and how long ago) so you can restore any one of them. They're kept until you exit the app.
- Everything is saved automatically, by default to `%APPDATA%\StickyNotes\notes.json`.
- **First-time setup** on the first launch: choose the default colour for new notes, where to save your notes (any folder, e.g. a OneDrive folder), global shortcuts (show/hide notes, create new note), and whether to start with Windows. Change these later from **Settings…** in the tray menu. Changing the folder moves your notes there; if the folder already has notes, you can switch to them instead. The settings themselves are always kept in `%APPDATA%\StickyNotes\settings.json`.
- On upgrade, existing data from the previous default folder is copied automatically; the originals are retained as a backup. Custom notes folders stay unchanged, and existing Windows startup entries are renamed. Exit the older version before launching the update. If both default folders contain different notes and the new folder has no settings yet, migration stops with an error rather than overwriting either file.
- Tray icon (in the taskbar's hidden icons), drawn in your default note colour: right-click for **New note**, **Undo delete note**, **Recently deleted**, **Show desktop**, **Start with Windows**, **Settings…**, **Check for updates…**, **Exit**. Double-click the icon for a new note. The app keeps running in the tray even when you have no notes, and when it starts with Windows it waits there quietly instead of opening an empty note.

### Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| Bold / Italic / Underline | Ctrl+B / Ctrl+I / Ctrl+U |
| Strikethrough | Ctrl+T |
| Bulleted list | Ctrl+Shift+L, or type `- ` or `* ` at the start of a line |
| Numbered list | Ctrl+Shift+N, or type `1. ` at the start of a line |
| Checkbox / checklist | Ctrl+Shift+C, or type `[] ` at the start of a line |
| Leave a list | Press Enter on an empty list item |
| Indent / outdent list item | Tab / Shift+Tab at the start of the item |
| Bigger / smaller text | Ctrl+] / Ctrl+[ |
| Restore the last deleted note | Ctrl+Shift+Z (with a note focused), or tray menu |

## Install

Download `StickyNotes-Setup-<version>.exe` from the [Releases](https://github.com/JamieAinsworth/Sticky-Notes/releases) page and run it. It installs for the current user only (no admin rights needed) into `%LOCALAPPDATA%\Programs\Sticky Notes`, adds a Start menu shortcut, and can be removed from **Settings → Apps**. Uninstalling keeps your notes. The release also includes `StickyNotes-<version>-portable.exe`, a single exe you can run from anywhere without installing.

Neither download needs .NET installed. They aren't code-signed, so Windows SmartScreen may show "Windows protected your PC"; choose **More info → Run anyway**.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src\StickyNotes
```

To publish a copy you can keep running from one place:

```powershell
dotnet publish src\StickyNotes -c Release -o publish
dotnet .\publish\StickyNotes.dll
```

The app is built without a `StickyNotes.exe` launcher (some antivirus tools flag unsigned launchers); it runs through the .NET runtime's own `dotnet.exe` instead. To start it by double-clicking, create a shortcut (right-click the desktop → **New → Shortcut**) with this target, adjusting the path to your `publish` folder:

```
"C:\Program Files\dotnet\dotnet.exe" "C:\Apps\StickyNotes\publish\StickyNotes.dll"
```

Launching the app again while it's running just opens a new note. **Start with Windows** in the tray menu sets up the sign-in launch for you.

### Build the exe and installer

```powershell
.\build.ps1                              # self-contained artifacts\app\StickyNotes.exe
.\build.ps1 -Installer -Version 1.2.0    # also artifacts\installer\StickyNotes-Setup-1.2.0.exe
```

The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). Some antivirus tools block locally built, unsigned exes; if that happens, use the GitHub workflow below instead.

### Run the tests

```powershell
dotnet test tests\StickyNotes.Tests
```

The tests cover the checklist editing, reminders, undo for deleted notes, shortcuts and the update check. Some open real (off-screen) note windows, so they need a normal Windows desktop session; they use a temporary data folder and never touch your real notes. The [CI workflow](.github/workflows/ci.yml) builds and runs them on every pull request and on pushes to `main`.

### Publish a GitHub release

Push a version tag, and the [Release workflow](.github/workflows/release.yml) builds the installer and portable exe and attaches them to a new GitHub release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

To build without publishing a release, run the workflow manually from the **Actions** tab; the files are uploaded as build artifacts.
