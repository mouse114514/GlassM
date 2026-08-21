using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GlassM;

public static class Patches
{
	public static readonly List<string> Report = new List<string>();
	[HarmonyPatch(typeof(WorldGeneration), "WorldGenerateTerrain")]
	[HarmonyPrefix]
	private static bool WorldGenerateTerrain_Prefix(WorldGeneration __instance)
	{
		Plugin.Log.LogInfo((object)("CS: WorldGenerateTerrain intercepted, biome=" + __instance.biomeDepth));
		ChunkStreamer.OnNewWorld(__instance);
		ChunkStreamer.InitTerrain(__instance);
		ChunkStreamer.GenerateInitial();
		Plugin.Log.LogInfo((object)("CS: initial gen done, queue=" + ChunkStreamer.QueueCount));
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "UpdateWorld")]
	[HarmonyPrefix]
	private static bool UpdateWorld_Prefix()
	{
		Plugin.Log.LogInfo((object)"CS: UpdateWorld skipped");
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "WorldPlaceEntities")]
	[HarmonyPrefix]
	private static bool WorldPlaceEntities_Prefix()
	{
		Plugin.Log.LogInfo((object)"CS: WorldPlaceEntities skipped");
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "WorldGenerateStructures")]
	[HarmonyPrefix]
	private static bool WorldGenerateStructures_Prefix()
	{
		return true;
	}

	[HarmonyPatch(typeof(WorldGeneration), "Update")]
	[HarmonyPostfix]
	private static void Update_Postfix()
	{
		ChunkStreamer.Tick();
	}

	[HarmonyPatch(typeof(WorldGeneration), "Clear")]
	[HarmonyPostfix]
	private static void Clear_Postfix()
	{
		ChunkStreamer.OnClear();
	}
}