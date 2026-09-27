# Changelog

## 1.2.0 (in development)

### New

- **Live doctor.** While a game runs, its `ReShade.log` is read again every time it changes
  (LIVE badge). The diagnostic now also checks the disk: neural runtime missing, misplaced or
  untrusted, incomplete Streamline, outdated shader compiler, add-on not preloaded, and Streamline
  files from different versions.
- **Repair.** Applies every fix of the diagnostic in one go. All mod files are captured first: if
  one step fails, the game goes back exactly as it was.
- **Migrate to Prism.** An install made by another tool is set aside (restorable) and reinstalled
  by Prism in its verified version, so it can be checked and removed in one click.
- **Installed buttons.** Once DLSS 5, HDR or frame generation is in place and valid, its button
  reads *Installed*. Remove a file or break a signature and it goes back to *Install*, without a
  rescan. *Reinstall* stays available.
- **DLSS preset.** Pick K, M, L, J or Latest per game, or let the game decide. It goes into the
  game's NVIDIA driver profile, the same place the NVIDIA app writes to, so no game file is
  touched. *Reset injections* clears it too.
- **Older games.** The API is read from the executable's imports, so DirectX 9, DirectX 10 and
  OpenGL games are named as such, with a note on what still works (ReShade) and what doesn't
  (DLSS, frame generation). The Unreal version is read from the binary (*Unreal 4.26*). Unreal 3
  games are recognised, even 32-bit ones, and no longer get a generic HDR mod that can't work on
  them. On Unreal 4, the HDR steps drop the UE5-only advice.

### Fixed

- Mods set aside by Prism showed up under *Added by Prism*, where *Remove* would have put them
  back into the game.
- A version recorded as `5` by older builds was shown next to *RenoDX DLSS 5*.
- The search boxes of the Games and Logs pages were always in French.

## 1.1.1

### Fixed

- **RenoDX UE Extended is back.** The RenoDX wiki renamed its section to *Unreal Engine Extended*
  and moved the download to marat569's repository; Prism no longer found the add-on, so generic
  HDR for Unreal games could not be installed. It now uses the current build from the link the
  wiki gives.
- **UE Extended's own presets are respected.** The mod recognises dozens of games by their
  executable and sets their upgrade path and resource upgrades itself; a value written in
  `ReShade.ini` would override them. Prism no longer writes those keys for such games, and uses
  UE Extended for them even when the wiki only lists them under the legacy Unreal mod.
- A wiki note such as *"Native HDR is broken"* switched the game to its native HDR — the opposite
  of what it says. Negative notes are now shown as notes only.
- Unreal games that no list mentions get UE Extended with the wiki's recommended order: native HDR,
  then `Engine.ini` HDR on UE5, then *Upgrade Path: On* with resource upgrades.

## 1.1.0

### New

- **Reset injections.** One button brings a game back to how its platform installed it: every
  Prism install and its `ReShade.ini` / `Engine.ini` settings, then everything other tools left.
  It shows the exact count first and acts only on the second click.
- **Deep clean.** Removes what other installers, OptiScaler and manual installs left, even before
  Prism, by following each tool's own rules: `.original` markers, install manifests, OptiScaler
  under any DLL name. Full list shown first, every file copied aside, whole pass undoable.
- **Copy file list.** Everything Prism added, and everything else that isn't from the game, as text.
- **Diagnostic.** Reads the game's `ReShade.log` after each launch and tells whether the neural
  pass is **Working**, **Degraded** or **Failed** — with the log line as evidence and a fix button.
  Catches the case where the overlay says *active* but the image doesn't change.
- **Conflicts.** Two RenoDX DLSS add-ons, two neural-rendering paths, ReShade or OptiScaler loaded
  twice, leftovers from another installer.
- **Frame generation in games that don't have it** (DirectX 12, with DLSS, FSR 2+ or XeSS on):
  - **OptiScaler · DLSS FG** — NVIDIA's DLSS Frame Generation driven from the game's upscaler.
    ×2 on RTX 40, up to ×4 on RTX 50. NVIDIA's Streamline 2.14.1 files are checked against pinned
    SHA-256. Experimental: single-player games without anti-cheat.
  - **OptiScaler · FSR FG** — ×2 on any GPU.
- **RenoDX DLSS · ShortFuse** is now the recommended DLSS 5 path (DirectX 12 and 11), next to
  DLSS5 Tool.
- **Add game (.exe)** for anything the scan misses, and **Change** to pick a game's executable.
- **Game identity.** Games from Epic, GOG, Xbox, Ubisoft or added by hand get their official name
  and Steam AppID — from the publisher's files, then the Steam store search when unambiguous —
  so HDR mods are matched exactly as for Steam games. **Identify** fixes it by hand.

### Updated

- DLSS SR / FG / RR **310.9.1** and Streamline **2.14.1** in the DLSS 5 stack (was 310.8 / 2.13).
- rhi-repo release candidates (`-rc`) are never picked as the latest version.

### Fixed

- A game folder added in Settings was split into its sub-folders (`Engine`, `Binaries`…) instead
  of being recognised as one game. Unreal games are now detected from their `Engine` folder, and
  their `-Shipping.exe` preferred.
- OptiScaler frame generation wrote settings that don't exist in OptiScaler 0.9.4 and never chose
  a frame-generation input or output, so it could stay off. It also claimed ×3/×4 where only ×2 is
  delivered.
- OptiScaler forks: the frame count was written one too high (×2 asked, ×3 set), and
  `OverrideInterpolationCount` was written as `true` where a number is expected.
- Installing a second OptiScaler path, or reinstalling one, loaded a second copy under another
  DLL name. There is now one OptiScaler per game.
- Removing frame generation left `fakenvapi.dll` behind. Removal now follows Prism's registry.
- The DLSS 5 installer could pick the fork's `rtx40-mfg` variant by accident.
- An add-on version recorded as `5` by older builds is shown as its build date instead.

Earlier versions: [Releases](https://github.com/Skynizz/Prism/releases).
