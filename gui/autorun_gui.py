"""SystemAutorun Manager GUI - PySide6 中文界面。

设计风格参考 Material Design 3 暗色主题（参考 video_tool_gui.py）。
优先通过常驻 autorun-worker.ps1 获取数据（避免每次冷启动 pwsh）；
失败时回退到一次性 autorun.ps1 -Json。
任务列表始终来自 autorun.yaml，不硬编码。
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Optional

from PySide6 import QtCore, QtGui, QtWidgets

def _resolve_project_root() -> Path:
    """Find the external project root in both source and frozen exe modes."""
    candidates: list[Path] = []
    if getattr(sys, '_MEIPASS', None):
        exe_dir = Path(sys.executable).resolve().parent
        candidates.extend([exe_dir, exe_dir.parent, Path.cwd()])
    else:
        candidates.append(Path(__file__).resolve().parent.parent)

    seen: set[Path] = set()
    for candidate in candidates:
        root = candidate.resolve()
        if root in seen:
            continue
        seen.add(root)
        if (root / "autorun.ps1").exists():
            return root
    return candidates[0].resolve()


PROJECT_ROOT = _resolve_project_root()
AUTORUN_PS1 = PROJECT_ROOT / "autorun.ps1"
AUTORUN_WORKER_PS1 = PROJECT_ROOT / "autorun-worker.ps1"
AUTORUN_YAML = PROJECT_ROOT / "autorun.yaml"
LOGS_DIR = PROJECT_ROOT / "logs"
GUI_SETTINGS = PROJECT_ROOT / "autorun.gui.settings.json"
GUI_LOG = LOGS_DIR / "gui-start.log"
GUI_CACHE = PROJECT_ROOT / "autorun.gui.cache.json"

CACHEABLE_READ_COMMANDS = {"list", "doctor", "scan", "startup-list", "logs", "status"}
PERSISTED_CACHE_COMMANDS = {"list", "doctor", "scan", "startup-list"}
CACHE_MAX_ENTRIES = 80


def append_gui_log(message: str) -> None:
    """Best-effort diagnostic logging for startup and background failures."""
    try:
        LOGS_DIR.mkdir(parents=True, exist_ok=True)
        with GUI_LOG.open("a", encoding="utf-8") as file:
            file.write(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {message}\n")
    except Exception:
        pass


def _cache_key(command: str, args: list[str] | None = None) -> str:
    return json.dumps([command, list(args or [])], ensure_ascii=False, separators=(",", ":"))


def _format_cache_time(timestamp: float | None) -> str:
    if not timestamp:
        return ""
    age = max(0, int(time.time() - timestamp))
    if age < 10:
        return "刚刚"
    if age < 60:
        return f"{age} 秒前"
    if age < 3600:
        return f"{age // 60} 分钟前"
    local = time.localtime(timestamp)
    now = time.localtime()
    if (local.tm_year, local.tm_yday) == (now.tm_year, now.tm_yday):
        return time.strftime("%H:%M", local)
    return time.strftime("%m-%d %H:%M", local)


class _GuiDataCache:
    """Small thread-safe cache with disk persistence for list-like data."""

    def __init__(self, path: Path):
        self.path = path
        self._lock = threading.RLock()
        self._entries: dict[str, dict[str, Any]] = {}
        self._load()

    def _load(self) -> None:
        try:
            if not self.path.exists():
                return
            payload = json.loads(self.path.read_text(encoding="utf-8"))
            entries = payload.get("entries") if isinstance(payload, dict) else None
            if isinstance(entries, dict):
                self._entries = entries
        except Exception as exc:
            append_gui_log(f"Failed to load GUI cache: {exc}")

    def get(
        self,
        command: str,
        args: list[str] | None = None,
        max_age: float | None = None,
    ) -> tuple[object | None, float | None]:
        key = _cache_key(command, args)
        with self._lock:
            entry = self._entries.get(key)
            if not isinstance(entry, dict):
                return None, None
            timestamp = float(entry.get("timestamp") or 0)
            if max_age is not None and time.time() - timestamp > max_age:
                return None, timestamp
            return entry.get("data"), timestamp

    def set(self, command: str, args: list[str] | None, data: object) -> float:
        timestamp = time.time()
        key = _cache_key(command, args)
        with self._lock:
            self._entries[key] = {
                "command": command,
                "args": list(args or []),
                "timestamp": timestamp,
                "data": data,
            }
            if len(self._entries) > CACHE_MAX_ENTRIES:
                oldest = sorted(
                    self._entries,
                    key=lambda item: float(self._entries[item].get("timestamp") or 0),
                )[: len(self._entries) - CACHE_MAX_ENTRIES]
                for old_key in oldest:
                    self._entries.pop(old_key, None)
            if command in PERSISTED_CACHE_COMMANDS:
                self._persist()
        return timestamp

    def invalidate(self, commands: set[str] | None = None) -> None:
        with self._lock:
            if commands is None:
                self._entries.clear()
            else:
                self._entries = {
                    key: entry for key, entry in self._entries.items()
                    if entry.get("command") not in commands
                }
            self._persist()

    def _persist(self) -> None:
        try:
            persistent = {
                key: entry for key, entry in self._entries.items()
                if entry.get("command") in PERSISTED_CACHE_COMMANDS
            }
            payload = {"version": 1, "entries": persistent}
            temp_path = self.path.with_name(f"{self.path.name}.tmp")
            temp_path.write_text(
                json.dumps(payload, ensure_ascii=False, separators=(",", ":")),
                encoding="utf-8",
            )
            temp_path.replace(self.path)
        except Exception as exc:
            append_gui_log(f"Failed to persist GUI cache: {exc}")


_GUI_DATA_CACHE = _GuiDataCache(GUI_CACHE)


def get_cached_cli(
    command: str,
    args: list[str] | None = None,
    max_age: float | None = None,
) -> tuple[object | None, float | None]:
    return _GUI_DATA_CACHE.get(command, args, max_age=max_age)


def invalidate_cli_cache(commands: set[str] | None = None) -> None:
    _GUI_DATA_CACHE.invalidate(commands)


def load_gui_settings() -> dict[str, Any]:
    defaults = {"auto_start_missing_on_launch": False}
    try:
        if GUI_SETTINGS.exists():
            loaded = json.loads(GUI_SETTINGS.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                defaults.update(loaded)
    except Exception as exc:
        append_gui_log(f"Failed to load GUI settings: {exc}")
    return defaults


def save_gui_settings(settings: dict[str, Any]) -> None:
    try:
        GUI_SETTINGS.write_text(
            json.dumps(settings, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
    except Exception as exc:
        append_gui_log(f"Failed to save GUI settings: {exc}")


# ─── CLI 调用层 ──────────────────────────────────────────────────

def _is_pwsh_alias_stub(path: str) -> bool:
    """True for App Execution Alias stubs that cannot be used with UAC runas."""
    norm = path.replace("/", "\\").lower()
    # Real Store package path contains Microsoft.PowerShell_*; stub does not.
    if "microsoft.powershell_" in norm:
        return False
    return norm.endswith("\\windowsapps\\pwsh.exe")


def _resolve_pwsh() -> str:
    """Resolve a real pwsh.exe path (never return WindowsApps execution-alias stub)."""
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    preferred = [
        program_files / "PowerShell" / "7" / "pwsh.exe",
        Path(r"C:\Program Files\PowerShell\7\pwsh.exe"),
        program_files / "PowerShell" / "7-preview" / "pwsh.exe",
    ]
    for candidate in preferred:
        if candidate.exists():
            return str(candidate)

    # Ask a short-lived pwsh for its real MainModule path (resolves Store alias → package).
    which = shutil.which("pwsh") or "pwsh"
    try:
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        proc = subprocess.run(
            [
                which,
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName",
            ],
            capture_output=True,
            text=True,
            timeout=20,
            creationflags=creationflags,
        )
        lines = [ln.strip() for ln in (proc.stdout or "").splitlines() if ln.strip()]
        if lines:
            resolved = lines[-1]
            if Path(resolved).exists() and not _is_pwsh_alias_stub(resolved):
                return resolved
    except Exception as exc:
        append_gui_log(f"resolve pwsh MainModule failed: {exc}")

    # Scan Store package folder
    store = program_files / "WindowsApps"
    if store.exists():
        try:
            for folder in sorted(store.glob("Microsoft.PowerShell_*"), reverse=True):
                exe = folder / "pwsh.exe"
                if exe.exists():
                    return str(exe)
        except OSError as exc:
            append_gui_log(f"scan WindowsApps failed: {exc}")

    if which and not _is_pwsh_alias_stub(which):
        return which
    # Last resort: leave a name; callers should surface a clear error
    return "pwsh"


def _parse_cli_json_payload(out: str):
    """解析 CLI stdout 中的 JSON（允许前置噪音行）。"""
    text = (out or "").strip()
    if not text:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        for line in reversed(text.splitlines()):
            candidate = line.strip()
            if not candidate or candidate[0] not in "[{":
                continue
            try:
                return json.loads(candidate)
            except json.JSONDecodeError:
                continue
        raise RuntimeError(f"解析 JSON 失败: {error}\n原始: {text[:500]}") from error


class _PersistentCliWorker:
    """常驻 pwsh worker：模块与配置热缓存，刷新不再冷启动。

    任务列表始终来自 autorun.yaml（+ overrides），不硬编码。
    """

    def __init__(self):
        self._lock = threading.RLock()
        self._proc: subprocess.Popen | None = None
        self._next_id = 1
        self._stderr_thread: threading.Thread | None = None

    @property
    def available(self) -> bool:
        return AUTORUN_WORKER_PS1.exists()

    def _alive(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    def _drain_stderr(self) -> None:
        proc = self._proc
        if not proc or not proc.stderr:
            return
        try:
            for line in proc.stderr:
                text = (line or "").strip()
                if text:
                    append_gui_log(f"worker stderr: {text}")
        except Exception:
            pass

    def start(self) -> None:
        with self._lock:
            if self._alive():
                return
            if not self.available:
                raise RuntimeError(f"未找到 worker 脚本：{AUTORUN_WORKER_PS1}")

            creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            self._proc = subprocess.Popen(
                [
                    _resolve_pwsh(),
                    "-NoProfile",
                    "-NoLogo",
                    "-NonInteractive",
                    "-File",
                    str(AUTORUN_WORKER_PS1),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                shell=False,
                creationflags=creationflags,
                cwd=str(PROJECT_ROOT),
            )
            self._stderr_thread = threading.Thread(
                target=self._drain_stderr, name="autorun-worker-stderr", daemon=True
            )
            self._stderr_thread.start()

            # 等待 ready（worker 冷启动只付一次）
            deadline = time.time() + 45
            while time.time() < deadline:
                if not self._alive():
                    err = ""
                    try:
                        err = (self._proc.stderr.read() if self._proc and self._proc.stderr else "") or ""
                    except Exception:
                        pass
                    raise RuntimeError(f"worker 启动失败。{err[:400]}")
                line = self._proc.stdout.readline() if self._proc and self._proc.stdout else ""
                if not line:
                    time.sleep(0.02)
                    continue
                line = line.strip()
                if not line:
                    continue
                try:
                    payload = json.loads(line)
                except json.JSONDecodeError:
                    append_gui_log(f"worker boot noise: {line[:200]}")
                    continue
                if payload.get("event") == "ready":
                    append_gui_log(
                        f"CLI worker ready pid={payload.get('pid')} root={payload.get('root')}"
                    )
                    return
            self.stop()
            raise RuntimeError("worker 启动超时（未收到 ready）")

    def stop(self) -> None:
        with self._lock:
            proc = self._proc
            self._proc = None
            if not proc:
                return
            try:
                if proc.poll() is None and proc.stdin:
                    req = json.dumps({"id": "shutdown", "command": "shutdown", "args": []}, ensure_ascii=False)
                    proc.stdin.write(req + "\n")
                    proc.stdin.flush()
            except Exception:
                pass
            try:
                if proc.poll() is None:
                    proc.terminate()
                proc.wait(timeout=2)
            except Exception:
                try:
                    proc.kill()
                except Exception:
                    pass

    def request(self, command: str, args: list[str] | None = None, timeout: int = 60):
        with self._lock:
            if not self._alive():
                self.start()
            assert self._proc and self._proc.stdin and self._proc.stdout

            req_id = str(self._next_id)
            self._next_id += 1
            payload = {
                "id": req_id,
                "command": command,
                "args": list(args or []),
            }
            line = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
            try:
                self._proc.stdin.write(line + "\n")
                self._proc.stdin.flush()
            except Exception as exc:
                self.stop()
                raise RuntimeError(f"写入 worker 失败: {exc}") from exc

            deadline = time.time() + max(1, timeout)
            while time.time() < deadline:
                if not self._alive():
                    self.stop()
                    raise RuntimeError("worker 进程已退出")
                # readline 可能阻塞；用短轮询 + 超时通过外层 deadline 控制
                # Windows 上管道 readline 无法设超时，依赖 worker 快速返回。
                response_line = self._proc.stdout.readline()
                if not response_line:
                    if not self._alive():
                        self.stop()
                        raise RuntimeError("worker 进程已退出（无输出）")
                    continue
                response_line = response_line.strip()
                if not response_line:
                    continue
                try:
                    response = json.loads(response_line)
                except json.JSONDecodeError:
                    append_gui_log(f"worker non-json: {response_line[:200]}")
                    continue
                if str(response.get("id")) != req_id:
                    # 忽略过期/无关行
                    continue
                if response.get("ok") is True:
                    return response.get("data")
                error = response.get("error") or "worker 返回失败"
                raise RuntimeError(str(error))
            self.stop()
            raise TimeoutError(f"worker 请求超时（{timeout}s）：{command}")


_CLI_WORKER = _PersistentCliWorker()


class _CliDispatcher(QtCore.QObject):
    done = QtCore.Signal(object, object)
    error = QtCore.Signal(object, object)

    def __init__(self):
        super().__init__()
        self.done.connect(self._handle_done, QtCore.Qt.QueuedConnection)
        self.error.connect(self._handle_error, QtCore.Qt.QueuedConnection)

    @QtCore.Slot(object, object)
    def _handle_done(self, callback, result):
        callback(result)

    @QtCore.Slot(object, object)
    def _handle_error(self, callback, exc):
        callback(exc)


_CLI_DISPATCHER: Optional[_CliDispatcher] = None
_CLI_INFLIGHT_LOCK = threading.RLock()
_CLI_INFLIGHT: dict[str, list[tuple[object, object]]] = {}


def _cli_dispatcher() -> _CliDispatcher:
    global _CLI_DISPATCHER
    if _CLI_DISPATCHER is None:
        _CLI_DISPATCHER = _CliDispatcher()
    return _CLI_DISPATCHER


def _run_cli_oneshot(command: str, args: list[str] | None = None, timeout: int = 60):
    """一次性 pwsh -File autorun.ps1（worker 不可用时的回退）。"""
    cmd = [_resolve_pwsh(), "-NoProfile", "-NonInteractive", "-File", str(AUTORUN_PS1),
           command, *(args or []), "-Json"]
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=timeout,
        shell=False,
        creationflags=creationflags,
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"CLI 退出码 {proc.returncode}\n"
            f"STDERR: {proc.stderr.strip()}\n"
            f"STDOUT: {proc.stdout.strip()[:500]}"
        )
    return _parse_cli_json_payload(proc.stdout)


def run_cli(command: str, args: list[str] | None = None, timeout: int = 60):
    """优先走常驻 worker；失败自动回退一次性 CLI。

    list 等数据始终来自 autorun.yaml，不硬编码任务列表。
    """
    normalized_args = list(args or [])
    if _CLI_WORKER.available:
        try:
            return _CLI_WORKER.request(command, normalized_args, timeout=timeout)
        except Exception as worker_error:
            append_gui_log(f"worker request failed ({command}): {worker_error}; fallback oneshot")
            try:
                _CLI_WORKER.stop()
            except Exception:
                pass
    return _run_cli_oneshot(command, normalized_args, timeout=timeout)


def shutdown_cli_worker() -> None:
    try:
        _CLI_WORKER.stop()
    except Exception as exc:
        append_gui_log(f"shutdown worker failed: {exc}")


def run_cli_async(
    on_done,
    on_error,
    command: str,
    args: list[str] | None = None,
    timeout: int = 60,
    cache_result: bool | None = None,
    deduplicate: bool | None = None,
):
    """异步调 CLI；只读请求会缓存结果并合并并发中的重复调用。"""
    dispatcher = _cli_dispatcher()
    normalized_args = list(args or [])
    cache_result = command in CACHEABLE_READ_COMMANDS if cache_result is None else cache_result
    deduplicate = command in CACHEABLE_READ_COMMANDS if deduplicate is None else deduplicate
    key = _cache_key(command, normalized_args)
    joined_existing = False

    if deduplicate:
        with _CLI_INFLIGHT_LOCK:
            if key in _CLI_INFLIGHT:
                _CLI_INFLIGHT[key].append((on_done, on_error))
                joined_existing = True
            else:
                _CLI_INFLIGHT[key] = [(on_done, on_error)]

    if joined_existing:
        return

    def drain_callbacks() -> list[tuple[object, object]]:
        if not deduplicate:
            return [(on_done, on_error)]
        with _CLI_INFLIGHT_LOCK:
            return _CLI_INFLIGHT.pop(key, [(on_done, on_error)])

    def worker():
        try:
            result = run_cli(command, normalized_args, timeout=timeout)
            if cache_result:
                _GUI_DATA_CACHE.set(command, normalized_args, result)
            pending_callbacks = drain_callbacks()
            for done_callback, _error_callback in pending_callbacks:
                dispatcher.done.emit(done_callback, result)
        except Exception as e:
            pending_callbacks = drain_callbacks()
            for _done_callback, error_callback in pending_callbacks:
                dispatcher.error.emit(error_callback, e)
    threading.Thread(target=worker, daemon=True).start()


def user_message_from_error(value: object) -> str:
    text = str(value)
    if isinstance(value, dict):
        return str(value.get("error") or value.get("message") or "操作失败。")

    marker = "STDOUT:"
    if marker in text:
        stdout = text.split(marker, 1)[1].strip()
        first_line = stdout.splitlines()[0].strip() if stdout else ""
        if first_line.startswith("{"):
            try:
                data = json.loads(first_line)
                if isinstance(data, dict) and data.get("error"):
                    return str(data["error"])
            except Exception:
                pass
    return text


def format_action_result(command: str, name: str, result: object) -> tuple[bool, str]:
    if not isinstance(result, dict):
        return False, "操作没有返回有效结果。"

    if result.get("error"):
        return False, user_message_from_error(result)

    label = action_label(command)

    success = False
    if command == "disable":
        success = result.get("enabled") is False or result.get("disabled") is True
    elif command == "enable":
        success = result.get("enabled") is True
    elif command == "start":
        success = result.get("started") is True
    elif command == "stop":
        success = result.get("stopped") is True
    elif command == "restart":
        success = result.get("restarted") is True
    elif command == "remove-task":
        success = result.get("removed") is True

    if not success:
        return False, f"{label} {name} 失败。"

    extra = ""
    if command == "disable":
        extra = "当前正在运行的进程不会被停止。"
    elif command == "enable":
        extra = "下次自动启动时会执行该任务。"
    elif command == "remove-task":
        extra = "已从 autorun.yaml 移除；未停止正在运行的进程。"

    message = f"已{label} {name}。"
    if extra:
        message += f"\n{extra}"
    return True, message


def action_label(command: str) -> str:
    return {
        "start": "启动",
        "stop": "停止",
        "restart": "重启",
        "enable": "启用",
        "disable": "禁用",
        "remove-task": "移出列表",
    }.get(command, command)


def format_start_missing_result(result: object) -> str:
    if not isinstance(result, dict):
        return "补启动完成，但没有返回详细结果。"
    summary = result.get("summary") or {}
    started = summary.get("started", 0)
    skipped = summary.get("skipped", 0)
    failed = summary.get("failed", 0)
    lines = [f"补启动完成：启动 {started} 个，跳过 {skipped} 个，失败 {failed} 个。"]
    failed_rows = result.get("failed") or []
    if failed_rows:
        lines.append("")
        lines.append("失败任务：")
        for row in failed_rows[:8]:
            if isinstance(row, dict):
                lines.append(f"- {row.get('name', '未知任务')}：{row.get('reason') or row.get('detail') or '启动失败'}")
    return "\n".join(lines)


def format_apply_result(result: object) -> str:
    if not isinstance(result, dict):
        return "配置已应用。"
    if result.get("error"):
        return user_message_from_error(result)

    enabled = result.get("enabled_tasks")
    disabled = result.get("disabled_tasks")
    summary = "配置已应用。"
    if enabled is not None or disabled is not None:
        summary = (
            f"配置已应用（启用 {enabled if enabled is not None else '?'} 项，"
            f"禁用 {disabled if disabled is not None else '?'} 项）。"
        )

    boot = result.get("boot_task")
    installed_flag = result.get("boot_task_installed")
    if isinstance(boot, dict) and (
        boot.get("Installed") is True
        or boot.get("installed") is True
        or boot.get("State")
        or boot.get("state")
        or boot.get("TaskName")
        or boot.get("task_name")
    ):
        state = boot.get("State") or boot.get("state") or "未知"
        last = boot.get("LastRunTime") or boot.get("last_run_time") or "无"
        display = boot.get("Display") or boot.get("display") or r"\SystemAutorun\Boot"
        return f"{summary}\nBoot Task：{display}\n状态：{state}\n上次运行：{last}"

    if installed_flag is True:
        return f"{summary}\nBoot Task 已安装。"

    return (
        f"{summary}\n"
        "Boot Task 尚未安装（计划任务 \\SystemAutorun\\Boot）。\n"
        "如需开机自动启动，请在「工具 → 安装服务」中以管理员身份安装。"
    )


# ─── 全局样式 ────────────────────────────────────────────────────

THEME = {
    'bg':          '#131116',
    'bg_card':     '#211f27',
    'bg_inset':    '#1b191f',
    'bg_hover':    '#2c2932',
    'border':      '#4b4652',
    'border_dim':  '#332f39',
    'fg':          '#e6e0e9',
    'fg_dim':      '#c8c0d0',
    'fg_hint':     '#9e90a8',
    'primary':     '#58418f',
    'primary_hi':  '#6750a4',
    'primary_fg':  '#f4efff',
    'accent':      '#bfabf1',
    'success':     '#7ee787',
    'error':       '#ffb4ab',
    'warn':        '#ffd166',
    'off':         '#9e90a8',
}


def apply_dark_theme(app: QtWidgets.QApplication) -> None:
    palette = QtGui.QPalette()
    palette.setColor(QtGui.QPalette.Window,            QtGui.QColor(THEME['bg']))
    palette.setColor(QtGui.QPalette.WindowText,        QtGui.QColor(THEME['fg']))
    palette.setColor(QtGui.QPalette.Base,              QtGui.QColor(THEME['bg']))
    palette.setColor(QtGui.QPalette.AlternateBase,     QtGui.QColor(THEME['bg_inset']))
    palette.setColor(QtGui.QPalette.Text,              QtGui.QColor(THEME['fg']))
    palette.setColor(QtGui.QPalette.Button,            QtGui.QColor(THEME['bg_card']))
    palette.setColor(QtGui.QPalette.ButtonText,        QtGui.QColor(THEME['fg']))
    palette.setColor(QtGui.QPalette.Highlight,         QtGui.QColor(THEME['primary']))
    palette.setColor(QtGui.QPalette.HighlightedText,   QtGui.QColor(THEME['fg']))
    app.setPalette(palette)
    app.setStyle('Fusion')
    app.setStyleSheet(f"""
        QWidget {{
            background: {THEME['bg']};
            color: {THEME['fg']};
            font-family: "Microsoft YaHei UI", "PingFang SC", "Noto Sans CJK SC", sans-serif;
            font-size: 13px;
        }}
        QLabel {{
            background: transparent;
        }}
        QMainWindow, QDialog {{
            background: {THEME['bg']};
        }}
        QFrame#CardFrame, QFrame#DetailFrame, QFrame#ActionFrame {{
            background: {THEME['bg_card']};
            border: 1px solid {THEME['border']};
            border-radius: 0px;
        }}
        QFrame#PanelSection {{
            background: {THEME['bg_inset']};
            border: 1px solid {THEME['border_dim']};
            border-radius: 0px;
        }}
        QWidget#HeaderBar, QWidget#TableToolbar, QWidget#SidePanel {{
            background: transparent;
        }}
        QLabel#PageTitle {{
            font-size: 24px;
            font-weight: 500;
            color: {THEME['fg']};
        }}
        QLabel#PageSubtitle {{
            color: {THEME['fg_hint']};
            font-size: 12px;
        }}
        QLabel#SectionTitle {{
            color: {THEME['fg']};
            font-size: 14px;
            font-weight: 500;
        }}
        QLabel#PanelTitle {{
            color: {THEME['fg']};
            font-size: 16px;
            font-weight: 600;
        }}
        QLabel#MetricValue {{
            color: {THEME['fg']};
            font-size: 20px;
            font-weight: 700;
        }}
        QLabel#MetricLabel {{
            color: {THEME['fg_hint']};
            font-size: 11px;
        }}
        QLabel#HintLabel {{ color: {THEME['fg_hint']}; }}
        QLabel#DetailLabel {{ color: {THEME['fg_dim']}; }}
        QTableWidget {{
            background: {THEME['bg_card']};
            alternate-background-color: {THEME['bg_inset']};
            color: {THEME['fg']};
            gridline-color: {THEME['border']};
            border: none;
            selection-background-color: #342a4a;
            selection-color: {THEME['fg']};
        }}
        QTableWidget::item {{ padding: 8px 10px; border-bottom: 1px solid {THEME['border_dim']}; }}
        QTableWidget::item:hover {{ background: {THEME['bg_hover']}; }}
        QTableWidget::item:selected {{ background: #342a4a; color: {THEME['fg']}; }}
        QHeaderView::section {{
            background: {THEME['bg_inset']};
            color: {THEME['fg_dim']};
            padding: 9px 8px;
            border: none;
            border-bottom: 1px solid {THEME['border']};
            font-weight: 500;
        }}
        QPushButton {{
            background: {THEME['bg_card']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            border-radius: 0px;
            padding: 9px 14px;
            min-height: 26px;
            font-weight: 500;
        }}
        QPushButton:hover {{
            background: {THEME['bg_hover']};
            border-color: {THEME['primary']};
        }}
        QPushButton:focus {{
            border: 1px solid {THEME['accent']};
        }}
        QPushButton:disabled {{
            color: #585460;
            background: {THEME['bg_inset']};
            border-color: {THEME['border_dim']};
        }}
        QPushButton#PrimaryButton {{
            background: {THEME['primary']};
            color: {THEME['primary_fg']};
            border: 1px solid {THEME['primary']};
            font-weight: 500;
        }}
        QPushButton#PrimaryButton:hover {{
            background: {THEME['primary_hi']};
            border-color: {THEME['primary_hi']};
        }}
        QPushButton#DangerButton {{
            background: {THEME['bg_card']};
            color: {THEME['error']};
            border: 1px solid {THEME['error']};
        }}
        QPushButton#DangerButton:hover {{
            background: #2d1d23;
        }}
        QPushButton#SubtleButton {{
            background: {THEME['bg_inset']};
            color: {THEME['fg_dim']};
            border-color: {THEME['border_dim']};
        }}
        QPushButton#SubtleButton:hover {{
            color: {THEME['fg']};
            border-color: {THEME['accent']};
        }}
        QPushButton#PanelActionButton {{
            background: {THEME['bg_inset']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            border-left: 3px solid {THEME['border']};
            padding: 8px 14px;
            min-height: 34px;
            text-align: center;
        }}
        QPushButton#PanelActionButton:hover {{
            background: {THEME['bg_hover']};
            border-color: {THEME['accent']};
            border-left-color: {THEME['accent']};
        }}
        QPushButton#PanelActionButton:focus {{
            border-color: {THEME['accent']};
            border-left-color: {THEME['accent']};
        }}
        QPushButton#PanelActionButton:disabled {{
            color: #686270;
            background: {THEME['bg_inset']};
            border-color: {THEME['border_dim']};
            border-left-color: {THEME['border_dim']};
        }}
        QPushButton#PanelPrimaryButton {{
            background: {THEME['primary']};
            color: {THEME['primary_fg']};
            border: 1px solid {THEME['primary']};
            border-left: 3px solid {THEME['accent']};
            padding: 8px 14px;
            min-height: 34px;
            text-align: center;
            font-weight: 600;
        }}
        QPushButton#PanelPrimaryButton:hover {{
            background: {THEME['primary_hi']};
            border-color: {THEME['accent']};
        }}
        QPushButton#PanelPrimaryButton:disabled {{
            color: #686270;
            background: {THEME['bg_inset']};
            border-color: {THEME['border_dim']};
            border-left-color: {THEME['border_dim']};
        }}
        QPushButton#PanelDangerButton {{
            background: {THEME['bg_inset']};
            color: {THEME['error']};
            border: 1px solid {THEME['border']};
            border-left: 3px solid {THEME['error']};
            padding: 8px 14px;
            min-height: 34px;
            text-align: center;
        }}
        QPushButton#PanelDangerButton:hover {{
            background: #2d1d23;
            border-color: {THEME['error']};
        }}
        QPushButton#PanelDangerButton:disabled {{
            color: #686270;
            background: {THEME['bg_inset']};
            border-color: {THEME['border_dim']};
            border-left-color: {THEME['border_dim']};
        }}
        QLineEdit, QComboBox {{
            background: {THEME['bg_inset']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            border-radius: 0px;
            min-height: 32px;
            padding: 5px 10px;
            selection-background-color: {THEME['primary']};
        }}
        QLineEdit:focus, QComboBox:focus {{
            border-color: {THEME['accent']};
        }}
        QComboBox::drop-down {{
            border: none;
            width: 24px;
        }}
        QComboBox QAbstractItemView {{
            background: {THEME['bg_card']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            selection-background-color: {THEME['primary']};
        }}
        QTextEdit, QPlainTextEdit {{
            background: {THEME['bg_inset']};
            color: {THEME['fg_dim']};
            border: 1px solid {THEME['border']};
            border-radius: 0px;
            padding: 8px;
            selection-background-color: {THEME['primary']};
            selection-color: {THEME['fg']};
            font-family: "Cascadia Code", "Consolas", monospace;
            font-size: 12px;
        }}
        QCheckBox {{ spacing: 8px; background: transparent; }}
        QCheckBox::indicator {{
            width: 16px; height: 16px;
            border: 1px solid {THEME['border']};
            background: {THEME['bg_card']};
            border-radius: 0px;
        }}
        QCheckBox::indicator:checked {{
            background: {THEME['primary']};
            border: 1px solid {THEME['primary']};
        }}
        QMenuBar {{
            background: {THEME['bg']};
            color: {THEME['fg']};
            border-bottom: 1px solid {THEME['border']};
        }}
        QMenuBar::item:selected {{ background: {THEME['bg_inset']}; }}
        QMenu {{
            background: {THEME['bg_card']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
        }}
        QMenu::item:selected {{ background: {THEME['primary']}; }}
        QToolTip {{
            background: {THEME['bg_card']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            padding: 6px 8px;
        }}
        QSpinBox {{
            background: {THEME['bg_inset']};
            color: {THEME['fg']};
            border: 1px solid {THEME['border']};
            border-radius: 0px;
            min-height: 32px;
            padding: 5px 10px;
            selection-background-color: {THEME['primary']};
        }}
        QSpinBox:focus {{ border-color: {THEME['accent']}; }}
        QStatusBar {{
            background: {THEME['bg']};
            color: {THEME['fg_hint']};
            border-top: 1px solid {THEME['border']};
        }}
        QProgressBar#BusyBar {{
            background: {THEME['bg_inset']};
            border: none;
            border-radius: 0px;
        }}
        QProgressBar#BusyBar::chunk {{
            background: {THEME['accent']};
            border-radius: 0px;
        }}
        QSplitter::handle {{
            background: {THEME['bg']};
        }}
        QSplitter::handle:horizontal {{
            width: 8px;
        }}
        QScrollBar:vertical {{
            background: {THEME['bg']};
            width: 8px; border: none;
        }}
        QScrollBar::handle:vertical {{
            background: {THEME['border']};
            min-height: 30px;
        }}
        QScrollBar::handle:vertical:hover {{ background: {THEME['primary']}; }}
        QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height: 0; }}
        QScrollBar:horizontal {{
            background: {THEME['bg']};
            height: 8px; border: none;
        }}
        QScrollBar::handle:horizontal {{
            background: {THEME['border']};
            min-width: 30px;
        }}
        QScrollBar::handle:horizontal:hover {{ background: {THEME['primary']}; }}
        QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {{ width: 0; }}
    """)


# ─── 自定义组件 ──────────────────────────────────────────────────

class StatusBadge(QtWidgets.QLabel):
    """彩色状态徽章。"""
    COLORS = {
        'OK':   (THEME['success'], THEME['bg_inset']),
        'FAIL': (THEME['error'],   THEME['bg_inset']),
        'OFF':  (THEME['off'],     THEME['bg_inset']),
    }

    def __init__(self, status: str = 'OFF'):
        super().__init__()
        self.setAlignment(QtCore.Qt.AlignCenter)
        self.setMinimumWidth(64)
        self.setMinimumHeight(26)
        self.set_status(status)

    def set_status(self, status: str):
        fg, bg = self.COLORS.get(status, self.COLORS['OFF'])
        self.setText(status)
        self.setStyleSheet(
            f"color:{fg}; background:{bg}; border:1px solid {fg}; "
            f"padding:3px 10px; font-weight:600; font-size:11px; border-radius:0px;"
        )


class CachedTableWidget(QtWidgets.QTableWidget):
    """Table tuned for frequent cache-to-live data replacement."""

    def __init__(self, rows: int = 0, columns: int = 0, parent=None):
        super().__init__(rows, columns, parent)
        self.setWordWrap(False)

    def replace_rows(self, row_count: int) -> None:
        self.setSortingEnabled(False)
        self.setUpdatesEnabled(False)
        self.blockSignals(True)
        self.clearContents()
        self.setRowCount(row_count)

    def finish_rows(self, sorting: bool = True) -> None:
        self.blockSignals(False)
        self.setUpdatesEnabled(True)
        self.setSortingEnabled(sorting)
        self.viewport().update()


# ─── 主窗口 ──────────────────────────────────────────────────────

class AutorunWindow(QtWidgets.QMainWindow):
    # 右侧操作区需要的舒适宽度（两列按钮 + 内边距，避免中文标签挤在一起）
    RIGHT_PANEL_MIN = 420
    RIGHT_PANEL_PREF = 460

    def __init__(self):
        super().__init__()
        self.setWindowTitle("系统自启动管理器")
        # 默认略放大：保证右侧 2 列按钮与顶栏四键有足够空间
        self.resize(1360, 880)
        self.setMinimumSize(1180, 760)

        self._selected_task: Optional[str] = None
        self._auto_refresh = False
        self._tasks: list[dict] = []
        self._counts = {"OK": 0, "FAIL": 0, "OFF": 0, "TOTAL": 0}
        self._gui_settings = load_gui_settings()
        self._self_startup_enabled = False
        self._busy_depth = 0
        self._current_task: dict[str, Any] | None = None
        self._prefetch_scheduled = False

        self._auto_refresh_timer = QtCore.QTimer(self)
        self._auto_refresh_timer.setInterval(10000)
        self._auto_refresh_timer.timeout.connect(self._auto_refresh_tick)

        self._build_menu()
        self._build_ui()
        self._build_statusbar()

        QtCore.QTimer.singleShot(0, lambda: self.refresh_list(show_cached=True))
        QtCore.QTimer.singleShot(700, self.refresh_self_startup)
        if self._gui_settings.get("auto_start_missing_on_launch"):
            QtCore.QTimer.singleShot(1600, self.start_missing_tasks_silent)

    # ─── UI 构建 ────────────────────────────────────────────────

    def _build_menu(self):
        menubar = self.menuBar()

        m_file = menubar.addMenu("文件(&F)")
        m_file.addAction("编辑配置 (autorun.yaml)", self.edit_yaml)
        m_file.addAction("打开日志目录",            self.open_logs_folder)
        m_file.addSeparator()
        m_file.addAction("退出",                    self.close)

        m_tools = menubar.addMenu("工具(&T)")
        m_tools.addAction("系统诊断 (Doctor)",  self.open_doctor)
        m_tools.addAction("扫描所有启动项",      self.open_scan)
        m_tools.addAction("管理自动启动项",      self.open_startup_manager)
        m_tools.addAction("添加程序任务",        self.open_add_program_dialog)
        m_tools.addSeparator()
        m_tools.addAction("安装服务 (开机自启)",  self.install_service)
        m_tools.addAction("卸载服务",             self.uninstall_service)
        m_tools.addAction("让本工具随登录启动",    self.enable_self_startup)
        m_tools.addAction("取消本工具随登录启动",  self.disable_self_startup)
        m_tools.addAction("应用配置 (同步)",      self.apply_config)

        m_help = menubar.addMenu("帮助(&H)")
        m_help.addAction("关于", self.open_about)

    def _build_ui(self):
        central = QtWidgets.QWidget()
        self.setCentralWidget(central)
        layout = QtWidgets.QVBoxLayout(central)
        layout.setContentsMargins(18, 14, 18, 14)
        layout.setSpacing(12)

        # 左右分栏：右侧顶栏按钮左缘与侧栏左缘对齐；按钮间距与侧栏一致（8px）
        splitter = QtWidgets.QSplitter(QtCore.Qt.Horizontal)
        splitter.setChildrenCollapsible(False)

        table_frame = QtWidgets.QFrame()
        table_frame.setObjectName('CardFrame')
        tfl = QtWidgets.QVBoxLayout(table_frame)
        tfl.setContentsMargins(0, 0, 0, 0)
        tfl.setSpacing(0)

        table_toolbar = QtWidgets.QWidget()
        table_toolbar.setObjectName("TableToolbar")
        tbl = QtWidgets.QHBoxLayout(table_toolbar)
        tbl.setContentsMargins(12, 10, 12, 8)
        tbl.setSpacing(8)

        table_title = QtWidgets.QLabel("任务列表")
        table_title.setObjectName('SectionTitle')
        tbl.addWidget(table_title)
        self.table_count_label = QtWidgets.QLabel("")
        self.table_count_label.setObjectName("HintLabel")
        tbl.addWidget(self.table_count_label)
        tbl.addStretch(1)

        self.search_edit = QtWidgets.QLineEdit()
        self.search_edit.setPlaceholderText("搜索名称、类型、原因或详情")
        self.search_edit.setAccessibleName("搜索任务")
        self.search_edit.setClearButtonEnabled(True)
        self.search_edit.setMinimumWidth(180)
        self.search_edit.textChanged.connect(self._render_tasks)
        tbl.addWidget(self.search_edit, 1)

        self.status_filter = QtWidgets.QComboBox()
        self.status_filter.addItem("全部状态", "")
        self.status_filter.addItem("正常 OK", "OK")
        self.status_filter.addItem("异常 FAIL", "FAIL")
        self.status_filter.addItem("禁用 OFF", "OFF")
        self.status_filter.setAccessibleName("按任务状态筛选")
        self.status_filter.currentIndexChanged.connect(self._render_tasks)
        tbl.addWidget(self.status_filter, 0)

        self.type_filter = QtWidgets.QComboBox()
        self.type_filter.addItem("全部类型", "")
        self.type_filter.addItem("Windows", "windows")
        self.type_filter.addItem("WSL Boot", "wsl_boot")
        self.type_filter.addItem("WSL systemd", "wsl_systemd")
        self.type_filter.addItem("WSL command", "wsl_command")
        self.type_filter.setAccessibleName("按任务类型筛选")
        self.type_filter.currentIndexChanged.connect(self._render_tasks)
        tbl.addWidget(self.type_filter, 0)

        self.cb_auto_refresh = QtWidgets.QCheckBox("自动刷新")
        self.cb_auto_refresh.setToolTip("每 10 秒刷新一次任务状态")
        self.cb_auto_refresh.toggled.connect(self._toggle_auto_refresh)
        tbl.addWidget(self.cb_auto_refresh, 0)

        tfl.addWidget(table_toolbar)

        self.table = CachedTableWidget(0, 6)
        self.table.setHorizontalHeaderLabels(["状态", "名称", "类型", "Distro", "原因", "详情"])
        self.table.verticalHeader().setVisible(False)
        self.table.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.setEditTriggers(QtWidgets.QAbstractItemView.NoEditTriggers)
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.setSortingEnabled(True)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.horizontalHeader().setSectionResizeMode(0, QtWidgets.QHeaderView.Fixed)
        self.table.setColumnWidth(0, 80)
        self.table.setColumnWidth(1, 220)
        self.table.setColumnWidth(2, 110)
        self.table.setColumnWidth(3, 100)
        self.table.setColumnWidth(4, 200)
        self.table.verticalHeader().setDefaultSectionSize(34)
        self.table.itemSelectionChanged.connect(self._on_select)
        self.table.doubleClicked.connect(self.show_status_detail)
        tfl.addWidget(self.table)

        splitter.addWidget(table_frame)

        # 右侧列：顶栏工具键 + 详情操作面板（统一按钮尺寸，避免默认高度下挤在一起）
        btn_h = 38
        btn_gap = 10
        right_column = QtWidgets.QWidget()
        right_column.setMinimumWidth(self.RIGHT_PANEL_MIN)
        right_column.setMaximumWidth(520)
        rcl = QtWidgets.QVBoxLayout(right_column)
        rcl.setContentsMargins(0, 0, 0, 0)
        rcl.setSpacing(10)

        def _style_panel_button(btn: QtWidgets.QPushButton, object_name: str) -> None:
            btn.setObjectName(object_name)
            btn.setMinimumHeight(btn_h)
            btn.setMinimumWidth(120)
            btn.setSizePolicy(QtWidgets.QSizePolicy.Expanding, QtWidgets.QSizePolicy.Fixed)

        # 顶栏：仅保留「刷新」「添加程序」同一行
        top_bar = QtWidgets.QWidget()
        top_bar.setObjectName("HeaderBar")
        top_row = QtWidgets.QHBoxLayout(top_bar)
        top_row.setContentsMargins(0, 0, 0, 0)
        top_row.setSpacing(btn_gap)

        self.btn_refresh = QtWidgets.QPushButton("刷新")
        _style_panel_button(self.btn_refresh, "PanelPrimaryButton")
        self.btn_refresh.clicked.connect(self.refresh_list)

        self.btn_add_program = QtWidgets.QPushButton("添加程序")
        _style_panel_button(self.btn_add_program, "PanelActionButton")
        self.btn_add_program.clicked.connect(self.open_add_program_dialog)

        self.top_buttons: list[QtWidgets.QPushButton] = [
            self.btn_refresh,
            self.btn_add_program,
        ]
        top_row.addWidget(self.btn_refresh, 1)
        top_row.addWidget(self.btn_add_program, 1)
        rcl.addWidget(top_bar, 0)

        side_panel = QtWidgets.QFrame()
        side_panel.setObjectName('ActionFrame')
        spl = QtWidgets.QVBoxLayout(side_panel)
        spl.setContentsMargins(16, 14, 16, 14)
        spl.setSpacing(12)

        detail_title = QtWidgets.QLabel("任务详情")
        detail_title.setObjectName('PanelTitle')
        spl.addWidget(detail_title)

        self.detail_badge = StatusBadge("OFF")
        self.detail_badge.setToolTip("当前选中任务状态")
        spl.addWidget(self.detail_badge, 0, QtCore.Qt.AlignLeft)

        self.detail_hint = QtWidgets.QLabel("选择左侧任务查看状态与操作。")
        self.detail_hint.setObjectName("HintLabel")
        self.detail_hint.setWordWrap(True)
        spl.addWidget(self.detail_hint)

        # 详情文案压缩高度，把垂直空间让给按钮区
        self.detail_text = QtWidgets.QLabel("")
        self.detail_text.setObjectName("DetailLabel")
        self.detail_text.setWordWrap(True)
        self.detail_text.setMinimumHeight(44)
        self.detail_text.setMaximumHeight(72)
        spl.addWidget(self.detail_text, 0)

        action_title = QtWidgets.QLabel("操作")
        action_title.setObjectName('SectionTitle')
        spl.addWidget(action_title)

        action_grid = QtWidgets.QGridLayout()
        action_grid.setContentsMargins(0, 0, 0, 0)
        action_grid.setHorizontalSpacing(btn_gap)
        action_grid.setVerticalSpacing(btn_gap)
        action_grid.setColumnStretch(0, 1)
        action_grid.setColumnStretch(1, 1)
        self.action_buttons: list[QtWidgets.QPushButton] = []
        for text, slot, name, attr, row, column in [
            ("启动", self.start_selected, 'PanelPrimaryButton', 'btn_start', 0, 0),
            ("停止", self.stop_selected, 'PanelDangerButton', 'btn_stop', 0, 1),
            ("重启", self.restart_selected, 'PanelActionButton', 'btn_restart', 1, 0),
            ("启用", self.enable_selected, 'PanelActionButton', 'btn_enable', 1, 1),
            ("禁用", self.disable_selected, 'PanelDangerButton', 'btn_disable', 2, 0),
            ("移出列表", self.remove_selected_from_list, 'PanelDangerButton', 'btn_remove', 2, 1),
        ]:
            btn = QtWidgets.QPushButton(text)
            _style_panel_button(btn, name)
            btn.clicked.connect(slot)
            btn.setEnabled(False)
            setattr(self, attr, btn)
            self.action_buttons.append(btn)
            action_grid.addWidget(btn, row, column, 1, 1)
        spl.addLayout(action_grid)

        secondary_title = QtWidgets.QLabel("辅助")
        secondary_title.setObjectName('SectionTitle')
        spl.addWidget(secondary_title)

        self.secondary_buttons: list[QtWidgets.QPushButton] = []
        secondary_grid = QtWidgets.QGridLayout()
        secondary_grid.setContentsMargins(0, 0, 0, 0)
        secondary_grid.setHorizontalSpacing(btn_gap)
        secondary_grid.setVerticalSpacing(btn_gap)
        secondary_grid.setColumnStretch(0, 1)
        secondary_grid.setColumnStretch(1, 1)
        for text, slot, row, column in [
            ("查看日志", self.open_logs_for_selected, 0, 0),
            ("详细信息", self.show_status_detail, 0, 1),
            ("打开所在目录", self.open_exec_path, 1, 0),
        ]:
            btn = QtWidgets.QPushButton(text)
            _style_panel_button(btn, "PanelActionButton")
            btn.clicked.connect(slot)
            btn.setEnabled(False)
            self.secondary_buttons.append(btn)
            column_span = 2 if text == "打开所在目录" else 1
            secondary_grid.addWidget(btn, row, column, 1, column_span)
        spl.addLayout(secondary_grid)

        global_title = QtWidgets.QLabel("全局选项")
        global_title.setObjectName('SectionTitle')
        spl.addWidget(global_title)

        self.cb_self_startup = QtWidgets.QCheckBox("本工具随用户登录启动")
        self.cb_self_startup.setToolTip("写入 HKCU Run，不需要管理员权限。")
        self.cb_self_startup.toggled.connect(self._toggle_self_startup)
        spl.addWidget(self.cb_self_startup)

        self.cb_start_missing_on_launch = QtWidgets.QCheckBox("启动后自动补启动未运行任务")
        self.cb_start_missing_on_launch.setToolTip("工具启动后，自动启动任务列表中已启用但健康检查失败的任务。")
        self.cb_start_missing_on_launch.setChecked(bool(self._gui_settings.get("auto_start_missing_on_launch")))
        self.cb_start_missing_on_launch.toggled.connect(self._toggle_start_missing_on_launch)
        spl.addWidget(self.cb_start_missing_on_launch)

        self.global_buttons: list[QtWidgets.QPushButton] = []
        for text, slot in [
            ("立即补启动未运行任务", self.start_missing_tasks),
        ]:
            btn = QtWidgets.QPushButton(text)
            _style_panel_button(btn, "PanelActionButton")
            btn.clicked.connect(slot)
            self.global_buttons.append(btn)
            spl.addWidget(btn)

        spl.addStretch(1)
        rcl.addWidget(side_panel, 1)
        splitter.addWidget(right_column)
        # 左右初始比例：给右侧足够固定感宽度，避免默认被压窄
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 2)
        splitter.setSizes([880, self.RIGHT_PANEL_PREF])
        self._main_splitter = splitter
        layout.addWidget(splitter, 1)

    def _build_statusbar(self):
        self.statusbar = self.statusBar()
        self.lbl_status = QtWidgets.QLabel("就绪。")
        self.busy_bar = QtWidgets.QProgressBar()
        self.busy_bar.setObjectName("BusyBar")
        self.busy_bar.setRange(0, 0)
        self.busy_bar.setTextVisible(False)
        self.busy_bar.setFixedSize(64, 4)
        self.busy_bar.hide()
        self.lbl_count = QtWidgets.QLabel("")
        self.statusbar.addWidget(self.lbl_status, 1)
        self.statusbar.addPermanentWidget(self.busy_bar)
        self.statusbar.addPermanentWidget(self.lbl_count)

    # ─── 数据加载 ───────────────────────────────────────────────

    def refresh_list(self, show_cached: bool = False):
        if show_cached and not self._tasks:
            cached, timestamp = get_cached_cli("list")
            if isinstance(cached, list):
                self._apply_task_data(cached)
                self._set_status(f"已显示缓存（{_format_cache_time(timestamp)}），正在后台更新…")
        if getattr(self, "_list_refreshing", False):
            self._set_status("刷新已在进行中…")
            return
        self._list_refreshing = True
        self._set_refreshing(True)
        self._set_status("正在后台刷新…" if self._tasks else "正在加载任务状态…")
        run_cli_async(
            on_done=self._on_list_loaded,
            on_error=self._on_list_error,
            command="list",
        )

    def _on_list_loaded(self, tasks: list):
        self._list_refreshing = False
        self._set_refreshing(False)
        self._apply_task_data(tasks)
        self._set_status(f"刷新完成（{time.strftime('%H:%M:%S')}）")
        self._schedule_cache_prefetch()

    def _schedule_cache_prefetch(self):
        if self._prefetch_scheduled:
            return
        self._prefetch_scheduled = True
        for delay, command in ((1200, "startup-list"), (2600, "scan"), (4200, "doctor")):
            QtCore.QTimer.singleShot(delay, lambda cmd=command: self._prefetch_command(cmd))

    def _prefetch_command(self, command: str):
        fresh, _timestamp = get_cached_cli(command, max_age=60)
        if fresh is not None:
            return
        run_cli_async(
            on_done=lambda _data: None,
            on_error=lambda exc, cmd=command: append_gui_log(
                f"Background cache prefetch failed for {cmd}: {user_message_from_error(exc)}"
            ),
            command=command,
        )

    def _apply_task_data(self, tasks: list):
        if not isinstance(tasks, list):
            tasks = []
        self._tasks = tasks
        self._counts = {"OK": 0, "FAIL": 0, "OFF": 0, "TOTAL": 0}
        for t in self._tasks:
            s = self._status_text(t)
            self._counts[s] = self._counts.get(s, 0) + 1
            self._counts["TOTAL"] += 1

        self._update_counts_label()
        self._render_tasks()

    def _on_list_error(self, exc: Exception):
        self._list_refreshing = False
        self._set_refreshing(False)
        message = user_message_from_error(exc)
        if self._tasks:
            self._set_status(f"刷新失败，继续显示现有数据：{message}")
            append_gui_log(f"Task list refresh failed: {message}")
            return
        self._on_cli_error(exc)

    def _set_refreshing(self, refreshing: bool):
        if hasattr(self, "btn_refresh"):
            self.btn_refresh.setEnabled(not refreshing and self._busy_depth == 0)
            self.btn_refresh.setText("刷新中…" if refreshing else "刷新")
        if hasattr(self, "busy_bar"):
            self.busy_bar.setVisible(refreshing or self._busy_depth > 0)
        if refreshing:
            for button in getattr(self, "action_buttons", []):
                button.setEnabled(False)
            for button in getattr(self, "global_buttons", []):
                button.setEnabled(False)
        elif self._busy_depth == 0:
            self._set_task_actions_enabled(bool(self._selected_task))
            for button in getattr(self, "global_buttons", []):
                button.setEnabled(True)

    def _render_tasks(self, *_args):
        selected_name = self._selected_task
        query = self.search_edit.text().strip().lower() if hasattr(self, "search_edit") else ""
        status_filter = self.status_filter.currentData() if hasattr(self, "status_filter") else ""
        type_filter = self.type_filter.currentData() if hasattr(self, "type_filter") else ""

        filtered = []
        for task in self._tasks:
            status = self._status_text(task)
            if status_filter and status != status_filter:
                continue
            if type_filter and task.get("type") != type_filter:
                continue
            if query and not self._task_matches_query(task, status, query):
                continue
            filtered.append(task)

        if hasattr(self, "table_count_label"):
            self.table_count_label.setText(f"{len(filtered)} / {len(self._tasks)}")

        self.table.setSortingEnabled(False)
        self.table.setUpdatesEnabled(False)
        self.table.blockSignals(True)
        while self.table.rowCount() > len(filtered):
            self.table.removeRow(self.table.rowCount() - 1)
        while self.table.rowCount() < len(filtered):
            self.table.insertRow(self.table.rowCount())
        for row, t in enumerate(filtered):
            s = self._status_text(t)
            badge = self.table.cellWidget(row, 0)
            if isinstance(badge, StatusBadge):
                badge.set_status(s)
            else:
                self.table.setCellWidget(row, 0, StatusBadge(s))
            for col, value in enumerate([
                t.get("name", ""),
                t.get("type", ""),
                t.get("distro") or "",
                t.get("reason", ""),
                t.get("detail", "") or "",
            ], start=1):
                item = self.table.item(row, col) or QtWidgets.QTableWidgetItem()
                item.setText(str(value))
                item.setToolTip(str(value))
                if col == 4 and s == "FAIL":
                    item.setForeground(QtGui.QColor(THEME['error']))
                else:
                    item.setForeground(QtGui.QColor(THEME['fg']))
                self.table.setItem(row, col, item)

            # 名称列带任务名数据（用于选中）
            name_item = self.table.item(row, 1)
            if name_item:
                name_item.setData(QtCore.Qt.UserRole, t.get("name", ""))
                name_item.setData(QtCore.Qt.UserRole + 1, t)

        self.table.blockSignals(False)
        self.table.setUpdatesEnabled(True)
        self.table.finish_rows()

        if selected_name:
            self._restore_selection(selected_name)
        if not self.table.selectionModel().selectedRows():
            if self.table.rowCount() > 0:
                self.table.selectRow(0)
            else:
                self._clear_selection_detail()

    @staticmethod
    def _task_matches_query(task: dict, status: str, query: str) -> bool:
        fields = [
            status,
            task.get("name", ""),
            task.get("type", ""),
            task.get("distro", ""),
            task.get("reason", ""),
            task.get("detail", ""),
            task.get("description", ""),
            task.get("exec", ""),
        ]
        return query in " ".join(str(v or "").lower() for v in fields)

    def _update_counts_label(self):
        self.lbl_count.setText(
            f"正常 {self._counts.get('OK', 0)}  ·  "
            f"异常 {self._counts.get('FAIL', 0)}  ·  "
            f"禁用 {self._counts.get('OFF', 0)}  ·  "
            f"总数 {self._counts.get('TOTAL', 0)}"
        )

    def _on_cli_error(self, exc: Exception):
        self._end_busy()
        message = user_message_from_error(exc)
        self._set_status(f"错误：{message}")
        QtWidgets.QMessageBox.critical(self, "操作失败", message)

    def _set_status(self, text: str):
        self.lbl_status.setText(text)

    def _begin_busy(self):
        self._busy_depth += 1
        self._set_busy_enabled(False)
        if hasattr(self, "busy_bar"):
            self.busy_bar.show()

    def _end_busy(self):
        self._busy_depth = max(0, self._busy_depth - 1)
        if self._busy_depth == 0:
            self._set_busy_enabled(True)
            if hasattr(self, "busy_bar") and not getattr(self, "_list_refreshing", False):
                self.busy_bar.hide()

    def _set_busy_enabled(self, enabled: bool):
        for btn in getattr(self, "top_buttons", []):
            btn.setEnabled(enabled)
        if hasattr(self, "btn_refresh") and getattr(self, "_list_refreshing", False):
            self.btn_refresh.setEnabled(False)
        for btn in getattr(self, "global_buttons", []):
            btn.setEnabled(enabled)
        if enabled:
            self._set_task_actions_enabled(bool(self._selected_task))
        else:
            self._set_task_actions_enabled(False)
        if hasattr(self, "cb_auto_refresh"):
            self.cb_auto_refresh.setEnabled(enabled)
        if hasattr(self, "cb_self_startup"):
            self.cb_self_startup.setEnabled(enabled)
        if hasattr(self, "cb_start_missing_on_launch"):
            self.cb_start_missing_on_launch.setEnabled(enabled)

    @staticmethod
    def _status_text(task: dict) -> str:
        if not task.get("enabled"):
            return "OFF"
        if task.get("healthy"):
            return "OK"
        return "FAIL"

    def _restore_selection(self, name: str):
        for r in range(self.table.rowCount()):
            item = self.table.item(r, 1)
            if item and item.data(QtCore.Qt.UserRole) == name:
                self.table.selectRow(r)
                return

    # ─── 选中处理 ───────────────────────────────────────────────

    def _on_select(self):
        rows = self.table.selectionModel().selectedRows()
        if not rows:
            self._clear_selection_detail()
            return
        row = rows[0].row()
        name_item = self.table.item(row, 1)
        if not name_item:
            return
        name = name_item.data(QtCore.Qt.UserRole) or name_item.text()
        task = name_item.data(QtCore.Qt.UserRole + 1) or {}
        self._selected_task = name
        self._current_task = task

        status_widget = self.table.cellWidget(row, 0)
        status_text = status_widget.text() if status_widget else ""

        type_text = self.table.item(row, 2).text() if self.table.item(row, 2) else ""
        distro_text = self.table.item(row, 3).text() if self.table.item(row, 3) else ""
        reason_text = self.table.item(row, 4).text() if self.table.item(row, 4) else ""
        detail_text = self.table.item(row, 5).text() if self.table.item(row, 5) else ""

        text = (
            f"类型      : {type_text or '-'}\n"
            f"Distro    : {distro_text or '-'}\n"
            f"原因      : {reason_text or '-'}\n"
            f"详情      : {detail_text or '-'}"
        )
        self.detail_text.setText(text)
        self.detail_hint.setText(f"已选中：{name}")
        if hasattr(self, "detail_badge"):
            self.detail_badge.set_status(status_text or "OFF")
        self._set_task_actions_enabled(True)

    def _clear_selection_detail(self):
        self._selected_task = None
        self._current_task = None
        if hasattr(self, "detail_text"):
            self.detail_text.setText("")
        if hasattr(self, "detail_hint"):
            self.detail_hint.setText("选择左侧任务查看状态与操作。")
        if hasattr(self, "detail_badge"):
            self.detail_badge.set_status("OFF")
        self._set_task_actions_enabled(False)

    def _set_task_actions_enabled(self, enabled: bool):
        task = self._current_task or {}
        status = self._status_text(task) if task else "OFF"
        for attr, state in (
            ("btn_start", enabled and status == "FAIL"),
            ("btn_stop", enabled and status == "OK"),
            ("btn_restart", enabled and status == "OK"),
            ("btn_enable", enabled and status == "OFF"),
            ("btn_disable", enabled and status != "OFF"),
            ("btn_remove", enabled),
        ):
            button = getattr(self, attr, None)
            if button:
                button.setEnabled(state)
        for btn in getattr(self, "secondary_buttons", []):
            btn.setEnabled(enabled)

    # ─── 自动刷新 ───────────────────────────────────────────────

    def _toggle_auto_refresh(self, checked: bool):
        self._auto_refresh = checked
        if checked:
            self._auto_refresh_timer.start()
            self._auto_refresh_tick()
        else:
            self._auto_refresh_timer.stop()

    def _auto_refresh_tick(self):
        if not self._auto_refresh:
            return
        if self._busy_depth == 0:
            self.refresh_list()

    # ─── 操作（Start/Stop/Restart/Enable/Disable） ─────────────

    def _require_selection(self) -> bool:
        if not self._selected_task:
            QtWidgets.QMessageBox.information(self, "未选中", "请先在表格中选择一个任务。")
            return False
        return True

    def _patch_task_row(self, task_row: dict) -> bool:
        """用动作返回的单行快照更新列表，避免再跑整表 list。"""
        if not isinstance(task_row, dict):
            return False
        name = task_row.get("name")
        if not name or not self._tasks:
            return False
        updated = False
        for index, existing in enumerate(self._tasks):
            if existing.get("name") == name:
                merged = dict(existing)
                merged.update(task_row)
                self._tasks[index] = merged
                updated = True
                break
        if not updated:
            return False
        selected = self._selected_task
        self._apply_task_data(self._tasks)
        if selected:
            self._restore_selection(selected)
        return True

    def _do_action(self, command: str, success_key: str):
        if not self._require_selection():
            return
        name = self._selected_task
        self._begin_busy()
        self._set_status(f"正在{action_label(command)} {name}…")

        def on_done(result):
            self._end_busy()
            ok, message = format_action_result(command, name, result)
            if ok:
                invalidate_cli_cache({"list", "status", "doctor"})
                self._set_status(message.replace("\n", " "))
                # 优先局部更新；没有 task 快照时才回退整表刷新
                patched = False
                if isinstance(result, dict):
                    patched = self._patch_task_row(result.get("task") or {})
                if not patched:
                    self.refresh_list()
            else:
                self._set_status(message)
                QtWidgets.QMessageBox.warning(self, "操作未完成", message)
                self.refresh_list()

        run_cli_async(on_done=on_done, on_error=self._on_cli_error,
                      command=command, args=[name])

    def start_selected(self):   self._do_action("start",   "started")
    def stop_selected(self):    self._do_action("stop",    "stopped")
    def restart_selected(self): self._do_action("restart", "restarted")
    def enable_selected(self):  self._do_action("enable",  "enabled")
    def disable_selected(self): self._do_action("disable", "disabled")

    def remove_selected_from_list(self):
        """从 autorun.yaml 移出当前任务（不停止进程）。"""
        if not self._require_selection():
            return
        name = self._selected_task
        answer = QtWidgets.QMessageBox.question(
            self,
            "移出列表",
            f"确定将「{name}」从任务列表中移除吗？\n\n"
            "将从 autorun.yaml 删除该任务配置。\n"
            "不会停止当前已在运行的进程。",
            QtWidgets.QMessageBox.Yes | QtWidgets.QMessageBox.No,
            QtWidgets.QMessageBox.No,
        )
        if answer != QtWidgets.QMessageBox.Yes:
            return

        self._begin_busy()
        self._set_status(f"正在移出列表 {name}…")

        def on_done(result):
            self._end_busy()
            ok, message = format_action_result("remove-task", name, result)
            if ok:
                invalidate_cli_cache({"list", "status", "doctor", "scan", "startup-list"})
                self._selected_task = None
                self._current_task = None
                self._set_status(message.replace("\n", " "))
                self.refresh_list()
            else:
                self._set_status(message)
                QtWidgets.QMessageBox.warning(self, "移出失败", message)

        run_cli_async(
            on_done=on_done,
            on_error=self._on_cli_error,
            command="remove-task",
            args=[name],
        )

    # ─── 全局启动选项 ───────────────────────────────────────────

    def refresh_self_startup(self):
        self._begin_busy()
        run_cli_async(on_done=self._on_self_startup_loaded,
                      on_error=self._on_cli_error,
                      command="self-startup", args=["status"])

    def _on_self_startup_loaded(self, result):
        self._end_busy()
        enabled = bool(result.get("enabled")) if isinstance(result, dict) else False
        self._self_startup_enabled = enabled
        if hasattr(self, "cb_self_startup"):
            self.cb_self_startup.blockSignals(True)
            self.cb_self_startup.setChecked(enabled)
            self.cb_self_startup.blockSignals(False)

    def _toggle_self_startup(self, checked: bool):
        command_arg = "enable" if checked else "disable"
        self._begin_busy()
        self._set_status("正在更新本工具登录自启…")
        run_cli_async(on_done=self._on_self_startup_updated,
                      on_error=self._on_self_startup_error,
                      command="self-startup", args=[command_arg])

    def _on_self_startup_updated(self, result):
        invalidate_cli_cache({"scan", "startup-list"})
        self._on_self_startup_loaded(result)
        self._set_status("本工具登录自启已更新")

    def _on_self_startup_error(self, exc: Exception):
        if hasattr(self, "cb_self_startup"):
            self.cb_self_startup.blockSignals(True)
            self.cb_self_startup.setChecked(self._self_startup_enabled)
            self.cb_self_startup.blockSignals(False)
        self._on_cli_error(exc)

    def enable_self_startup(self):
        if hasattr(self, "cb_self_startup"):
            self.cb_self_startup.setChecked(True)
        else:
            self._toggle_self_startup(True)

    def disable_self_startup(self):
        if hasattr(self, "cb_self_startup"):
            self.cb_self_startup.setChecked(False)
        else:
            self._toggle_self_startup(False)

    def _toggle_start_missing_on_launch(self, checked: bool):
        self._gui_settings["auto_start_missing_on_launch"] = bool(checked)
        save_gui_settings(self._gui_settings)
        self._set_status("自动补启动选项已保存")

    def start_missing_tasks(self):
        self._begin_busy()
        self._set_status("正在补启动未运行任务…")
        run_cli_async(on_done=self._on_start_missing_done,
                      on_error=self._on_cli_error,
                      command="start-missing", timeout=180)

    def start_missing_tasks_silent(self):
        self._begin_busy()
        self._set_status("正在按设置补启动未运行任务…")
        run_cli_async(on_done=self._on_start_missing_done,
                      on_error=lambda exc: (self._end_busy(), self._set_status(f"自动补启动失败：{exc}")),
                      command="start-missing", timeout=180)

    def _on_start_missing_done(self, result):
        self._end_busy()
        summary = result.get("summary", {}) if isinstance(result, dict) else {}
        started = summary.get("started", 0)
        failed = summary.get("failed", 0)
        skipped = summary.get("skipped", 0)
        self._set_status(f"补启动完成：启动 {started}，跳过 {skipped}，失败 {failed}")
        if failed:
            QtWidgets.QMessageBox.warning(
                self, "补启动完成",
                format_start_missing_result(result)
            )
        invalidate_cli_cache({"list", "status"})
        self.refresh_list()

    # ─── Tools 菜单 ────────────────────────────────────────────

    def open_doctor(self):
        cached, timestamp = get_cached_cli("doctor")
        dialog = DoctorDialog(self, cached, cached_at=timestamp)
        QtCore.QTimer.singleShot(0, dialog.refresh)
        dialog.show()

    def open_scan(self):
        cached, timestamp = get_cached_cli("scan")
        dialog = ScanDialog(self, cached, cached_at=timestamp)
        QtCore.QTimer.singleShot(0, dialog.refresh)
        dialog.show()

    def open_startup_manager(self):
        cached, timestamp = get_cached_cli("startup-list")
        dialog = StartupManagerDialog(self, cached, cached_at=timestamp)
        QtCore.QTimer.singleShot(0, lambda: dialog.refresh(background=True))
        dialog.show()

    def open_add_program_dialog(self, preset: dict | None = None):
        dialog = AddProgramDialog(self, preset or {})
        if dialog.exec() != QtWidgets.QDialog.Accepted:
            return
        payload = dialog.payload()
        if not payload:
            return
        self._begin_busy()
        self._set_status(f"正在添加任务：{payload.get('name')}…")
        run_cli_async(on_done=self._on_add_program_done,
                      on_error=self._on_cli_error,
                      command="add-windows",
                      args=[json.dumps(payload, ensure_ascii=False)])

    def _on_add_program_done(self, result):
        self._end_busy()
        if isinstance(result, dict) and result.get("added"):
            invalidate_cli_cache({"list", "doctor", "scan", "startup-list", "status"})
            self._set_status(f"已添加任务：{result.get('name')}")
            self.refresh_list()
            QtWidgets.QMessageBox.information(
                self, "添加成功",
                f"已添加到任务列表：{result.get('name')}\n\n"
                "若要随系统 Boot Task 自动启动，请确认该任务保持启用。"
            )
        else:
            msg = user_message_from_error(result)
            self._set_status(f"添加失败：{msg}")
            QtWidgets.QMessageBox.warning(self, "添加失败", msg)

    def edit_yaml(self):
        if not AUTORUN_YAML.exists():
            QtWidgets.QMessageBox.critical(self, "未找到", f"配置文件不存在：\n{AUTORUN_YAML}")
            return
        path = str(AUTORUN_YAML.resolve())
        try:
            # 用默认关联程序打开；"edit" 操作在部分机器上无 handler 会静默失败
            os.startfile(path)  # type: ignore[attr-defined]
            self._set_status(f"已打开配置文件：{AUTORUN_YAML.name}")
            return
        except OSError as first_error:
            append_gui_log(f"startfile yaml failed: {first_error}")
        try:
            # 记事本需要显示窗口，勿使用 CREATE_NO_WINDOW
            subprocess.Popen(["notepad.exe", path], shell=False)
            self._set_status(f"已用记事本打开：{AUTORUN_YAML.name}")
            return
        except OSError as second_error:
            append_gui_log(f"notepad yaml failed: {second_error}")
        try:
            # 在资源管理器中选中该文件，便于手动打开
            subprocess.Popen(["explorer.exe", f"/select,{path}"], shell=False)
            self._set_status("已在资源管理器中定位配置文件")
        except OSError as third_error:
            QtWidgets.QMessageBox.critical(
                self,
                "无法打开配置",
                f"无法打开配置文件：\n{path}\n\n{third_error}",
            )

    def open_logs_folder(self):
        if not LOGS_DIR.exists():
            LOGS_DIR.mkdir(parents=True, exist_ok=True)
        os.startfile(str(LOGS_DIR))

    def open_about(self):
        QtWidgets.QMessageBox.about(
            self, "关于",
            "<h3>系统自启动管理器</h3>"
            "<p>SystemAutorun Manager</p>"
            "<p>统一管理 Windows 进程与 WSL systemd 服务的开机启动。</p>"
            f"<p><b>项目目录：</b>{PROJECT_ROOT}</p>"
            f"<p><b>Python：</b>{sys.version.split()[0]}  ·  <b>PySide6：</b>{QtCore.__version__}</p>"
        )

    # ─── 详情面板操作 ──────────────────────────────────────────

    def open_logs_for_selected(self):
        if not self._require_selection(): return
        name = self._selected_task
        args = [name, "-Tail", "200"]
        cached, timestamp = get_cached_cli("logs", args)
        dialog = LogsDialog(self, name, cached, cached_at=timestamp)
        QtCore.QTimer.singleShot(0, dialog.refresh)
        dialog.show()

    def show_status_detail(self):
        if not self._require_selection(): return
        name = self._selected_task
        args = [name]
        cached, timestamp = get_cached_cli("status", args)
        dialog = StatusDialog(self, name, cached, cached_at=timestamp)
        QtCore.QTimer.singleShot(0, dialog.refresh)
        dialog.show()

    def open_exec_path(self):
        if not self._require_selection(): return
        name = self._selected_task
        self._begin_busy()
        run_cli_async(on_done=lambda d: self._on_status_for_path(d, name),
                      on_error=self._on_cli_error,
                      command="status", args=[name])

    def _on_status_for_path(self, data, name: str):
        self._end_busy()
        task = data.get("task") if isinstance(data, dict) else None
        if not task:
            return
        exec_path = task.get("exec")
        if not exec_path:
            QtWidgets.QMessageBox.information(self, "无路径",
                f"任务 '{name}' 没有可执行路径（type={task.get('type')}）")
            return
        target = Path(exec_path)
        if target.is_file():
            os.startfile(str(target.parent))
        else:
            QtWidgets.QMessageBox.warning(self, "路径不存在", f"路径不存在：\n{target}")

    # ─── Install / Uninstall / Apply ───────────────────────────

    def _is_process_elevated(self) -> bool:
        try:
            import ctypes
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        except Exception:
            return False

    def _run_elevated_cli(self, command: str, args: list[str] | None = None) -> dict:
        """以管理员身份执行 install/uninstall（弹出 UAC）。

        关键点：
        - ShellExecute 的可执行文件必须是真实 pwsh.exe，不能是 WindowsApps 占位符
        - 提升脚本内直接 dot-source 并调用 Register，避免再套一层 pwsh
        """
        import ctypes
        from ctypes import wintypes

        pwsh = _resolve_pwsh()
        if _is_pwsh_alias_stub(pwsh) or pwsh.lower() in ("pwsh", "pwsh.exe"):
            raise RuntimeError(
                "未找到可用的 PowerShell 7 真实路径（当前只有 WindowsApps 占位符）。\n"
                "请先安装：winget install Microsoft.PowerShell\n"
                "然后重新打开本工具再安装服务。"
            )

        try:
            LOGS_DIR.mkdir(parents=True, exist_ok=True)
        except Exception:
            pass

        stamp = f"{command}-{os.getpid()}-{int(time.time())}"
        out_file = LOGS_DIR / f"elevated-{stamp}.json"
        script_file = LOGS_DIR / f"elevated-{stamp}.ps1"
        root_ps = str(PROJECT_ROOT).replace("'", "''")
        out_ps = str(out_file).replace("'", "''")
        cmd_ps = command.replace("'", "''")

        # 提升后直接加载 scheduler，不依赖外层 worker / 二次 pwsh
        if command == "install":
            action_ps = """
$reg = Register-AutorunBootTask
$status = Get-BootTaskStatus
if (-not $status) { throw 'Registered but verification failed: task not queryable.' }
$result = [ordered]@{
  installed = $true
  task = $reg
  boot_task = $status
}
"""
        elif command == "uninstall":
            action_ps = """
Unregister-AutorunBootTask
$result = [ordered]@{
  uninstalled = $true
  boot_task_installed = [bool](Get-BootTaskStatus)
}
"""
        else:
            # 通用回退：调用 autorun.ps1 -Json
            action_ps = f"""
$raw = & (Join-Path $root 'autorun.ps1') '{cmd_ps}' -Json
if ($raw -is [array]) {{ $raw = $raw[-1] }}
$result = $raw | ConvertFrom-Json
"""

        script_body = f"""# auto-generated elevated helper — do not edit
$ErrorActionPreference = 'Stop'
$root = '{root_ps}'
$outFile = '{out_ps}'
$global:AutorunProjectRoot = $root
$global:AutorunQuiet = $true
$Json = $true
try {{
  Set-Location -LiteralPath $root
  . (Join-Path $root 'lib\\config.ps1')
  . (Join-Path $root 'lib\\health.ps1')
  . (Join-Path $root 'lib\\logger.ps1')
  . (Join-Path $root 'lib\\scan.ps1')
  . (Join-Path $root 'lib\\tasks.ps1')
  . (Join-Path $root 'lib\\scheduler.ps1')
  . (Join-Path $root 'lib\\startup.ps1')
  . (Join-Path $root 'lib\\json-command.ps1')
{action_ps}
  ($result | ConvertTo-Json -Depth 10 -Compress) | Set-Content -LiteralPath $outFile -Encoding utf8
  exit 0
}} catch {{
  $errObj = [ordered]@{{ error = $_.Exception.Message }}
  ($errObj | ConvertTo-Json -Compress) | Set-Content -LiteralPath $outFile -Encoding utf8
  exit 1
}}
"""
        script_file.write_text(script_body, encoding="utf-8")
        append_gui_log(f"elevated host={pwsh} script={script_file}")

        params = f'-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{script_file}"'
        SEE_MASK_NOCLOSEPROCESS = 0x00000040

        class SHELLEXECUTEINFO(ctypes.Structure):
            _fields_ = [
                ("cbSize", wintypes.DWORD),
                ("fMask", wintypes.ULONG),
                ("hwnd", wintypes.HWND),
                ("lpVerb", wintypes.LPCWSTR),
                ("lpFile", wintypes.LPCWSTR),
                ("lpParameters", wintypes.LPCWSTR),
                ("lpDirectory", wintypes.LPCWSTR),
                ("nShow", ctypes.c_int),
                ("hInstApp", wintypes.HINSTANCE),
                ("lpIDList", ctypes.c_void_p),
                ("lpClass", wintypes.LPCWSTR),
                ("hKeyClass", wintypes.HKEY),
                ("dwHotKey", wintypes.DWORD),
                ("hIcon", wintypes.HANDLE),
                ("hProcess", wintypes.HANDLE),
            ]

        sei = SHELLEXECUTEINFO()
        sei.cbSize = ctypes.sizeof(SHELLEXECUTEINFO)
        sei.fMask = SEE_MASK_NOCLOSEPROCESS
        sei.hwnd = None
        sei.lpVerb = "runas"
        sei.lpFile = pwsh
        sei.lpParameters = params
        sei.lpDirectory = str(PROJECT_ROOT)
        sei.nShow = 0

        if not ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(sei)):
            err = ctypes.GetLastError()
            try:
                script_file.unlink()
            except Exception:
                pass
            if err in (1223, 0x4C7):
                raise RuntimeError("已取消管理员授权（UAC）。")
            raise RuntimeError(
                f"无法以管理员启动 PowerShell（Win32={err}）。\n"
                f"host={pwsh}\n"
                "若持续失败，请右键本程序 → 以管理员身份运行，再点安装服务。"
            )

        exit_code = 1
        if sei.hProcess:
            ctypes.windll.kernel32.WaitForSingleObject(sei.hProcess, 0xFFFFFFFF)
            code = wintypes.DWORD()
            ctypes.windll.kernel32.GetExitCodeProcess(sei.hProcess, ctypes.byref(code))
            exit_code = int(code.value)
            ctypes.windll.kernel32.CloseHandle(sei.hProcess)

        if not out_file.exists():
            raise RuntimeError(
                f"提升进程未写出结果（exit={exit_code}）。\n"
                f"host={pwsh}\n"
                "请尝试：右键 SystemAutorun.exe → 以管理员身份运行 → 安装服务。"
            )

        text = out_file.read_text(encoding="utf-8-sig", errors="replace").strip()
        for p in (out_file, script_file):
            try:
                p.unlink()
            except Exception:
                pass

        data = _parse_cli_json_payload(text)
        if not isinstance(data, dict):
            raise RuntimeError((text or f"提升进程失败 exit={exit_code}")[:500])
        if data.get("error"):
            raise RuntimeError(str(data.get("error")))
        if data.get("installed") is False:
            raise RuntimeError(str(data.get("error") or "安装失败"))
        return data

    def install_service(self):
        ok = QtWidgets.QMessageBox.question(
            self, "安装服务",
            "将注册计划任务 \\SystemAutorun\\Boot（开机时以 SYSTEM 运行 boot.ps1）。\n\n"
            "需要管理员权限（会弹出 UAC）。是否继续？"
        )
        if ok != QtWidgets.QMessageBox.Yes:
            return
        self._begin_busy()
        self._set_status("正在安装 Boot Task（可能弹出 UAC）…")

        def thread_main():
            try:
                # install 必须走最新脚本；已提权用 oneshot，否则 UAC 提升
                if self._is_process_elevated():
                    result = _run_cli_oneshot("install", timeout=120)
                else:
                    result = self._run_elevated_cli("install")
                _cli_dispatcher().done.emit(self._on_install_done, result)
            except Exception as exc:
                _cli_dispatcher().error.emit(self._on_cli_error, exc)

        threading.Thread(target=thread_main, daemon=True).start()

    def _on_install_done(self, result):
        self._end_busy()
        if isinstance(result, dict) and result.get("installed"):
            invalidate_cli_cache({"doctor", "list"})
            boot = result.get("boot_task") if isinstance(result.get("boot_task"), dict) else {}
            state = boot.get("State") or boot.get("state") or "已注册"
            display = boot.get("Display") or r"\SystemAutorun\Boot"
            self._set_status(f"服务已安装：{display}（{state}）")
            QtWidgets.QMessageBox.information(
                self, "安装成功",
                f"Boot Task 已注册并完成校验。\n\n"
                f"任务：{display}\n"
                f"状态：{state}\n\n"
                "下次开机时，所有启用的任务将自动启动（无需登录）。"
            )
            # 立刻再查一次 apply 状态，避免界面与系统不一致
            self.apply_config()
        else:
            msg = user_message_from_error(result)
            self._set_status(f"安装失败：{msg}")
            if "Administrator" in str(msg) or "管理员" in str(msg) or "UAC" in str(msg):
                QtWidgets.QMessageBox.critical(
                    self, "需要管理员权限",
                    "注册 SYSTEM 任务需要管理员权限。\n\n"
                    "请在 UAC 对话框中点「是」，或右键本程序 → 以管理员身份运行后再安装。"
                )
            else:
                QtWidgets.QMessageBox.critical(self, "安装失败", msg)

    def uninstall_service(self):
        ok = QtWidgets.QMessageBox.question(
            self, "卸载服务",
            "将移除计划任务 \\SystemAutorun\\Boot。下次开机时不再自动启动。\n"
            "需要管理员权限（会弹出 UAC）。"
        )
        if ok != QtWidgets.QMessageBox.Yes:
            return
        self._begin_busy()
        self._set_status("正在卸载 Boot Task（可能弹出 UAC）…")

        def thread_main():
            try:
                if self._is_process_elevated():
                    result = _run_cli_oneshot("uninstall", timeout=120)
                else:
                    result = self._run_elevated_cli("uninstall")
                _cli_dispatcher().done.emit(self._on_uninstall_done, result)
            except Exception as exc:
                _cli_dispatcher().error.emit(self._on_cli_error, exc)

        threading.Thread(target=thread_main, daemon=True).start()

    def _on_uninstall_done(self, result):
        self._end_busy()
        if isinstance(result, dict) and result.get("uninstalled"):
            invalidate_cli_cache({"doctor", "list"})
            self._set_status("服务已卸载")
            QtWidgets.QMessageBox.information(self, "卸载成功", "Boot Task 已移除。")
        else:
            msg = user_message_from_error(result)
            QtWidgets.QMessageBox.critical(self, "卸载失败", msg)

    def apply_config(self):
        self._begin_busy()
        self._set_status("应用配置…")
        # 确保 worker 重新加载 scheduler 逻辑
        try:
            _CLI_WORKER.stop()
        except Exception:
            pass
        run_cli_async(on_done=self._on_apply_done, on_error=self._on_cli_error,
                      command="apply")

    def _on_apply_done(self, result):
        self._end_busy()
        message = format_apply_result(result)
        self._set_status(message.splitlines()[0])
        QtWidgets.QMessageBox.information(
            self, "应用配置",
            message
        )


# ─── 弹出对话框 ──────────────────────────────────────────────────

class _TableDialog(QtWidgets.QDialog):
    def __init__(self, parent, title: str, width: int = 900, height: int = 540):
        super().__init__(parent)
        self.setWindowTitle(title)
        self.resize(width, height)
        self.setModal(True)
        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(14, 12, 14, 12)
        layout.setSpacing(8)

        header = QtWidgets.QHBoxLayout()
        self.summary_label = QtWidgets.QLabel("")
        self.summary_label.setStyleSheet(f"color:{THEME['fg_dim']}; font-weight:600;")
        header.addWidget(self.summary_label, 1)
        self.cache_label = QtWidgets.QLabel("")
        self.cache_label.setObjectName("HintLabel")
        header.addWidget(self.cache_label)
        self.loading_bar = QtWidgets.QProgressBar()
        self.loading_bar.setObjectName("BusyBar")
        self.loading_bar.setRange(0, 0)
        self.loading_bar.setTextVisible(False)
        self.loading_bar.setFixedSize(54, 4)
        self.loading_bar.hide()
        header.addWidget(self.loading_bar)
        layout.addLayout(header)

        self.table = CachedTableWidget()
        self.table.verticalHeader().setVisible(False)
        self.table.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.setEditTriggers(QtWidgets.QAbstractItemView.NoEditTriggers)
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.verticalHeader().setDefaultSectionSize(34)
        layout.addWidget(self.table, 1)

        self.btn_refresh = QtWidgets.QPushButton("刷新")
        self.btn_refresh.setObjectName("SubtleButton")
        self.btn_refresh.clicked.connect(self.refresh)
        self.btn_refresh.hide()
        btn_close = QtWidgets.QPushButton("关闭")
        btn_close.setObjectName("SubtleButton")
        btn_close.clicked.connect(self.accept)
        bb = QtWidgets.QHBoxLayout()
        bb.addWidget(self.btn_refresh)
        bb.addStretch(1)
        bb.addWidget(btn_close)
        layout.addLayout(bb)

    def show(self):
        self.exec()

    def _set_loading(self, loading: bool, cached_at: float | None = None):
        self.loading_bar.setVisible(loading)
        self.btn_refresh.setEnabled(not loading)
        self.btn_refresh.setText("刷新中…" if loading else "刷新")
        if loading:
            self.cache_label.setText(
                f"缓存于 {_format_cache_time(cached_at)} · 正在更新" if cached_at else "正在加载"
            )
        elif cached_at:
            self.cache_label.setText(f"更新于 {_format_cache_time(cached_at)}")
        else:
            self.cache_label.clear()

    def _on_refresh_error(self, exc: Exception):
        self._set_loading(False)
        message = user_message_from_error(exc)
        if self.table.rowCount() > 0:
            self.cache_label.setText("更新失败 · 已保留现有数据")
            append_gui_log(f"{self.windowTitle()} refresh failed: {message}")
        else:
            self.summary_label.setText(f"加载失败：{message}")
            self.summary_label.setStyleSheet(f"color:{THEME['error']}; font-weight:600;")


class DoctorDialog(_TableDialog):
    def __init__(self, parent, data=None, cached_at: float | None = None):
        super().__init__(parent, "系统诊断 - 环境检查")
        self._cached_at = cached_at
        self.btn_refresh.show()
        self.table.setColumnCount(3)
        self.table.setHorizontalHeaderLabels(["检查项", "状态", "详情"])
        self.table.horizontalHeader().setSectionResizeMode(0, QtWidgets.QHeaderView.Fixed)
        self.table.horizontalHeader().setSectionResizeMode(1, QtWidgets.QHeaderView.Fixed)
        self.table.setColumnWidth(0, 320)
        self.table.setColumnWidth(1, 80)
        self.set_data(data, cached_at)

    def set_data(self, data, timestamp: float | None = None):
        if not isinstance(data, dict):
            data = {}
        all_ok = data.get("all_ok", False)
        passed = data.get("passed", 0)
        failed = data.get("failed", 0)
        summary = f"通过 {passed}  ·  失败 {failed}  ·  " + ("全部正常" if all_ok else "需要关注")
        color = THEME['success'] if all_ok else THEME['error']
        self.summary_label.setText(summary)
        self.summary_label.setStyleSheet(f"color:{color}; font-weight:600; font-size:14px;")

        checks = list(data.get("checks") or [])
        self.table.replace_rows(len(checks))
        for row, c in enumerate(checks):
            ok = bool(c.get("ok"))
            badge = StatusBadge("OK" if ok else "FAIL")
            self.table.setCellWidget(row, 1, badge)
            self.table.setItem(row, 0, QtWidgets.QTableWidgetItem(c.get("check", "")))
            self.table.setItem(row, 2, QtWidgets.QTableWidgetItem(c.get("detail", "")))
        self.table.finish_rows(sorting=False)
        if timestamp:
            self._cached_at = timestamp
            self._set_loading(False, timestamp)

    def refresh(self):
        self._set_loading(True, self._cached_at)
        run_cli_async(
            on_done=self._on_refreshed,
            on_error=self._on_refresh_error,
            command="doctor",
        )

    def _on_refreshed(self, data):
        _cached, timestamp = get_cached_cli("doctor")
        self.set_data(data, timestamp)


class ScanDialog(_TableDialog):
    def __init__(self, parent, data=None, cached_at: float | None = None):
        super().__init__(parent, "扫描 - 所有自启动项", 1100, 600)
        self._cached_at = cached_at
        self.btn_refresh.show()
        self.table.setColumnCount(5)
        self.table.setHorizontalHeaderLabels(["管理", "来源", "名称", "目标", "参数"])
        self.table.horizontalHeader().setSectionResizeMode(0, QtWidgets.QHeaderView.Fixed)
        self.table.horizontalHeader().setSectionResizeMode(1, QtWidgets.QHeaderView.Fixed)
        self.table.horizontalHeader().setSectionResizeMode(2, QtWidgets.QHeaderView.Fixed)
        self.table.setColumnWidth(0, 72)
        self.table.setColumnWidth(1, 130)
        self.table.setColumnWidth(2, 220)
        self.set_data(data, cached_at)

    def set_data(self, data, timestamp: float | None = None):
        if not isinstance(data, dict):
            data = {}
        total = data.get("total", 0)
        managed = data.get("managed", 0)
        self.summary_label.setText(
            f"总数 {total}  ·  已被本工具管理 {managed}"
        )

        entries = list(data.get("entries") or [])
        self.table.replace_rows(len(entries))
        for row, e in enumerate(entries):
            is_managed = bool(e.get("managed"))
            managed_item = QtWidgets.QTableWidgetItem("已托管" if is_managed else "未托管")
            managed_item.setForeground(QtGui.QColor(THEME['primary'] if is_managed else THEME['fg_hint']))
            self.table.setItem(row, 0, managed_item)
            for col, value in enumerate([
                e.get("source", ""),
                e.get("name", ""),
                e.get("target", "") or "",
                e.get("args", "") or "",
            ], start=1):
                item = QtWidgets.QTableWidgetItem(str(value))
                item.setToolTip(str(value))
                self.table.setItem(row, col, item)
        self.table.finish_rows()
        if timestamp:
            self._cached_at = timestamp
            self._set_loading(False, timestamp)

    def refresh(self):
        self._set_loading(True, self._cached_at)
        run_cli_async(
            on_done=self._on_refreshed,
            on_error=self._on_refresh_error,
            command="scan",
        )

    def _on_refreshed(self, data):
        _cached, timestamp = get_cached_cli("scan")
        self.set_data(data, timestamp)


class StartupManagerDialog(QtWidgets.QDialog):
    def __init__(self, parent: AutorunWindow, data=None, cached_at: float | None = None):
        super().__init__(parent)
        self.parent_window = parent
        self._entries: list[dict] = []
        self._selected_entry: dict | None = None
        self._cached_at = cached_at
        self._refreshing = False
        self.setWindowTitle("自动启动项管理")
        self.resize(1160, 640)
        self.setModal(True)

        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(14, 12, 14, 12)
        layout.setSpacing(8)

        top = QtWidgets.QHBoxLayout()
        self.summary_label = QtWidgets.QLabel("")
        self.summary_label.setStyleSheet(f"color:{THEME['fg_dim']}; font-weight:600;")
        top.addWidget(self.summary_label, 1)
        self.count_label = QtWidgets.QLabel("")
        self.count_label.setObjectName("HintLabel")
        top.addWidget(self.count_label)
        self.cache_label = QtWidgets.QLabel("")
        self.cache_label.setObjectName("HintLabel")
        top.addWidget(self.cache_label)
        self.loading_bar = QtWidgets.QProgressBar()
        self.loading_bar.setObjectName("BusyBar")
        self.loading_bar.setRange(0, 0)
        self.loading_bar.setTextVisible(False)
        self.loading_bar.setFixedSize(54, 4)
        self.loading_bar.hide()
        top.addWidget(self.loading_bar)

        self.filter_combo = QtWidgets.QComboBox()
        self.filter_combo.addItem("全部", "")
        self.filter_combo.addItem("活动项", "active")
        self.filter_combo.addItem("已禁用", "disabled")
        self.filter_combo.addItem("本工具托管", "managed")
        self.filter_combo.currentIndexChanged.connect(self._render)
        top.addWidget(self.filter_combo)

        self.search_edit = QtWidgets.QLineEdit()
        self.search_edit.setPlaceholderText("搜索来源、名称、目标或参数")
        self.search_edit.setClearButtonEnabled(True)
        self.search_edit.textChanged.connect(self._render)
        self.search_edit.setMinimumWidth(280)
        top.addWidget(self.search_edit)
        layout.addLayout(top)

        self.table = CachedTableWidget(0, 7)
        self.table.setHorizontalHeaderLabels(["状态", "托管", "权限", "来源", "名称", "目标", "参数"])
        self.table.verticalHeader().setVisible(False)
        self.table.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.setEditTriggers(QtWidgets.QAbstractItemView.NoEditTriggers)
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.horizontalHeader().setSectionResizeMode(0, QtWidgets.QHeaderView.Fixed)
        self.table.horizontalHeader().setSectionResizeMode(1, QtWidgets.QHeaderView.Fixed)
        self.table.horizontalHeader().setSectionResizeMode(2, QtWidgets.QHeaderView.Fixed)
        self.table.setColumnWidth(0, 78)
        self.table.setColumnWidth(1, 70)
        self.table.setColumnWidth(2, 90)
        self.table.setColumnWidth(3, 130)
        self.table.setColumnWidth(4, 230)
        self.table.itemSelectionChanged.connect(self._on_select)
        layout.addWidget(self.table, 1)

        hint = QtWidgets.QLabel("禁用会先备份原始启动项；恢复仅支持由本工具禁用过的项。HKLM / Common Startup 项需要管理员权限。")
        hint.setObjectName("HintLabel")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        buttons = QtWidgets.QHBoxLayout()
        self.btn_refresh = QtWidgets.QPushButton("刷新")
        self.btn_refresh.setObjectName("SubtleButton")
        self.btn_refresh.clicked.connect(self.refresh)
        buttons.addWidget(self.btn_refresh)

        self.btn_disable = QtWidgets.QPushButton("禁用选中启动项")
        self.btn_disable.setObjectName("DangerButton")
        self.btn_disable.clicked.connect(self.disable_selected)
        buttons.addWidget(self.btn_disable)

        self.btn_restore = QtWidgets.QPushButton("恢复选中启动项")
        self.btn_restore.clicked.connect(self.restore_selected)
        buttons.addWidget(self.btn_restore)

        self.btn_add_task = QtWidgets.QPushButton("加入任务列表")
        self.btn_add_task.setObjectName("PrimaryButton")
        self.btn_add_task.clicked.connect(self.add_selected_to_tasks)
        buttons.addWidget(self.btn_add_task)
        self.action_buttons = [self.btn_refresh, self.btn_disable, self.btn_restore, self.btn_add_task]

        buttons.addStretch(1)
        btn_close = QtWidgets.QPushButton("关闭")
        btn_close.clicked.connect(self.accept)
        buttons.addWidget(btn_close)
        layout.addLayout(buttons)

        self.set_data(data, cached_at)

    def show(self):
        self.exec()

    def set_data(self, data, timestamp: float | None = None):
        if not isinstance(data, dict):
            data = {}
        selected_key = None
        if self._selected_entry:
            selected_key = (
                self._selected_entry.get("source"),
                self._selected_entry.get("name"),
                self._selected_entry.get("backup_id"),
            )
        self._entries = list(data.get("entries") or [])
        self.summary_label.setText(
            f"活动 {data.get('total', 0)}  ·  已托管 {data.get('managed', 0)}  ·  "
            f"已禁用备份 {data.get('disabled', 0)}"
        )
        if timestamp:
            self._cached_at = timestamp
            self.cache_label.setText(f"更新于 {_format_cache_time(timestamp)}")
        self._render(selected_key=selected_key)

    def refresh(self, background: bool = False):
        if self._refreshing:
            return
        self._refreshing = True
        self._set_busy(True)
        self.loading_bar.show()
        self.cache_label.setText(
            f"缓存于 {_format_cache_time(self._cached_at)} · 正在更新"
            if self._cached_at else "正在加载"
        )
        if not background:
            self.parent_window._set_status("正在刷新自动启动项…")
        run_cli_async(on_done=self._on_refresh_done,
                      on_error=self._on_error,
                      command="startup-list")

    def _on_refresh_done(self, data):
        self._refreshing = False
        self.loading_bar.hide()
        self._set_busy(False)
        self.parent_window._set_status("自动启动项已刷新")
        _cached, timestamp = get_cached_cli("startup-list")
        self.set_data(data, timestamp)

    def _on_error(self, exc: Exception):
        self._refreshing = False
        self.loading_bar.hide()
        self._set_busy(False)
        message = user_message_from_error(exc)
        if self._entries:
            self.cache_label.setText("更新失败 · 已保留现有数据")
            self.parent_window._set_status(f"自动启动项更新失败：{message}")
            append_gui_log(f"Startup list refresh failed: {message}")
        else:
            self.parent_window._on_cli_error(exc)

    def _set_busy(self, busy: bool):
        for btn in getattr(self, "action_buttons", []):
            btn.setEnabled(not busy)
        # 浏览、筛选和搜索保持可用；只锁定可能修改系统状态的操作。
        self.table.setEnabled(True)
        self.filter_combo.setEnabled(True)
        self.search_edit.setEnabled(True)
        if not busy:
            self._update_buttons()

    def _render(self, *_args, selected_key=None):
        if selected_key is None and self._selected_entry:
            selected_key = (
                self._selected_entry.get("source"),
                self._selected_entry.get("name"),
                self._selected_entry.get("backup_id"),
            )
        query = self.search_edit.text().strip().lower()
        mode = self.filter_combo.currentData()
        filtered = []
        for entry in self._entries:
            if mode == "active" and entry.get("disabled"):
                continue
            if mode == "disabled" and not entry.get("disabled"):
                continue
            if mode == "managed" and not entry.get("managed"):
                continue
            if query:
                text = " ".join(str(entry.get(k) or "") for k in (
                    "source", "name", "target", "args", "origin", "task"
                )).lower()
                if query not in text:
                    continue
            filtered.append(entry)

        self.count_label.setText(f"{len(filtered)} / {len(self._entries)}")
        self.table.replace_rows(len(filtered))
        for row, entry in enumerate(filtered):
            state = "已禁用" if entry.get("disabled") else "活动"
            managed = entry.get("task") or ("是" if entry.get("managed") else "")
            permission = self._permission_text(entry)
            values = [
                state,
                managed,
                permission,
                entry.get("source", ""),
                entry.get("name", ""),
                entry.get("target", "") or "",
                entry.get("args", "") or "",
            ]
            for col, value in enumerate(values):
                item = QtWidgets.QTableWidgetItem(str(value))
                if col == 0:
                    item.setForeground(QtGui.QColor(THEME['off'] if entry.get("disabled") else THEME['success']))
                if col == 1 and entry.get("managed"):
                    item.setForeground(QtGui.QColor(THEME['primary']))
                if col == 2 and entry.get("requires_admin"):
                    item.setForeground(QtGui.QColor(THEME['warn']))
                if col == 4:
                    item.setData(QtCore.Qt.UserRole, entry)
                item.setToolTip(str(value))
                self.table.setItem(row, col, item)
        self.table.finish_rows()
        selected_row = -1
        if selected_key:
            for row in range(self.table.rowCount()):
                item = self.table.item(row, 4)
                entry = item.data(QtCore.Qt.UserRole) if item else None
                if entry and (
                    entry.get("source"), entry.get("name"), entry.get("backup_id")
                ) == selected_key:
                    selected_row = row
                    break
        if selected_row >= 0:
            self.table.selectRow(selected_row)
        elif self.table.rowCount() > 0:
            self.table.selectRow(0)
        else:
            self._selected_entry = None
        self._update_buttons()

    def _on_select(self):
        rows = self.table.selectionModel().selectedRows()
        if not rows:
            self._selected_entry = None
        else:
            item = self.table.item(rows[0].row(), 4)
            self._selected_entry = item.data(QtCore.Qt.UserRole) if item else None
        self._update_buttons()

    @staticmethod
    def _needs_admin(entry: dict) -> bool:
        return bool(entry.get("requires_admin")) and not bool(entry.get("can_modify"))

    @staticmethod
    def _permission_text(entry: dict) -> str:
        if entry.get("requires_admin"):
            return "管理员" if entry.get("can_modify") else "需管理员"
        return "当前用户"

    def _update_buttons(self):
        entry = self._selected_entry or {}
        has_entry = bool(entry)
        disabled = bool(entry.get("disabled"))
        can_modify = has_entry and not self._needs_admin(entry)
        self.btn_disable.setEnabled(can_modify and not disabled)
        self.btn_restore.setEnabled(can_modify and disabled and bool(entry.get("backup_id")))
        self.btn_add_task.setEnabled(has_entry and not disabled and bool(entry.get("target")) and not bool(entry.get("managed")))
        admin_tip = "该启动项属于全局系统启动位置，需要以管理员身份运行本工具。"
        self.btn_disable.setToolTip(admin_tip if has_entry and self._needs_admin(entry) else "")
        self.btn_restore.setToolTip(admin_tip if has_entry and self._needs_admin(entry) else "")

    def disable_selected(self):
        entry = self._selected_entry
        if not entry:
            return
        source = str(entry.get("source") or "")
        name = str(entry.get("name") or "")
        if not source or not name:
            QtWidgets.QMessageBox.warning(
                self, "无法禁用",
                "该启动项缺少来源或名称，无法定位系统中的真实启动项。"
            )
            return
        if self._needs_admin(entry):
            QtWidgets.QMessageBox.information(
                self, "需要管理员权限",
                f"{source} 是全局系统启动位置，禁用该启动项需要管理员权限。\n\n"
                "请右键 dist\\SystemAutorun.exe 以管理员身份运行后重试。"
            )
            return
        ok = QtWidgets.QMessageBox.question(
            self, "禁用启动项",
            f"将禁用启动项：{source} / {name}\n\n"
            "本工具会先保存备份，之后可在此窗口恢复。是否继续？"
        )
        if ok != QtWidgets.QMessageBox.Yes:
            return
        args = [source, name]
        if entry.get("origin"):
            args.append(entry.get("origin", ""))
        self._set_busy(True)
        self.parent_window._set_status(f"正在禁用启动项：{name}…")
        run_cli_async(on_done=lambda _d: self._after_mutation("启动项已禁用"),
                      on_error=self._on_error,
                      command="startup-disable", args=args)

    def restore_selected(self):
        entry = self._selected_entry
        if not entry or not entry.get("backup_id"):
            return
        if self._needs_admin(entry):
            QtWidgets.QMessageBox.information(
                self, "需要管理员权限",
                f"{entry.get('source')} 是全局系统启动位置，恢复该启动项需要管理员权限。\n\n"
                "请右键 dist\\SystemAutorun.exe 以管理员身份运行后重试。"
            )
            return
        ok = QtWidgets.QMessageBox.question(
            self, "恢复启动项",
            f"将恢复启动项：{entry.get('source')} / {entry.get('name')}\n\n是否继续？"
        )
        if ok != QtWidgets.QMessageBox.Yes:
            return
        self._set_busy(True)
        self.parent_window._set_status(f"正在恢复启动项：{entry.get('name')}…")
        run_cli_async(on_done=lambda _d: self._after_mutation("启动项已恢复"),
                      on_error=self._on_error,
                      command="startup-restore", args=[entry.get("backup_id")])

    def _after_mutation(self, status: str):
        invalidate_cli_cache({"scan", "startup-list"})
        self.parent_window._set_status(status)
        self.refresh()

    def add_selected_to_tasks(self):
        entry = self._selected_entry
        if not entry:
            return
        preset = {
            "name": entry.get("name") or "",
            "description": entry.get("name") or "",
            "exec": entry.get("target") or "",
            "args_text": entry.get("args") or "",
            "working_dir": entry.get("working_dir") or "",
            "enabled": True,
        }
        self.accept()
        self.parent_window.open_add_program_dialog(preset)


class AddProgramDialog(QtWidgets.QDialog):
    def __init__(self, parent, preset: dict | None = None):
        super().__init__(parent)
        preset = preset or {}
        self.setWindowTitle("添加程序到任务列表")
        self.resize(640, 430)
        self.setModal(True)

        layout = QtWidgets.QVBoxLayout(self)
        layout.setContentsMargins(16, 14, 16, 14)
        layout.setSpacing(10)

        form = QtWidgets.QFormLayout()
        form.setLabelAlignment(QtCore.Qt.AlignRight)
        form.setFormAlignment(QtCore.Qt.AlignTop)
        form.setHorizontalSpacing(10)
        form.setVerticalSpacing(10)

        self.name_edit = QtWidgets.QLineEdit()
        self.name_edit.setPlaceholderText("例如 jellyfin")
        self.name_edit.setMinimumHeight(34)
        form.addRow("任务名", self.name_edit)

        self.desc_edit = QtWidgets.QLineEdit()
        self.desc_edit.setMinimumHeight(34)
        form.addRow("描述", self.desc_edit)

        exec_row = QtWidgets.QHBoxLayout()
        self.exec_edit = QtWidgets.QLineEdit()
        self.exec_edit.setPlaceholderText(r"C:\Path\App.exe")
        self.exec_edit.textChanged.connect(self._infer_fields)
        self.exec_edit.setMinimumHeight(34)
        exec_row.addWidget(self.exec_edit, 1)
        btn_browse = QtWidgets.QPushButton("浏览")
        btn_browse.setObjectName("SubtleButton")
        btn_browse.clicked.connect(self.browse_exec)
        exec_row.addWidget(btn_browse)
        form.addRow("执行文件", exec_row)

        self.args_edit = QtWidgets.QLineEdit()
        self.args_edit.setPlaceholderText("可选，例如 --tray")
        self.args_edit.setMinimumHeight(34)
        form.addRow("参数", self.args_edit)

        work_row = QtWidgets.QHBoxLayout()
        self.work_edit = QtWidgets.QLineEdit()
        self.work_edit.setMinimumHeight(34)
        work_row.addWidget(self.work_edit, 1)
        btn_work = QtWidgets.QPushButton("浏览")
        btn_work.setObjectName("SubtleButton")
        btn_work.clicked.connect(self.browse_workdir)
        work_row.addWidget(btn_work)
        form.addRow("工作目录", work_row)

        self.delay_spin = QtWidgets.QSpinBox()
        self.delay_spin.setRange(0, 3600)
        self.delay_spin.setSuffix(" 秒")
        self.delay_spin.setValue(5)
        form.addRow("启动延迟", self.delay_spin)

        self.health_edit = QtWidgets.QLineEdit()
        self.health_edit.setPlaceholderText("默认使用执行文件名，例如 App.exe")
        self.health_edit.setMinimumHeight(34)
        form.addRow("进程检查", self.health_edit)

        self.enabled_check = QtWidgets.QCheckBox("启用该任务")
        self.enabled_check.setChecked(True)
        form.addRow("", self.enabled_check)

        layout.addLayout(form)

        hint = QtWidgets.QLabel("该功能会向 autorun.yaml 追加 Windows 任务，并使用进程名做默认健康检查。")
        hint.setObjectName("HintLabel")
        hint.setWordWrap(True)
        layout.addWidget(hint)
        self.error_label = QtWidgets.QLabel("")
        self.error_label.setObjectName("HintLabel")
        self.error_label.setStyleSheet(f"color:{THEME['error']};")
        self.error_label.setWordWrap(True)
        layout.addWidget(self.error_label)

        buttons = QtWidgets.QHBoxLayout()
        buttons.addStretch(1)
        btn_cancel = QtWidgets.QPushButton("取消")
        btn_cancel.clicked.connect(self.reject)
        buttons.addWidget(btn_cancel)
        btn_ok = QtWidgets.QPushButton("添加")
        btn_ok.setObjectName("PrimaryButton")
        btn_ok.clicked.connect(self.accept_if_valid)
        buttons.addWidget(btn_ok)
        layout.addLayout(buttons)

        self._apply_preset(preset)

    def _apply_preset(self, preset: dict):
        self.name_edit.setText(self._slugify(preset.get("name") or ""))
        self.desc_edit.setText(preset.get("description") or preset.get("name") or "")
        self.exec_edit.setText(preset.get("exec") or "")
        self.args_edit.setText(preset.get("args_text") or "")
        self.work_edit.setText(preset.get("working_dir") or "")
        self.enabled_check.setChecked(bool(preset.get("enabled", True)))
        if preset.get("startup_delay") is not None:
            self.delay_spin.setValue(int(preset.get("startup_delay")))
        self._infer_fields()

    def browse_exec(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(
            self, "选择程序", "", "Programs (*.exe *.bat *.cmd *.ps1);;All files (*.*)"
        )
        if path:
            self.exec_edit.setText(path)

    def browse_workdir(self):
        path = QtWidgets.QFileDialog.getExistingDirectory(self, "选择工作目录", self.work_edit.text() or "")
        if path:
            self.work_edit.setText(path)

    def _infer_fields(self):
        exec_path = self.exec_edit.text().strip()
        if not exec_path:
            return
        path = Path(exec_path)
        if not self.name_edit.text().strip():
            self.name_edit.setText(self._slugify(path.stem))
        if not self.desc_edit.text().strip():
            self.desc_edit.setText(path.stem)
        if not self.work_edit.text().strip() and path.parent:
            self.work_edit.setText(str(path.parent))
        if not self.health_edit.text().strip() and path.name:
            self.health_edit.setText(path.name)

    @staticmethod
    def _slugify(value: str) -> str:
        value = value.strip()
        if not value:
            return ""
        value = re.sub(r"\.[A-Za-z0-9]+$", "", value)
        value = re.sub(r"[^A-Za-z0-9_-]+", "-", value).strip("-").lower()
        return value or "program"

    def accept_if_valid(self):
        if not self.name_edit.text().strip():
            self.error_label.setText("请填写任务名。")
            self.name_edit.setFocus()
            return
        if not self.exec_edit.text().strip():
            self.error_label.setText("请选择或填写执行文件路径。")
            self.exec_edit.setFocus()
            return
        if not Path(self.exec_edit.text().strip()).exists():
            self.error_label.setText("执行文件路径不存在，请检查路径。")
            self.exec_edit.setFocus()
            return
        self.error_label.clear()
        self.accept()

    def payload(self) -> dict[str, Any]:
        args_text = self.args_edit.text().strip()
        payload = {
            "name": self.name_edit.text().strip(),
            "description": self.desc_edit.text().strip(),
            "exec": self.exec_edit.text().strip(),
            "args_text": args_text,
            "working_dir": self.work_edit.text().strip(),
            "startup_delay": self.delay_spin.value(),
            "health_pattern": self.health_edit.text().strip(),
            "enabled": self.enabled_check.isChecked(),
        }
        return payload


class LogsDialog(_TableDialog):
    def __init__(self, parent, task_name: str, data=None, cached_at: float | None = None):
        super().__init__(parent, f"日志 - {task_name}", 900, 500)
        self.task_name = task_name
        self.args = [task_name, "-Tail", "200"]
        self._cached_at = cached_at
        self.btn_refresh.show()

        # 日志用文本框，不用表格
        self.table.setVisible(False)
        self.text = QtWidgets.QPlainTextEdit()
        self.text.setReadOnly(True)
        self.text.setStyleSheet(
            f"background:{THEME['bg_inset']}; color:{THEME['fg_dim']}; "
            f"border:1px solid {THEME['border']}; padding:8px; "
            f"font-family: Cascadia Code, Consolas, monospace; font-size:12px;"
        )
        # 重新布局：去掉表格，加文本
        idx = self.layout().indexOf(self.table)
        self.layout().removeWidget(self.table)
        self.layout().insertWidget(idx, self.text, 1)
        self.set_data(data, cached_at)

    def set_data(self, data, timestamp: float | None = None):
        if not isinstance(data, dict):
            data = {}
        tail = data.get("tail", 200)
        self.summary_label.setText(f"任务：{self.task_name}    显示最后 {tail} 行")
        lines = data.get("lines") or []
        if not lines:
            self.text.setPlainText("（暂无日志）")
        else:
            self.text.setPlainText("\n".join(lines))
            cursor = self.text.textCursor()
            cursor.movePosition(QtGui.QTextCursor.End)
            self.text.setTextCursor(cursor)
        if timestamp:
            self._cached_at = timestamp
            self._set_loading(False, timestamp)

    def refresh(self):
        self._set_loading(True, self._cached_at)
        run_cli_async(
            on_done=self._on_refreshed,
            on_error=self._on_refresh_error,
            command="logs",
            args=self.args,
        )

    def _on_refreshed(self, data):
        _cached, timestamp = get_cached_cli("logs", self.args)
        self.set_data(data, timestamp)


class StatusDialog(_TableDialog):
    def __init__(self, parent, task_name: str, data=None, cached_at: float | None = None):
        super().__init__(parent, f"详细信息 - {task_name}", 820, 600)
        self.task_name = task_name
        self.args = [task_name]
        self._cached_at = cached_at
        self.btn_refresh.show()

        self.table.setVisible(False)
        self.text = QtWidgets.QPlainTextEdit()
        self.text.setReadOnly(True)
        self.text.setStyleSheet(
            f"background:{THEME['bg_inset']}; color:{THEME['fg_dim']}; "
            f"border:1px solid {THEME['border']}; padding:10px; border-radius:0px; "
            f"font-family: Cascadia Code, Consolas, monospace; font-size:12px;"
        )
        idx = self.layout().indexOf(self.table)
        self.layout().removeWidget(self.table)
        self.layout().insertWidget(idx, self.text, 1)
        self.set_data(data, cached_at)

    def set_data(self, data, timestamp: float | None = None):
        if not isinstance(data, dict):
            data = {}
        task = data.get("task", {})
        health = data.get("health", {})
        ok = bool(health.get("healthy"))

        self.summary_label.setText(
            f"{self.task_name}  ·  " + ("正常" if ok else "异常")
        )
        self.summary_label.setStyleSheet(
            f"color:{THEME['success'] if ok else THEME['error']}; font-weight:600; font-size:14px;"
        )

        lines = ["─ 任务 ─────────────────────────────"]
        for k in ("name", "description", "type", "enabled", "exec", "args",
                  "working_dir", "distro", "service", "startup_delay", "depends_on"):
            v = task.get(k)
            if v is None or v == "":
                continue
            if isinstance(v, list):
                v = " ".join(str(x) for x in v)
            lines.append(f"  {k:<14}: {v}")

        lines.append("")
        lines.append("─ 健康检查 ─────────────────────────")
        lines.append(f"  healthy : {health.get('healthy')}")
        lines.append(f"  reason  : {health.get('reason')}")
        lines.append(f"  detail  : {health.get('detail')}")

        procs = data.get("processes") or []
        if procs:
            lines.append("")
            lines.append("─ 进程 ─────────────────────────────")
            lines.append(f"  {'PID':<8} {'Name':<24} {'Mem(MB)':<10} StartTime")
            for p in procs:
                lines.append(f"  {str(p.get('pid','')):<8} {p.get('name',''):<24} "
                             f"{str(p.get('memory_mb','')):<10} {p.get('start_time','') or ''}")

        logs = data.get("recent_logs") or []
        if logs:
            lines.append("")
            lines.append("─ 最近日志（最后 10 行）──────────")
            for l in logs:
                lines.append(f"  {l}")

        self.text.setPlainText("\n".join(lines))
        if timestamp:
            self._cached_at = timestamp
            self._set_loading(False, timestamp)

    def refresh(self):
        self._set_loading(True, self._cached_at)
        run_cli_async(
            on_done=self._on_refreshed,
            on_error=self._on_refresh_error,
            command="status",
            args=self.args,
        )

    def _on_refreshed(self, data):
        _cached, timestamp = get_cached_cli("status", self.args)
        self.set_data(data, timestamp)


# ─── 入口 ──────────────────────────────────────────────────────

def _resolve_app_icon() -> Optional[QtGui.QIcon]:
    """Locate icon.ico next to project root or frozen exe."""
    candidates = [
        PROJECT_ROOT / "icon.ico",
        Path(sys.executable).resolve().parent / "icon.ico",
    ]
    if getattr(sys, "_MEIPASS", None):
        candidates.insert(0, Path(sys._MEIPASS) / "icon.ico")  # type: ignore[attr-defined]
    for path in candidates:
        if path.exists():
            icon = QtGui.QIcon(str(path))
            if not icon.isNull():
                return icon
    return None


def main():
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    apply_dark_theme(app)
    app.aboutToQuit.connect(shutdown_cli_worker)
    app_icon = _resolve_app_icon()
    if app_icon is not None:
        app.setWindowIcon(app_icon)

    if not AUTORUN_PS1.exists():
        QtWidgets.QMessageBox.critical(None, "找不到 CLI",
            f"未找到 autorun.ps1：\n{AUTORUN_PS1}")
        sys.exit(1)

    # 写启动日志（便于诊断）
    append_gui_log(
        f"PID={os.getpid()} Python={sys.version.split()[0]} "
        f"PySide6={QtCore.__version__} Root={PROJECT_ROOT} "
        f"Pwsh={_resolve_pwsh()} Worker={AUTORUN_WORKER_PS1.exists()}"
    )

    # 预热常驻 worker + 首次 list（冷启动只付一次；任务仍来自 autorun.yaml）
    if AUTORUN_WORKER_PS1.exists():
        def _warmup_worker():
            try:
                _CLI_WORKER.start()
                # 预热配置解析 / WSL 缓存，使界面首次刷新更接近热路径
                data = _CLI_WORKER.request("list", timeout=90)
                if isinstance(data, list):
                    _GUI_DATA_CACHE.set("list", [], data)
                    append_gui_log(f"worker warmup list ok tasks={len(data)}")
            except Exception as exc:
                append_gui_log(f"worker warmup failed: {exc}")
        threading.Thread(target=_warmup_worker, name="autorun-worker-warmup", daemon=True).start()

    win = AutorunWindow()
    if app_icon is not None:
        win.setWindowIcon(app_icon)
    win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
