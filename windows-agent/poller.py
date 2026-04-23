# pip install mss psutil requests
"""
NickHQ Windows Agent — polls the task queue and executes tasks.

Tasks supported:
  cmd         — run a shell command, capture stdout/stderr/exit_code
  screenshot  — capture full screen as base64 PNG
  diagnostic  — collect system info (platform, disk, processes, env)

For 'cmd' tasks a screenshot is also captured after the command runs so
kees can see the terminal state.

Setup:
  1. Edit HQ_URL and AGENT_TOKEN below (or set as environment variables).
  2. pip install mss psutil requests
  3. Run: python poller.py
"""

import base64
import io
import logging
import os
import platform
import shutil
import subprocess
import sys
import time

import requests

# ── Config (edit these or set as env vars) ───────────────────────────────────

HQ_URL        = os.environ.get("HQ_URL",        "https://keess-mac-mini.taile6c48b.ts.net")
AGENT_TOKEN   = os.environ.get("AGENT_TOKEN",   "change-me")
POLL_INTERVAL = int(os.environ.get("POLL_INTERVAL", "5"))
LOG_FILE      = os.environ.get("LOG_FILE", os.path.join(os.path.dirname(__file__), "poller.log"))

# ── Logging ───────────────────────────────────────────────────────────────────

_handlers = [logging.StreamHandler(sys.stdout)]
if LOG_FILE:
    _handlers.append(logging.FileHandler(LOG_FILE, encoding="utf-8"))

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
    handlers=_handlers,
)
log = logging.getLogger("nickhq-agent")

# ── Helpers ───────────────────────────────────────────────────────────────────

_SESSION = requests.Session()
_SESSION.headers.update({"User-Agent": "NickHQ-WindowsAgent/1.0"})


def _poll() -> dict | None:
    """Fetch the next queued task, or None if the queue is empty."""
    resp = _SESSION.get(
        f"{HQ_URL}/tasks/poll",
        params={"token": AGENT_TOKEN},
        timeout=15,
    )
    resp.raise_for_status()
    return resp.json().get("task")


def _post_result(task_id: str, payload: dict):
    """Post task result back to the server."""
    resp = _SESSION.post(
        f"{HQ_URL}/tasks/{task_id}/result",
        params={"token": AGENT_TOKEN},
        json=payload,
        timeout=60,  # screenshots can be large
    )
    resp.raise_for_status()
    return resp.json()


def _take_screenshot() -> str | None:
    """Capture the full screen and return a base64-encoded PNG string, or None on failure."""
    try:
        import mss
        import mss.tools

        with mss.mss() as sct:
            monitor = sct.monitors[0]  # all monitors combined
            img = sct.grab(monitor)
            buf = io.BytesIO()
            mss.tools.to_png(img.rgb, img.size, output=buf)
            buf.seek(0)
            return base64.b64encode(buf.read()).decode("ascii")
    except ImportError:
        log.warning("mss not installed — skipping screenshot (pip install mss)")
        return None
    except Exception as exc:
        log.warning(f"Screenshot failed: {exc}")
        return None


def _run_cmd(payload: str) -> dict:
    """Run a shell command and return result dict."""
    log.info(f"Running cmd: {payload!r}")
    try:
        proc = subprocess.run(
            payload,
            shell=True,
            capture_output=True,
            text=True,
            timeout=120,
        )
        return {
            "stdout": proc.stdout,
            "stderr": proc.stderr,
            "exit_code": proc.returncode,
        }
    except subprocess.TimeoutExpired:
        return {"stdout": "", "stderr": "Command timed out after 120s", "exit_code": -1}
    except Exception as exc:
        return {"stdout": "", "stderr": str(exc), "exit_code": -1}


def _run_screenshot(_payload: str) -> dict:
    """Capture a screenshot and return result dict."""
    log.info("Taking screenshot")
    return {
        "stdout": "screenshot captured",
        "stderr": None,
        "exit_code": 0,
        "screenshot_b64": _take_screenshot(),
    }


# Env keys that are safe to include in diagnostics (block anything secret-looking)
_ENV_BLOCKLIST = {
    "token", "secret", "password", "passwd", "pwd", "key", "auth",
    "credential", "cred", "api_key", "apikey", "access_key", "private",
}


def _safe_env() -> dict:
    result = {}
    for k, v in os.environ.items():
        if any(blocked in k.lower() for blocked in _ENV_BLOCKLIST):
            result[k] = "***"
        else:
            result[k] = v
    return result


def _run_diagnostic(_payload: str) -> dict:
    """Collect system diagnostics and return result dict."""
    log.info("Collecting diagnostics")
    try:
        import psutil
        process_count = len(psutil.pids())
    except ImportError:
        process_count = None

    disk = shutil.disk_usage("/")

    diag = {
        "platform": platform.platform(),
        "python_version": sys.version,
        "cpu_count": os.cpu_count(),
        "disk_total_gb": round(disk.total / 1e9, 2),
        "disk_used_gb": round(disk.used / 1e9, 2),
        "disk_free_gb": round(disk.free / 1e9, 2),
        "process_count": process_count,
        "env": _safe_env(),
    }
    return {
        "stdout": "diagnostics collected",
        "stderr": None,
        "exit_code": 0,
        "diagnostics": diag,
    }


# ── Task dispatcher ───────────────────────────────────────────────────────────

_HANDLERS = {
    "cmd":        _run_cmd,
    "screenshot": _run_screenshot,
    "diagnostic": _run_diagnostic,
}


def _execute(task: dict) -> dict:
    task_type = task.get("type", "")
    payload   = task.get("payload", "")
    handler   = _HANDLERS.get(task_type)

    if handler is None:
        return {
            "stdout": "",
            "stderr": f"Unknown task type: {task_type!r}",
            "exit_code": -1,
        }

    result = handler(payload)

    # For cmd tasks: also grab a screenshot so kees can see terminal state
    if task_type == "cmd" and result.get("screenshot_b64") is None:
        result["screenshot_b64"] = _take_screenshot()

    return result


# ── Main loop ─────────────────────────────────────────────────────────────────

def main():
    log.info(f"NickHQ Windows Agent started — polling {HQ_URL} every {POLL_INTERVAL}s")

    while True:
        try:
            task = _poll()
            if task:
                task_id   = task["id"]
                task_type = task["type"]
                log.info(f"Got task {task_id} type={task_type}")

                result = _execute(task)

                _post_result(task_id, result)
                log.info(
                    f"Task {task_id} done — status={'done' if result.get('exit_code', 0) == 0 else 'failed'}"
                )
            else:
                log.debug("No tasks queued — sleeping")

        except requests.exceptions.ConnectionError as exc:
            log.warning(f"Connection error (server down? VPN?): {exc}")
        except requests.exceptions.HTTPError as exc:
            log.error(f"HTTP error from server: {exc}")
        except KeyboardInterrupt:
            log.info("Interrupted — shutting down")
            break
        except Exception as exc:
            log.exception(f"Unexpected error: {exc}")

        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    main()
