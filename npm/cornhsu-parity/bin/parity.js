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

// 平台 → 子套件名 / 執行檔名
const TARGETS = {
  "win32 x64": { pkg: "@cornhsu/parity-win32-x64", bin: "parity.exe" },
  "linux x64": { pkg: "@cornhsu/parity-linux-x64", bin: "parity" },
  "darwin x64": { pkg: "@cornhsu/parity-darwin-x64", bin: "parity" },
  "darwin arm64": { pkg: "@cornhsu/parity-darwin-arm64", bin: "parity" },
};

const key = `${process.platform} ${process.arch}`;
const target = TARGETS[key];

if (!target) {
  console.error(
    `Cornhsu.Parity 沒有 ${key} 的預先建置版本。\n` +
      `目前支援:${Object.keys(TARGETS).join("、")}\n` +
      `其他平台請改用 .NET 版本:dotnet tool install -g Cornhsu.Parity`
  );
  process.exit(1);
}

let binary;
try {
  // 從子套件的 package.json 反推安裝位置,不用猜 node_modules 的層級
  // (pnpm / yarn 的實體結構與 npm 不同,require.resolve 才可靠)
  const manifest = require.resolve(`${target.pkg}/package.json`);
  binary = path.join(path.dirname(manifest), "bin", target.bin);
} catch {
  console.error(
    `找不到平台套件 ${target.pkg}。\n` +
      `可能是安裝時加了 --no-optional,或安裝過程中斷。\n` +
      `請重新安裝:npm install cornhsu-parity`
  );
  process.exit(1);
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
  console.error(`無法執行 ${binary}:${result.error.message}`);
  process.exit(1);
}

// 被訊號中斷時 status 會是 null;轉成慣例的 128+signal,別讓 CI 誤判為成功
if (result.status === null && result.signal) {
  process.exit(128 + (require("node:os").constants.signals[result.signal] ?? 0));
}

process.exit(result.status ?? 0);
