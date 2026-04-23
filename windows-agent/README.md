# NickHQ Windows Agent

Polls the NickHQ task queue and executes tasks sent from Mac Claude.
Results (stdout, stderr, exit code, screenshots) are posted back to the server.

## Requirements

- Python 3.10 or later
- Network access to the Tailscale URL (must be on Tailscale or have direct access)

## Install dependencies

```
pip install mss psutil requests
```

- `requests` — HTTP polling and result posting
- `mss`      — cross-platform screen capture (screenshot tasks)
- `psutil`   — process count in diagnostic tasks

## Configuration

Edit the top of `poller.py`, or set environment variables:

| Variable        | Default                                           | Description                        |
|-----------------|---------------------------------------------------|------------------------------------|
| `HQ_URL`        | `https://keess-mac-mini.taile6c48b.ts.net`        | NickHQ server base URL             |
| `AGENT_TOKEN`   | `change-me`                                       | Must match `AGENT_TOKEN` on server |
| `POLL_INTERVAL` | `5`                                               | Seconds between polls              |

The `AGENT_TOKEN` value must match the `AGENT_TOKEN` environment variable set on the Mac mini where NickHQ runs.

## Run manually

```
python poller.py
```

Press Ctrl-C to stop.

## Run on Windows startup (Task Scheduler)

1. Open **Task Scheduler** → **Create Basic Task**
2. Trigger: **When the computer starts**
3. Action: **Start a program**
   - Program: `C:\Python312\python.exe` (adjust to your Python path)
   - Arguments: `C:\path\to\poller.py`
   - Start in: `C:\path\to\windows-agent\`
4. On the **General** tab, check **Run whether user is logged on or not**
5. On the **Settings** tab, check **Restart the task if it fails**, delay 1 minute

Alternatively, create a `.bat` launcher and add it to the Startup folder
(`shell:startup`):

```bat
@echo off
set HQ_URL=https://keess-mac-mini.taile6c48b.ts.net
set AGENT_TOKEN=your-token-here
python C:\path\to\windows-agent\poller.py
```

## Task types

| Type          | What happens                                                                 |
|---------------|------------------------------------------------------------------------------|
| `cmd`         | Runs `payload` as a shell command. Screenshot of screen attached to result.  |
| `screenshot`  | Captures full screen (all monitors combined). `payload` is ignored.          |
| `diagnostic`  | Collects platform info, disk usage, process count, filtered env vars.        |

## Server-side endpoints

All use the base URL `https://keess-mac-mini.taile6c48b.ts.net/tasks`.

| Method | Path                   | Auth          | Description                          |
|--------|------------------------|---------------|--------------------------------------|
| POST   | `/tasks`               | Bearer token  | Create a task                        |
| GET    | `/tasks`               | Bearer token  | List tasks (newest first)            |
| GET    | `/tasks?status=queued` | Bearer token  | Filter by status                     |
| GET    | `/tasks/{id}`          | Bearer token  | Get single task                      |
| DELETE | `/tasks/{id}`          | Bearer token  | Delete a task                        |
| GET    | `/tasks/poll`          | `?token=`     | Poller fetches next queued task      |
| POST   | `/tasks/{id}/result`   | `?token=`     | Agent posts result                   |
