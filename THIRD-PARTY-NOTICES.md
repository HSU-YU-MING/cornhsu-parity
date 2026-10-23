# Third-party notices

Parity itself is licensed under the [MIT License](LICENSE). Its published packages
also carry third-party components, listed below with the licenses they are
distributed under. None of these licenses is copyleft.

## What each distribution channel ships

| Channel | Package | Third-party content |
|---|---|---|
| NuGet (dotnet tool) | `Cornhsu.Parity` | The .NET libraries in the table below, the Playwright driver (`tools/net10.0/any/.playwright/package/`), Node.js for five platforms (`tools/net10.0/any/.playwright/node/`) and native SQLite libraries for every platform (`tools/net10.0/any/runtimes/`). The tool runs on the .NET you have installed, so it does **not** include the .NET runtime. |
| npm | `@cornhsu/parity-<platform>` (six packages) | A self-contained executable for one platform: the same .NET libraries and Playwright driver (`bin/.playwright/package/`), the native SQLite library for that platform, **plus the .NET runtime and the ASP.NET Core shared framework**. It does **not** include Node.js: `npm/prepare.mjs` removes the Node.js that Playwright ships, and the launcher uses the Node.js you already have. |
| npm | `cornhsu-parity` | Only Parity's own launcher script and README. No third-party code. |

This file is at the root of the NuGet package and of every npm platform package. The
local report UI that `parity serve` opens (`src/Parity.Cli/wwwroot`) is Parity's own
code: it loads no third-party scripts, stylesheets or fonts.

## Components

Versions are the ones resolved for `src/Parity.Cli` (`obj/project.assets.json`).

| Component | Version | License | Copyright | Project |
|---|---|---|---|---|
| Microsoft.Playwright (.NET binding) | 1.63.0 | MIT | © Microsoft Corporation | https://github.com/microsoft/playwright-dotnet |
| Playwright driver (`playwright-core`, inside Microsoft.Playwright) | 1.63.0 | Apache-2.0 | Copyright (c) Microsoft Corporation; portions Copyright 2017 Google Inc. | https://github.com/microsoft/playwright |
| Node.js (inside Microsoft.Playwright; **NuGet package only**) | 24.21.0 | MIT, plus the licenses of the dependencies it bundles | Copyright Node.js contributors; Copyright Joyent, Inc. and other Node contributors | https://github.com/nodejs/node |
| SixLabors.ImageSharp | 3.1.12 | Apache-2.0 ([why](#sixlaborsimagesharp-apache-20)) | Copyright © Six Labors | https://github.com/SixLabors/ImageSharp |
| SQLitePCLRaw.core, SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | Apache-2.0 | Copyright 2014-2025 SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | Apache-2.0 | Copyright 2014-2026 SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native `e_sqlite3`, NuGet package `SQLite` built by SourceGear) | 3.53.4 | Public domain | SQLite is in the public domain; package Copyright 2014-2026 SourceGear, LLC | https://sqlite.org/copyright.html |
| Microsoft.EntityFrameworkCore, .Abstractions, .Relational, .Sqlite, .Sqlite.Core | 10.0.12 | MIT | © Microsoft Corporation | https://github.com/dotnet/efcore |
| Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | © Microsoft Corporation | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyModel | 10.0.12 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| .NET runtime (**npm platform packages only**) | 10.0.x used by the release build | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| ASP.NET Core shared framework (**npm platform packages only**) | 10.0.x used by the release build | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/aspnetcore |

## SixLabors.ImageSharp: Apache-2.0

ImageSharp is published under the Six Labors Split License 1.0 (the `LICENSE` file in
the `SixLabors.ImageSharp` NuGet package). Section 2 of that license grants the Apache
License 2.0 when the work is consumed "for use in software licensed under an Open
Source or Source Available license". Parity is open source under MIT, so Parity uses
ImageSharp under Apache-2.0.

When you install Parity, ImageSharp reaches you through Parity, a third party unrelated
to Six Labors. Section 1 of the Split License calls that a "Transitive Package
Dependency", and Section 2 grants Apache-2.0 for it as well. The Split License asks
licensees to reference only the license they were granted, which is why ImageSharp
appears as Apache-2.0 throughout this file.

## Playwright driver: NOTICE and bundled licenses

The driver is Apache-2.0 and ships a `NOTICE` file, so Section 4(d) of the Apache
License applies: the notice has to travel with every redistribution. Both channels
carry the driver's own files unchanged:

- NuGet: `tools/net10.0/any/.playwright/package/NOTICE`, next to `LICENSE` and `ThirdPartyNotices.txt`
- npm platform packages: `bin/.playwright/package/NOTICE`, next to `LICENSE` and `ThirdPartyNotices.txt`

`npm/prepare.mjs` deletes only `.playwright/node`; `.playwright/package` is left as
Playwright ships it. The driver's `NOTICE` reads:

```text
Playwright
Copyright (c) Microsoft Corporation

This software contains code derived from the Puppeteer project (https://github.com/puppeteer/puppeteer),
available under the Apache 2.0 license (https://github.com/puppeteer/puppeteer/blob/master/LICENSE).
```

The driver also bundles npm packages of its own. Their licenses are in sidecar files next
to the bundles, in the same `.playwright/package/` directory: `lib/utilsBundle.js.LICENSE`,
`lib/serverRegistry.js.LICENSE` and `lib/webp_codec.LICENSE` (libwebp and Emscripten).

## Node.js (NuGet package only)

Playwright runs its driver on Node.js and ships Node.js binaries for that purpose. The
NuGet package keeps them for all five platforms (`PlaywrightPlatform=all` in
`src/Parity.Cli/Parity.Cli.csproj`). The complete Node.js license, including the
licenses of everything Node.js bundles, is at `tools/net10.0/any/.playwright/node/LICENSE`
in the package. The npm platform packages remove `.playwright/node` and do not
redistribute Node.js.

## .NET runtime and ASP.NET Core (npm platform packages only)

The npm platform packages are self-contained, so they redistribute the .NET runtime and
the ASP.NET Core shared framework (`parity serve` uses Kestrel). Both are MIT. Their own
third-party notices are published with their source:

- .NET runtime: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- ASP.NET Core: https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt

## SQLite

The native `e_sqlite3` library is SQLite as built and packaged by SourceGear (the
`SQLite` NuGet package). That package's `LICENSE.txt` states that SQLite is public
domain and points to https://sqlite.org/copyright.html. SQLitePCLRaw, the .NET binding
to it, is Apache-2.0.

## Keeping this file current

This file reflects the dependencies of `src/Parity.Cli` and its project references.
**When a dependency is added, removed or bumped, update this file in the same
commit**: versions, licenses, copyright lines and the channel table. Dependabot bumps
will not do it for you. A Microsoft.Playwright bump also changes the bundled Node.js
version and may change the driver's sidecar license files.

## MIT License

Applies to Microsoft.Playwright, Microsoft.EntityFrameworkCore (and its .Abstractions,
.Relational, .Sqlite and .Sqlite.Core packages), Microsoft.Data.Sqlite.Core,
Microsoft.Extensions.DependencyModel and Microsoft.Bcl.AsyncInterfaces; in the npm
platform packages also the .NET runtime and ASP.NET Core; in the NuGet package also
Node.js (whose complete license file is described above).

Copyright holders:

- © Microsoft Corporation. All rights reserved. (as declared by the Microsoft packages above)
- Copyright (c) .NET Foundation and Contributors (the .NET runtime and ASP.NET Core in the npm platform packages, and the `LICENSE.TXT` in Microsoft.Bcl.AsyncInterfaces)
- Copyright Node.js contributors. All rights reserved. (Node.js, NuGet package only)
- Copyright Joyent, Inc. and other Node contributors. All rights reserved. (Node.js, NuGet package only)

The MIT License text, copied from the `LICENSE.TXT` in the Microsoft.Bcl.AsyncInterfaces package:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache License 2.0

Applies to the Playwright driver (`playwright-core`), SixLabors.ImageSharp (as granted
under the Six Labors Split License, see above) and SQLitePCLRaw (`core`, `provider.e_sqlite3`,
`bundle_e_sqlite3`, `config.e_sqlite3`). Their copyright lines are in the components table.

The license text below is copied from the driver's `.playwright/package/LICENSE`, from the
first line to "END OF TERMS AND CONDITIONS" (the appendix that follows it in that file only
explains how to apply the license to a project).

```text
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS
```
