# TortoiseSVN integration

Verified against TortoiseSVN 1.14.x.

## What TortoiseSVN does for a `.uasset` diff

When you diff a `.uasset` (working copy vs BASE, or two revisions in the log
dialog), TortoiseSVN creates a temp copy of the older side — usually
`%TEMP%\svnXXXXXXXX.tmp` **without** a `.uasset` extension — and runs the
command registered for the `.uasset` extension. UnrealAssetTextDiff stages
that temp file back under the real asset name (from `%bname`/`%yname`), so
Unreal can load it, and labels the two sides correctly.

**Note:** the temp file is only the `.uasset`. If your project saves
packages split into `.uasset` + `.uexp`, the companion is not available to
the diff and the export may fail — this tool reports that rather than
showing a misleading comparison.

## Setup

Settings → **Diff Viewer** → **Advanced...** → **Add**:

- Extension: `.uasset`
- External program (single line, adjust the exe and project paths):

```
C:\Tools\UnrealAssetTextDiff\UnrealAssetTextDiff.exe --base %base --mine %mine --base-label %bname --mine-label %yname --project "C:\Projects\YourGame\YourGame.uproject"
```

Or rely on the saved project profile (set the project once in the app GUI or
via `--project`) and omit `--project` entirely:

```
C:\Tools\UnrealAssetTextDiff\UnrealAssetTextDiff.exe --base %base --mine %mine --base-label %bname --mine-label %yname
```

If the app path contains spaces, quote the exe path only:
`"D:\My Tools\UnrealAssetTextDiff.exe" --base %base ...`

This configuration is stored per-user in
`HKCU\Software\TortoiseSVN\DiffTools` under the `.uasset` value. The app
**does not** write this itself — add it manually (or delete the value to
restore the default TortoiseMerge behaviour for `.uasset`).

## Placeholders (verified)

- `%base` — temp copy of the older revision (or the older file for
  two-file diffs inside a working copy).
- `%mine` — your file / newer revision.
- `%bname`, `%yname` — display names, already quoted by TortoiseSVN;
  passed to TortoiseMerge as `/basename`/`/minename` and used to recover
  the real asset name for staging.

Use the quoted forms `%bname`/`%yname` as shown — the unquoted variants are
`%nqbname`/`%nqyname` and are not needed here.

### Known quirk

`TortoiseProc /command:diff` on two **unversioned** files whose names
contain spaces may fall back to TortoiseMerge instead of the registered
extension tool (file names without spaces work). Real `.uasset` revision
diffs go through `svnXXX.tmp` temp names which contain no spaces, so the
workflow path is unaffected.

## Usage

- Right-click a modified `.uasset` → TortoiseSVN → **Diff**.
- Log dialog → select a revision → **Compare with working copy** / select
  two → **Compare revisions**.

The export takes a while (engine startup ~30–90 s on a cold cache). Exit
code semantics are in the README; failed exports leave a session folder
with the engine log under `%LOCALAPPDATA%\UnrealAssetTextDiff\Sessions`.

When the run is incomplete or limited, the TortoiseMerge title/label shows
` [INCOMPLETE EXPORT]` or ` [LIMITED EXPORT - no class defaults]` — since
this may be the only UI you see, the flag is never only in the log. Class
defaults require the `UatdTextDiff` plugin (see README); append
`--filtered` to the command for the noise-normalized comparison view.
