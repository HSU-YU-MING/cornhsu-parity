#!/bin/sh
# 沒有 Playwright Node 的平台(Alpine 的 linux-musl-*、其他平台的 any):裝得起來、不開瀏覽器的指令照常、
# 要開瀏覽器的指令明講這個平台不行,exit code 跟單一套件時代一樣。CI 用(ci.yml 的 install-smoke-no-browser)。
#
# 用法:smoke-no-browser-platform.sh <套件目錄> <版本> <預期配到的平台> <demo 目錄(裡面要有基準資料庫)>
# 環境變數 INSTALL_RUNTIME_ID:只在安裝那一步讓 SDK 以為自己在這個平台。GitHub 沒有 32 位元 ARM 這種機器,
#   any 後備包配不配得到只能這樣驗(DOTNET_RUNTIME_ID 是 .NET 啟動器認的);跑的時候不設,所以指令真的在這台機器上跑。
#
# Alpine 的 mcr 映像沒有 bash,這支要維持 POSIX sh。
set -u

pkgs=$1
version=$2
expected=$3
demo=$4

work=$(mktemp -d)
fail=0
no() { echo "✗ $1"; fail=1; }

echo "SDK $(dotnet --version) on $(dotnet --info | grep -m1 'RID:' | awk '{print $2}')${INSTALL_RUNTIME_ID:+,安裝時假裝是 $INSTALL_RUNTIME_ID}"

if [ -n "${INSTALL_RUNTIME_ID:-}" ]; then
  DOTNET_RUNTIME_ID="$INSTALL_RUNTIME_ID" dotnet tool install --tool-path "$work/tool" --add-source "$pkgs" --version "$version" Cornhsu.Parity
else
  dotnet tool install --tool-path "$work/tool" --add-source "$pkgs" --version "$version" Cornhsu.Parity
fi || { echo "✗ 裝不起來"; exit 1; }

picked=$(ls "$work/tool/.store/cornhsu.parity/$version/" | grep '^cornhsu\.parity\.' | tr '\n' ' ')
[ "$picked" = "cornhsu.parity.$expected " ] || no "配到的平台包是 [$picked],預期 cornhsu.parity.$expected"

parity="$work/tool/parity"
"$parity" --version || no "--version 失敗"

cp -r "$demo" "$work/demo"
cd "$work/demo" || exit 1

# 示範頁刻意做壞,lint 預期 exit 1
"$parity" lint --config parity.config.json > "$work/lint.log" 2>&1
code=$?
[ "$code" -eq 1 ] || { no "lint 預期 exit 1,實際 $code"; tail -5 "$work/lint.log"; }

# 讀別台機器寫的基準資料庫:這是 SQLite 原生檔有沒有裝對的試金石
"$parity" baseline list --config parity.config.json > "$work/list.log" 2>&1
code=$?
[ "$code" -eq 0 ] && grep -q '#1' "$work/list.log" || { no "baseline list 讀不到基準(exit $code)"; tail -5 "$work/list.log"; }

# 要開瀏覽器的指令:exit code 跟以前一樣,訊息要看得懂
message='cannot run commands that open a browser'
"$parity" check --config parity.config.json > "$work/check.log" 2>&1
code=$?
[ "$code" -eq 2 ] || no "check 預期 exit 2,實際 $code"
grep -q "$message" "$work/check.log" || { no "check 沒有說明這個平台不能開瀏覽器"; tail -5 "$work/check.log"; }

"$parity" install-browser > "$work/install-browser.log" 2>&1
code=$?
[ "$code" -eq 1 ] || no "install-browser 預期 exit 1,實際 $code"
grep -q "$message" "$work/install-browser.log" || { no "install-browser 沒有說明這個平台不能開瀏覽器"; tail -5 "$work/install-browser.log"; }

[ "$fail" -eq 0 ] && echo "✓ $expected:裝得起來、不開瀏覽器的指令照常、開瀏覽器的指令有清楚的錯誤"
exit "$fail"
