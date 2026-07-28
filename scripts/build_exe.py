"""Backup previous exe and rebuild SystemAutorun.exe with icon."""
from __future__ import annotations

import shutil
import subprocess
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    dist_exe = ROOT / "dist" / "SystemAutorun.exe"
    backups = ROOT / "backups"
    backups.mkdir(exist_ok=True)

    if dist_exe.exists():
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        bak = backups / f"SystemAutorun.exe.{stamp}.bak"
        shutil.copy2(dist_exe, bak)
        print(f"Backed up -> {bak}")

    icon = ROOT / "icon.ico"
    if not icon.exists():
        print("icon.ico missing; generating...")
        r = subprocess.run([sys.executable, str(ROOT / "scripts" / "make_icon.py")], cwd=ROOT)
        if r.returncode != 0:
            return r.returncode

    cmd = [
        sys.executable,
        "-m",
        "PyInstaller",
        "--noconfirm",
        str(ROOT / "SystemAutorun.spec"),
    ]
    print("Running:", " ".join(cmd))
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        print("PyInstaller failed", r.returncode)
        return r.returncode

    if dist_exe.exists():
        print(f"Built {dist_exe} ({dist_exe.stat().st_size} bytes)")
        print(f"mtime {datetime.fromtimestamp(dist_exe.stat().st_mtime)}")
    else:
        print("ERROR: dist exe missing after build")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
