# Changelog

## 1.2.0

### New

- **Mods page: health check.** For the selected game Prism now checks that the installed mods
  can actually load. It lists the frameworks the mods present depend on (Cyberpunk: RED4ext,
  redscript, Cyber Engine Tweaks, ArchiveXL, TweakXL and the REDmod DLC; any game: SKSE, F4SE,
  SFSE, xNVSE, OBSE, UE4SS, BepInEx, MelonLoader) and flags one as missing only when a mod
  needs it. It also reports files Prism placed that are gone, missing Nexus requirements,
  conflicts, and the errors RED4ext, redscript and CET logged at the last launch. Each problem
  comes with its fix: download from Nexus (in the background for Premium), reinstall the mod,
  open the REDmod install in Steam, or open the log. *Fix all* runs every fix at once. The check
  runs again after each install and when the game closes.
- Premium downloads that fail now log why.

- **NVIDIA driver page.** Per-game driver profile settings without the NVIDIA app or Profile
  Inspector, sorted in four groups: DLSS (render scale 33–100 %, Ray Reconstruction and Frame
  Generation presets), latency (Low Latency Off/On/Ultra, VSync with Fast, frame limiter with a
  G-SYNC value just under the refresh rate), driver frame generation (Smooth Motion, RTX 40 and up,
  driver 571.86+) and power and display (power management, G-SYNC per game). IDs and values come
  from NVIDIA's own NvApiDriverSettings.h; the hidden ones (Ultra Low Latency, Smooth Motion) are
  cross-checked with Profile Inspector's reference. Only values set for the game count: settings
  inherited from the global profile or predefined by NVIDIA show as default. *Reset all* hands the
  game back to the driver. Low Latency and Smooth Motion need administrator rights; the page says
  so and can restart Prism elevated.

- **ReShade page and shader library.** A new ReShade page shows the game's ReShade and 49 shader
  packs sorted by use: HDR (Lilium, RenoFX, PumboAutoHDR, QD-OLED APL Fixer…), basics, color and
  sharpness, lighting and AO (iMMERSE, qUINT…), photo and cinema, retro and special. Search, one
  click to install or remove a pack, and *HDR essentials* for Lilium. Packs go into
  `reshade-shaders` like the official installer does, never overwrite each other, keep shared
  headers while a pack still needs them, and `ReShade.ini` gets the search paths it needs. The
  catalog updates from GitHub.
- **Work indicator.** Installs, injections and downloads show a moving bar at the top of the
  window and the current step in the status bar. Archive extraction and file copies run off the
  interface thread, so the window no longer freezes during an install.

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
- **Nexus Mods inside Prism.** A new page opens the Nexus site on the selected game's mods. Hit
  *Manual download* or *Mod Manager Download* and the archive comes to Prism instead of your
  Downloads folder. Prism reads it and puts it where it belongs: the game folder when the archive
  mirrors it, `~mods` for Unreal paks, next to the executable for ReShade add-ons, presets and
  proxy DLLs. ReShade is installed first when a mod needs it. Replaced files are backed up, and
  the mod shows under *Added by Prism* with a one-click *Remove*. When the right folder can't be
  told (FOMOD installers, unusual layouts), Prism lists the files and asks. Your Nexus login is
  kept between sessions. `.zip` and `.7z` work out of the box; `.rar` needs 7-Zip.
- **Mod requirements.** Open a mod page and Prism lists what it needs, and what those need in
  turn (a mod built on ArchiveXL also needs RED4ext). Each one shows *installed* or *missing* for
  your game, even if you installed it by hand: Prism checks the requirement's own files on disk.
  *Open* takes you to the missing one; download it and it's placed like any other.
  When some are missing, the strip reads *Required files needed* and *Download all* queues them:
  Prism opens each one on its main file's download page, installs it when it arrives and moves on
  by itself, then returns to the mod. Nexus requires one click per file, so you still press its
  download button once per requirement.
- **Nexus account and Premium.** Paste your personal API key once (it is encrypted by Windows and
  only ever sent to api.nexusmods.com). With a Premium account, *Download all* fetches and installs
  every requirement in the background through the Nexus API, without opening a single page. A free
  account keeps one click per file, as Nexus requires.
- **Mod updates.** A *Nexus mods* strip lists what Prism installed in the game with its version
  against the latest one of the same file on Nexus (optional files follow their own line).
  *Update all* replaces each outdated file in place: in the background with Premium, one page per
  file otherwise.
- **Smarter placement.** Every file of an archive gets its own destination. Cyberpunk 2077 follows
  the layout of its official Vortex extension (`archive\pc\mod`, `r6\scripts`, `r6\tweaks`, CET
  and REDmod folders). Bethesda games send plugins and assets to `Data`, script extenders to the
  game folder. Unreal paks go to `~mods`, UE4SS mods to `ue4ss\Mods`, BepInEx and MelonLoader
  plugins to their folders, and games with a mods folder (RimWorld, Stardew Valley, Witcher 3,
  Kenshi, X4...) get one folder per mod, created when missing. Wrapper folders are ignored at any
  depth. The rules live in `ModRules/rules.json` and update from GitHub without a new release.

### Fixed

- The DLSS preset could show a value inherited from the global driver profile as if it were forced
  for the game.
- Shader packs fall back on the downloaded archive when GitHub rate-limits anonymous requests.

- Secondary buttons on the Nexus page (*Get my key*, *Clean up*) and the API key field were white
  on white.

- *Remove* on a Nexus mod left its folders behind, empty or holding the settings and logs the mod
  wrote in game. The mod's folder now goes entirely (anything not placed by Prism is set aside and
  restorable), and the Nexus page offers to clean up the leftovers of mods removed earlier without
  touching other mods, ReShade or OptiScaler.
- A RenoDX DLSS 5 add-on identical to the latest release was reported outdated when the release was
  published days after it was built, so *Repair* kept reinstalling it. The file is now compared with
  the release itself.

- *Repair* could loop forever on "Streamline files from different versions" when the odd plugin was
  left by another tool (a 2.13 `sl.dlss_nr.dll` next to Streamline 2.14.1): reinstalling never
  touched it. The plugin is now compared with `sl.interposer.dll`, a foreign one is set aside
  (restorable), and *Repair* checks afterwards and says so when a problem is still there.
- OptiScaler could be moved to `d3d12.dll`, where it hooks into Direct3D's own start-up (seen in
  Cyberpunk 2077: the DXGI factory failed with ReShade on `dxgi.dll`). `wininet.dll` and
  `winhttp.dll` are now used when the game really loads them, `d3d12.dll` only as a last resort,
  and an OptiScaler already on `d3d12.dll` is flagged with a one-click rename.
- Deleting another tool's file inside an install kept no lasting backup.
- A Nexus file uploaded as `name.zip` kept `.zip` in its install name; the temporary OptiScaler
  copy made while moving it stayed in the cache.

- Nexus now names archives `ArchiveXL 4198 1.27.3 2026-09-07T10-15Z ....zip`; Prism reads the mod
  number from that form too, and from the page itself first.
- A mod removed with *Remove* still counted as an installed requirement, so nothing was reported
  missing afterwards.
- A requirement was reported installed as soon as a file with the same name existed: an OptiScaler
  on `winmm.dll` passed for RED4ext. Generic DLL names now only count when the size matches.
- Installing an optional file of a mod deleted the mod's main file. Each Nexus file is now its own
  install; a new version of the same file still replaces the old one.
- OptiScaler could take `winmm.dll` or `version.dll` in Cyberpunk 2077, the names RED4ext and
  Cyber Engine Tweaks load through. It no longer picks them, Prism flags an OptiScaler already
  sitting there, and installing the loader moves OptiScaler to a free name in the same step.

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
