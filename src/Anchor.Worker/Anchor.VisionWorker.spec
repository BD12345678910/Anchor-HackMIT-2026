from pathlib import Path
from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules

worker_root = Path(SPECPATH)
datas = collect_data_files("mediapipe")
datas += [(str(worker_root / "anchor_worker" / "models" / "face_landmarker.task"), "anchor_worker/models")]
binaries = collect_dynamic_libs("mediapipe")
hiddenimports = collect_submodules("mediapipe") + ["mss.windows"]

a = Analysis(
    [str(worker_root / "worker_entry.py")],
    pathex=[str(worker_root)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=["tkinter"],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="Anchor.VisionWorker",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
)
