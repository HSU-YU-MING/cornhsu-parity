// 把 dotnet publish 的產物組裝成可發布的 npm 套件。
//
// 用法:node npm/prepare.mjs <version> <publishRoot>
//   version     發布版號(取自 git tag,例:0.9.5)
//   publishRoot 內含各 RID 子目錄的資料夾,例:artifacts/publish/win-x64/…
//
// 產出:npm/dist/ 底下七個可直接 `npm publish` 的資料夾
//   cornhsu-parity/              主套件(啟動腳本)
//   parity-win32-x64/ 等         平台套件(自帶執行環境的 .NET 產物,六個 RID)
//
// 為什麼用腳本而不是全寫在 workflow YAML:版號要同步改七個 package.json、
// 還要刪掉 Playwright 自帶的 node —— 這些邏輯寫在 YAML 裡難讀也難在本機驗證。

import { cp, mkdir, readFile, rm, writeFile, access } from "node:fs/promises";
import path from "node:path";

const [version, publishRoot] = process.argv.slice(2);
if (!version || !publishRoot) {
  console.error("用法:node npm/prepare.mjs <version> <publishRoot>");
  process.exit(1);
}

// RID → npm 平台套件的 os/cpu 與執行檔名
const TARGETS = [
  { rid: "win-x64", pkg: "parity-win32-x64", os: "win32", cpu: "x64", bin: "parity.exe" },
  { rid: "win-arm64", pkg: "parity-win32-arm64", os: "win32", cpu: "arm64", bin: "parity.exe" },
  { rid: "linux-x64", pkg: "parity-linux-x64", os: "linux", cpu: "x64", bin: "parity" },
  { rid: "linux-arm64", pkg: "parity-linux-arm64", os: "linux", cpu: "arm64", bin: "parity" },
  { rid: "osx-x64", pkg: "parity-darwin-x64", os: "darwin", cpu: "x64", bin: "parity" },
  { rid: "osx-arm64", pkg: "parity-darwin-arm64", os: "darwin", cpu: "arm64", bin: "parity" },
];

const root = path.resolve(import.meta.dirname, "..");
const distDir = path.join(root, "npm", "dist");
await rm(distDir, { recursive: true, force: true });
await mkdir(distDir, { recursive: true });

const exists = async (p) => access(p).then(() => true, () => false);

// ── 平台套件 ──
const built = [];
for (const t of TARGETS) {
  const src = path.join(publishRoot, t.rid);
  if (!(await exists(src))) {
    console.warn(`⚠ 跳過 ${t.rid}:找不到 ${src}`);
    continue;
  }

  const out = path.join(distDir, t.pkg);
  await cp(src, path.join(out, "bin"), { recursive: true });

  // Playwright 自帶的 Node 一律刪除 —— 啟動腳本會把 PLAYWRIGHT_NODEJS_PATH
  // 指向使用者現成的 Node。這一步是整個 npm 通路能成立的關鍵:
  // win-x64 的 node.exe 單檔就 88 MB,佔了未處理前總體積的四成。
  const bundledNode = path.join(out, "bin", ".playwright", "node");
  if (await exists(bundledNode)) {
    await rm(bundledNode, { recursive: true, force: true });
  } else {
    console.warn(`⚠ ${t.rid}:找不到 .playwright/node,可能 Playwright 版面有變,請確認體積`);
  }

  // 授權:平台套件是散布第三方元件的那一包(.NET runtime、ASP.NET Core、Playwright driver、
  // SQLite 原生檔、ImageSharp…),所以帶上 Parity 自己的 LICENSE 與第三方授權聲明。
  // driver 自己的 NOTICE 在 bin/.playwright/package/,上面只刪 .playwright/node,不會動到它。
  // 來源檔不存在時 cp 會丟例外、整個 prepare 失敗——寧可發不出去,也不要發出缺授權的包。
  await cp(path.join(root, "LICENSE"), path.join(out, "LICENSE"));
  await cp(path.join(root, "THIRD-PARTY-NOTICES.md"), path.join(out, "THIRD-PARTY-NOTICES.md"));

  await writeFile(
    path.join(out, "package.json"),
    JSON.stringify(
      {
        name: `@cornhsu/${t.pkg}`,
        version,
        description: `The ${t.os}-${t.cpu} binary for Cornhsu.Parity. Install cornhsu-parity instead — do not depend on this package directly.`,
        homepage: "https://cornhsu.com/parity",
        repository: {
          type: "git",
          url: "git+https://github.com/HSU-YU-MING/cornhsu-parity.git",
        },
        license: "MIT",
        author: "許彧銘 Hsu Yu-Ming (https://cornhsu.com/)",
        os: [t.os],
        cpu: [t.cpu],
        // npm 會自動收錄 LICENSE,但不會自動收錄 THIRD-PARTY-NOTICES.md;兩個都明列,不靠隱含規則
        files: ["bin/", "LICENSE", "THIRD-PARTY-NOTICES.md"],
      },
      null,
      2
    ) + "\n"
  );

  built.push(t);
  console.log(`✔ ${t.pkg}`);
}

if (built.length === 0) {
  console.error("沒有任何平台產物,中止");
  process.exit(1);
}

// ── 主套件 ──
const mainSrc = path.join(root, "npm", "cornhsu-parity");
const mainOut = path.join(distDir, "cornhsu-parity");
await cp(mainSrc, mainOut, { recursive: true });

const manifest = JSON.parse(await readFile(path.join(mainOut, "package.json"), "utf8"));
manifest.version = version;
// 只列出這次真的有建出來的平台,避免安裝時去要一個不存在的版本
manifest.optionalDependencies = Object.fromEntries(
  built.map((t) => [`@cornhsu/${t.pkg}`, version])
);
await writeFile(path.join(mainOut, "package.json"), JSON.stringify(manifest, null, 2) + "\n");

await cp(path.join(root, "README.md"), path.join(mainOut, "README.md"));
// 主套件只有自己的啟動腳本,沒有第三方程式碼,帶 LICENSE 就夠(npm 會自動收錄,不必列進 files)
await cp(path.join(root, "LICENSE"), path.join(mainOut, "LICENSE"));

console.log(`✔ cornhsu-parity(${built.length} 個平台,版本 ${version})`);
