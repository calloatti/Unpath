Include ..\AGENTS.md

# UnPath — Mod-Specific Agent Instructions

## Identity
- **Assembly:** `unpath`
- **Namespace:** `Calloatti.Unpath`
- **Framework:** Harmony, Bindito DI
- **ModId:** `Calloatti.UnPath`
- **Min Game Version:** 1.0.12.5 — uses `timberborn-decompiled-1.0.*`

## What This Mod Does
Allows placing buildings on top of existing paths. Overrides `BlockService.AnyNonOverridableObjectsAt` to allow building placement over path tiles, and automatically removes the overlapped paths when the building is placed.

## Source Architecture (`Version-1.0/Source/`)

| File | Role |
|---|---|
| `UnPathPatch.cs` | `IModStarter` entry point, `UnpathConfigurator`, `UnpathUiListener`, validation and execution patches |
