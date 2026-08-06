Include ..\AGENTS.md

# UnPath — Mod-Specific Agent Instructions

## Identity
- **Assembly:** `unpath`
- **Namespace:** `Calloatti.Unpath`
- **Framework:** Harmony, Bindito DI
- **ModId:** `Calloatti.UnPath`
- **Min Game Version:** 1.0.12.5 — uses `timberborn-decompiled-1.0.*`
- **Latest Manifest Version:** 1.0.4 (see `Version-1.0/manifest.json`)
- **Required Mods:** Harmony (`MinVersion 2.4.1`)
- **Publishing:** Steam Workshop ID `3688600322`, mod.io ID `6250227`

## What This Mod Does
Allows placing buildings on top of existing paths. Overrides `BlockService.AnyNonOverridableObjectsAt` (via Harmony Postfix) to make the game treat path-only tiles as valid, then deletes the overlapped paths when the building is actually placed.

Scope is intentionally narrow:
- Only **1x1, zero-cost path** tiles are removable (see `PathDetector.IsRemovablePath`).
- Only **player-built structures** (`BuildingSpec`) may trigger the override (`PathDetector.IsExcludedObject`).
- Map-editor objects, ruins, natural resources, and anything without a `BuildingSpec` are never touched.

## Repository Layout
| Path | Purpose |
|---|---|
| `Version-1.0/` | The active mod version folder (source + project). Contains `Source/`, `UnPath.csproj`, `manifest.json`, `changelog.txt`, `prebuild.ps1`, `postbuild.ps1`, `CommonModSettings.props` |
| `Version-1.0/Source/UnPathPatch.cs` | All mod code — entry point, DI config, and every Harmony patch (single file) |
| `.meta/` | Publishing metadata: workshop/mod.io names, tags, descriptions, version tracking (`workshop_version.txt` = `1.0:1.0.4`) |
| `workshop_data.json` | Workshop upload config (backed up to repo root from the deployed mod folder on prebuild) |
| `.scratch/` | Historical crash logs / player logs / save metadata (gitignored; kept as debug evidence) |
| `thumbnail.jpg`, `README.md` | Root assets deployed to the mod folder |

## Source Architecture (`Version-1.0/Source/UnPathPatch.cs`)

All code lives in one file. Order of types:

| Type | Kind | Role |
|---|---|---|
| `UnpathConfigurator` | `Configurator` (`[Context("Game")]`) | Bindito DI: binds `UnpathUiListener` as singleton |
| `UnpathUiListener` | `ILoadableSingleton` | Listens for `ShowPrimaryUIEvent`; sets `PlacementContext.IsFunctional = true` (mod activates only after primary UI loads) |
| `UnpathPlugin` | `IModStarter` | Entry point; calls `new Harmony("calloatti.unpath").PatchAll()` |
| `PlacementContext` | `static class` | Shared state: `CurrentValidatingObject`, `IsFunctional`, and `OverriddenCoords` (the ledger) |
| `PathDetector` | `static class` | Validation helpers: `IsExcludedObject(BlockObject)`, `IsRemovablePath(BlockObject)` |
| `BlockObjectTool_Place_Patch` | Harmony Prefix | Clears the `OverriddenCoords` ledger right before a real placement |
| `BlockObject_IsValid_Patch` | Harmony Prefix + Finalizer | Stores the object currently being validated; Finalizer guarantees cleanup even on exception |
| `BlockObject_IsAlmostValid_Patch` | Harmony Prefix + Finalizer | Same as above for the "almost valid" preview path |
| `BlockService_AnyNonOverridableObjectsAt_Patch` | Harmony Postfix | **The validation spoof** — allows placement when only paths block |
| `BlockObject_AddToService_Patch` | Harmony Prefix | **The execution phase** — deletes the overlapped paths when the building is placed |

## How It Works (Flow)

1. **Activation:** `UnpathUiListener.Load()` registers with the EventBus; on `ShowPrimaryUIEvent` it flips `PlacementContext.IsFunctional = true`. All patches early-out unless `IsFunctional`.
2. **Validation phase:** While the game checks a placement, `BlockObject.IsValid`/`IsAlmostValid` Prefix stores the preview object in `PlacementContext.CurrentValidatingObject` (cleared by a `[HarmonyFinalizer]`). The `AnyNonOverridableObjectsAt` Postfix then runs only if the game already reported `__result == true`. It inspects every object at the tile; if the *only* blockers are removable paths (and it's not stacking the same template on itself), it sets `__result = false` and records the coordinate in `OverriddenCoords[coordinates] = Time.frameCount`.
3. **Execution phase:** `BlockObject.AddToService` Prefix iterates the new building's footprint. For each coordinate in the ledger within 30 frames of the spoof (stale-abort guard), it fetches `__instance._blockService.GetObjectsAt(...)` and deletes any `PathSpec` object via `__instance._entityService.Delete(...)`, then removes the coordinate from the ledger.
4. **Safety net:** `BlockObjectTool.Place` Prefix clears the ledger before each real placement so an aborted placement cannot leave ghost state that deletes paths later.

## Key Game APIs (verified against `timberborn-decompiled-1.0.13.1-*`)

| API | Location | Notes |
|---|---|---|
| `BlockService.AnyNonOverridableObjectsAt(Vector3Int, BlockOccupations)` | `Timberborn.BlockSystem.cs:2279` | Return value is `NonOverridableBlockOccupations.Intersects(occupations)`. Spoofed to `false` |
| `BlockObject.IsValid()` | `Timberborn.BlockSystem.cs:1192` | Calls `BlockValidator.BlocksValid` + `_blockObjectValidationService.IsValid` |
| `BlockObject.IsAlmostValid()` | `Timberborn.BlockSystem.cs:1187` | Calls `BlockValidator.BlocksAlmostValid` |
| `BlockObject.AddToService()` | `Timberborn.BlockSystem.cs:1216` | **Now private**; patched via string `"AddToService"` — works because the assembly is publicized |
| `BlockObjectTool.Place(IEnumerable<Placement>)` | `Timberborn.BlockObjectTools.cs:756` | Private; patched by string |
| `BlockObjectSpec` (record: `Size`, `Overridable`) | `Timberborn.BlockSystem.cs:1632` | `Size` must be `(1,1,1)` for removable paths |
| `BuildingSpec` (record: `BuildingCost`) | `Timberborn.Buildings.cs:1060` | Presence = player structure; non-empty `BuildingCost` excludes it |
| `PathSpec` (record) | `Timberborn.PathSystem.cs:916` | Marks removable path objects |
| `TemplateSpec` (record: `TemplateName`) | `Timberborn.TemplateSystem.cs:332` | Used to prevent stacking the same template on itself |

## Build & Deploy Workflow

- **Project:** `UnPath.csproj` is minimal — it only sets `AssemblyName`/`RootNamespace` and imports `CommonModSettings.props` (shared; **never edit** that file).
- **Build:** `dotnet build` or VS build of `Version-1.0/UnPath.csproj`. `TargetFramework` is `netstandard2.1`, C# 10.
- **Game references:** Game DLLs are publicized via Krafs.Publicizer; refs come from `C:\Program Files (x86)\Steam\steamapps\common\timberborn_main\Timberborn_Data\Managed` and 0Harmony from the Steam workshop folder (paths hardcoded in `CommonModSettings.props`).
- **prebuild.ps1:** Backs up deployed `workshop_data.json` into the repo root, then cleans `bin/` and the destination mod subfolder.
- **postbuild.ps1:** Deploys to `%USERPROFILE%\Documents\Timberborn\Mods\UnPath\Version-1.0`, copies root assets (`thumbnail.jpg`, `workshop_data.json`, `README.md`) to the mod root, and strips `*.ps1`, `AGENTS.md`, `*.zip`.
- **Deployed layout:** `Documents\Timberborn\Mods\UnPath\Version-1.0\{unpath.dll, manifest.json, changelog.txt, ...}`.

## Versioning & Release Notes
- Bump `Version` in `Version-1.0/manifest.json` and `.meta/workshop_version.txt` (format `1.0:1.0.4`), and add an entry at the top of `Version-1.0/changelog.txt`.
- Release history (top of `changelog.txt`): 1.0.4 added `BuildingSpec` check; 1.0.3 defensive null-checks; 1.0.2 crash fix + `ShowPrimaryUIEvent`; 1.0.1 do-not-remove-when-overridable + player-only interception.

## Known Pitfalls & Gotchas
- **Defensive null-checks everywhere:** Historically an `NullReferenceException` occurred in the `AnyNonOverridableObjectsAt` Postfix (see `.scratch/1 Exception *.txt` from game 1.0.12.5). Keep null guards on `PositionedBlocks`, components, and specs when touching this code.
- **Ledger staleness:** The 30-frame window on `OverriddenCoords` is the anti-"state-leak" guard against aborted placements. If you touch timing, keep the frame-based stamping (`Time.frameCount`).
- **`GetComponent<T>()` not `GetComponentFast`:** Follow the root AGENTS.md — `GetComponentFast` doesn't exist in Timberborn.
- **ECS lists:** Do not use `GetComponents<T>()` return value (it returns `void`); pass a pre-allocated `List<T>`.
- **Publicized private members:** `_blockService` and `_entityService` on `BlockObject` are only accessible because `Timberborn.BlockSystem` and `Timberborn.EntitySystem` are publicized in `CommonModSettings.props`.

## Commands
- Build / deploy (Debug): `dotnet build Version-1.0/UnPath.csproj`
- There is no test project for this mod; verify in-game by placing buildings over paths in a sandbox save.
