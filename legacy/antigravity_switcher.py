# -*- coding: utf-8 -*-
"""
Antigravity 2.0 - Quản Lý & Chuyển Đổi Danh Sách Tài Khoản Ultra (Bản Python Dự Phòng)
- Hỗ trợ lưu danh sách không giới hạn tài khoản (accounts.json) + tự động đồng bộ từ slot cũ.
- Khi bấm chọn Antigravity.exe: kiểm tra nếu đường dẫn mặc định (%LOCALAPPDATA%\Programs\Antigravity)
  tồn tại thì mở thẳng từ thư mục đó luôn, nếu không tồn tại mới mở theo mặc định.
"""

import base64
import ctypes
import ctypes.wintypes as wt
import datetime
import json
import os
import re
import subprocess
import sys
import winreg
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk

CRED_TYPE_GENERIC = 1
CRED_PERSIST_LOCAL_MACHINE = 2
TARGET_NAME = "gemini:antigravity"
USER_NAME = "antigravity"


class FILETIME(ctypes.Structure):
    _fields_ = [("dwLowDateTime", wt.DWORD), ("dwHighDateTime", wt.DWORD)]


class CREDENTIAL(ctypes.Structure):
    _fields_ = [
        ("Flags", wt.DWORD),
        ("Type", wt.DWORD),
        ("TargetName", wt.LPWSTR),
        ("Comment", wt.LPWSTR),
        ("LastWritten", FILETIME),
        ("CredentialBlobSize", wt.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", wt.DWORD),
        ("AttributeCount", wt.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wt.LPWSTR),
        ("UserName", wt.LPWSTR),
    ]


advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
advapi32.CredReadW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
advapi32.CredReadW.restype = wt.BOOL
advapi32.CredWriteW.argtypes = [ctypes.POINTER(CREDENTIAL), wt.DWORD]
advapi32.CredWriteW.restype = wt.BOOL
advapi32.CredFree.argtypes = [ctypes.c_void_p]
advapi32.CredFree.restype = None


def read_antigravity_credential():
    pcred = ctypes.POINTER(CREDENTIAL)()
    if not advapi32.CredReadW(TARGET_NAME, CRED_TYPE_GENERIC, 0, ctypes.byref(pcred)):
        err = ctypes.get_last_error()
        return None, err
    try:
        cred = pcred.contents
        if not cred.CredentialBlob or cred.CredentialBlobSize == 0:
            return None, 0
        raw = ctypes.string_at(cred.CredentialBlob, cred.CredentialBlobSize)
        return raw.decode("utf-8"), 0
    finally:
        advapi32.CredFree(pcred)


def write_antigravity_credential(json_str):
    raw = json_str.encode("utf-8")
    buf = (ctypes.c_byte * len(raw)).from_buffer_copy(raw)
    cred = CREDENTIAL()
    cred.Flags = 0
    cred.Type = CRED_TYPE_GENERIC
    cred.TargetName = TARGET_NAME
    cred.Comment = None
    cred.CredentialBlobSize = len(raw)
    cred.CredentialBlob = ctypes.cast(buf, ctypes.POINTER(ctypes.c_byte))
    cred.Persist = CRED_PERSIST_LOCAL_MACHINE
    cred.AttributeCount = 0
    cred.Attributes = None
    cred.TargetAlias = None
    cred.UserName = USER_NAME
    if not advapi32.CredWriteW(ctypes.byref(cred), 0):
        return False, ctypes.get_last_error()
    return True, 0


def decode_jwt_payload(jwt_str):
    try:
        parts = jwt_str.split(".")
        if len(parts) < 2:
            return {}
        payload = parts[1]
        payload += "=" * (-len(payload) % 4)
        return json.loads(base64.urlsafe_b64decode(payload).decode("utf-8", errors="replace"))
    except Exception:
        return {}


def get_default_antigravity_install_dir():
    local_app_data = os.environ.get("LOCALAPPDATA", os.path.expanduser(r"~\AppData\Local"))
    return os.path.join(local_app_data, "Programs", "Antigravity")


def get_default_antigravity_exe_path():
    return os.path.join(get_default_antigravity_install_dir(), "Antigravity.exe")


class SwitcherCorePy:
    def __init__(self):
        self.base_dir = os.path.dirname(os.path.abspath(__file__))
        self.profiles_dir = os.path.join(self.base_dir, "profiles")
        self.accounts_path = os.path.join(self.profiles_dir, "accounts.json")
        self.settings_path = os.path.join(self.profiles_dir, "switcher_settings.json")
        self.rollback_path = os.path.join(self.profiles_dir, "last_rollback_backup.json")
        os.makedirs(self.profiles_dir, exist_ok=True)
        self.logs = []
        self.last_error_code = "NONE"
        self.last_error_detail = "Chưa có lỗi nào ghi nhận."
        self.settings = self.load_settings()
        self.accounts = self.load_accounts()
        self.log(f"Khởi tạo thành công. Đã nạp {len(self.accounts)} tài khoản trong danh sách.")

    def log(self, msg):
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        self.logs.append(f"[{ts}] {msg}")
        if len(self.logs) > 250:
            self.logs.pop(0)

    def record_error(self, code, detail):
        self.last_error_code = code
        self.last_error_detail = detail
        self.log(f"❌ LỖI [{code}]: {detail}")

    def load_settings(self):
        defaults = {
            "CustomAntigravityExePath": "",
            "ConfirmBeforeClosingApp": True,
            "AutoRelaunchAfterSwitch": True,
            "MinimizeToTrayOnClose": True,
        }
        try:
            if os.path.exists(self.settings_path):
                with open(self.settings_path, "r", encoding="utf-8-sig") as f:
                    defaults.update(json.load(f))
        except Exception:
            pass
        return defaults

    def save_settings(self):
        try:
            with open(self.settings_path, "w", encoding="utf-8") as f:
                json.dump(self.settings, f, indent=2, ensure_ascii=False)
        except Exception as e:
            self.record_error("ERR_SAVE_SETTINGS", str(e))

    def load_accounts(self):
        accs = []
        try:
            if os.path.exists(self.accounts_path):
                with open(self.accounts_path, "r", encoding="utf-8-sig") as f:
                    accs = json.load(f) or []
            for s in range(1, 6):
                lp = os.path.join(self.profiles_dir, f"slot_{s}.json")
                if os.path.exists(lp):
                    with open(lp, "r", encoding="utf-8-sig") as f:
                        item = json.load(f)
                        if item and item.get("CredentialJson"):
                            if not any(a.get("Email", "").lower() == item.get("Email", "").lower() for a in accs):
                                item.setdefault("Note", f"Tài khoản Ultra #{len(accs) + 1}")
                                accs.append(item)
            for idx, a in enumerate(accs):
                a["SlotId"] = idx + 1
        except Exception as e:
            self.record_error("ERR_LOAD_ACCOUNTS", str(e))
        return accs

    def save_accounts(self):
        try:
            for idx, a in enumerate(self.accounts):
                a["SlotId"] = idx + 1
            with open(self.accounts_path, "w", encoding="utf-8") as f:
                json.dump(self.accounts, f, indent=2, ensure_ascii=False)
        except Exception as e:
            self.record_error("ERR_SAVE_ACCOUNTS", str(e))

    def locate_antigravity_exe_dialog(self, parent=None):
        default_dir = get_default_antigravity_install_dir()
        default_exe = get_default_antigravity_exe_path()
        kwargs = {
            "parent": parent,
            "title": "Chọn file Antigravity.exe trên máy của bạn",
            "filetypes": [("Antigravity Executable", "Antigravity.exe"), ("All Executables", "*.exe")],
        }
        if os.path.exists(default_exe):
            kwargs["initialdir"] = default_dir
            kwargs["initialfile"] = "Antigravity.exe"
        elif os.path.isdir(default_dir):
            kwargs["initialdir"] = default_dir
        chosen = filedialog.askopenfilename(**kwargs)
        if chosen:
            self.settings["CustomAntigravityExePath"] = chosen
            self.save_settings()
            self.log(f"Đã cập nhật đường dẫn Antigravity.exe: {chosen}")
        return chosen


if __name__ == "__main__":
    exe_candidate = os.path.join(os.path.dirname(os.path.abspath(__file__)), "AntigravitySwitcher.exe")
    if os.path.exists(exe_candidate) and "--force-py" not in sys.argv:
        subprocess.Popen([exe_candidate] + sys.argv[1:])
        sys.exit(0)
    core = SwitcherCorePy()
    print(f"Antigravity Switcher Python Ready. Loaded {len(core.accounts)} account(s).")
