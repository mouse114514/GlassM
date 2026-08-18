using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GlassM;

[BepInPlugin("com.cu.glassm", "GlassM", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
	internal static ManualLogSource Log;

	private void Awake()
	{
		Log = Logger;
		Log.LogInfo("GlassM loading");
		try
		{
			Harmony harmony = new Harmony(Info.Metadata.GUID);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateTerrain", "WorldGenerateTerrain_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "UpdateWorld", "UpdateWorld_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "WorldPlaceEntities", "WorldPlaceEntities_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateStructures", "WorldGenerateStructures_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "Update", "Update_Postfix", false);
			Patch(harmony, typeof(WorldGeneration), "Clear", "Clear_Postfix", false);
			Patch(harmony, typeof(Body), "PlaceBody", "PlaceBody_Prefix", true);
			Log.LogInfo("GlassM: patches applied");
		}
		catch (Exception ex)
		{
			Log.LogError("GlassM: patch failed " + ex);
		}
	}

	private static void Patch(Harmony harmony, Type targetType, string target, string patchMethod, bool prefix)
	{
		MethodInfo methodInfo = AccessTools.Method(targetType, target);
		if (methodInfo == null)
		{
			Log.LogError("CS: target method not found: " + target);
			return;
		}
		HarmonyMethod hm = new HarmonyMethod(typeof(Patches).GetMethod(patchMethod, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
		if (prefix)
			harmony.Patch(methodInfo, prefix: hm);
		else
			harmony.Patch(methodInfo, postfix: hm);
		Log.LogInfo("CS: patched " + target);
	}
}
