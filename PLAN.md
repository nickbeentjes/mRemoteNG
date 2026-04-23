# mRemoteNG — AI Layer Plan

Fork: github.com/nickbeentjes/mRemoteNG  
Base: .NET 10, WinForms+WPF, DockPanelSuite

---

## What we're building

Three additions on top of stock mRemoteNG:

1. **Claude Chat Panel** — docked bottom, always visible, Claude API chat with `cgo` command parsing
2. **Smart Connections Panel** — parallel to the existing tree, auto-discovers hosts from live sources
3. **`cgo` command handler** — typed in the chat panel, launches connections and GSD commands

The existing Connections tree stays untouched. Smart panel sits alongside it as a second tab.

---

## Phase 1 — Claude Chat Panel

**New file:** `mRemoteNG/UI/Window/ClaudeChatWindow.cs`  
Inherits `BaseWindow : DockContent` (same pattern as `ConnectionTreeWindow`)

UI layout (WinForms):
```
┌─────────────────────────────────┐
│  [scrollable message history]   │
├─────────────────────────────────┤
│  [text input]        [Send ▶]  │
└─────────────────────────────────┘
```

**Claude API integration:**
- Add `Anthropic.SDK` NuGet package (or raw `HttpClient` to `api.anthropic.com/v1/messages`)
- Store API key in mRemoteNG's existing credential store (`CredentialRecord` / `AppRegistry`)
- Streaming responses via SSE → append text chunks to message history as they arrive

**`cgo` command parsing** (prefix `cgo` in the input box):
- `cgo --connect <name>` → fuzzy match against all loaded connections, call `ConnectionInitiator.OpenConnection()`
- `cgo --discover` → trigger Smart Connections refresh
- `cgo --gsd <cmd>` → SSH into a designated "GSD host", run `claude --<cmd>` in a new terminal tab
- Anything else → regular Claude chat

**Wire-up in `frmMain.cs`:**
- Instantiate `ClaudeChatWindow` alongside existing panels
- Dock bottom by default, same as other utility panels

---

## Phase 2 — Smart Connections Panel

**New file:** `mRemoteNG/UI/Window/SmartConnectionsWindow.cs`  
Inherits `BaseWindow : DockContent`

UI: `TreeView` with a refresh button and status line ("Last scan: 14:32 · 12 hosts")

**Discovery sources** (pluggable, each is an `IConnectionSource`):
```
IConnectionSource
  ├── TailscaleSource       — calls localhost Tailscale API (http://localhost:41112/localapi/v0/status)
  ├── SshKnownHostsSource   — parses ~/.ssh/known_hosts
  ├── AwsEc2Source          — AWS SDK, lists running instances (optional, needs creds)
  └── ManualSource          — static JSON file the user maintains
```

Each source returns `List<DiscoveredHost>` with: hostname, IP, tags, suggested connection type (SSH/RDP/VNC).

**Tree grouping:** by source, then by tag. Clicking a node → `ConnectionInitiator.OpenConnection()` with auto-detected protocol.

**Auto-refresh:** on app start + every 5 minutes + on `cgo --discover`.

---

## Phase 3 — `cgo --gsd` launcher

When user types `cgo --gsd <phase-cmd>` in the chat panel:

1. Look up a saved "GSD host" connection (configured in settings, or prompted once)
2. Open an SSH tab to that host via `ConnectionInitiator.OpenConnection()`
3. Once connected, send keystrokes: `claude --<phase-cmd>\n` via `ConnectionTab.Focus()` + `SendKeys`
4. Chat panel responds: "Launched `--<phase-cmd>` on `<host>` in Tab 3"

For the GSD host config — add one new settings field: `Settings.GsdSshHost` (connection name from the tree).

---

## Phase 4 — Session Recording

**How it works:** PuTTY (which backs all Telnet/SSH connections via `PuttyBase`) supports `-sessionlog <file>` as a launch argument. We pass this automatically for every connection.

**Changes to `PuttyBase`:**
- In the argument builder, append `-sessionlog "<LogDir>\<hostname>_<timestamp>.log"` 
- Log dir: `%APPDATA%\mRemoteNG\SessionLogs\` (created on first use)
- Format: plain text, one file per session

**New: `SessionLogWindow.cs`** — a searchable log browser panel:
- `ListBox` on the left: all session log files, sorted by date
- `RichTextBox` on the right: contents of selected log
- `TextBox` + "Search" button: highlights all matches in the log view
- Docked as a tab alongside the connections panels

**`cgo --logs` command** in the chat panel → focuses the SessionLogWindow.

---

## Phase 5 — Copy/Paste Toolbar

**The problem:** embedded PuTTY windows make text selection awkward and clipboard interaction unreliable.

**Solution — two features:**

**1. Paste to Remote button** (toolbar on each `ConnectionTab`):
- Reads `Clipboard.GetText()` 
- Sends it to the focused PuTTY child window via `PostMessage(hwnd, WM_CHAR, ...)` char-by-char
- Or falls back to `SendKeys.Send()` if hwnd not available
- Handles multi-line paste (sends each line + `\r\n`)

**2. "Freeze & Copy" button** (same toolbar):
- Reads the **current session log file** for this connection (already being written by Phase 4)
- Opens a floating `Form` with a `RichTextBox` showing the last 200 lines — fully selectable
- User selects text, Ctrl+C, closes. Normal Windows text selection, no hacks.
- "Tail" mode: auto-scrolls to bottom when opened
- Title shows: "Copy from <hostname> — scroll to find text, Ctrl+C to copy"

**Toolbar placement:** thin strip at the top of each `ConnectionTab`. Two buttons: `📋 Paste` and `❄ Freeze & Copy`. Minimal — doesn't intrude on the terminal.

---

## File changes summary

| File | Action |
|------|--------|
| `mRemoteNG/UI/Window/ClaudeChatWindow.cs` | New |
| `mRemoteNG/UI/Window/SmartConnectionsWindow.cs` | New |
| `mRemoteNG/Connection/Discovery/IConnectionSource.cs` | New |
| `mRemoteNG/Connection/Discovery/TailscaleSource.cs` | New |
| `mRemoteNG/Connection/Discovery/SshKnownHostsSource.cs` | New |
| `mRemoteNG/App/Config/Settings.cs` | Add `ClaudeApiKey`, `GsdSshHost` fields |
| `mRemoteNG/UI/Forms/frmMain.cs` | Instantiate + dock new panels |
| `Directory.Packages.props` | Add `Anthropic.SDK` or keep raw HttpClient |
| `mRemoteNG/Connection/Protocol/PuttyBase.cs` | Append `-sessionlog` arg on launch |
| `mRemoteNG/UI/Window/SessionLogWindow.cs` | New — log browser with search |
| `mRemoteNG/UI/Tabs/ConnectionTab.cs` | Add Paste + Freeze & Copy toolbar |

---

## Build / dev setup

- Visual Studio 2022 on Windows (solution: `mRemoteNG.sln`)
- Or `dotnet build` from CLI (targets net10.0-windows)
- Claude API key stored in Windows Credential Manager via existing `AppRegistry` pattern

---

## Order of work

1. ✅ Chat panel with Claude API + `cgo` stubs — docking proven
2. Session recording — `-sessionlog` in PuttyBase, log dir, SessionLogWindow browser
3. Copy/Paste toolbar — Paste to Remote + Freeze & Copy on ConnectionTab
4. Smart Connections panel with `SshKnownHostsSource` first (no external deps)
5. Add `TailscaleSource`
6. Wire `cgo --connect` to ConnectionInitiator
7. Add `cgo --gsd` launcher
8. Polish: settings UI, streaming responses, panel themes
