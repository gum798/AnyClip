# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for the Windows tray build.

Build with:
    pyinstaller anyclip.spec
The resulting executable lands at dist/AnyClip/AnyClip.exe (one-folder
layout: faster startup and smaller diffs in autoupdate downloads than
--onefile). Console window is suppressed so launching the .exe shows
only the tray icon.
"""

import os

block_cipher = None

_datas = [
    ('app/icons/anyclip.ico', 'app/icons'),
]
# WinSparkle.dll is dropped into app/ by the release workflow's
# download step. PyInstaller copies it into the bundle root so the
# runtime ctypes.CDLL("WinSparkle.dll") resolution works. The file is
# absent in plain source checkouts, so we conditionally include it
# rather than failing the build outright.
if os.path.exists('app/WinSparkle.dll'):
    _datas.append(('app/WinSparkle.dll', '.'))


a = Analysis(
    ['anyclip.py'],
    pathex=['.'],
    binaries=[],
    datas=_datas,
    hiddenimports=[
        'anyclip',
        'app',
        'app.daemon_supervisor',
        'app.tray_win',
        'app.onboarding_win',
        'autostart',
        'config_store',
        'peer_state',
        'permission_probe',
        'version_negotiator',
        'pyperclip',
        'zeroconf',
        'pystray',
        'PIL.Image',
        'PIL.ImageDraw',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='AnyClip',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    # --noconsole equivalent: hides the console window on launch.
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='app/icons/anyclip.ico',
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='AnyClip',
)
