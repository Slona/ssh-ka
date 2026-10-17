# SSH-ka

**English** | [Русский](README.ru.md)

A free SSH/Telnet client for the desktop, in the spirit of MobaXterm. Only what you need day to day:
session folders, a terminal with highlighting, an SFTP/SCP file explorer and a server status bar.

Stack: C# / .NET 10, Avalonia 12 (cross-platform UI; developed and tested on Windows).

Website and downloads: https://sshka.slona.me

## Features

**Connections**
- SSH and Telnet, each connection in its own tab; every tab has a close button. The tab opens immediately, before the connection is made; if the connection fails, the reason and the "Session stopped" hints (Enter / R / S) appear in the same tab, and **R** retries without losing the output.
- Login with a user name and password or with a private key (with a passphrase). If the password is not saved in the session, it is asked on connect.
  Password login also works through keyboard-interactive (VMware ESXi, servers with PAM).
- A local terminal (PowerShell) in a tab: the "Local terminal" item in the session tree's context menu.
- Host key verification like in OpenSSH: on the first connection the fingerprint is shown and confirmation is requested;
  if the key changes, a red warning about a possible man-in-the-middle is shown (the default button is "Cancel").

**Sessions**
- A session tree in the left panel, folders of any depth, no limit on the number of sessions. Folders have yellow icons, and the login is shown in parentheses after the session name.
- The **Session** button at the top creates a session and connects to it at once; the "New session" item of the tree menu only creates it.
- A search box above the tree: type part of a name, host, login or folder (several words are allowed); the tree is replaced by a list of matches (Enter connects, ↑↓ move, Esc clears).
- A session with an open tab is highlighted in green; if the session in that tab has ended (the tab is still open), it is highlighted in red.
- Sessions and folders can be dragged with the mouse: above, below or into a folder (the target is shown with a line or a frame). Which folders are expanded is remembered.
- Context menu: connect, new session, new folder, local terminal, edit, duplicate, delete. Double-click opens a tab.
- Everything is stored in one encrypted file protected by a master password (see "Data and security").
- **Import** (the icon in the bottom row of the left panel): from MobaXterm (`.mxtsessions` or `MobaXterm.ini`), from PuTTY (Windows registry) and from `~/.ssh/config`.
  Imported sessions go into a separate folder. Passwords are not imported: MobaXterm and PuTTY store them separately and protected.

**Terminal**
- An xterm emulator (ANSI/256/truecolor, scrollback, selection, vim/htop/mc and so on) based on `Iciclecreek.Avalonia.Terminal`.
- Plain system fonts: Segoe UI 12 for the interface, Consolas 14 regular weight for the terminal. Font and size can be changed in Settings → Terminal.
  The server's "bold" is shown with bright colors by default (like PuTTY); real bold can be enabled in settings.
- The cursor is a block: filled while the window and the terminal are active, and an empty outline when the window is in the background or focus is elsewhere. It does not blink.
- The mouse pointer over the terminal is a text pointer (I-beam).
- Text selected with the mouse is copied to the clipboard immediately (like PuTTY; can be turned off in settings). The text has a left margin of about half a character. Right-click pastes.
- **Rule-based highlighting**: regular expressions with color, bold and underline (errors, warnings, success, up/down states, links, IP and MAC addresses, dates, log levels).
  Rules are edited in Settings → Highlighting; their order sets priority. Text already colored by the server (`ls --color`) is left alone;
  rules are not applied in full-screen programs.
- **Code highlighting in editors**, two ways:
  1. *On the server*: the "🎨 Highlighting in editors…" button checks vim and nano and, after confirmation, appends `syntax on` to `~/.vimrc`
     and the rule include to `~/.nanorc` (a copy of the previous file is kept as `.sshka-bak`).
  2. *On the client*: the app detects the foreground editor and file, picks the language by extension and colors the code itself.
     If the editor colors by itself, the client adds nothing. A checkbox in Settings → Highlighting.

**When a session ends**
- When the connection closes (exit, drop, closed by the server), a "Session stopped" message with hints appears in the same terminal window:
  **Enter** closes the tab, **R** restarts the session in the same window (the previous output stays above a "Session restarted" line), **S** saves the terminal output to a text file.
  A server that reboots or drops off the network without closing the connection is detected in about 15-20 seconds (the client probes the server every 5 seconds).

**Window and top bar**
- Context menu on a tab title: "Duplicate tab" (a new connection of the same session next to it), "Close tab", "Save terminal output".
- Large buttons with round icons: **Session** (create), **Settings**, **Split**, **About**, **Exit** (with confirmation).
- **Split**: the window shows one, two (side by side) or four (2×2) panes, each with its own tab. A tab clicked on the tab bar goes into the
  active pane (marked with a blue frame); a pane becomes active when clicked.
- **Settings**: interface language (Russian or English; the system language by default: Russian if the system is Russian, English otherwise; applies after a restart), terminal font and size, bold,
  copy on select, highlighting and its rules, security (master password).
- In the bottom row of the left panel: the "Sessions / Explorer" switch; on the right, session import, the server monitor button (pressed means the status bar is shown)
  and the "collapse/expand panel" button (a collapsed panel stays as a narrow strip with the same buttons).

**Explorer** (the left panel; the icon buttons at the bottom switch it between "Sessions" and "Explorer"; every SSH tab has its own explorer)
- A table: name, size, modification date, owner (the permissions column is hidden; permissions are visible in the "Change permissions" window). Click a header to sort; folders are always on top.
  Each column can be resized with the mouse, and the table scrolls horizontally.
  A `..` row at the top goes one level up. When the last tab is closed, the left panel returns from the explorer to the session list.
- File icons depend on the type: archives, images, video, audio, documents, code, scripts, configs, keys, databases, logs, links.
- A toolbar of icons with tooltips: up, refresh, download, upload files, upload folder, new folder, rename, permissions, delete.
- A context menu on files and folders: download, upload here, rename, change permissions, delete, new folder, refresh.
- **Changing permissions**: a window with read/write/execute checkboxes for owner, group and others, an `rw-r--r--` string and an octal code.
- Path navigation (understands `~`), downloading and uploading files and folders (recursively); multiple selection.
- The transfer protocol is chosen in the panel: **SFTP** or **SCP**. For SCP the file list is obtained with `ls` (GNU ls is required).
- The **"Follow the terminal"** option: the panel moves to the directory the shell is in on the server by itself. Nothing is typed into the terminal for this.
- The progress of uploads and downloads is shown in the bottom strip of the window: the file name, a percentage bar (a third of the window width, centered) and a cancel button. A cancelled transfer leaves no partial file behind.
- On a name conflict a question is asked: replace, skip, replace all, skip all, cancel.

**Server status bar** (at the bottom of an SSH tab)
- Host name, CPU load with a mini graph, RAM, network upload and download speed, uptime, disk usage (≥90% is shown in red).
- Every indicator has its own icon. Hold the pointer over one for half a second to see a tooltip: host name and kernel version; CPU load; RAM (total, used, available, cached, buffers);
  per network interface sent or received, errors, drops; uptime in seconds; the output of `df -h`. The indicators can be hidden with the monitor button in the bottom left row.
- Works on Linux. Every 2 seconds one short script runs over a separate channel; nothing is installed on the server.

## Build and run

You need the [.NET SDK 10](https://dotnet.microsoft.com/download). On Windows:

```powershell
winget install Microsoft.DotNet.SDK.10
```

If the NuGet package source is not configured:

```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
```

Build and run from the project root:

```powershell
dotnet build src/SshKa.App
dotnet run --project src/SshKa.App
```

On the first start the app asks you to choose a master password (at least 8 characters). It cannot be recovered.
After the first entry the key is remembered on this computer and the password is not asked on later starts (see "Data and security").

## Data and security

Everything is stored in `%APPDATA%\SshKa\`:

| File | Contents |
|---|---|
| `vault.json` | The session tree, logins, passwords, passphrases. Encrypted as a whole: a key derived from the master password with Argon2id (64 MB, 3 passes), AES-256-GCM cipher. Writes are atomic, and `vault.json.bak` is kept next to it. |
| `settings.json` | Interface settings, highlighting rules. No secrets, not encrypted. |
| `vault.key` | The vault key protected by Windows (DPAPI, the user account). Present unless "Ask for the master password on every start" is enabled. Not created outside Windows. |
| `known_hosts.json` | Remembered server key fingerprints (SHA256). Not encrypted. |

Things worth knowing:
- If you lose the master password, you lose access to the saved sessions.
- Settings → Security: change the master password, check it and choose whether to ask for it on every start. While the key is remembered, anyone working under your Windows account opens the vault without a password; if that is not acceptable, enable the prompt on start.
- Paths to key files are stored, the keys themselves are not. Private keys stay where they were.
- With SCP, file names are escaped for the server shell (protection against command injection).
- Server fingerprints are stored unencrypted: they are public data, but tampering with the file would weaken verification.

## Code layout

```
src/SshKa.App/
  Models/      SessionItem: a tree node (folder or session)
  Storage/     SessionVault (encrypted storage), AppSettings, HostKeyVerifier
  Terminal/    SshShellConnection, TelnetConnection: connections implementing the terminal's IPtyConnection;
               HighlightingPtyConnection (highlighting layer), ShellCwdTracker (directory and editor on the server),
               ServerStatsCollector (status bar), EditorSetup (vim/nano highlighting on the server)
  Highlight/   highlighting rules and engine, HighlightingStream (stream parser), CodeLanguages (code languages)
  Files/       IRemoteFileSystem and the SftpFileSystem, ScpFileSystem implementations; PosixPath; RemoteEntry
  Import/      MobaXtermImporter, PuttyImporter, SshConfigImporter
  Views/       MainWindow (window, terminal panes), SessionView (SSH tab), FilePanel (explorer), ServerStatusBar,
               SettingsWindow, AboutWindow, TerminalFactory (font, margin, copy on select), dialogs
  Localization/ Loc (translation keyed by the Russian text), Strings (English dictionary)
  Assets/      the application icon (a cat in a terminal window)
  Styles/      Theme.axaml (fonts, colors, tabs, tables, icon buttons), Icons.axaml (vector icons)
```

The key idea: the terminal control accepts any connection that implements `IPtyConnection` (a reading and a writing stream).
So SSH, Telnet and the highlighting stream plug into the same terminal in the same way.

Dependencies: Avalonia 12, SSH.NET, Iciclecreek.Avalonia.Terminal (XTerm.NET, Porta.Pty), Konscious.Security.Cryptography.Argon2, CommunityToolkit.Mvvm.

## Known limitations

- **Theme**: light only (a slightly gray background). There is no theme switch.
- **Language**: changes after a restart. Highlighting rule names already saved in Russian in the settings stay as they are (standard names are translated).
- **Split window**: a tab that left a pane keeps running but is not shown until it is selected on the tab bar.
- **Bold**: the server's "bold" is replaced with bright colors; if an escape sequence is split by a packet boundary, one such sequence may pass through as is.
- **Telnet**: the login and password are typed manually in the terminal; there is no automatic fill. Telnet has no explorer and no status bar.
- **Following the terminal** shows the directory of the root shell: after `sudo -i` or `su` the panel will not follow. It needs Linux with `ps` and `/dev/pts`.
- **The explorer** shows a directory listing, not an expandable tree. Dragging files from Windows Explorer into the panel is not supported.
- Special permissions (setuid, setgid, sticky) are set with `chmod` over the SSH connection, so that connection must be open.
- **SCP mode** needs GNU `ls` on the server (use SFTP on BusyBox).
- **Highlighting**: a word split by a boundary between two data packets may stay uncolored. Client-side code highlighting works line by line
  (multi-line comments and strings are not tracked), and under `sudo vim` a redraw of the editor cannot be requested.
- **MobaXterm import** was built from the export format but tested on a self-made sample. If something transferred incorrectly, please report it.
- **PuTTY `.ppk` keys**: the path is imported, but opening depends on the format version.
- Moving sessions between computers is currently only possible by copying `vault.json` together with the master password.
- There are no automated tests in the repository: checks were done with throwaway programs that are not included.

## Plans

- A color label for sessions (for example, red for production servers), a dark theme, a new-tab button.
- Drag and drop of files from Windows Explorer into the file panel.
- Telnet auto-login, an expandable directory tree, managing known hosts from the interface.
- Automated tests (Telnet and highlighting stream parsing, importers, the vault).

## Website and releases

The landing page is in `site/` and is published at https://sshka.slona.me (nginx, directory `/var/www/sshka`).
Release: `dotnet publish src/SshKa.App -c Release -r win-x64 --self-contained`, remove `*.pdb` from the result, pack it into `SSH-ka-win-x64.zip`
and put it into `/var/www/sshka/download/`; in `site/index.html` update the version, size and SHA-256.

## License

MIT, see [LICENSE](LICENSE). You are free to use, change and distribute the program as long as the copyright notice is kept.

## Feedback

Bugs and feature requests are welcome in the repository's Issues: https://github.com/Slona/ssh-ka/issues. The program's website: https://sshka.slona.me.

## Versions

The version is stored in `src/SshKa.App/SshKa.App.csproj` (`<Version>`); the third number is the build number. It grows with every delivered build and is shown in the About window.
