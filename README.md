# UnrealAssetTextDiff

Compare two Unreal Engine `.uasset` revisions by exporting them to readable
text and opening them in TortoiseMerge — without launching the normal editor
UI, and without modifying the source assets.

This is the **source distribution**: build the portable exe locally, then
use it standalone.

## Contents

```
src/UnrealAssetTextDiff/              WPF app (net8.0-windows)
tests/UnrealAssetTextDiff.Tests/      xUnit unit tests
Plugins/UatdTextDiff/                 optional editor plugin (source only)
scripts/                              integration + GUI exercise scripts
docs/TORTOISESVN.md                   TortoiseSVN setup details
Build-Package.ps1                     one-command build -> .\dist\
```

## Build

Requires **.NET SDK 8.0+** (built with 9.x; `net8.0-windows` target). The
app itself needs no NuGet packages; tests restore xUnit from nuget.org.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Package.ps1            # -> .\dist\UnrealAssetTextDiff.exe (~160 MB)
powershell -ExecutionPolicy Bypass -File .\Build-Package.ps1 -RunTests  # build + unit tests first
```

(`-ExecutionPolicy Bypass` is only needed if your machine blocks unsigned
scripts.)

Or run dotnet directly:

```
dotnet publish src\UnrealAssetTextDiff\UnrealAssetTextDiff.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The result is a **single standalone exe** — no sibling DLLs, no .NET
runtime install needed on target machines. If you publish without
`IncludeNativeLibrariesForSelfExtract`, WPF emits native DLLs beside the
exe and it will fail to launch when moved alone.

## Runtime requirements

- **Windows**, **Unreal Engine 4.27+ / 5.x** installed (tested on UE 5.6).
  The app auto-detects `UnrealEditor-Cmd.exe` from the registry; override
  with `--engine` or the GUI.
- **TortoiseSVN** (for `TortoiseMerge.exe`) — or any text-diff viewer via
  `--export-only`.

## Quick start

1. Build (above), copy `UnrealAssetTextDiff.exe` anywhere.
2. Launch, pick two `.uasset` files, confirm the **project** (auto-detected
   by walking up from the asset path, or set once — it is saved) and
   **engine** paths.
3. Export + compare. Engine startup takes ~30–90 s on a cold cache.

## CLI

```
UnrealAssetTextDiff.exe --base <old.uasset> --mine <new.uasset>
    [--project <path.uproject>] [--engine <UnrealEditor-Cmd.exe>]
    [--tortoise <TortoiseMerge.exe>]
    [--base-label <text>] [--mine-label <text>]
    [--export-only] [--filtered] [--no-viewer]
```

Settings persist in `%LOCALAPPDATA%\UnrealAssetTextDiff\settings.json` —
configure once in the GUI, then scripted calls need only `--base`/`--mine`.

| Exit | Meaning |
|------|---------|
| 0 | Success |
| 1 | Invalid usage |
| 2 | Configuration error (missing engine/viewer/project) |
| 3 | Export failure |
| 4 | Viewer launch failure |
| 5 | Cancellation |
| 6 | Incomplete export (partial text produced; flagged) |

Session outputs (raw + comparison exports, engine log, diagnostics) land in
`%LOCALAPPDATA%\UnrealAssetTextDiff\Sessions\<id>` and are kept on failure.

## Optional: UatdTextDiff plugin (Blueprint class defaults)

The built-in `DiffAssets` commandlet does **not** export Blueprint variable
default values. The plugin in `Plugins\UatdTextDiff\` adds them
(`-run=UatdAssetDiff`). If absent, the app falls back to the stock
commandlet and labels the result `LIMITED`.

Install (per project, per engine):

1. Copy `Plugins\UatdTextDiff\` into `<YourProject>\Plugins\UatdTextDiff\`.
2. Build the plugin module against your project:

   ```
   <EngineRoot>\Engine\Build\BatchFiles\Build.bat <Target>Editor Win64 Development ^
       -Project="<YourProject>\<Name>.uproject" ^
       -Plugin="<YourProject>\Plugins\UatdTextDiff\UatdTextDiff.uplugin" -WaitMutex
   ```

   (`<Target>Editor` is your project's editor target name — same stem as
   the `.uproject`.)
3. The app auto-detects
   `<project>\Plugins\UatdTextDiff\Binaries\Win64\UnrealEditor-UatdTextDiff.dll`.
   No manual config needed after building.

The plugin DLL is engine- and project-specific — it is **not** shipped
prebuilt. Rebuild it when you change engine version.

## TortoiseSVN

See `docs\TORTOISESVN.md`. Short version: Settings → Diff Viewer →
Advanced → Add → extension `.uasset` →

```
C:\path\to\UnrealAssetTextDiff.exe --base %base --mine %mine --base-label %bname --mine-label %yname --project "C:\path\to\Your.uproject"
```

## What the comparison shows

- **Default view (conservative):** the full Unreal text export with only
  the session-specific staged package name normalized. All identifiers,
  NSLOCTEXT keys, and property values are preserved verbatim — this view
  can show per-load identifier churn that is not a real change.
- **Filtered view (`--filtered` / GUI checkbox):** additionally masks
  compiler-regenerated graph internals (`ExecuteUbergraph_*`, merged
  graphs, intermediate-node contexts) and resolves pin `PinId`/`LinkedTo`
  GUIDs into readable `(node, pin)` tokens. Use it for a readable diff; use
  the default view when you need to trust every byte.
- **Class defaults** (plugin installed): appended under a `CLASS DEFAULTS`
  header — `[var]`/`[override]`/`[inherited]` properties from each
  revision's generated-class CDO.

Raw, unmodified exports are always kept alongside the normalized ones
(`*.raw.t3d`).

## Status flags

- `INCOMPLETE` — export text was produced but something failed (load/
  import/parent-class diagnostics, nonzero/crashed engine exit, missing
  defaults). The comparison opens anyway; inspect the banner and session
  diagnostics before trusting it.
- `LIMITED` — plugin missing: no class defaults, otherwise valid.
- Inputs are never modified (hashes are checked and reported).

## Known limitations

- Variable defaults require the plugin (above). Without it, default-value
  changes are invisible.
- The engine export has known coverage gaps — some binary differences
  produce no text difference. `Binary inputs identical` / `raw exports
  identical` / `comparison identical` are reported separately.
- `.umap` and cooked assets are not supported.
- Split-package assets (`.uasset` + `.uexp`): only the `.uasset` travels
  through SVN diff temp files; exports may be incomplete and are flagged.
- Non-Blueprint assets get a `NoBlueprintGeneratedClass` marker for the
  defaults section (N/A, not a failure).

## Verification

After building + installing the plugin on a project, run
`scripts\Invoke-IntegrationCheck.ps1` with your project, engine, working
copy root, and a `.uasset` that changed between two revisions — it performs
a real end-to-end run and reports per-check results.
`scripts\Exercise-Gui.ps1` drives the GUI through UIAutomation.
