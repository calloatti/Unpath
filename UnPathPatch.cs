using HarmonyLib;
using System.Collections.Generic;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.Modding;
using Timberborn.ModManagerScene;
using Timberborn.PathSystem;
using Timberborn.TemplateSystem;
using UnityEngine;

namespace Calloatti.Unpath
{
  public class UnpathPlugin : IModStarter
  {
    public void StartMod(IModEnvironment modEnvironment)
    {
      Harmony harmony = new Harmony("calloatti.unpath");
      harmony.PatchAll();
    }
  }

  public static class PlacementContext
  {
    public static BlockObject CurrentValidatingObject;
    public static bool IsPlayerPlacing = false;
  }

  public static class PathDetector
  {
    public static bool IsRemovablePath(BlockObject blockObject)
    {
      if (blockObject == null) return false;

      // Must have the PathSpec component
      if (!blockObject.HasComponent<PathSpec>()) return false;

      // Must be exactly 1x1x1 in size
      if (blockObject.GetComponent<BlockObjectSpec>().Size != new Vector3Int(1, 1, 1)) return false;

      // Must have zero construction cost
      var buildingSpec = blockObject.GetComponent<BuildingSpec>();
      if (buildingSpec == null || buildingSpec.BuildingCost.Length > 0) return false;

      // Must exclusively occupy the Path layer (or None)
      foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
      {
        if (block.Occupation != BlockOccupations.Path && block.Occupation != BlockOccupations.None)
        {
          return false;
        }
      }

      return true;
    }
  }

  [HarmonyPatch(typeof(BlockObject), nameof(BlockObject.IsValid))]
  static class BlockObject_IsValid_Patch
  {
    static void Prefix(BlockObject __instance) => PlacementContext.CurrentValidatingObject = __instance;
    static void Postfix() => PlacementContext.CurrentValidatingObject = null;
  }

  [HarmonyPatch(typeof(BlockObject), nameof(BlockObject.IsAlmostValid))]
  static class BlockObject_IsAlmostValid_Patch
  {
    static void Prefix(BlockObject __instance) => PlacementContext.CurrentValidatingObject = __instance;
    static void Postfix() => PlacementContext.CurrentValidatingObject = null;
  }

  // Set the IsPlayerPlacing flag only when the player actually clicks to place a building
  [HarmonyPatch(typeof(DefaultBlockObjectPlacer), nameof(DefaultBlockObjectPlacer.Place))]
  static class DefaultBlockObjectPlacer_Place_Patch
  {
    static void Prefix() => PlacementContext.IsPlayerPlacing = true;
    static void Postfix() => PlacementContext.IsPlayerPlacing = false;
  }

  [HarmonyPatch(typeof(BlockService), nameof(BlockService.AnyNonOverridableObjectsAt))]
  static class BlockService_AnyNonOverridableObjectsAt_Patch
  {
    static void Postfix(Vector3Int coordinates, BlockOccupations occupations, ref bool __result, BlockService __instance)
    {
      if (!__result || PlacementContext.CurrentValidatingObject == null) return;

      // Get the template name of the building currently in the player's "hand"
      string placingTemplate = PlacementContext.CurrentValidatingObject.GetComponent<TemplateSpec>().TemplateName;

      var objectsAtTile = __instance.GetObjectsAt(coordinates);
      bool onlyBlockedByRemovablePaths = true;
      bool foundRemovablePath = false;

      foreach (var obj in objectsAtTile)
      {
        if (obj == PlacementContext.CurrentValidatingObject) continue;

        // Check if the object on the ground is the same as the one we are placing
        string existingTemplate = obj.GetComponent<TemplateSpec>().TemplateName;
        if (existingTemplate == placingTemplate)
        {
          // If they are identical, treat it as a hard block so we don't place paths on paths
          onlyBlockedByRemovablePaths = false;
          break;
        }

        if (PathDetector.IsRemovablePath(obj))
        {
          foundRemovablePath = true;
          continue;
        }

        if (!obj.Overridable && obj.PositionedBlocks.GetBlock(coordinates).Occupation.Intersects(occupations))
        {
          onlyBlockedByRemovablePaths = false;
          break;
        }
      }

      if (foundRemovablePath && onlyBlockedByRemovablePaths)
      {
        __result = false;
      }
    }
  }

  [HarmonyPatch(typeof(BlockObject), "AddToService")]
  static class BlockObject_AddToService_Patch
  {
    static void Prefix(BlockObject __instance, IBlockService ____blockService, EntityService ____entityService)
    {
      // Bail out immediately if this is during save-loading (IsPlayerPlacing == false)
      if (!PlacementContext.IsPlayerPlacing || __instance.IsPreview || __instance.AddedToService) return;

      foreach (var block in __instance.PositionedBlocks.GetAllBlocks())
      {
        if (block.Occupation == BlockOccupations.None) continue;

        var objectsAtTile = ____blockService.GetObjectsAt(block.Coordinates);
        List<BlockObject> toDelete = new List<BlockObject>();

        foreach (var objAtTile in objectsAtTile)
        {
          if (PathDetector.IsRemovablePath(objAtTile))
          {
            // Only delete the path if its occupation intersects with the building's occupation at this block.
            var pathOccupation = objAtTile.PositionedBlocks.GetBlock(block.Coordinates).Occupation;
            if (block.Occupation.Intersects(pathOccupation))
            {
              toDelete.Add(objAtTile);
            }
          }
        }

        foreach (var path in toDelete)
        {
          ____entityService.Delete(path);
        }
      }
    }
  }
}