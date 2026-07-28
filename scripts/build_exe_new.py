"""Build SystemAutorun-new.exe when dist\\SystemAutorun.exe is locked."""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
spec = (ROOT / "SystemAutorun.spec").read_text(encoding="utf-8")
spec2 = re.sub(r"name='SystemAutorun'", "name='SystemAutorun-new'", spec, count=1)
out_spec = ROOT / "SystemAutorun-new.spec"
out_spec.write_text(spec2, encoding="utf-8")
r = subprocess.run(
    [sys.executable, "-m", "PyInstaller", "--noconfirm", str(out_spec)],
    cwd=ROOT,
)
print("exit", r.returncode)
exe = ROOT / "dist" / "SystemAutorun-new.exe"
if exe.exists():
    print("built", exe, exe.stat().st_size)
sys.exit(r.returncode)
