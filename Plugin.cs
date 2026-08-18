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
		Logger.LogInfo("GlassM loading");
		try
		{
			var harmony = new Harmony(Info.Metadata.GUID);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateTerrain", "WorldGenerateTerrain_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "UpdateWorld", "UpdateWorld_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "WorldPlaceEntities", "WorldPlaceEntities_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateStructures", "WorldGenerateStructures_Prefix", true);
			Patch(harmony, typeof(WorldGeneration), "Update", "Update_Postfix", false);
			Patch(harmony, typeof(WorldGeneration), "Clear", "Clear_Postfix", false);
			Patch(harmony, typeof(Body), "PlaceBody", "PlaceBody_Prefix", true);
			Logger.LogInfo("GlassM: patches applied");
		}
		catch (Exception ex)
		{
			Logger.LogError("GlassM: patch failed " + ex);
		}
	}

	static void Patch(Harmony harmony, Type targetType, string target, string patchMethod, bool prefix)
	{
		var method = AccessTools.Method(targetType, target);
		if (method == null)
		{
			Log.LogError("CS: target method not found: " + target);
			return;
		}
		var pm = new HarmonyMethod(typeof(Patches).GetMethod(patchMethod, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
		if (prefix) harmony.Patch(method, prefix: pm);
		else harmony.Patch(method, postfix: pm);
		Log.LogInfo("CS: patched " + target);
	}
}