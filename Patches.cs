using HarmonyLib;
using UnityEngine;

namespace GlassM;

// =====================================================================
// Patch:把原版 GenerateWorld 的全图生成替换为分区块流式生成。
// 原版流程 Terrain → Borders → UpdateWorld → PlacePlayer → PlaceEntities
// → Structures → Finish;其中全图循环由 ChunkStreamer 按区块接管。
// =====================================================================
public static class Patches
{
	// 地形生成:初始化噪声 + 同步生成中心 9x9 + 其余入后台队列
	[HarmonyPatch(typeof(WorldGeneration), "WorldGenerateTerrain")]
	[HarmonyPrefix]
	static bool WorldGenerateTerrain_Prefix(WorldGeneration __instance)
	{
		Plugin.Log.LogInfo("CS: WorldGenerateTerrain intercepted, biome=" + __instance.biomeDepth);
		ChunkStreamer.OnNewWorld(__instance);
		ChunkStreamer.InitTerrain(__instance);
		ChunkStreamer.GenerateInitial();
		Plugin.Log.LogInfo("CS: initial gen done, queue=" + ChunkStreamer.QueueCount);
		return false;
	}

	// 全图 tile 刷新:分块生成已直接渲染,跳过(中心区由区块渲染覆盖)
	[HarmonyPatch(typeof(WorldGeneration), "UpdateWorld")]
	[HarmonyPrefix]
	static bool UpdateWorld_Prefix()
	{
		Plugin.Log.LogInfo("CS: UpdateWorld skipped");
		return false;
	}

	// 实体:并入区块生成,全图阶段跳过
	[HarmonyPatch(typeof(WorldGeneration), "WorldPlaceEntities")]
	[HarmonyPrefix]
	static bool WorldPlaceEntities_Prefix()
	{
		Plugin.Log.LogInfo("CS: WorldPlaceEntities skipped");
		return false;
	}

	// 结构:并入区块生成,全图阶段跳过
	[HarmonyPatch(typeof(WorldGeneration), "WorldGenerateStructures")]
	[HarmonyPrefix]
	static bool WorldGenerateStructures_Prefix()
	{
		Plugin.Log.LogInfo("CS: WorldGenerateStructures skipped");
		return false;
	}

	// 每帧调度
	[HarmonyPatch(typeof(WorldGeneration), "Update")]
	[HarmonyPostfix]
	static void Update_Postfix()
	{
		ChunkStreamer.Tick();
	}

	// 层过渡/重开世界时清状态
	[HarmonyPatch(typeof(WorldGeneration), "Clear")]
	[HarmonyPostfix]
	static void Clear_Postfix()
	{
		ChunkStreamer.OnClear();
	}
}