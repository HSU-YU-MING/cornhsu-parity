#!/usr/bin/env pwsh
<#
.SYNOPSIS
    檢查兩份 README 裡「會隨開發自然過期」的數字，跟真實情況是否一致。

.DESCRIPTION
    README 的 At a glance 表格與 GitHub Action 範例裡有幾個硬寫的事實，改動程式碼或發版時
    很容易忘了跟著更新 —— v0.13.1 當下就已經漂了：README 寫「195 條測試」，實際是 197。
    人工比對一定會漏，因為加測試的人不會想到 README。所以讓 CI 每次自己數一次再比。

    檢查四類事實：

      1. 測試條數          ← 實際跑 `dotnet test --list-tests` 數出來
      2. 累計發佈版數      ← CHANGELOG.md 裡的版本標題數量
      3. 最新版號          ← CHANGELOG.md 最上面那個版本標題
      4. Action 的 @v pin  ← 同上，兩份 README 各兩處

    為什麼 2、3 對 CHANGELOG 比而不對 git tag 比：README 的版號是在**推 tag 之前**就要
    commit 的，拿「現有最新 tag」當基準的話，發版那個 commit 必紅。CHANGELOG 是這個時間點
    唯一寫得下新版號的地方，所以它是 pre-tag 的版本真相源；tag 與 CHANGELOG 一致這件事
    由 release.yml 在推 tag 時另外把關。

    兩份 README 一起檢查、一起更新 —— 這個腳本有一半的用途就是防它們彼此漂開。

.PARAMETER Update
    不報錯，直接把兩份 README 改成正確的值。確認新數字才是對的之後用這個一鍵重貼。

.PARAMETER NoBuild
    數測試時不重建（CI 在同一個 job 裡已經建過了）。

.PARAMETER TestCount
    直接指定測試條數，跳過 dotnet test。給沒有 .NET 的環境或想快速跑一次時用。

.EXAMPLE
    pwsh scripts/verify-readme-facts.ps1
    pwsh scripts/verify-readme-facts.ps1 -Update
#>
[CmdletBinding()]
param(
    [switch]$Update,
    [switch]$NoBuild,
    [int]$TestCount = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$enPath = Join-Path $repo 'README.md'
$zhPath = Join-Path $repo 'README.zh-Hant.md'
$changelogPath = Join-Path $repo 'CHANGELOG.md'

# 明確指定 UTF-8 無 BOM 讀寫：兩份 README 都是 UTF-8 無 BOM + LF，
# 用 Set-Content 會依系統 codepage 寫出去，中文那份會直接毀掉。
function Read-Utf8([string]$path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}
function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
}
function Get-LineNumber([string]$text, [int]$index) {
    return ([regex]::Matches($text.Substring(0, $index), "`n")).Count + 1
}

# ── 事實來源 1：CHANGELOG ────────────────────────────────────────────────
# 「## 未發佈」不算一版，所以只認帶版號的標題。兩種格式都吃：「## 0.13.1」與「## [1.1.0] — …」
$changelog = Read-Utf8 $changelogPath
$headings = [regex]::Matches($changelog, '(?m)^##\s+\[?(\d+\.\d+\.\d+)\]?')
if ($headings.Count -eq 0) {
    throw 'CHANGELOG.md 裡找不到任何版本標題（預期形如「## 0.13.1」）——版本真相源壞了，先修它。'
}
$latestVersion = $headings[0].Groups[1].Value
$releaseCount = $headings.Count

# ── 事實來源 2：實際測試條數 ─────────────────────────────────────────────
if ($TestCount -le 0) {
    $listArgs = @('test', (Join-Path $repo 'Parity.slnx'), '-c', 'Release', '--list-tests')
    if ($NoBuild) { $listArgs += '--no-build' }
    Write-Host "數測試中：dotnet $($listArgs -join ' ')"
    $listed = & dotnet @listArgs
    if ($LASTEXITCODE -ne 0) {
        $listed | ForEach-Object { Write-Host $_ }
        throw "dotnet test --list-tests 失敗（exit $LASTEXITCODE）。"
    }
    $TestCount = @($listed | Where-Object { $_ -match '^\s+Parity\.Tests\.' }).Count
    if ($TestCount -eq 0) {
        throw '--list-tests 一條測試都沒列出來——輸出格式可能變了，這個檢查會變成空轉，先修它。'
    }
}

# ── 待驗事實 ─────────────────────────────────────────────────────────────
# 每一條的 Pattern 都設計成「整個 match 就是那個值本身」（用 lookaround 把周圍文字排除在
# match 之外），所以檢查是比對 match、更新是直接把 match 換成 Expected，兩邊共用同一條規則。
# Occurrences 是預期出現次數：漏掉這格的話，某天有人改寫了那段文字、regex 不再命中，
# 檢查會安靜地變成「零項通過」而不是報錯。
$facts = @(
    @{ Label = '測試條數 (en)'; Path = $enPath; Occurrences = 1
       Pattern = '(?<=\| Tests \| \*\*)\d+(?=\*\*)'
       Expected = "$TestCount" }
    @{ Label = '測試條數 (zh)'; Path = $zhPath; Occurrences = 1
       Pattern = '(?<=\| 測試 \| \*\*)\d+(?= 條\*\*)'
       Expected = "$TestCount" }

    @{ Label = '發佈版數 (en)'; Path = $enPath; Occurrences = 1
       Pattern = '(?<=\| Releases \| )\d+(?= on NuGet)'
       Expected = "$releaseCount" }
    @{ Label = '發佈版數 (zh)'; Path = $zhPath; Occurrences = 1
       Pattern = '(?<=\| 發佈 \| NuGet 共 )\d+(?= 版)'
       Expected = "$releaseCount" }

    @{ Label = '最新版號 (en)'; Path = $enPath; Occurrences = 1
       Pattern = '(?<=\| Releases \| \d+ on NuGet \(v\d+\.\d+\.\d+ → v)\d+\.\d+\.\d+'
       Expected = $latestVersion }
    @{ Label = '最新版號 (zh)'; Path = $zhPath; Occurrences = 1
       Pattern = '(?<=\| 發佈 \| NuGet 共 \d+ 版\(v\d+\.\d+\.\d+ → v)\d+\.\d+\.\d+'
       Expected = $latestVersion }

    # 1.0 起 README 一律引用移動式 @v1（0.x 時代是 pin 確切版本，這兩條當時對著 CHANGELOG 版號比）。
    # 進 2.0 時把 Expected 改成 'v2'。
    @{ Label = 'Action @v ref (en)'; Path = $enPath; Occurrences = 2
       Pattern = '(?<=HSU-YU-MING/cornhsu-parity@)v[\d.]+'
       Expected = 'v1' }
    @{ Label = 'Action @v ref (zh)'; Path = $zhPath; Occurrences = 2
       Pattern = '(?<=HSU-YU-MING/cornhsu-parity@)v[\d.]+'
       Expected = 'v1' }
)

Write-Host ''
Write-Host "真實情況：測試 $TestCount 條 ／ 已發佈 $releaseCount 版 ／ 最新 v$latestVersion（取自 CHANGELOG.md）"
Write-Host ''

$problems = @()
$broken = 0
$changed = @{}

foreach ($fact in $facts) {
    $text = if ($changed.ContainsKey($fact.Path)) { $changed[$fact.Path] } else { Read-Utf8 $fact.Path }
    $name = Split-Path -Leaf $fact.Path
    $found = [regex]::Matches($text, $fact.Pattern)

    if ($found.Count -ne $fact.Occurrences) {
        $broken++
        $problems += "✗ $($fact.Label)：在 $name 找到 $($found.Count) 處，預期 $($fact.Occurrences) 處。" +
                     'README 的措辭被改過，這條檢查已經失效——修 regex，不要刪掉它。'
        continue
    }

    $bad = @($found | Where-Object { $_.Value -ne $fact.Expected })
    if ($bad.Count -eq 0) {
        Write-Host "✓ $($fact.Label)：$($fact.Expected)"
        continue
    }

    foreach ($m in $bad) {
        $line = Get-LineNumber $text $m.Index
        $problems += "✗ $($fact.Label)：${name}:${line} 寫「$($m.Value)」，實際是「$($fact.Expected)」"
    }

    if ($Update) {
        $changed[$fact.Path] = [regex]::Replace($text, $fact.Pattern, $fact.Expected)
    }
}

Write-Host ''

if ($problems.Count -eq 0) {
    Write-Host 'README 的數字與實際一致。'
    exit 0
}

$problems | ForEach-Object { Write-Host $_ }

if ($Update) {
    Write-Host ''
    foreach ($path in @($changed.Keys)) {
        Write-Utf8 $path $changed[$path]
        Write-Host "已更新 $(Split-Path -Leaf $path)"
    }
    if ($broken -gt 0) {
        Write-Host ''
        Write-Host "但有 $broken 條 regex 對不上，那幾項沒有被更新——請看上面的訊息。"
        exit 1
    }
    exit 0
}

Write-Host ''
Write-Host '確認新的數字才是對的之後，跑 pwsh scripts/verify-readme-facts.ps1 -Update 一鍵更新兩份 README。'
exit 1
