using Bindito.Core;
using HarmonyLib;
using System.Collections.Generic;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.CoreUI;
using Timberborn.EntitySystem;
using Timberborn.Modding;
using Timberborn.ModManagerScene;
using Timberborn.PathSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;

namespace Calloatti.Unpath
{
  [Context("Game")]
  public class UnpathConfigurator : Configurator
  {
    protected override void Configure()
    {
      Bind<UnpathUiListener>().AsSingleton();
    }
  }

  public class UnpathUiListener : ILoadableSingleton
  {
    private readonly EventBus _eventBus;
    public UnpathUiListener(EventBus eventBus) => _eventBus = eventBus;
    public void Load() => _eventBus.Register(this);

    [OnEvent]
    public void OnShowPrimaryUI(ShowPrimaryUIEvent @event)
    {
      PlacementContext.IsFunctional = true;
      Debug.Log("[UNPATH] UI loaded, mod functionality enabled.");
    }
  }

  public class UnpathPlugin : IModStarter
  {
    public void StartMod(IModEnvironment modEnvironment)
    {
      new Harmony("calloatti.unpath").PatchAll();
      Debug.Log("[UNPATH] Harmony patches applied successfully.");
    }
  }

  public static class PlacementContext
  {
    public static BlockObject CurrentValidatingObject;
    public static bool IsFunctional = false;

    // The Ledger: Tracks the exact map coordinates where the mod told the game a blocked tile was actually clear.
    // We store the exact Unity Time.frameCount when this spoof occurred. This prevents "state leaks" where 
    // an aborted placement leaves ghost data behind that might accidentally delete paths later.
    public static Dictionary<Vector3Int, int> OverriddenCoords = new Dictionary<Vector3Int, int>();
  }

  public static class PathDetector
  {
    // Helper to identify map editor objects, ruins, and natural resources using the ECS approach.
    public static bool IsExcludedObject(BlockObject blockObject)
    {
      if (blockObject == null) return false;
      // If it doesn't have a BuildingSpec, it is not a standard player-built structure. Exclude it.
      return !blockObject.HasComponent<BuildingSpec>();
    }

    // A strict validation method to determine if an object is 100% safely removable as a path.
    public static bool IsRemovablePath(BlockObject blockObject)
    {
      // Defensive null-checks: If the object is missing components, it's not a standard path.
      if (blockObject == null) return false;
      if (!blockObject.HasComponent<PathSpec>()) return false;

      var blockObjectSpec = blockObject.GetComponent<BlockObjectSpec>();
      if (blockObjectSpec == null || blockObjectSpec.Size != new Vector3Int(1, 1, 1)) return false;

      var buildingSpec = blockObject.GetComponent<BuildingSpec>();
      if (buildingSpec == null || buildingSpec.BuildingCost.Length > 0) return false;

      if (blockObject.PositionedBlocks == null) return false;

      // Verify that every block in its footprint is registered strictly as a path or empty space.
      foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
      {
        if (block.Occupation != BlockOccupations.Path && block.Occupation != BlockOccupations.None) return false;
      }
      return true;
    }
  }

  [HarmonyPatch(typeof(BlockObjectTool), "Place")]
  static class BlockObjectTool_Place_Patch
  {
    static void Prefix()
    {
      // Housekeeping: We still wipe the tracking list clean right before a known placement 
      // is processed by the tool to keep the dictionary's memory footprint as close to zero as possible.
      if (PlacementContext.IsFunctional) PlacementContext.OverriddenCoords.Clear();
    }
  }

  [HarmonyPatch(typeof(BlockObject), nameof(BlockObject.IsValid))]
  static class BlockObject_IsValid_Patch
  {
    static void Prefix(BlockObject __instance)
    {
      // Store the preview building we are currently trying to place.
      if (PlacementContext.IsFunctional) PlacementContext.CurrentValidatingObject = __instance;
    }

    // [HarmonyFinalizer] acts like a C# try-finally block. 
    // It guarantees CurrentValidatingObject is cleared even if the game throws a random 
    // exception during validation. This completely prevents permanent memory leaks.
    [HarmonyFinalizer]
    static void Finalizer() => PlacementContext.CurrentValidatingObject = null;
  }

  [HarmonyPatch(typeof(BlockObject), nameof(BlockObject.IsAlmostValid))]
  static class BlockObject_IsAlmostValid_Patch
  {
    static void Prefix(BlockObject __instance)
    {
      if (PlacementContext.IsFunctional) PlacementContext.CurrentValidatingObject = __instance;
    }

    [HarmonyFinalizer]
    static void Finalizer() => PlacementContext.CurrentValidatingObject = null;
  }

  // VALIDATION PHASE: The game asks if a specific tile is blocked. We intercept to say "No" if it's just a path.
  [HarmonyPatch(typeof(BlockService), nameof(BlockService.AnyNonOverridableObjectsAt))]
  static class BlockService_AnyNonOverridableObjectsAt_Patch
  {
    static void Postfix(Vector3Int coordinates, BlockOccupations occupations, ref bool __result, BlockService __instance)
    {
      // If __result is already FALSE, the vanilla game natively accepts this placement. Do nothing!
      if (!PlacementContext.IsFunctional || !__result || PlacementContext.CurrentValidatingObject == null) return;

      // NEW: If the object being PLACED lacks a BuildingSpec, do not spoof. Let vanilla game handle it.
      if (PathDetector.IsExcludedObject(PlacementContext.CurrentValidatingObject)) return;

      // Safely extract the template name. If it's a weird entity without a template, we just use null.
      TemplateSpec placingTemplateSpec = PlacementContext.CurrentValidatingObject.GetComponent<TemplateSpec>();
      string placingTemplate = placingTemplateSpec != null ? placingTemplateSpec.TemplateName : null;

      var objectsAtTile = __instance.GetObjectsAt(coordinates);

      bool onlyBlockedByRemovablePaths = true;
      bool foundRemovablePath = false;

      foreach (var obj in objectsAtTile)
      {
        // Skip null objects or the very object we are trying to validate
        if (obj == null || obj == PlacementContext.CurrentValidatingObject) continue;

        // Safely check the existing object's template
        TemplateSpec existingTemplateSpec = obj.GetComponent<TemplateSpec>();
        string existingTemplate = existingTemplateSpec != null ? existingTemplateSpec.TemplateName : null;

        // Prevent stacking the exact same building on top of itself
        if (placingTemplate != null && existingTemplate == placingTemplate)
        {
          onlyBlockedByRemovablePaths = false;
          break;
        }

        // If it's a removable path, flag it and keep checking the rest of the tile
        if (PathDetector.IsRemovablePath(obj))
        {
          foundRemovablePath = true;
          continue;
        }

        // If it's not overridable and it's not a path, check if their hitboxes actually intersect.
        if (!obj.Overridable)
        {
          // If we can't verify physical blocks, assume it's a hard blocker to prevent false spoofing.
          if (obj.PositionedBlocks == null)
          {
            onlyBlockedByRemovablePaths = false;
            break;
          }
          else if (obj.PositionedBlocks.GetBlock(coordinates).Occupation.Intersects(occupations))
          {
            // It's a real, solid object blocking the way. Abort spoofing.
            onlyBlockedByRemovablePaths = false;
            break;
          }
        }
      }

      // If the ONLY thing in our way is a path...
      if (foundRemovablePath && onlyBlockedByRemovablePaths)
      {
        // Spoof the result so the game allows the placement
        __result = false;
        // Add it to the ledger, stamped with the EXACT frame this occurred
        PlacementContext.OverriddenCoords[coordinates] = Time.frameCount;
      }
    }
  }

  // EXECUTION PHASE: The player clicked "Build". The game is spawning the real building.
  [HarmonyPatch(typeof(BlockObject), "AddToService")]
  static class BlockObject_AddToService_Patch
  {
    // Cleaned up the injected parameters since the DLL is now publicized
    static void Prefix(BlockObject __instance)
    {
      if (!PlacementContext.IsFunctional || __instance.IsPreview || __instance.AddedToService) return;

      // If the building has no physical blocks, it physically cannot conflict. Safe to abort.
      if (__instance.PositionedBlocks == null) return;

      // Check EVERY tile in the new building's footprint individually
      foreach (var block in __instance.PositionedBlocks.GetAllBlocks())
      {
        // 1. Ledger Check: Did we spoof this specific coordinate?
        if (!PlacementContext.OverriddenCoords.TryGetValue(block.Coordinates, out int spoofedFrame)) continue;

        // 2. Lifespan Check: Give it a cushy 30-frame buffer to account for engine lag or auto-saves.
        // If the spoof occurred more than ~0.5 seconds ago, it's stale garbage from an aborted placement. Ignore it.
        if (Time.frameCount - spoofedFrame > 30) continue;

        // Directly accessing the publicized _blockService
        var objectsAtTile = __instance._blockService.GetObjectsAt(block.Coordinates);

        List<BlockObject> toDelete = new List<BlockObject>();

        foreach (var objAtTile in objectsAtTile)
        {
          // Make absolutely sure we do not delete objects excluded from the mod's control
          if (PathDetector.IsExcludedObject(objAtTile)) continue;

          // Aggressive Execution: Because we already strictly vetted this tile during the Validation Phase,
          // we drop the strict PathDetector checks. If we spoofed this tile, we MUST delete any paths here 
          // to prevent a fatal engine collision crash.
          if (objAtTile != null && objAtTile.HasComponent<PathSpec>())
          {
            toDelete.Add(objAtTile);
          }
        }

        // Execute the deletions using publicized _entityService
        foreach (var path in toDelete)
        {
          if (path != null) __instance._entityService.Delete(path);
        }

        // 3. Self-Cleaning: Remove the coordinate from the ledger immediately after consumption.
        PlacementContext.OverriddenCoords.Remove(block.Coordinates);
      }
    }
  }
}