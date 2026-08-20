using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GlassM;

[BepInPlugin("com.cu.glassm", "GlassM", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
	internal static ManualLogSource Log;

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.B))
		{
			string text = ChunkStreamer.BuildDiagText();
			try
			{
				GUIUtility.systemCopyBuffer = text;
			}
			catch
			{
			}
			ChunkStreamer.LogToGameConsole("CSDIAG copied to clipboard: " + ChunkStreamer.DiagBufferCount + " lines, " + text.Length + " chars");
		}
	}

	private void Awake()
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Expected O, but got Unknown
		Log = Logger;
		Logger.LogInfo((object)"GlassM loading");
		try
		{
			Harmony harmony = new Harmony(Info.Metadata.GUID);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateTerrain", "WorldGenerateTerrain_Prefix", prefix: true);
			Patch(harmony, typeof(WorldGeneration), "UpdateWorld", "UpdateWorld_Prefix", prefix: true);
			Patch(harmony, typeof(WorldGeneration), "WorldPlaceEntities", "WorldPlaceEntities_Prefix", prefix: true);
			Patch(harmony, typeof(WorldGeneration), "WorldGenerateStructures", "WorldGenerateStructures_Prefix", prefix: true);
			Patch(harmony, typeof(WorldGeneration), "Update", "Update_Postfix", prefix: false);
			Patch(harmony, typeof(WorldGeneration), "Clear", "Clear_Postfix", prefix: false);
			Patch(harmony, typeof(WorldGeneration), "GenerateObjectAtPos", "GenerateObjectAtPos_Postfix", prefix: false);
			Patch(harmony, typeof(ConsoleScript), "RegisterAllCommands", "RegisterAllCommands_Postfix", prefix: false);
			Logger.LogInfo((object)"GlassM: patches applied");
		}
		catch (Exception ex)
		{
			Logger.LogError((object)("GlassM: patch failed " + ex));
		}
	}

	private static void Patch(Harmony harmony, Type targetType, string target, string patchMethod, bool prefix)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(targetType, target, (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			Log.LogError((object)("CS: target method not found: " + target));
			return;
		}
		HarmonyMethod val = new HarmonyMethod(typeof(Patches).GetMethod(patchMethod, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
		if (prefix)
		{
			harmony.Patch((MethodBase)methodInfo, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
		}
		else
		{
			harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
		}
		Log.LogInfo((object)("CS: patched " + target));
	}
}