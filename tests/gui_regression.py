import os
import sys
import tempfile
import threading
import time
from pathlib import Path
from types import SimpleNamespace

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT_ROOT))

from PySide6 import QtWidgets

from gui import autorun_gui as gui


def assert_true(value, message):
    if not value:
        raise AssertionError(message)


app = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])
original_single_shot = gui.QtCore.QTimer.singleShot
gui.QtCore.QTimer.singleShot = staticmethod(lambda *_args, **_kwargs: None)

style_text = Path(gui.__file__).read_text(encoding="utf-8")
for declaration in __import__("re").findall(r"border-radius:\s*([^;]+);", style_text):
    assert_true(declaration.strip() == "0px", "The design system requires square corners everywhere.")

with tempfile.TemporaryDirectory() as temp_dir:
    cache = gui._GuiDataCache(Path(temp_dir) / "cache.json")
    stamp = cache.set("list", [], [{"name": "cached"}])
    data, loaded_stamp = cache.get("list")
    assert_true(data == [{"name": "cached"}], "Cache must return stored data.")
    assert_true(loaded_stamp == stamp, "Cache timestamp must round-trip.")
    reloaded = gui._GuiDataCache(Path(temp_dir) / "cache.json")
    persisted, _ = reloaded.get("list")
    assert_true(persisted == data, "Persistent list cache must survive reload.")

window = gui.AutorunWindow()
window._apply_task_data([
    {
        "name": "healthy",
        "type": "windows",
        "enabled": True,
        "healthy": True,
        "reason": "process-running",
        "detail": "PID=1",
    }
])
assert_true(window.table.rowCount() == 1, "Main table should render cached rows.")
assert_true(window._selected_task == "healthy", "First cached task should be selected.")
assert_true(not window.btn_start.isEnabled(), "Healthy task must not offer Start.")
assert_true(window.btn_stop.isEnabled(), "Healthy task should offer Stop.")
assert_true(window.btn_remove.isEnabled(), "Selected task should offer Remove-from-list.")
panel_buttons = [
    window.btn_start,
    window.btn_stop,
    window.btn_restart,
    window.btn_enable,
    window.btn_disable,
    window.btn_remove,
    *window.secondary_buttons,
    *window.global_buttons,
    *window.top_buttons,
]
assert_true(
    len({button.minimumHeight() for button in panel_buttons}) == 1,
    "Right-panel buttons must share one consistent height.",
)
assert_true(
    all(button.sizePolicy().horizontalPolicy() == QtWidgets.QSizePolicy.Expanding for button in panel_buttons),
    "Right-panel buttons must expand to aligned grid columns.",
)
assert_true(window.btn_start.objectName() == "PanelPrimaryButton", "Start must use the panel primary style.")
assert_true(window.btn_stop.objectName() == "PanelDangerButton", "Stop must use the panel danger style.")
assert_true(window.btn_restart.objectName() == "PanelActionButton", "Restart must use the panel action style.")
assert_true(window.btn_remove.objectName() == "PanelDangerButton", "Remove must use the panel danger style.")
assert_true(window.btn_refresh.objectName() == "PanelPrimaryButton", "Refresh must match panel primary style.")
first_badge = window.table.cellWidget(0, 0)
window._apply_task_data([
    {
        "name": "healthy",
        "type": "windows",
        "enabled": True,
        "healthy": True,
        "reason": "process-running",
        "detail": "PID=2",
    }
])
assert_true(window.table.cellWidget(0, 0) is first_badge, "Main refresh should reuse existing row widgets.")

doctor = gui.DoctorDialog(
    window,
    {"all_ok": True, "passed": 1, "failed": 0,
     "checks": [{"check": "x", "ok": True, "detail": "ok"}]},
    cached_at=time.time(),
)
scan = gui.ScanDialog(
    window,
    {"total": 1, "managed": 0,
     "entries": [{"managed": False, "source": "HKCU", "name": "x",
                  "target": "x.exe", "args": ""}]},
    cached_at=time.time(),
)
startup = gui.StartupManagerDialog(
    window,
    {"total": 0, "managed": 0, "disabled": 0, "entries": []},
    cached_at=time.time(),
)
assert_true(doctor.table.rowCount() == 1, "Doctor dialog should render cache immediately.")
assert_true(scan.table.rowCount() == 1, "Scan dialog should render cache immediately.")
assert_true(startup.table.isEnabled(), "Startup list must remain browsable while refreshing.")

original_run_cli = gui.run_cli
original_cache = gui._GUI_DATA_CACHE
try:
    counter = SimpleNamespace(value=0)
    lock = threading.Lock()
    started = threading.Event()
    release = threading.Event()

    def fake_run_cli(command, args=None, timeout=60):
        del args, timeout
        with lock:
            counter.value += 1
        del command
        started.set()
        release.wait(2)
        return {"ok": True}

    gui.run_cli = fake_run_cli
    gui._GUI_DATA_CACHE = cache
    results = []
    errors = []
    gui.run_cli_async(results.append, errors.append, "cache-dedupe-test", cache_result=False, deduplicate=True)
    assert_true(started.wait(1), "First async request did not start.")
    assert_true(gui._cache_key("cache-dedupe-test", []) in gui._CLI_INFLIGHT, "First request must register as in-flight.")
    gui.run_cli_async(results.append, errors.append, "cache-dedupe-test", cache_result=False, deduplicate=True)
    assert_true(len(gui._CLI_INFLIGHT[gui._cache_key("cache-dedupe-test", [])]) == 2, "Second request must join the in-flight callbacks.")
    assert_true(counter.value == 1, f"Second request unexpectedly started a worker (calls={counter.value}).")
    release.set()
    deadline = time.time() + 2
    while len(results) < 2 and not errors and time.time() < deadline:
        app.processEvents()
        time.sleep(0.01)
    assert_true(counter.value == 1, f"Concurrent identical reads must be deduplicated (calls={counter.value}).")
    assert_true(len(results) == 2 and not errors, "All deduplicated callers must receive data.")
finally:
    gui.run_cli = original_run_cli
    gui._GUI_DATA_CACHE = original_cache
    gui.QtCore.QTimer.singleShot = original_single_shot

window.close()
doctor.close()
scan.close()
startup.close()
print("GUI regression checks passed.")
