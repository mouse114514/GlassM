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

	internal static string ScreenToast;

	private float toastUntil;

	private bool showDiag = true;

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
			ScreenToast = "CSDIAG copied to clipboard: " + ChunkStreamer.DiagBufferCount + " lines, " + text.Length + " chars";
			toastUntil = Time.realtimeSinceStartup + 6f;
		}
		if (Input.GetKeyDown(KeyCode.F3))
		{
			showDiag = !showDiag;
			ScreenToast = "diag panel " + (showDiag ? "ON" : "OFF");
			toastUntil = Time.realtimeSinceStartup + 2f;
		}
	}

	private void OnGUI()
	{
		if (ScreenToast != null && Time.realtimeSinceStartup < toastUntil)
		{
			GUIStyle val = new GUIStyle(GUI.skin.label);
			val.fontSize = 16;
			val.normal.textColor = Color.white;
			val.wordWrap = true;
			GUI.Box(new Rect(12f, 12f, 520f, 70f), "");
			GUI.Label(new Rect(20f, 16f, 504f, 62f), ScreenToast, val);
		}
		if (showDiag)
		{
			string[] lines = ChunkStreamer.LastDiagLines(14);
			if (lines.Length > 0)
			{
				GUIStyle style = new GUIStyle(GUI.skin.label);
				style.fontSize = 12;
				style.normal.textColor = Color.yellow;
				GUI.Box(new Rect(12f, 90f, 620f, 12 + lines.Length * 16), "");
				for (int i = 0; i < lines.Length; i++)
				{
					GUI.Label(new Rect(20f, 96f + i * 16, 600f, 16), lines[i], style);
				}
			}
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