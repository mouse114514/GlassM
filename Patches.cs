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
			Plugin.Log.LogInfo("CS: PlaceBody diag worldNull=" + (WorldGeneration.world == null) + " biome=" + ((WorldGeneration.world != null) ? WorldGeneration.world.biomeOverride.ToString() : "?") + " cam=" + (PlayerCamera.main != null) + " body=" + (PlayerCamera.main != null && PlayerCamera.main.body != null) + " spawn=" + (GameObject.Find("TUTORIALSPAWN") != null));
			if (ChunkStreamer.WB != null)
			{
				StringBuilder sb = new StringBuilder("CS: col512 top:");
				for (int y = 512; y > 460; y--)
				{
					sb.Append(ChunkStreamer.WB[512, y]).Append(',');
				}
				Plugin.Log.LogInfo(sb.ToString());
				int firstSolid = -1;
				int firstSolidAfterAir = -1;
				bool sawAir = false;
				for (int y = 512; y >= 380; y--)
				{
					if (ChunkStreamer.WB[512, y] == 0)
					{
						sawAir = true;
					}
					else
					{
						if (firstSolid < 0)
						{
							firstSolid = y;
						}
						if (sawAir)
						{
							firstSolidAfterAir = y;
							break;
						}
					}
				}
				Plugin.Log.LogInfo("CS: col512 firstSolid=" + firstSolid + " firstSolidAfterAir=" + firstSolidAfterAir + " (halfHeight=512, depth=" + (512 - firstSolidAfterAir) + "m)");
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogInfo("CS: PlaceBody diag err " + ex.Message);
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
