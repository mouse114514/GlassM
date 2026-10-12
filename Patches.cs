using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace GlassM;

public static class Patches
{
	public static readonly List<string> Report = new List<string>();

	private static float lastDarkenTime = float.MinValue;

	private static Func<bool> mpRunningCheck;

	private static bool mpCheckDone;

	private static bool IsMultiplayerRunning()
	{
		if (mpCheckDone)
		{
			return mpRunningCheck != null && mpRunningCheck();
		}
		mpCheckDone = true;
		try
		{
			var asm = AppDomain.CurrentDomain.GetAssemblies()
				.FirstOrDefault(a => a.GetName().Name == "KrokoshaCasualtiesMP");
			if (asm == null) return false;
			var t = asm.GetType("KrokoshaCasualtiesMP.KrokoshaScavMultiplayer");
			if (t == null) return false;
			var f = t.GetField("network_system_is_running",
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			if (f == null || f.FieldType != typeof(bool)) return false;
			mpRunningCheck = () => (bool)f.GetValue(null);
			return mpRunningCheck();
		}
		catch { return false; }
	}

	[HarmonyPatch(typeof(GlobalDark), "Darken")]
	[HarmonyPostfix]
	private static void Darken_Postfix()
	{
		lastDarkenTime = Time.realtimeSinceStartup;
	}

	[HarmonyPatch(typeof(GlobalDark), "IsDarkening")]
	[HarmonyPrefix]
	private static bool IsDarkening_Prefix(ref bool __result)
	{
		if ((UnityObject)(object)GlobalDark.main == (UnityObject)null)
		{
			__result = false;
			return false;
		}
		if (Time.realtimeSinceStartup - lastDarkenTime > 3f)
		{
			__result = false;
			return false;
		}
		return true;
	}
	[HarmonyPatch(typeof(WorldGeneration), "WorldGenerateTerrain")]
	[HarmonyPrefix]
	private static bool WorldGenerateTerrain_Prefix(WorldGeneration __instance, ref IEnumerator __result)
	{
		if ((int)__instance.biomeOverride > 0)
		{
			Plugin.Log.LogInfo((object)("CS: override scene " + (object)__instance.biomeOverride + ", vanilla pipeline"));
			ChunkStreamer.Active = false;
			ChunkStreamer.OnClear();
			Plugin.Log.LogInfo((object)("CS: override cleared, returning true, biomeOverride=" + (object)__instance.biomeOverride));
			return true;
		}
		Plugin.Log.LogInfo((object)("CS: normal mode biome=" + __instance.biomeDepth));
		try
		{
			Plugin.Log.LogInfo((object)"CS: calling OnNewWorld");
			ChunkStreamer.OnNewWorld(__instance);
			Plugin.Log.LogInfo((object)"CS: OnNewWorld done");
		}
		catch (Exception ex2)
		{
			Plugin.Log.LogError((object)("CS: OnNewWorld failed: " + ex2));
			__result = EmptyCoroutine();
			return false;
		}
		try
		{
			Plugin.Log.LogInfo((object)"CS: calling InitTerrain");
			ChunkStreamer.InitTerrain(__instance);
			Plugin.Log.LogInfo((object)"CS: InitTerrain done");
		}
		catch (Exception ex3)
		{
			Plugin.Log.LogError((object)("CS: InitTerrain failed: " + ex3));
			__result = EmptyCoroutine();
			return false;
		}
		try
		{
			Plugin.Log.LogInfo((object)"CS: calling GenerateInitial");
			ChunkStreamer.GenerateInitial();
			Plugin.Log.LogInfo((object)("CS: GenerateInitial done, queue=" + ChunkStreamer.QueueCount));
		}
		catch (Exception ex4)
		{
			Plugin.Log.LogError((object)("CS: GenerateInitial failed: " + ex4));
			__result = EmptyCoroutine();
			return false;
		}
		__result = EmptyCoroutine();
		return false;
	}

	private static IEnumerator EmptyCoroutine()
	{
		yield break;
	}

	[HarmonyPatch(typeof(WorldGeneration), "UpdateWorld")]
	[HarmonyPrefix]
	private static bool UpdateWorld_Prefix()
	{
		Plugin.Log.LogInfo((object)("CS: UpdateWorld called, Active=" + ChunkStreamer.Active));
		if (!ChunkStreamer.Active)
		{
			return true;
		}
		Plugin.Log.LogInfo((object)"CS: UpdateWorld skipped");
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "WorldPlaceEntities")]
	[HarmonyPrefix]
	private static bool WorldPlaceEntities_Prefix(ref IEnumerator __result)
	{
		Plugin.Log.LogInfo((object)("CS: WorldPlaceEntities called, Active=" + ChunkStreamer.Active));
		if (!ChunkStreamer.Active)
		{
			return true;
		}
		Plugin.Log.LogInfo((object)"CS: WorldPlaceEntities skipped");
		__result = EmptyCoroutine();
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

	[HarmonyPatch(typeof(Body), "PlaceBody")]
	[HarmonyPrefix]
	private static bool PlaceBody_Prefix(Body __instance)
	{
		if (!ChunkStreamer.Active)
		{
			return true;
		}
		if (IsMultiplayerRunning())
		{
			return true;
		}
		ChunkStreamer.PlaceBodyOverride(__instance);
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "Clear")]
	[HarmonyPostfix]
	private static void Clear_Postfix()
	{
		ChunkStreamer.OnClear();
	}
}