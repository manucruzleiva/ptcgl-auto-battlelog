# Third-party notices

PTCGL Auto Battle Log is licensed under the MIT License (see [LICENSE](LICENSE)). It redistributes
the third-party software below, which keeps its own license.

## What is redistributed, and how

`PtcglAutoBattleLog.exe` embeds, as a resource, the official BepInEx release archive
**`BepInEx_win_x64_5.4.23.3.zip`**, byte for byte as published by the BepInEx project
(SHA-256 `41a089e5b1b1f0713b331346baf6677b1184c69eabebf51101097954e854c749`, verified by the build
scripts), downloaded from
<https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip>.

The app extracts the files of that archive, unmodified, into the Pokémon TCG Live game folder,
where they stay separate files: you can replace or remove them (see "Uninstall" in the
[README](README.md)). The plugin `PtcglAutoBattleLog.Plugin.dll` is this project's own code; it
uses BepInEx and HarmonyX through their public APIs and contains no code from them.

These notices and the license texts travel with the binaries: the exe embeds this file, this
project's [LICENSE](LICENSE) and the full [LGPL-2.1](licenses/LGPL-2.1.txt) text and shows them
in the tray menu's **Licenses** window, and the portable zip contains them as `LICENSE.txt`,
`THIRD-PARTY-NOTICES.txt` and `licenses/LGPL-2.1.txt`.

| Files in the archive | Component | Version | License |
|---|---|---|---|
| `BepInEx/core/BepInEx.dll`, `BepInEx/core/BepInEx.Preloader.dll` (+ `.xml` docs), `changelog.txt` | [BepInEx](https://github.com/BepInEx/BepInEx) | 5.4.23.3 | MIT |
| `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) (NeighTools) | 4.4.0 | LGPL-2.1 |
| `BepInEx/core/0Harmony.dll` (+ `.xml`) | [HarmonyX](https://github.com/BepInEx/HarmonyX) | 2.9.0 | MIT |
| `BepInEx/core/BepInEx.Harmony.dll` (+ `.xml`), `BepInEx/core/HarmonyXInterop.dll`, `BepInEx/core/0Harmony20.dll` | [BepInEx.Harmony](https://github.com/BepInEx/BepInEx.Harmony) (Harmony wrapper; HarmonyXInterop and HarmonyX2Interop projects) | 2.0.0 (HarmonyXInterop 1.0.0) | MIT |
| `BepInEx/core/MonoMod.RuntimeDetour.dll`, `BepInEx/core/MonoMod.Utils.dll` (+ `.xml`) | [MonoMod](https://github.com/MonoMod/MonoMod) | 22.01.29.01 | MIT |
| `BepInEx/core/Mono.Cecil.dll`, `Mono.Cecil.Mdb.dll`, `Mono.Cecil.Pdb.dll`, `Mono.Cecil.Rocks.dll` | [Mono.Cecil](https://github.com/jbevain/cecil) | 0.10.4 | MIT |

### LGPL-2.1 component (Unity Doorstop)

Unity Doorstop is free software, licensed under the GNU Lesser General Public License, version
2.1. The full license text is in [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt) (also at
<https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt>). It is distributed **unmodified** and
**without any warranty**.

Its complete corresponding source code is available from the project itself:
<https://github.com/NeighTools/UnityDoorstop/tree/v4.4.0>. If that link ever stops working, open
an issue in this repository and the maintainers will provide the source code of the version
distributed here.

The source code of BepInEx 5.4.23.3 (MIT, below) is at
<https://github.com/BepInEx/BepInEx/tree/v5.4.23.3> (commit
`cd593c47566adfe971f1d96225fa82d1864ae877`, the commit named in the binaries' version info).
Only BepInEx 6 and later are LGPL-2.1; the 5.x line distributed here is MIT.

### MIT components

BepInEx, HarmonyX, BepInEx.Harmony, MonoMod and Mono.Cecil are licensed under the MIT License:

- BepInEx: Copyright (c) 2018 Bepis
- HarmonyX: Copyright (c) 2020 BepInEx
- BepInEx.Harmony: Copyright (c) 2019 (its license file names no copyright holder)
- MonoMod: Copyright (c) 2015 - 2020 0x0ade
- Mono.Cecil: Copyright (c) 2008 - 2015 Jb Evain; Copyright (c) 2008 - 2011 Novell, Inc.

```
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

## Credits (not redistributed)

- **[PtcglLeaderboard](https://github.com/Jxkobyte/PtcglLeaderboard)** by Jxkobyte, MIT License,
  Copyright (c) 2026 Inevitable2002. Its open-source code identified the client's battle-log
  classes (`MatchManager.BattleLogExportData`, `BattleLogExporter.ExportBattleLog` and the match
  state classes) that this plugin reaches by reflection. No code from it is included.
- Build-time only, not part of any download: the .NET SDK and
  `Microsoft.NETFramework.ReferenceAssemblies` (MIT, Microsoft), and the `UnityEngine.Modules`
  reference assemblies (compile-time references only; no Unity code is shipped).

## Trademarks

Pokémon and Pokémon TCG Live are trademarks of their respective owners. This project is not
affiliated with, endorsed, sponsored or approved by Nintendo, The Pokémon Company, The Pokémon
Company International, Creatures Inc. or GAME FREAK inc.
