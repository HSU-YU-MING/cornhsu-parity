#!/usr/bin/env bash
# 把「每個平台一包」的 dotnet tool 推上 NuGet:平台包先推、等 nuget.org 真的查得到、指標包最後推。
#
# 為什麼要分三段而不是 `dotnet nuget push *.nupkg`:
#   - 使用者裝的是指標包,指標包只寫「去拿 Cornhsu.Parity.<rid>」。指標包先上架、平台包還沒好的那段時間,
#     所有人的 dotnet tool install(含 GitHub Action 的 "2.*")都會失敗。官方文件也要求平台包先發。
#   - nuget.org 收到之後還要驗證、建索引才查得到(2.1.0 那個 243 MB 的包等了半小時以上)。
#     「push 成功」≠「查得到」,所以中間要等,等到 flat container 列出這一版才推指標包。
#   - NuGet/login 換到的臨時金鑰只活 1 小時;等驗證可能超過,所以 release.yml 在推指標包之前重新登入一次。
#
# 用法:scripts/push-tool-packages.sh <platform|wait|pointer|all> <版本> <目錄>
#   platform  推指標包列出的每個平台包(先確認每個檔案都在,缺一個就一個都不推)
#   wait      等每個平台包在 nuget.org 查得到這一版(不需要金鑰)
#   pointer   再確認一次平台包都查得到,然後推指標包
#   all       依序做上面三段(CI 對本機資料夾乾跑用)
# 環境變數:
#   NUGET_API_KEY     推送金鑰(platform/pointer 需要;推到本機資料夾時可以是任意字串)
#   NUGET_SOURCE      預設 https://api.nuget.org/v3/index.json
#   FLAT_CONTAINER    預設 https://api.nuget.org/v3-flatcontainer;設成空字串 = 不等、也不在推指標包前確認(本機資料夾)
#   WAIT_TIMEOUT_MIN  預設 120
#   PACKAGE_ID        預設 Cornhsu.Parity
#
# 冪等:每次 push 都帶 --skip-duplicate,發到一半失敗時直接重跑 job 即可(已上架的會被略過)。
set -euo pipefail

phase="${1:?用法:push-tool-packages.sh <platform|wait|pointer|all> <版本> <目錄>}"
version="${2:?缺版本}"
dir="${3:?缺目錄}"
package_id="${PACKAGE_ID:-Cornhsu.Parity}"
source_url="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
flat="${FLAT_CONTAINER-https://api.nuget.org/v3-flatcontainer}"
timeout_min="${WAIT_TIMEOUT_MIN:-120}"

pointer="$dir/$package_id.$version.nupkg"
[ -f "$pointer" ] || { echo "✗ 找不到指標包 $pointer" >&2; exit 1; }

# 平台包的 ID 以指標包自己寫的為準(DotnetToolSettings.xml 的 RuntimeIdentifierPackages),
# 不另外維護一份清單——推的就是使用者安裝時會去要的那幾個。
mapfile -t rid_ids < <(unzip -p "$pointer" 'tools/*/DotnetToolSettings.xml' | tr -d '\r' \
  | grep -oE '<RuntimeIdentifierPackage [^>]*Id="[^"]+"' | sed -E 's/.*Id="([^"]+)"/\1/')
[ "${#rid_ids[@]}" -gt 0 ] || { echo "✗ $pointer 沒有列出任何平台包——這不是「每個平台一包」的指標包" >&2; exit 1; }

for id in "${rid_ids[@]}"; do
  [ -f "$dir/$id.$version.nupkg" ] || { echo "✗ 指標包列了 $id,但 $dir 裡沒有 $id.$version.nupkg;一個都不推" >&2; exit 1; }
done

push() {
  dotnet nuget push "$1" -k "${NUGET_API_KEY:?需要 NUGET_API_KEY}" -s "$source_url" --skip-duplicate
}

# flat container 的路徑一律小寫(套件 ID 與版本都是)
is_available() {
  local id_lc ver_lc
  id_lc=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  ver_lc=$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')
  curl -fsS --max-time 30 "$flat/$id_lc/index.json" 2>/dev/null | grep -qF "\"$ver_lc\""
}

wait_all() {
  [ -n "$flat" ] || { echo "↷ FLAT_CONTAINER 是空的,不等"; return 0; }
  local deadline=$(( $(date +%s) + timeout_min * 60 )) pending
  while :; do
    pending=()
    for id in "${rid_ids[@]}"; do is_available "$id" || pending+=("$id"); done
    [ "${#pending[@]}" -eq 0 ] && { echo "✓ ${#rid_ids[@]} 個平台包在 $flat 都查得到 $version"; return 0; }
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "✗ 等了 $timeout_min 分鐘,這些平台包還查不到 $version:${pending[*]}" >&2
      echo "  指標包沒有推,使用者仍然裝到上一版。確認 nuget.org 上這幾個套件的驗證狀態後重跑這個 job。" >&2
      return 1
    fi
    echo "… 還在等 ${#pending[@]} 個:${pending[*]}"
    sleep 30
  done
}

case "$phase" in
  platform)
    for id in "${rid_ids[@]}"; do push "$dir/$id.$version.nupkg"; done ;;
  wait)
    wait_all ;;
  pointer)
    wait_all
    push "$pointer" ;;
  all)
    for id in "${rid_ids[@]}"; do push "$dir/$id.$version.nupkg"; done
    wait_all
    push "$pointer" ;;
  *)
    echo "✗ 不認得的階段 $phase(platform|wait|pointer|all)" >&2; exit 2 ;;
esac
