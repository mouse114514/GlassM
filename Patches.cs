using System;
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
	[HarmonyPostfix]
	private static void Update_Postfix()
	{
		ChunkStreamer.Tick();
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
