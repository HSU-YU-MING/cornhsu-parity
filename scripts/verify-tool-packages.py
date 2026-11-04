#!/usr/bin/env python3
"""檢查「每個平台一包」的 dotnet tool 套件長得對不對。CI(ci.yml 的 rid-pack)與發版(release.yml)共用。

【實驗】這支腳本屬於「每個平台一包」的可行性實驗,見 PR 說明。

為什麼要有它:pack 綠燈不代表包是對的。0.9.1 就是 pack 綠、裝起來才 Driver not found。
改成每個平台一包之後,會出錯的地方換成這幾個,而且**全都不會讓 pack 失敗**:

  - 平台包夾帶了別的平台的 Playwright Node(實測:Parity.Engine 沒設 PlaywrightPlatform 時,
    在 ubuntu 上 pack 的 win-x64 包會多一份 linux-x64 Node,+45 MB)
  - 平台包少了自己平台的 Node 或 SQLite 原生檔(裝得起來,一跑 check 才爆)
  - 指標包列的平台跟實際產出的平台包對不上(使用者在那個平台上安裝失敗)
  - 部署形式不是預期的那一種(SDK 改預設時,framework 變 self-contained 或反過來,大小差一倍)
  - 單一包超過 nuget.org 的 250 MB 上限(push 才會被拒,那時 tag 已經推出去了)

用法:
  verify-tool-packages.py <目錄> <版本> [--form framework|self-contained] [--summary <檔案>]
  verify-tool-packages.py --sizes <nupkg>... [--summary <檔案>]      只列大小(比較用)

--summary 會把大小表附加到那個檔案(CI 傳 $GITHUB_STEP_SUMMARY)。有任何一項不對就 exit 1。
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import zipfile

PACKAGE_ID = "Cornhsu.Parity"

# 與 src/Parity.Cli/Parity.Cli.csproj 的 RuntimeIdentifiers 一致。改清單時兩邊一起改:
# 這裡多一個或少一個,檢查就紅——那正是要它做的事(指標包列的平台 = 使用者裝得到的平台)。
EXPECTED_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]

# Playwright 的 Node 資料夾名。win-arm64 刻意是 win32_x64:Playwright 沒有 ARM 版 Windows 的 Node,
# 它在 Windows 上一律找 win32_x64\node.exe(Windows 11 ARM 用 x64 模擬執行)。
NODE_DIR = {
    "win-x64": "win32_x64",
    "win-arm64": "win32_x64",
    "linux-x64": "linux-x64",
    "linux-arm64": "linux-arm64",
    "osx-x64": "darwin-x64",
    "osx-arm64": "darwin-arm64",
}

SQLITE_NATIVE = {"win": "e_sqlite3.dll", "linux": "libe_sqlite3.so", "osx": "libe_sqlite3.dylib"}

# nuget.org 的單包上限是 250 MB。這裡取 250×10^6 位元組(比 250 MiB 小),寧可保守。
NUGET_LIMIT_BYTES = 250 * 1000 * 1000

CORECLR = re.compile(r"(^|/)(lib)?coreclr\.(dll|so|dylib)$")


def mb(n: int) -> str:
    return f"{n / 1e6:.1f}"


def tool_settings(z: zipfile.ZipFile) -> tuple[str, str]:
    names = [n for n in z.namelist() if n.endswith("/DotnetToolSettings.xml") and n.startswith("tools/")]
    if len(names) != 1:
        raise ValueError(f"找到 {len(names)} 個 DotnetToolSettings.xml(應該剛好 1 個)")
    return names[0], z.read(names[0]).decode("utf-8-sig")


def package_types(z: zipfile.ZipFile) -> list[str]:
    nuspec = [n for n in z.namelist() if n.endswith(".nuspec") and "/" not in n]
    text = z.read(nuspec[0]).decode("utf-8-sig")
    return re.findall(r'<packageType name="([^"]+)"', text)


def check_pointer(path: str, errors: list[str]) -> None:
    with zipfile.ZipFile(path) as z:
        _, xml = tool_settings(z)
        listed = re.findall(r'<RuntimeIdentifierPackage RuntimeIdentifier="([^"]+)" Id="([^"]+)"', xml)
        rids = [rid for rid, _ in listed]
        if sorted(rids) != sorted(EXPECTED_RIDS):
            errors.append(f"指標包列的平台是 {rids},預期 {EXPECTED_RIDS}")
        for rid, pkg_id in listed:
            if pkg_id != f"{PACKAGE_ID}.{rid}":
                errors.append(f"指標包裡 {rid} 指向 {pkg_id},預期 {PACKAGE_ID}.{rid}")
        dlls = [n for n in z.namelist() if n.endswith(".dll")]
        if dlls:
            errors.append(f"指標包不該有任何 dll(實作在平台包裡),卻有 {len(dlls)} 個,例如 {dlls[0]}")
        if package_types(z) != ["DotnetTool"]:
            errors.append(f"指標包的 packageType 是 {package_types(z)},預期 ['DotnetTool']")


def check_rid_package(path: str, rid: str, form: str, errors: list[str]) -> None:
    def err(msg: str) -> None:
        errors.append(f"{os.path.basename(path)}:{msg}")

    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        settings_path, xml = tool_settings(z)
        root = settings_path.rsplit("/", 1)[0] + "/"  # tools/<tfm|any>/<rid>/
        if not root.endswith(f"/{rid}/"):
            err(f"DotnetToolSettings.xml 在 {settings_path},不在 {rid} 的資料夾")
        if package_types(z) != ["DotnetToolRidPackage"]:
            err(f"packageType 是 {package_types(z)},預期 ['DotnetToolRidPackage']")

        # framework:Runner="dotnet"、不附自己的啟動檔,安裝時由使用者的 SDK 產生——跟現在的單一套件一樣。
        # 如果變成 Runner="executable",SDK 在 Windows 上產生的是 parity.cmd,bash(action.yml)叫不到 parity。
        # self-contained 一定是 executable(自帶 runtime 要有自己的啟動檔),Windows 上就是 parity.cmd。
        apphost = root + ("parity.exe" if rid.startswith("win-") else "parity")
        if form == "framework":
            if 'EntryPoint="parity.dll" Runner="dotnet"' not in xml:
                err('framework 平台包應該是 Runner="dotnet"、EntryPoint="parity.dll"(否則 Windows 上的啟動檔會變成 parity.cmd)')
            if apphost in names:
                err(f"framework 平台包不該附自己的啟動檔 {apphost}(UseAppHost=false 沒生效?)")
            if root + "parity.dll" not in names:
                err(f"找不到 {root}parity.dll")
        else:
            if 'Runner="executable"' not in xml:
                err('self-contained 平台包應該是 Runner="executable"')
            if apphost not in names:
                err(f"找不到啟動檔 {apphost}")

        node_dirs = sorted({n[len(root):].split("/")[2] for n in names
                            if n.startswith(root + ".playwright/node/") and n.count("/") > root.count("/") + 2})
        if node_dirs != [NODE_DIR[rid]]:
            err(f"Playwright Node 資料夾是 {node_dirs},預期只有 ['{NODE_DIR[rid]}']")
        node_exe = f"{root}.playwright/node/{NODE_DIR[rid]}/" + ("node.exe" if rid.startswith("win-") else "node")
        if node_exe not in names:
            err(f"找不到 {node_exe}")
        if root + ".playwright/package/cli.js" not in names:
            err("找不到 Playwright driver 的 .playwright/package/cli.js")

        sqlite = [n for n in names if re.search(r"(^|/)(lib)?e_sqlite3\.(dll|so|dylib)$", n)]
        want = root + SQLITE_NATIVE[rid.split("-")[0]]
        if sqlite != [want]:
            err(f"SQLite 原生檔是 {sqlite},預期只有 ['{want}']")
        if any("/runtimes/" in n for n in names):
            err("平台包不該還有 runtimes/ 子目錄(那是全平台原生檔的形狀)")

        has_runtime = any(CORECLR.search(n) for n in names)
        if form == "framework" and has_runtime:
            err("預期依賴使用者的 .NET runtime(framework),包裡卻帶了 coreclr")
        if form == "self-contained" and not has_runtime:
            err("預期自帶 .NET runtime(self-contained),包裡卻沒有 coreclr")


def size_rows(paths: list[str]) -> list[str]:
    rows = []
    for p in paths:
        size = os.path.getsize(p)
        rows.append(f"| `{os.path.basename(p)}` | {mb(size)} | {mb(NUGET_LIMIT_BYTES - size)} |")
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", nargs="?")
    ap.add_argument("version", nargs="?")
    ap.add_argument("--form", choices=["framework", "self-contained"], default="framework")
    ap.add_argument("--sizes", nargs="+", metavar="NUPKG")
    ap.add_argument("--summary")
    ap.add_argument("--title", default="")
    args = ap.parse_args()

    errors: list[str] = []
    if args.sizes:
        paths = args.sizes
    else:
        if not args.dir or not args.version:
            ap.error("需要 <目錄> <版本>(或 --sizes)")
        pointer = os.path.join(args.dir, f"{PACKAGE_ID}.{args.version}.nupkg")
        paths = [pointer] + [os.path.join(args.dir, f"{PACKAGE_ID}.{rid}.{args.version}.nupkg") for rid in EXPECTED_RIDS]
        missing = [p for p in paths if not os.path.exists(p)]
        if missing:
            for p in missing:
                errors.append(f"找不到 {p}")
        else:
            check_pointer(pointer, errors)
            for rid in EXPECTED_RIDS:
                check_rid_package(os.path.join(args.dir, f"{PACKAGE_ID}.{rid}.{args.version}.nupkg"), rid, args.form, errors)
        extra = sorted(f for f in os.listdir(args.dir) if f.endswith(".nupkg") and os.path.join(args.dir, f) not in paths)
        if extra:
            errors.append(f"目錄裡有 EXPECTED_RIDS 以外的套件(平台清單改了而這支腳本沒跟著改?):{extra}")
        paths = [p for p in paths if os.path.exists(p)]

    for p in paths:
        if os.path.getsize(p) >= NUGET_LIMIT_BYTES:
            errors.append(f"{os.path.basename(p)} 是 {mb(os.path.getsize(p))} MB,超過 nuget.org 上限 250 MB")

    table = ["| 套件 | MB | 距 250 MB 上限 |", "|---|---:|---:|"] + size_rows(paths)
    print("\n".join(table))
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as f:
            if args.title:
                f.write(f"\n### {args.title}\n\n")
            f.write("\n".join(table) + "\n")

    if errors:
        print("\n✗ 套件檢查失敗:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1
    if not args.sizes:
        print(f"\n✓ 指標包 + {len(EXPECTED_RIDS)} 個平台包({args.form})形狀正確")
    return 0


if __name__ == "__main__":
    sys.exit(main())
