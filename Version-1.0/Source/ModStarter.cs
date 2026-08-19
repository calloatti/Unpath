using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace Calloatti.Unpath
{
  public class UnpathPlugin : IModStarter
  {
    public void StartMod(IModEnvironment modEnvironment)
    {
      new Harmony("Calloatti.UnPath").PatchAll();
      Debug.Log("[UNPATH] Harmony patches applied successfully.");
    }
  }
}
