using HarmonyLib;
using System.Collections.Generic;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.PathSystem;
using Timberborn.TemplateSystem;
using UnityEngine;

namespace Calloatti.Unpath
{
  public static class PlacementContext
  {
    public static BlockObject CurrentValidatingObject;
    public static bool IsFunctional = false;

    public static Dictionary<Vector3Int, int> OverriddenCoords = new Dictionary<Vector3Int, int>();
  }

  public static class PathDetector
  {
    public static bool IsExcludedObject(BlockObject blockObject)
    {
      if (blockObject == null) return false;
      return !blockObject.HasComponent<BuildingSpec>();
    }

    public static bool IsRemovablePath(BlockObject blockObject)
    {
      if (blockObject == null) return false;
      if (!blockObject.HasComponent<PathSpec>()) return false;

      var blockObjectSpec = blockObject.GetComponent<BlockObjectSpec>();
      if (blockObjectSpec == null || blockObjectSpec.Size != new Vector3Int(1, 1, 1)) return false;

      var buildingSpec = blockObject.GetComponent<BuildingSpec>();
      if (buildingSpec == null || buildingSpec.BuildingCost.Length > 0) return false;

      if (blockObject.PositionedBlocks == null) return false;

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
      if (PlacementContext.IsFunctional) PlacementContext.OverriddenCoords.Clear();
    }
  }

  [HarmonyPatch(typeof(BlockObject), nameof(BlockObject.IsValid))]
  static class BlockObject_IsValid_Patch
  {
    static void Prefix(BlockObject __instance)
    {
      if (PlacementContext.IsFunctional) PlacementContext.CurrentValidatingObject = __instance;
    }

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

  [HarmonyPatch(typeof(BlockService), nameof(BlockService.AnyNonOverridableObjectsAt))]
  static class BlockService_AnyNonOverridableObjectsAt_Patch
  {
    static void Postfix(Vector3Int coordinates, BlockOccupations occupations, ref bool __result, BlockService __instance)
    {
      if (!PlacementContext.IsFunctional || !__result || PlacementContext.CurrentValidatingObject == null) return;

      if (PathDetector.IsExcludedObject(PlacementContext.CurrentValidatingObject)) return;

      TemplateSpec placingTemplateSpec = PlacementContext.CurrentValidatingObject.GetComponent<TemplateSpec>();
      string placingTemplate = placingTemplateSpec != null ? placingTemplateSpec.TemplateName : null;

      var objectsAtTile = __instance.GetObjectsAt(coordinates);

      bool onlyBlockedByRemovablePaths = true;
      bool foundRemovablePath = false;

      foreach (var obj in objectsAtTile)
      {
        if (obj == null || obj == PlacementContext.CurrentValidatingObject) continue;

        TemplateSpec existingTemplateSpec = obj.GetComponent<TemplateSpec>();
        string existingTemplate = existingTemplateSpec != null ? existingTemplateSpec.TemplateName : null;

        if (placingTemplate != null && existingTemplate == placingTemplate)
        {
          onlyBlockedByRemovablePaths = false;
          break;
        }

        if (PathDetector.IsRemovablePath(obj))
        {
          foundRemovablePath = true;
          continue;
        }

        if (!obj.Overridable)
        {
          if (obj.PositionedBlocks == null)
          {
            onlyBlockedByRemovablePaths = false;
            break;
          }
          else if (obj.PositionedBlocks.GetBlock(coordinates).Occupation.Intersects(occupations))
          {
            onlyBlockedByRemovablePaths = false;
            break;
          }
        }
      }

      if (foundRemovablePath && onlyBlockedByRemovablePaths)
      {
        __result = false;
        PlacementContext.OverriddenCoords[coordinates] = Time.frameCount;
      }
    }
  }

  [HarmonyPatch(typeof(BlockObject), "AddToService")]
  static class BlockObject_AddToService_Patch
  {
    static void Prefix(BlockObject __instance)
    {
      if (!PlacementContext.IsFunctional || __instance.IsPreview || __instance.AddedToService) return;

      if (__instance.PositionedBlocks == null) return;

      foreach (var block in __instance.PositionedBlocks.GetAllBlocks())
      {
        if (!PlacementContext.OverriddenCoords.TryGetValue(block.Coordinates, out int spoofedFrame)) continue;

        if (Time.frameCount - spoofedFrame > 30) continue;

        var objectsAtTile = __instance._blockService.GetObjectsAt(block.Coordinates);

        List<BlockObject> toDelete = new List<BlockObject>();

        foreach (var objAtTile in objectsAtTile)
        {
          if (PathDetector.IsExcludedObject(objAtTile)) continue;

          if (objAtTile != null && objAtTile.HasComponent<PathSpec>())
          {
            toDelete.Add(objAtTile);
          }
        }

        foreach (var path in toDelete)
        {
          if (path != null) __instance._entityService.Delete(path);
        }

        PlacementContext.OverriddenCoords.Remove(block.Coordinates);
      }
    }
  }
}
