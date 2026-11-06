#!/usr/bin/env bash
# 把 npm 通路發上 registry:平台子套件先發、等到真的下載得到、入口套件最後發。跟 push-tool-packages.sh 同一個想法。
#
# 為什麼要分三段而不是一口氣發完:
#   - 使用者裝的是入口套件(cornhsu-parity),平台子套件是它的選用相依。入口套件先到、子套件還沒到的那段時間,
#     npm 不報錯、默默跳過,裝完一執行才 exit 2。2.3.1 的子套件 06:01 發佈,win32-x64 到 07:02 才裝得起來。
#   - npx 還會把這份半成品留在快取,同步完成之後還是壞的,所以不能靠「等一下就好」。
#   - 「查得到」不算數:套件清單與 /<版本> 都回 200 之後,壓縮檔還 404 了幾分鐘(2.3.1 實測)。
#     這裡看的是 npm install 實際讀的那份清單(install-v1),再實際下載它指向的壓縮檔。
#   - 這是從發版機器看出去的,npm 的 CDN 各地不一定同步,所以全部好了之後再等一段重查一次。
#     只能縮小空窗,不保證全世界同時到;漏網的由啟動腳本的錯誤訊息講清楚。
#
# 用法:bash scripts/publish-npm-packages.sh <platform|wait|entry> <目錄>
#   platform  發入口套件列出的每個平台子套件(先確認每個資料夾都在,缺一個就一個都不發)
#   wait      等每個平台子套件的壓縮檔都下載得到(不需要金鑰)
#   entry     再確認一次,然後發入口套件
# <目錄> 是 npm/prepare.mjs 的輸出(npm/dist)。
# 環境變數:
#   NPM_REGISTRY       預設 https://registry.npmjs.org
#   WAIT_TIMEOUT_MIN   預設 180(2.3.0 的 win32-arm64 花了超過 1.5 小時)
#   WAIT_INTERVAL_SEC  預設 60
#   WAIT_SETTLE_SEC    預設 300:全部好了之後隔多久再確認一次
#
# 冪等:已存在的版本跳過而不是報錯,發到一半失敗時直接重跑 job 即可。這是 v0.9.6 實際踩到的狀況:
# 四個平台包發成功、主套件因未設定而 ENEEDAUTH,當時無法直接重跑。
# 不帶任何 token:npm CLI 在每次 publish 時用 GitHub 的 OIDC 換短效金鑰(見 release.yml 的說明)。
set -euo pipefail

phase="${1:?用法:publish-npm-packages.sh <platform|wait|entry> <目錄>}"
dist="${2:?缺目錄}"
registry="${NPM_REGISTRY:-https://registry.npmjs.org}"
timeout_min="${WAIT_TIMEOUT_MIN:-180}"
interval_sec="${WAIT_INTERVAL_SEC:-60}"
settle_sec="${WAIT_SETTLE_SEC:-300}"

entry="$dist/cornhsu-parity"
[ -f "$entry/package.json" ] || { echo "✗ 找不到入口套件 $entry" >&2; exit 1; }

field() { node -p "require(require('node:path').resolve(process.argv[1]))['$2']" "$1" | tr -d '\r'; }

version=$(field "$entry/package.json" version)

# 平台子套件以入口套件自己寫的為準,不另外維護一份清單——發的就是使用者安裝時會去要的那幾個
names=()
while IFS= read -r line; do
  [ -n "$line" ] && names+=("$line")
done < <(node -p "Object.keys(require(require('node:path').resolve(process.argv[1])).optionalDependencies ?? {}).join('\n')" "$entry/package.json" | tr -d '\r')
[ "${#names[@]}" -gt 0 ] || { echo "✗ $entry 沒有列出任何平台子套件" >&2; exit 1; }

dir_of() { printf '%s/%s' "$dist" "${1##*/}"; }

for name in "${names[@]}"; do
  [ -f "$(dir_of "$name")/package.json" ] || { echo "✗ 入口套件列了 $name,但 $dist 裡沒有它;一個都不發" >&2; exit 1; }
done

publish_one() {
  local dir="$1" name out
  name=$(field "$dir/package.json" name)
  if npm view "$name@$version" version >/dev/null 2>&1; then
    echo "↷ $name@$version 已存在,跳過"
    return 0
  fi
  echo "::group::publish $name@$version"
  # 重跑多半發生在 registry 還在同步的時候:npm view 查不到,但其實已經收下了。這也算已存在,否則重跑永遠過不了
  if ! out=$(npm publish "$dir" --access public 2>&1); then
    if printf '%s' "$out" | grep -q 'previously published'; then
      echo "↷ $name@$version 已發佈過(registry 還在同步),跳過"
      echo "::endgroup::"
      return 0
    fi
    printf '%s\n' "$out" >&2
    echo "::endgroup::"
    return 1
  fi
  printf '%s\n' "$out"
  echo "::endgroup::"
}

# npm install 實際讀的清單裡,這一版的壓縮檔網址。名稱裡的 / 要編碼;還沒有這一版就是空字串
tarball_of() {
  curl -fsS --max-time 30 -H 'Accept: application/vnd.npm.install-v1+json' "$registry/${1/\//%2f}" 2>/dev/null \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).versions[process.argv[1]].dist.tarball)}catch{}})' "$version"
}

is_ready() {
  local tarball
  tarball=$(tarball_of "$1") || return 1
  tarball="${tarball%$'\r'}"
  [ -n "$tarball" ] || return 1
  curl -fsS --max-time 60 -r 0-0 -o /dev/null "$tarball" 2>/dev/null
}

wait_all() {
  local settle="$1" deadline clean_once=""
  deadline=$(( $(date +%s) + timeout_min * 60 ))
  while :; do
    pending=()
    for name in "${names[@]}"; do is_ready "$name" || pending+=("$name"); done
    if [ "${#pending[@]}" -eq 0 ]; then
      if [ "$settle" -le 0 ] || [ -n "$clean_once" ]; then
        echo "✓ ${#names[@]} 個平台子套件的 $version 壓縮檔都下載得到"
        return 0
      fi
      clean_once=1
      echo "… 都下載得到了,$settle 秒後再確認一次(npm 的 CDN 各地不一定同步)"
      sleep "$settle"
      continue
    fi
    clean_once=""
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "✗ 等了 $timeout_min 分鐘,這些平台子套件的 $version 壓縮檔還下載不到:${pending[*]}" >&2
      echo "  入口套件沒有發,使用者仍然裝到上一版。晚一點重跑這個 job 即可(已發佈的會跳過)。" >&2
      return 1
    fi
    echo "… 還在等 ${#pending[@]} 個:${pending[*]}"
    sleep "$interval_sec"
  done
}

case "$phase" in
  platform)
    for name in "${names[@]}"; do publish_one "$(dir_of "$name")"; done ;;
  wait)
    wait_all "$settle_sec" ;;
  entry)
    wait_all 0
    publish_one "$entry" ;;
  *)
    echo "✗ 不認得的階段 $phase(platform|wait|entry)" >&2; exit 2 ;;
esac
