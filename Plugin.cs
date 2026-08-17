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
			Patch(harmony, "WorldGenerateTerrain", "WorldGenerateTerrain_Prefix", true);
			Patch(harmony, "UpdateWorld", "UpdateWorld_Prefix", true);
			Patch(harmony, "WorldPlaceEntities", "WorldPlaceEntities_Prefix", true);
			Patch(harmony, "WorldGenerateStructures", "WorldGenerateStructures_Prefix", true);
			Patch(harmony, "Update", "Update_Postfix", false);
			Patch(harmony, "Clear", "Clear_Postfix", false);
			Logger.LogInfo("GlassM: patches applied");
		}
		catch (Exception ex)
		{
			Logger.LogError("GlassM: patch failed " + ex);
		}
	}

	static void Patch(Harmony harmony, string target, string patchMethod, bool prefix)
	{
		var method = AccessTools.Method(typeof(WorldGeneration), target);
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