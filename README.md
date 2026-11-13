# Bjornhjarta - Enshrouded ToolBox

**Application version:** 1.0.0-rc.2 – Release Candidate, **not stable** (UI Smoke Hotfix 1)  
**Platform:** Windows 10/11 x64  
**Framework:** .NET 10 / WPF  
**Windows evidence (13 November 2026):** The project owner confirmed RC1 UI Hotfix 1 Release build, all Core tests, win-x64 publish and SHA-256 verification. RC2 has new code and requires its own build/acceptance.  
**Scope:** Blueprint Forge 2D/3D, experimental Image-to-Blueprint, Placeables / local GLB model mapping, EML Client and Dedicated Server profile management, and safe local EML ZIP operations.

## v1.0.0 RC2 — extracted-EXE UI startup checks

- New `scripts/smoke-test-portable.ps1`: verifies ZIP, extracts it to TEMP, launches **the actual published EXE** in `--ui-smoke` mode, and requires an explicit JSON report with five passing WPF window/layout checks.
- WPF `--ui-smoke` does not load EML user preferences or touch game directories. EML **scan only** no longer silently re-saves preference files.
- More thorough ZIP contents verification; the previous UI alignment fix and portable publish hotfix are retained.
- **This remains a release candidate.** Mouse/keyboard interactions and mod operations still require manual GUI testing. See [RC2 Windows checklist](docs/V1.0.0-RC2-TEST-CHECKLIST.md) and [RC2 notes](docs/V1.0.0-RC2-RELEASE-NOTES.md).

## v1.0.0 RC1 — release-candidate hardening

- Built from the *complete v0.19.0 packaging-hotfix1 project*, preserving the successful quote-free TEMP publish workaround.
- Version stamped consistently as `1.0.0-rc.1` in desktop, blueprint metadata, scripts and archive.
- Publish script verifies the generated portable ZIP has the expected EXE/README and its SHA-256 matches the checksum file. `scripts/verify-package.ps1` can also be run independently.
- EML preferences now reject valid-JSON `null` and unsupported profile values without silently resetting settings or writing invalid values. New Core smoke checks cover these cases.
- Dashboard catches Blueprint Forge startup failures, writes a diagnostic log, and remains available (like the existing EML-manager error handling).
- Manual Windows acceptance and extracted-EXE testing remain mandatory; `stableRelease=false` in the generated package manifest.

**Quick start:** Run `scripts/validate-release.ps1`, then `scripts/package-release.ps1`. Extract the portable ZIP to a *new* folder and run `Bjornhjarta.Desktop.exe`. Do not run it from inside the ZIP. See [RC1 Windows acceptance checklist](docs/V1.0.0-RC1-TEST-CHECKLIST.md), [release gates](docs/RELEASE-GATES-1.0.md), and [changelog](docs/CHANGELOG.md).

## v0.19.0 Preview — previous verified milestone

The previous v0.19.0 version passed the project's Windows release build and all Core smoke tests, and the packaging hotfix worked in the owner's Windows environment. RC1 introduces changes, so those results cannot automatically certify RC1.

## New in v0.18.0 Preview — EML Mod Manager package safety

- **EML mod ZIP preview:** choose a local ZIP and review its mod ID, name, version, file count, decompressed size, destination, and whether it will replace an existing mod. Archive entries and manifest requirements are checked before extraction.
- **Safe local ZIP installation:** only one valid EML mod is accepted per archive; path traversal, symbolic links, unexpected package folder layouts, invalid manifests, duplicate file names and oversized entries are rejected. The mod is extracted into a unique staging folder before being moved into `mods/<mod-id>`.
- **Update backup:** if an active mod with the same ID exists, its entire original folder is moved into `.bjornhjarta/backups/eml-mods/` before the replacement. If the final move fails, the manager attempts to restore the previous folder.
- **Enable/disable mods:** move mod folders between `mods/<mod-id>` and `.bjornhjarta/disabled-mods/<mod-id>` without deleting their files. The inventory includes **Enabled** and **Disabled** states. Changes take effect on the next launch of the target installation.
- **Separate profiles and process checks:** actions operate only on the selected Client or Dedicated Server installation. The target game/server must be stopped and no EMM validation may be running. A confirmation prompt is required before writes.
- **Regression tests:** synthetic ZIP import, disabled inventory, enable/disable, replacement backups, invalid ZIP traversal and invalid mod IDs have been added to the framework-free Core smoke-test project.
- **Not yet implemented:** automatic downloads/updates, third-party mod compatibility verification, dependency resolution, full mod profiles, undo for arbitrary file corruption, or running mod scripts within Bjornhjarta.

**Important:** EML and individual mods are third-party software. A successful ZIP import means the file structure was acceptable, not that a mod is safe, compatible or guaranteed to work in-game. Do not modify a live client or server. The imported mod's code may execute later when Enshrouded starts.

**Build status:** Static source checks and packaging only. Full .NET 10/WPF build, smoke tests and actual client/server interactions still require Windows testing. See [v0.18 Windows checklist](docs/V0.18-TEST-CHECKLIST.md).

## New in v0.17.0 Preview — GLB geometry & material diagnostics

- **Model Asset Library:** shows a WPF preview-compatibility report distinguishing base-color texture preview from normal, metallic/roughness, occlusion, emissive, and material-extension features not implemented by the WPF renderer. Required glTF extensions are flagged as unsupported for the current viewport. These warnings do not claim the original game material is invalid.
- **Inspect GLB dimensions:** opt-in model loading in the library measures the **actual scene-node-transformed GLB geometry bounds** (width X, height Y, depth Z, and origin-relative minimum) using the same loader as the 3D editor. These are **GLB-local coordinate units**, not verified Enshrouded meters. Failed/unsupported geometry is reported, not fabricated.
- **3D Editor:** selection outlines now follow the loaded mesh's rotated/transformed bounds instead of drawing a misleading universal 4 × 4 × 4 meter frame. Explicit 4-meter placeholders remain for unmapped pieces. Fit Scene uses actual loaded geometry extents and defaults to blueprint positions where necessary.
- **Material honesty:** the existing WPF renderer still only displays basic base-color textures/factors. Full PBR, shaders, game-specific effects, and automatic validation of game-native dimensions are **not** implemented.
- **Core smoke tests:** new test cases cover material channels and unsupported required extensions. The previously implemented image-wall, Type ID resolver, JSON backup, and read-only EML regression cases remain.

**Build status:** Static XML/event/resource checks will be performed on this source package; .NET 10/WPF build and the included smoke tests require Windows execution. See [v0.17 Windows checklist](docs/V0.17-TEST-CHECKLIST.md).

## New in v0.16.0 Preview — Image-to-Blueprint wall correction and diagnostic overlay

- The 2D plan preview now offers **Toggle interior wall** in its tool selector. Click an interior seam between two occupied cells to **remove an incorrect detected partition, restore it, or add a missing partition**. Exterior edges are protected. Changes stay in preview until **Insert preview into blueprint**.
- Manually added partitions generate real wall elements at each configured wall layer. Suppressed partitions generate **no wall elements**; any doorway/window on a removed partition is cleared in the preview. The whole import is still one undo step.
- Detected interior walls now carry **image-evidence samples** (six or seven hits across seven sampled points). Walls with only six hits appear in **amber** for review, manually added walls appear **teal**, and suppressed walls appear as **dashed red** lines. These scores are heuristic evidence, **not** accuracy guarantees.
- The generator explicitly rejects invalid, duplicate, contradictory, or stale wall corrections. It also rejects openings on walls that have been suppressed.
- Added Core regression tests for toggled walls, manual partitions, valid openings, invalid edits and preview/generation consistency.
- **Not yet available:** reliable automatic recognition of doors/windows from arbitrary 2D drawings, precise roof geometry, or faithful reconstruction from photos. These require verified real-world sample datasets and further validation.

**Build status:** Source and XAML have been statically inspected; .NET 10/WPF build and the included Core tests still require execution on Windows. See [v0.16 Windows checklist](docs/V0.16-TEST-CHECKLIST.md).

## New in v0.15.0 Preview — Automatic Model Mapping and safety improvements

- **Automatic Model Resolver:** unique, valid local GLB/glTF mappings are resolved by the element's Type ID, case-insensitively, preserving existing explicit model selections. The 3D editor can resolve unmapped pieces without modifying blueprint data.
- **Auto-map Models:** Blueprint Forge toolbar command persists missing model references for all placed pieces in one undo step. The status bar reports linked, missing, ambiguous and existing entries. No fake or unverified Enshrouded Type IDs are invented.
- **Image-to-Blueprint integration:** newly inserted generated elements and manually chosen Placeables automatically reuse existing unique model mappings. Existing blueprints also offer local linking on open, with an explicit note to save them.
- **Model Asset Library:** model metadata can now be saved with an empty Type ID. A model only becomes auto-resolvable after the user supplies a Type ID. GLB import remains strictly local and non-destructive.
- **Safer JSON persistence:** Blueprint saves use a temporary file then replace, retaining the previous version as `.bak`. 100 MB file size guard and schema checks were added.
- **Release regression gate:** additional Core smoke tests for model mapping, duplicate Type IDs, JSON round-trips/backups, and read-only EML scanning, plus a Windows PowerShell validation script and GitHub Actions workflow.

**IMPORTANT:** This is **not** a verified stable 1.0.0 release. The .NET SDK is unavailable in this packaging environment. Windows compilation, automated tests, manual GUI testing and real game-model verification must pass first. See [v0.15 checklist](docs/V0.15-TEST-CHECKLIST.md) and [v1.0.0 release gates](docs/RELEASE-GATES-1.0.md).

## New in v0.14.0 Preview — Interior wall openings and source consistency

- **Image-to-Blueprint** now supports painting **doors and windows onto detected interior walls** in addition to exterior walls. The preview and generated blueprint share the same traced interior-wall edges.
- The mouse uses line-segment hit testing rather than selecting arbitrary nearby grid edges. **Only visible generated wall lines** can receive openings. The app refuses to place a door/window on an untraced cell seam.
- Painted openings replace a single selected Wall layer (Door: lowest; Window: approximately halfway up). Remaining wall layers and foundations/roofs are preserved.
- Two marks on opposite sides of the same interior wall normalize to one edge, and duplicate markings are rejected in the generator.
- Updated .NET Core smoke-test source covers interior-door and interior-window substitution, opposite-side normalization, duplicates and invalid cell seams; previous exterior tests remain.
- Inherits the **v0.13.0 Hotfix 3 corrected generator**, including `OpeningKind`/`OpeningSelection` and `bool lightInk = false;`. The previous v0.13.0 ZIP and CS0165/CS0426 hotfix archives must **not** be mixed with the new version.
- The latest testable code still uses `example.*` placeholders, and Image-to-Blueprint remains a deterministic tracing aid, **not** automatic photo-to-3D reconstruction or a verified Enshrouded game-block generator.

**Verification:** XML and source-contract checks are performed in the packaging environment. Neither `dotnet build` nor interactive WPF behavior can be verified without the Windows/.NET runtime. See [v0.14 Windows checklist](docs/V0.14-TEST-CHECKLIST.md).

## Previous: v0.13.0 — Interactive 2D plan annotation

The **Image to Blueprint** dialog now offers a more useful *editable plan preview* rather than only a footprint mask:

1. Import a local **2D outline / floorplan** and click **Trace image and generate preview**. Existing auto-size, irregular footprints and approximate interior wall tracing remain.
2. Light-colored lines show exterior and approximate interior walls that will be generated. Preview remains a simplified 4-metre-cell interpretation, not a pixel-perfect or 1:1 recreation of a drawing.
3. In **Allowed building pieces**, tick a **Door** and/or **Window** entry from the `Doors & Windows` catalog category (together with any Foundation/Wall/Roof entries). Bundled `example.door` and `example.window` are demonstration IDs, not real Enshrouded pieces. Custom Type IDs are user-supplied and unverified.
4. Select **Paint door**, **Paint window**, or **Erase opening** above the preview. Click near the *outside edge* of an occupied footprint cell. Selected door/window replaces **one** wall piece at that edge; the remaining wall layers stay in place. Door replaces the lowest layer; window replaces an approximate mid-layer. Red marks doors, blue marks windows.
5. Painted openings require a Wall piece and clicks close to a real exterior edge. Clicking a cell centre does not select an unrelated wall. The preview shows live generated piece counts. Nothing is inserted until **Insert preview into blueprint**. The entire insertion remains one undo/redo action, and the 2D and 3D editor use the same generated blueprint.
6. Editing the source image, palette, scale or dimensions clears painted openings. Generate a new preview and reapply openings to the new grid as appropriate.
7. A 3D reference photograph still supports only **approximate façade silhouettes**. Door/window annotations apply only to the 2D plan mode, and the tool does **not** automatically recognize doors, windows, roof geometry, hidden rooms or real dimensions from arbitrary photographs.

A dependency-free image-generator smoke-test executable is provided under `tests/Bjornhjarta.Core.SmokeTests`. On a machine with the .NET 10 SDK run `dotnet run --project tests/Bjornhjarta.Core.SmokeTests/Bjornhjarta.Core.SmokeTests.csproj`. It checks sample generation, one-door/one-window replacement, invalid annotations and fail-closed behavior. This does not substitute for testing the WPF mouse interactions.

See [v0.13.0 Windows test checklist](docs/V0.13-TEST-CHECKLIST.md). The GitHub Wiki welcome-page draft is bundled as [docs/GITHUB-WIKI-HOME.md](docs/GITHUB-WIKI-HOME.md). The original app logo and multi-resolution Windows icon remain unchanged. For GitHub, an additional optimized PNG (under 1 MB) is included in `docs/BjornhjartaToolBox-GitHub-under-1MB.png`.

## New in v0.12.0 — Branding and Image-to-Blueprint refinements

### Brand identity and visibility

- The exact user-supplied Norse warrior emblem (`BjornhjartaToolBox - Logo.png`) is the app logo.
- A generated multi-size Windows icon (`.ico`, sizes 16–256px) is embedded in the compiled application and referenced by its WPF windows. The original PNG is preserved as a bundled resource.
- Shared theme uses graphite/iron-black surfaces, rune-green highlights, muted crimson, and ivory text. Dropdown menus now have an explicit dark popup/selection template to avoid the previous unreadable default white dropdown.
- All modules share the new application icon; the existing four small Norse module symbols remain distinct for navigation.

### Image to Blueprint — image-derived dimensions and shape

1. Load a local PNG/JPEG/BMP into **Image to Blueprint**, accessible from either editor.
2. By default, **Auto-size building grid from image** derives the blueprint X/Z cell count from the image's **detected content bounds**, not the viewport's old 8×8 setting. The canvas fits the resulting blueprint after insertion.
3. If known, enter the real building width in metres (4m per grid cell) for a meaningful scale. If unknown, the user-adjustable image-pixels-per-cell value provides **an estimate only**. Image pixels cannot reveal real dimensions by themselves.
4. **2D plan**: traces the actual enclosed dark/light line outline, preserves irregular shapes, and attempts to recognize long interior wall strokes. The generator no longer silently falls back to a box: if no reliable outline exists, it refuses to insert.
5. **3D reference photo**: attempts a high-contrast **visible façade silhouette**, translating stepped height variation into a basic conceptual structure. The depth is still estimated or manually specified. This is **not** accurate 3D reconstruction from a photo, and hidden rooms/geometry/materials cannot be inferred.
6. Choose allowed Foundations, Walls and Roofs from the local Placeables catalog. All generated pieces stay editable, and insertion is one undo step. The `example.*` Type IDs are only placeholders, not verified game pieces.
7. The 2D and 3D editor share the generated blueprint, and both can be used after insertion. The image is never uploaded or used to modify game files.

For instructions and verification: [v0.12.0 Windows test checklist](docs/V0.12-TEST-CHECKLIST.md).

### Previous modules retained

- Placeables Library + Placed Elements sidebar.
- 2D editing and interactive textured 3D editing with mouse drag / right-click panning / surface snapping.
- EML Manager with separate Client and Dedicated Server installation profiles.
- v0.11.0 Hotfix 1 namespace and `Path` compilation fixes are included.

### 3D placement: move between floors

Dragging pieces now tests the upper-facing surface underneath the cursor and can snap onto another piece or the Y=0 ground plane, instead of always staying at the original Y. `Alt+left-drag` continues to adjust elevation manually. This uses preview geometry/4m placeholder bounds, not verified Enshrouded collision geometry, and needs real Windows testing.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio 2022/2026 or VS Code with the C# extension

## Build and run

Run these commands from the project root in PowerShell:

```powershell
dotnet clean .\Bjornhjarta.sln
dotnet build .\Bjornhjarta.sln
dotnet run --project .\src\Bjornhjarta.Desktop\Bjornhjarta.Desktop.csproj
```

A one-line clean, build, and run command is included in `run.txt`.

## Historical release notes

### v0.10.2 — 3D drag-and-drop placement

- Choose **2D Editor** or **3D Editor** using the high-contrast dropdown; no separate Textured 3D button.
- **Left-click a 3D piece or placeholder and drag** on the X/Z plane to reposition it, snapping to 4-metre increments. Let go of the mouse to commit the change, once, in Blueprint Forge's undo history.
- **Alt + left-drag a piece** for vertical movement on Y, also in 4-metre increments. This is an optional alternative to changing elevation in the inspector.
- **Ctrl/Shift+click** to select multiple pieces, then left-drag any selected piece to move the whole group, preserving relative positions.
- A drag shows a live position preview without modifying blueprint data. Releasing mouse capture unexpectedly cancels the preview.
- **Left-drag empty space** to orbit the camera; **right-drag or middle-drag** to pan; mouse wheel zooms.
- Removed the intrusive X+/X-/Y+/Y-/Z+/Z- and Undo button bar. `Ctrl+Z` remains available, as do optional arrow and Page Up/Down keyboard nudges.
- Scene is an embedded WPF editor. Local GLB models are previewed with base-color textures, and missing models have obvious placeholders. No real game assets are included.
- Planned **Placeables Library** with verified Foundations, Walls, Furniture, Magic Chests etc. remains a separate future deliverable; existing side panel still shows placed elements.

### v0.10.1 — Unified 2D/3D editing navigation

- Both editor modes are in the same workspace, with clearer dropdown contrast and right-drag pan in both.

## Blueprint Forge v0.10.2 preview

### Existing 2D workflow

- Create, open, save, and validate JSON blueprints.
- Add/remove elements and edit Type IDs, positions, rotations, and variants.
- 2D top-down planning grid: X is horizontal, Z is depth, Y is elevation.
- 4 metre coordinate grid snap.
- Multi-select elements in the list with Ctrl/Shift, or use a mouse-drag rectangle on empty grid space.
- Ctrl-click toggles a grid item in the selection; Shift-click adds it.
- Drag any selected marker to move the complete selected group while preserving relative positions.
- Copy/paste groups with Ctrl+C / Ctrl+V using the editor clipboard. Pasted groups receive new IDs.
- Duplicate selected elements or groups with Ctrl+D.
- Rotate selected elements 90 degrees around the Y axis.
- Automatic estimated footprint and vertical anchor-span display.
- Mouse-wheel zoom, middle-mouse pan, Space+left-drag pan, and Fit / Center.
- Undo/redo history; group moves and group paste are each a single undo step.

### Historical v0.7–v0.10 isometric coordinate grid

- Select **2D Editor** or **3D Editor** from the high-contrast dropdown. The 3D choice embeds the textured GLB editor directly in Blueprint Forge.
- Both views use the same blueprint elements and the same X/Y/Z coordinate data.
- The 3D view projects X, Y (elevation), and Z into an isometric coordinate grid.
- Displays a ground-plane X/Z grid, highlighted world axes, a vertical Y axis, elevation guide lines, and lighter grid slices at occupied height levels.
- Grid markers are placed using all three coordinates, so stacked floors and elevated elements appear at different heights.
- Rectangular selection follows projected marker positions in 3D mode.
- Drag selected groups on the X/Z ground plane; use **Alt+left-drag** in 3D mode to move a group vertically along Y.
- Zoom, pan, and Fit / Center adapt to the selected grid mode.

### Blueprint Forge scope and limitations

That earlier 3D mode was an **isometric coordinate projection**, with point markers. As of **v0.10.1**, the dropdown **3D Editor** instead embeds a true WPF textured-GLB scene with an orbit camera, selection and grid-step movement. It is not yet an exact renderer for Enshrouded: missing assets still display placeholders, and actual game model bounds are unverified. Switching editors does not change blueprint coordinates or file schema.

Blueprint Forge works on local blueprint documents only. It does not modify Enshrouded game files or world saves.

### Local Model Asset Library foundation

- Scan local glTF 2.0 (`.glb` and `.gltf`) exports.
- Import GLB files into a user-selected local model library.
- Inspect mesh, material, texture and image counts from the glTF container.
- Detect standard glTF Base Color/Albedo, Metallic/Roughness, Normal, Occlusion and Emissive texture channels.
- Distinguish embedded from external images and flag duplicate asset IDs before blueprint assignment.
- Validate model containers and flag missing or unsafe external `.gltf` resource paths.
- Store a `.bjornhjarta.asset.json` sidecar with a stable asset ID, display name, Enshrouded Type ID, optional source RenderModel GUID and notes.
- Assign a mapped model asset to selected blueprint elements.
- Store the model asset ID and display name in the blueprint JSON; group copy/paste and duplicate preserve the assignment.
- Includes a Blender export helper script and a documented workflow for user-exported Enshrouded models.
- Optionally converts detected Enshrouded RMA maps to standard glTF ORM channel order during export, and records the conversion state in sidecar metadata.
- Includes a synthetic textured GLB fixture (`samples/model-assets/Demo-Textured-Triangle.glb`) to test scanning without touching game files.

### Textured 3D mesh editor — drag-and-drop editing in v0.10.2

- Switch the Blueprint Forge dropdown to **3D Editor**. It embeds the textured GLB editor in the central editing area, replacing the former separate preview window. Choose **2D Editor** to return to the top-down canvas.
- Click 3D model geometry or placeholders to select a piece; Ctrl/Shift+click toggles multiple pieces. Selection also appears in the parent editor.
- Left-drag a selected 3D model/placeholder to place it on the X/Z grid. A drag previews the final snapped position (4 m increments); release to create one undo step. Alt+left-drag moves vertically on Y. Selected groups retain relative positioning.
- The former X/Y/Z move button strip is removed. Optional keyboard arrow and Page Up/Down nudges remain available.
- Use Ctrl+Z for Undo while the editor has keyboard focus. Save the blueprint afterward in Blueprint Forge to keep your changes.
- Imported GLB can be assigned to an already-selected piece with **Quick assign to selected pieces**, which automatically creates a local manifest for that piece's Type ID if none exists. For pre-mapped assets with a different Type ID, assignment requires a confirmation and is explicitly preview-only. No real blueprint Type ID is silently changed.
- Loads compatible local `.glb` geometry by matching blueprint `modelAssetId` to the asset library's sidecar IDs.
- Positions and rotates each model according to its blueprint X/Y/Z and Pitch/Yaw/Roll coordinates, without changing the JSON blueprint.
- Shows triangles, UV0 base-color textures (albedo), and basic WPF diffuse lighting.
- Has orbit (left-drag on empty space), pan (right- or middle-drag), zoom (wheel/buttons), and fit-to-content camera controls.
- Shows a 4 m ground grid and explicit 4 m placeholder blocks for pieces without a mapped/compatible model.
- Shows asset/mesh/placeholder counts, warnings, and a cap of 350 rendered blueprint elements per preview to protect the UI.
- Supports **GLB only for rendering**; `.gltf` is inventory-only in the asset library until a later renderer. Compatible GLB files use standard glTF 2.0 triangle meshes, float POSITION/NORMAL/UV0, indices, node transforms and base-color textures.
- Uses built-in WPF Viewport3D; **no new NuGet dependencies** and no redistribution or extraction of copyrighted Enshrouded assets.

**Important limitations:** v0.10.1 is **not** full Enshrouded-shader fidelity. WPF does not implement normal maps, ORM/PBR channels, emissive lighting, alpha-mode parity, shadows, skins, morph targets, or compressed glTF mesh extensions. Blueprint elements without verified models remain placeholders and have **no verified game dimensions**. A model exported with incorrect scale or origin will not align correctly. Models must be exported locally by the user using separate tools they trust. See [v0.10.2 Windows Test Checklist](docs/V0.10.2-TEST-CHECKLIST.md).

The RMA → glTF ORM conversion from v0.8.0 remains relevant preparation for a future PBR renderer, but **is not used by the current WPF diffuse 3D view**. The future full PBR stack and texture parity decision remain tracked in [3D Renderer Decision](docs/3D-RENDERER-DECISION.md).

## EML Mod Manager v0.7.0 preview

Bjornhjarta uses **EML (Enshrouded Mod Loader)** as its supported mod-loader integration. EML is community-developed, not an official Keen Games modding system.

### Separate Client and Dedicated Server profiles

- Manage an **Enshrouded Client** installation and an **Enshrouded Dedicated Server** installation from the same EML Manager window.
- Browse to the correct executable for each profile (`enshrouded.exe` or `enshrouded_server.exe`).
- Persist the Client root and Dedicated Server root independently.
- Remember the last active profile.
- Display separate status summaries and scan both configured installations read-only.
- Keep each installation's `dbghelp.dll`, `mods` folder, `eml.json`, and mod inventory tied to its own root.
- Switch active profile before performing an action; every write or EMM CLI command targets only that active profile.
- Share the selected `emm.exe` path because the CLI accepts a target game directory through `-g`.
- Migrate the previous single-installation preference into the matching Client or Server profile when possible.

### EML inspection and configuration

- Detect the documented EML proxy file `dbghelp.dll`.
- Report `dinput8.dll` as an alternative proxy candidate only; it is not automatically treated as confirmed EML.
- Detect and create the root `mods` directory.
- Inventory top-level mod folders using the documented `mods/<mod-id>/mod.json` layout.
- Check required manifest fields (`id`, `name`, `version`, and `capabilities`), semantic-version syntax, duplicate IDs, folder/ID alignment, and known capability names.
- Launch EML's interactive `emm.exe create -g <game-dir>` workflow for the active profile.
- Install or replace a user-selected `dbghelp.dll` with backup of an existing proxy.
- Read and edit `eml.json` settings while preserving unknown JSON properties and backing up an existing configuration.
- Validate the active installation's mods with `emm.exe run -g <game-dir>` in validation-only mode (no `--patch` or `--export` flags).
- Capture CLI output and exit code.
- When the user saves `eml.json` with export enabled, ensure the configured relative export directory exists; paths outside the active installation are rejected.
- Refuse proxy replacement while the corresponding client or server process is running.

### EML Manager scope and limitations

- The application does not download EML binaries or third-party mods. The user selects a locally downloaded `dbghelp.dll` and `emm.exe`.
- Only use loader files from a trusted EML release source and mods from authors you trust.
- The manager does not yet install mods from ZIP archives, enable/disable mods, resolve dependencies or conflicts, or automatically update EML.
- A valid manifest does not prove a mod is compatible with the installed game build.
- Scanning and validation do not modify game archives or world saves.
- EML's proxy DLL method is documented for both client and server installations; the manager keeps their files/configuration separate. EML CLI validation uses the selected game root.

## EML references

- Official EML usage documentation: https://brabb3l.github.io/kfc-parser/eml/usage.html
- EML source and releases: https://github.com/Brabb3l/kfc-parser

The EML usage guide describes the proxy DLL method as recommended for both client and server installations and describes `emm.exe run -g <game-dir>` as validation-only by default unless capability flags are supplied. Check release notes and mod requirements before use; Enshrouded updates can affect loader and mod compatibility.

## Release and test notes

- Blueprint Forge v0.5.0 was confirmed working by the project owner.
- In v0.6.0, the user confirmed that the client installation was detected and EMM validation exited with code 0. This did not verify that mods were actually applied or test the dedicated-server profile.
- v0.7.0 adds a visible dual-profile manager and the new isometric grid mode. These new UI and interaction changes require Windows build and user testing; they are not claimed as tested in this environment.
- v0.8.0 adds local GLB/glTF model inventory, sidecar Type ID mapping and blueprint references.
- v0.9.0 adds a first read-only 3D mesh viewport with GLB geometry, UV0 base-color textures and camera controls; full game material fidelity remains future work.
- A full WPF/.NET build must be run on Windows with the .NET 10 SDK.


## 0.7.0 hotfix 1

This hotfix corrects the WPF `Point`/`Vector` parameter mismatch in the Blueprint Forge grid drag conversion. Rebuild on Windows with the .NET 10 SDK to verify the full desktop application.


## v0.7.0 Hotfix 2

This hotfix addresses an EML Mod Manager startup issue. The profile ComboBox could raise `SelectionChanged` while its XAML was still being initialized. Initialization is now guarded from before `InitializeComponent`, and the navigation command catches and logs startup exceptions so the main application remains open.

If the EML window still fails to open, inspect `%LOCALAPPDATA%\Bjornhjarta\Logs\bjornhjarta.log` and share the latest entry.


## Custom Bjornhjarta Icon Pack

The desktop UI now uses the project's four custom Norse symbols (compass emblem, crossed axes, Berkana rune and raven) instead of generic navigation glyphs. Original transparent PNGs are preserved in `src/Bjornhjarta.Desktop/Assets/Icons/Original/`; warm-ivory and Bjornhjarta-green variants are embedded as WPF resources. See [Branding Icons](docs/BRANDING-ICONS.md) for the assignment map.


## v0.8.0 preview — Model Asset Library foundation

- Added a local Model Asset Library window to Blueprint Forge.
- Added GLB import and recursive GLB/glTF 2.0 library scanning.
- Added glTF container inspection for mesh, material and texture counts.
- Added safety checks for external glTF resource references.
- Added sidecar metadata mapping for stable asset IDs, Enshrouded Type IDs and optional source RenderModel GUIDs.
- Added assignment of mapped assets to selected blueprint elements.
- Extended blueprint elements with optional `modelAssetId` and `modelAssetName` fields while keeping schema version 1 backward-compatible.
- Preserved model references during duplicate and group copy/paste operations.
- Added `tools/blender/export_selected_model_for_bjornhjarta.py` as an optional local Blender export helper.
- Added optional Enshrouded RMA → glTF ORM channel conversion, embedded GLB image data, and texture-packing provenance in sidecar metadata.
- No Enshrouded game archives or world saves are modified.
- In v0.8.0, full mesh rendering was future work. v0.9.0 now adds a **separate limited base-color textured mesh preview**, while the isometric editing grid still uses markers. Full PBR remains planned; the proposed packages are not added.


## v0.9.0 preview — first textured 3D viewer

- Added a WPF Viewport3D preview window, preserving the tested 2D and 3D isometric editing surfaces.
- Loads user-provided mapped GLB assets with glTF triangle meshes, indices, UV0 albedo textures and node transforms.
- Applies blueprint transforms and renders placeholders for unmapped/unsupported pieces.
- Added orbit, pan, zoom, fit, simple lighting, 4m grid, status diagnostics and an explicit preview size limit.
- This is a read-only prototype and does not automatically extract Enshrouded assets.

## v0.10.0 preview — Interactive selection and simple local model linking

This build adds **direct mouse selection** in the textured 3D window, **4-metre X/Y/Z movement** of one or several selected elements (one undo step per nudge), and **Quick assign** of a local GLB to an already selected blueprint piece without manually entering a test Type ID. Selecting a mapped asset with a different Type ID prompts for an explicit preview-only link and does not rewrite actual piece IDs. Selection frame bounds are guide anchors, **not verified Enshrouded model extents**.

This is not yet a full drag-to-place editor: movement is by snapped buttons/keyboard steps. The WPF preview still does not support full PBR, normal/ORM shaders, accurate collision/size, automatic official game model extraction, or writing to Enshrouded worlds. Models are user-imported local GLBs. See [v0.10.2 Windows Test Checklist](docs/V0.10.2-TEST-CHECKLIST.md).

## RC2 UI Smoke Hotfix 1

See `docs/V1.0.0-RC2-UI-SMOKE-HOTFIX1.md`. The regular WPF StartupUri is now used for smoke mode and the smoke-test script prints report and log failures automatically. No Core or mod operations changed.
