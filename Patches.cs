using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlassM;

public static class Patches
{
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
		Plugin.Log.LogInfo((object)"CS: WorldGenerateStructures skipped");
		return false;
	}

	[HarmonyPatch(typeof(WorldGeneration), "Update")]
	[HarmonyPrefix]
	private static void Update_Prefix()
	{
		PerfDiag.WGBegin();
	}

	[HarmonyPatch(typeof(WorldGeneration), "Update")]
	[HarmonyPostfix]
	private static void Update_Postfix()
	{
		ChunkStreamer.Tick();
		PerfDiag.WGEnd();
	}

	[HarmonyPatch(typeof(FluidManager), "Update")]
	[HarmonyPrefix]
	private static void FluidUpdate_Prefix()
	{
		PerfDiag.FluidBegin();
	}

	[HarmonyPatch(typeof(FluidManager), "Update")]
	[HarmonyPostfix]
	private static void FluidUpdate_Postfix()
	{
		PerfDiag.FluidEnd();
	}

	[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
	[HarmonyPrefix]
	private static void FluidFixedUpdate_Prefix()
	{
		PerfDiag.FluidFixedBegin();
	}

	[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
	[HarmonyPostfix]
	private static void FluidFixedUpdate_Postfix()
	{
		PerfDiag.FluidFixedEnd();
	}

	[HarmonyPatch(typeof(Body), "Update")]
	[HarmonyPrefix]
	private static void BodyUpdate_Prefix()
	{
		PerfDiag.BodyBegin();
	}

	[HarmonyPatch(typeof(Body), "Update")]
	[HarmonyPostfix]
	private static void BodyUpdate_Postfix()
	{
		PerfDiag.BodyEnd();
	}

	[HarmonyPatch(typeof(Body), "PlaceBody")]
	[HarmonyPrefix]
	private static bool PlaceBody_Prefix(Body __instance)
	{
		try
		{
			Plugin.Log.LogInfo((object)("CS: PlaceBody diag worldNull=" + ((Object)(object)WorldGeneration.world == (Object)null) + " biome=" + (((Object)(object)WorldGeneration.world != (Object)null) ? WorldGeneration.world.biomeOverride.ToString() : "?") + " cam=" + ((Object)(object)PlayerCamera.main != (Object)null) + " body=" + ((Object)(object)PlayerCamera.main != (Object)null && (Object)(object)PlayerCamera.main.body != (Object)null) + " spawn=" + ((Object)(object)GameObject.Find("TUTORIALSPAWN") != (Object)null)));
			if (ChunkStreamer.WB != null)
			{
				StringBuilder stringBuilder = new StringBuilder("CS: col512 top:");
				for (int num = 512; num > 460; num--)
				{
					stringBuilder.Append(ChunkStreamer.WB[512, num]).Append(',');
				}
				Plugin.Log.LogInfo((object)stringBuilder.ToString());
				int num2 = -1;
				int num3 = -1;
				bool flag = false;
				for (int num4 = 512; num4 >= 380; num4--)
				{
					if (ChunkStreamer.WB[512, num4] == 0)
					{
						flag = true;
					}
					else
					{
						if (num2 < 0)
						{
							num2 = num4;
						}
						if (flag)
						{
							num3 = num4;
							break;
						}
					}
				}
				Plugin.Log.LogInfo((object)("CS: col512 firstSolid=" + num2 + " firstSolidAfterAir=" + num3 + " (halfHeight=512, depth=" + (512 - num3) + "m)"));
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogInfo((object)("CS: PlaceBody diag err " + ex.Message));
		}
		return true;
	}

	[HarmonyPatch(typeof(WorldGeneration), "Clear")]
	[HarmonyPostfix]
	private static void Clear_Postfix()
	{
		ChunkStreamer.OnClear();
	}
}

internal static class PerfDiag
{
	private static long wgStart;

	private static long fluidStart;

	private static long fluidFixedStart;

	private static long bodyStart;

	private static long wgTicks;

	private static long fluidTicks;

	private static long fluidFixedTicks;

	private static long bodyTicks;

	private static int frames;

	private static float nextLog;

	internal static void WGBegin()
	{
		wgStart = Stopwatch.GetTimestamp();
	}

	internal static void WGEnd()
	{
		wgTicks += Stopwatch.GetTimestamp() - wgStart;
		frames++;
		MaybeReport();
	}

	internal static void FluidBegin()
	{
		fluidStart = Stopwatch.GetTimestamp();
	}

	internal static void FluidEnd()
	{
		fluidTicks += Stopwatch.GetTimestamp() - fluidStart;
	}

	internal static void FluidFixedBegin()
	{
		fluidFixedStart = Stopwatch.GetTimestamp();
	}

	internal static void FluidFixedEnd()
	{
		fluidFixedTicks += Stopwatch.GetTimestamp() - fluidFixedStart;
	}

	internal static void BodyBegin()
	{
		bodyStart = Stopwatch.GetTimestamp();
	}

	internal static void BodyEnd()
	{
		bodyTicks += Stopwatch.GetTimestamp() - bodyStart;
	}

	private static void MaybeReport()
	{
		if (Time.unscaledTime < nextLog || frames <= 0)
		{
			return;
		}
		nextLog = Time.unscaledTime + 5f;
		double num = Stopwatch.Frequency / 1000.0;
		Plugin.Log.LogInfo((object)string.Format("CS: perf {0} frames | wg {1:F2}ms fluid {2:F2}ms fluidFixed {3:F2}ms body {4:F2}ms | fps {5:F1}", frames, (double)wgTicks / (double)frames / num, (double)fluidTicks / (double)frames / num, (double)fluidFixedTicks / (double)frames / num, (double)bodyTicks / (double)frames / num, (double)frames / 5.0));
		SpriteRenderer[] array = Object.FindObjectsOfType<SpriteRenderer>();
		int num2 = 0;
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i].enabled)
			{
				num2++;
			}
		}
		Plugin.Log.LogInfo((object)string.Format("CS: scene sprites={0} limbs={1} items={2}", num2, Object.FindObjectsOfType<Limb>().Length, Object.FindObjectsOfType<Item>().Length));
		wgTicks = 0L;
		fluidTicks = 0L;
		fluidFixedTicks = 0L;
		bodyTicks = 0L;
		frames = 0;
	}
}
