# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec - 打包 SystemAutorun GUI 为单 exe。"""
from pathlib import Path
import sys

is_win = sys.platform == 'win32'
project_dir = Path(SPECPATH)

datas = []

# ── 图标 ──────────────────────────────────────────────────────────
icon_dat_path = None
icon_path = project_dir / 'icon.ico'
if icon_path.exists():
    icon_dat_path = str(icon_path)
    # 打包进运行时，便于 QApplication/窗口 setWindowIcon
    datas.append((str(icon_path), '.'))


# ── PyInstaller Analysis ─────────────────────────────────────────
a = Analysis(
    ['gui/autorun_gui.py'],
    pathex=[str(project_dir)],
    binaries=[],
    datas=datas,
    hiddenimports=[
        'PySide6.QtCore',
        'PySide6.QtGui',
        'PySide6.QtWidgets',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        'tkinter',
        'matplotlib',
        'numpy',
        'pandas',
        'PIL',
        'PyQt5',
        'PyQt6',
    ],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

# ── 单文件 EXE ───────────────────────────────────────────────────
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='SystemAutorun-new',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=icon_dat_path,
)
