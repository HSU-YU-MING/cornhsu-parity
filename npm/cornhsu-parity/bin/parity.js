#!/usr/bin/env node
// npm 通路的啟動腳本。
//
// 為什麼要有這一層(而不是直接把執行檔當 bin):
//   1. 依平台挑出對應的子套件 —— npm 靠 optionalDependencies 的 os/cpu 只下載一份,
//      但仍需要在執行時解析出實際路徑。
//   2. 把 PLAYWRIGHT_NODEJS_PATH 指向「正在跑這支腳本的 Node」。
//      Playwright for .NET 預設會自帶一份 Node(win-x64 是 88 MB),但 npm 的使用者
//      本來就有 Node —— 不指定的話等於是發一份 Node 給 Node 使用者。
//      指定之後每個平台包從 218 MB 降到 129 MB(壓縮後 85 MB → 53 MB)。
//
// 這支腳本刻意不做 postinstall 下載:那種做法會被 `npm ci --ignore-scripts` 擋掉,
// 而不少公司的 CI 預設就是關腳本的 —— 對一個主打「進 CI」的工具是致命的。

"use strict";

const { spawnSync } = require("node:child_process");
const path = require("node:path");

// 平台 → 子套件名 / 執行檔名。必須與 npm/prepare.mjs 的 TARGETS 一一對應
// (RepoInvariantsTests 會比對;1.1.0 漏了兩個 arm64,平台包有發佈、npx 卻說不支援)
const TARGETS = {
  "win32 x64": { pkg: "@cornhsu/parity-win32-x64", bin: "parity.exe" },
  "win32 arm64": { pkg: "@cornhsu/parity-win32-arm64", bin: "parity.exe" },
  "linux x64": { pkg: "@cornhsu/parity-linux-x64", bin: "parity" },
  "linux arm64": { pkg: "@cornhsu/parity-linux-arm64", bin: "parity" },
  "darwin x64": { pkg: "@cornhsu/parity-darwin-x64", bin: "parity" },
  "darwin arm64": { pkg: "@cornhsu/parity-darwin-arm64", bin: "parity" },
};

// 啟動腳本自己的錯誤一律 exit 2:CLI 的契約是 0 通過 / 1 落差超 gate / 2 執行錯誤 / 3 可信度不足,
// 回 1 會被 CI 讀成「設計還原度不合格」。訊息用英文,與 CLI 本體一致(0.12.0 起)。
const EXECUTION_ERROR = 2;

const key = `${process.platform} ${process.arch}`;
const target = TARGETS[key];

if (!target) {
  console.error(
    `cornhsu-parity: there is no prebuilt build for ${key}.\n` +
      `Supported: ${Object.keys(TARGETS).join(", ")}\n` +
      `On other platforms use the .NET tool instead: dotnet tool install -g Cornhsu.Parity`
  );
  process.exit(EXECUTION_ERROR);
}

let binary;
try {
  // 從子套件的 package.json 反推安裝位置,不用猜 node_modules 的層級
  // (pnpm / yarn 的實體結構與 npm 不同,require.resolve 才可靠)
  const manifest = require.resolve(`${target.pkg}/package.json`);
  binary = path.join(path.dirname(manifest), "bin", target.bin);
} catch {
  console.error(
    `cornhsu-parity: the platform package ${target.pkg} is not installed.\n` +
      `It is an optional dependency, so the install may have skipped it (--no-optional / --omit=optional) or been interrupted.\n` +
      `Reinstall: npm install cornhsu-parity`
  );
  process.exit(EXECUTION_ERROR);
}

const result = spawnSync(binary, process.argv.slice(2), {
  stdio: "inherit",
  env: {
    ...process.env,
    // 讓 Playwright 用使用者現成的 Node,平台包因此不必自帶一份
    PLAYWRIGHT_NODEJS_PATH: process.env.PLAYWRIGHT_NODEJS_PATH || process.execPath,
  },
});

if (result.error) {
  console.error(`cornhsu-parity: could not run ${binary}: ${result.error.message}`);
  process.exit(EXECUTION_ERROR);
}

// 被訊號中斷時 status 會是 null;轉成慣例的 128+signal,別讓 CI 誤判為成功
if (result.status === null && result.signal) {
  process.exit(128 + (require("node:os").constants.signals[result.signal] ?? 0));
}

process.exit(result.status ?? 0);
