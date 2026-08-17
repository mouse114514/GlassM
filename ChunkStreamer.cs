using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace GlassM;

// =====================================================================
// 分区块流式生成:保持原版分层与地形生成逻辑(噪声配置/判定公式完全
// 照搬 WorldGenerateTerrain),仅把"全图一次性生成"改为按玩家位置
// 增量生成区块;远离的区块卸载 TilemapCollider2D(数据保留,行为一致)。
// =====================================================================
public static class ChunkStreamer
{
	public static readonly int CS = WorldGeneration.CHUNKSIZE;

	public static WorldGeneration W;
	public static bool Active;

	// private 字段反射(worldBlocks/chunks)
	static readonly FieldInfo f_worldBlocks = typeof(WorldGeneration).GetField("worldBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
	static readonly FieldInfo f_chunks = typeof(WorldGeneration).GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic);
	public static ushort[,] WB => (ushort[,])f_worldBlocks.GetValue(W);
	public static Tilemap[,] CH => (Tilemap[,])f_chunks.GetValue(W);

	// 地形噪声(按 biomeDepth 初始化,跨区块持续使用,保证与全量生成一致)
	static FastNoiseLite caveNoise;
	static FastNoiseLite dirtPerlin;
	static FastNoiseLite frequencyMap;
	static FastNoiseLite biomeMap;
	static FastNoiseLite toxicNoise;
	static FastNoiseLite biomeMap2;
	static FastNoiseLite marbleMap;
	static float minMarble;
	static int biome;

	// 区块状态
	public static readonly bool[,] genData = new bool[16, 16]; // worldBlocks 数据已生成
	static readonly bool[,] colliderOn = new bool[16, 16];     // collider 当前状态
	static readonly bool[,] inQueue = new bool[16, 16];        // 该块是否已在生成队列(避免 List.Contains O(n))
	static readonly List<Vector2Int> queue = new List<Vector2Int>();
	static readonly List<Vector2Int> pendingFull = new List<Vector2Int>(); // 初始只生成地形的块,待补实体/矿物/液体

	// 渲染复用数组(RenderChunk 每帧多次调用,避免反复分配)
	static readonly Vector3Int[] renderPos = new Vector3Int[CS * CS];
	static readonly TileBase[] renderTiles = new TileBase[CS * CS];

	// 结构资源缓存(Resources.Load 结果,避免每块重复加载)
	static readonly Dictionary<string, GameObject> structRes = new Dictionary<string, GameObject>();
	static GameObject GetStruct(string name)
	{
		if (!structRes.TryGetValue(name, out var go))
		{
			go = Resources.Load<GameObject>(name);
			structRes[name] = go;
		}
		return go;
	}
	static GameObject GetStructObj(string name)
	{
		if (!structRes.TryGetValue(name, out var go))
		{
			go = (GameObject)Resources.Load(name);
			structRes[name] = go;
		}
		return go;
	}

	// 调度参数
	public const int GEN_RADIUS = 5;     // 生成半径(区块),11x11
	public const int UNLOAD_RADIUS = 7;  // 卸载半径(区块),超出关闭 collider
	public const int INIT_RADIUS = 1;    // 初始同步生成半径,3x3
	public const int MAX_PER_FRAME = 4;  // 每帧后台生成区块数上限

	public static Vector2Int PlayerChunk = new Vector2Int(8, 8);
	static Vector2Int lastScanChunk = new Vector2Int(int.MinValue, int.MinValue); // 上次卸载扫描的玩家块
	public static int QueueCount => queue.Count;
	static float nextLogTime;

	// ===== 状态管理 =====
	public static void OnNewWorld(WorldGeneration w)
	{
		W = w;
		Active = true;
		Array.Clear(genData, 0, genData.Length);
		Array.Clear(colliderOn, 0, colliderOn.Length);
		Array.Clear(inQueue, 0, inQueue.Length);
		queue.Clear();
	}

	public static void OnClear()
	{
		Array.Clear(genData, 0, genData.Length);
		Array.Clear(colliderOn, 0, colliderOn.Length);
		Array.Clear(inQueue, 0, inQueue.Length);
		queue.Clear();
		pendingFull.Clear();
	}

	static bool InWorld(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < 16 && c.y < 16;

	// ===== 地形噪声初始化(照搬原版 WorldGenerateTerrain) =====
	public static void InitTerrain(WorldGeneration w)
	{
		W = w;
		biome = w.biomeDepth;
		if (biome <= 1)
		{
			caveNoise = NewNoise();
			caveNoise.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
			caveNoise.SetFrequency(0.06f);
			caveNoise.SetFractalOctaves(3);
			caveNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
			caveNoise.SetFractalLacunarity(1.5f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
			dirtPerlin.SetFractalType(FastNoiseLite.FractalType.FBm);
			dirtPerlin.SetFractalOctaves(7);
			dirtPerlin.SetFrequency(0.035f);
			frequencyMap = NewNoise();
			frequencyMap.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
			frequencyMap.SetFrequency(0.00037f);
			biomeMap = NewNoise();
			biomeMap.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
			biomeMap.SetFrequency(0.04f);
			biomeMap.SetCellularDistanceFunction(FastNoiseLite.CellularDistanceFunction.EuclideanSq);
			biomeMap.SetCellularReturnType(FastNoiseLite.CellularReturnType.Distance);
			biomeMap.SetCellularJitter(1f);
			biomeMap.SetFractalType(FastNoiseLite.FractalType.Ridged);
			biomeMap.SetFractalLacunarity(1.5f);
		}
		else if (biome == 2 || biome == 3)
		{
			biomeMap = NewNoise();
			biomeMap.SetNoiseType(FastNoiseLite.NoiseType.Value);
			biomeMap.SetFrequency(0.086f);
			biomeMap.SetFractalType(FastNoiseLite.FractalType.FBm);
			biomeMap.SetFractalOctaves(biome == 2 ? 2 : 3);
			biomeMap.SetFractalGain(0.49f);
			biomeMap.SetFractalWeightedStrength(2.34f);
			biomeMap.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
			biomeMap.SetDomainWarpAmp(22f);
			frequencyMap = NewNoise();
			frequencyMap.SetFrequency(0.006f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
			dirtPerlin.SetFrequency(0.02f);
			dirtPerlin.SetFractalType(FastNoiseLite.FractalType.Ridged);
			dirtPerlin.SetFractalGain(0.65f);
			caveNoise = NewNoise();
			caveNoise.SetFrequency(0.005f);
			caveNoise.SetFractalType(FastNoiseLite.FractalType.PingPong);
			caveNoise.SetFractalGain(0.35f);
			caveNoise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
			caveNoise.SetDomainWarpAmp(40f);
			toxicNoise = NewNoise();
			toxicNoise.SetFrequency(0.012f);
			toxicNoise.SetFractalType(FastNoiseLite.FractalType.PingPong);
			toxicNoise.SetFractalGain(0.3f);
			toxicNoise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
			toxicNoise.SetDomainWarpAmp(50f);
			biomeMap2 = NewNoise();
			biomeMap2.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
			biomeMap2.SetFrequency(0.05f);
			biomeMap2.SetCellularDistanceFunction(FastNoiseLite.CellularDistanceFunction.EuclideanSq);
			biomeMap2.SetCellularReturnType(FastNoiseLite.CellularReturnType.Distance);
			biomeMap2.SetCellularJitter(1f);
			biomeMap2.SetFractalType(FastNoiseLite.FractalType.Ridged);
			biomeMap2.SetFractalLacunarity(1.5f);
			marbleMap = NewNoise();
			marbleMap.SetFrequency(biome == 2 ? 0.007f : 0.035f);
			marbleMap.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
			marbleMap.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
			marbleMap.SetDomainWarpAmp(100f);
			minMarble = biome == 2 ? 0.45f : 1f;
		}
		else
		{
			marbleMap = NewNoise();
			marbleMap.SetNoiseType(FastNoiseLite.NoiseType.Value);
			marbleMap.SetFractalType(FastNoiseLite.FractalType.Ridged);
			marbleMap.SetFractalOctaves(3);
			marbleMap.SetFractalLacunarity(2.29f);
			marbleMap.SetFractalGain(4f);
			marbleMap.SetFractalWeightedStrength(1.2f);
			marbleMap.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
			marbleMap.SetDomainWarpAmp(41f);
			biomeMap2 = NewNoise();
			biomeMap2.SetFrequency(0.02f);
			biomeMap2.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
			biomeMap2.SetDomainWarpAmp(25f);
		}
	}

	static FastNoiseLite NewNoise() => new FastNoiseLite(UnityEngine.Random.Range(0, int.MaxValue));

	public static readonly bool StreamOn = true; // 调试:false 时只生成初始区块,不流式扩展

		// ===== 初始生成:同步生成玩家周围区块 + 其余入队 =====
	public static void GenerateInitial()
	{
		Vector2Int center = new Vector2Int((int)(W.width / 2 / CS), (int)(W.height / 2 / CS));
		PlayerChunk = center;
		EnqueueAround(center, INIT_RADIUS, true);
		GenSpawnCavity();
		if (StreamOn)
			EnqueueAround(center, GEN_RADIUS, false);
	}

	// 出生腔:原版出生点 0~15 米(世界顶部 y≈505 附近),PlaceBody 从顶部
	// 往下扫"上空下实"。分区块生成时顶部尚未生成会被当成空气,出生点
	// 会掉到已生成区域的顶部。这里在顶部中心挖出腔体(仿原版出生区):
	// 腔体 x∈[508,516] y∈[1012,1022] 空气,腔底 y=1011 实心,顶部开口。
	static void GenSpawnCavity()
	{
		if (W == null || WB == null) return;
		int cx0 = 508 / CS, cx1 = 516 / CS; // 块 7,8
		int cy = 1011 / CS;                 // 块 15
		for (int cx = cx0; cx <= cx1; cx++)
		{
			if (!genData[cx, cy])
			{
				GenChunk(new Vector2Int(cx, cy), false);
				pendingFull.Add(new Vector2Int(cx, cy));
			}
		}
		for (int x = 508; x <= 516; x++)
		{
			for (int y = 1012; y <= 1022; y++)
				WB[x, y] = 0;
			WB[x, 1011] = 1;
			WB[x, 1023] = 0;
		}
		for (int x = 506; x <= 518; x++)
		{
			WB[x, 1023] = 0;
		}
		for (int cx = cx0; cx <= cx1; cx++)
			RenderChunk(new Vector2Int(cx, cy));
		Plugin.Log.LogInfo("CS: spawn cavity done, cols=" + cx0 + "-" + cx1 + " row=" + cy);
	}

	static void EnqueueAround(Vector2Int c, int radius, bool genNow)
	{
		for (int x = c.x - radius; x <= c.x + radius; x++)
		{
			for (int y = c.y - radius; y <= c.y + radius; y++)
			{
				Vector2Int cc = new Vector2Int(x, y);
				if (!InWorld(cc) || genData[cc.x, cc.y]) continue;
				if (genNow) GenChunk(cc, false);
				else { queue.Add(cc); inQueue[cc.x, cc.y] = true; }
			}
		}
	}

	// ===== 每帧调度 =====
	public static void Tick()
	{
		if (!Active || W == null || WB == null || CH == null) return;
		Vector2Int pc = PlayerChunk;
		try
		{
			Vector3 pos = WorldGeneration.world != null && PlayerCamera.main != null && PlayerCamera.main.body != null
				? PlayerCamera.main.body.transform.position : Vector3.zero;
			pc = new Vector2Int(Mathf.Clamp((int)(pos.x + 512f) / CS, 0, 15), Mathf.Clamp((int)(pos.y + 512f) / CS, 0, 15));
			PlayerChunk = pc;
		}
		catch { }
		// 新进入范围的区块入队
		bool added = false;
		if (StreamOn)
		for (int x = pc.x - GEN_RADIUS; x <= pc.x + GEN_RADIUS; x++)
		{
			for (int y = pc.y - GEN_RADIUS; y <= pc.y + GEN_RADIUS; y++)
			{
				if (x < 0 || y < 0 || x > 15 || y > 15) continue;
				if (!genData[x, y] && !inQueue[x, y])
				{
					queue.Add(new Vector2Int(x, y));
					inQueue[x, y] = true;
					added = true;
				}
			}
		}
		// 优先生成最近的(仅当有新块入队时才排序)
		if (queue.Count > 0)
		{
			if (added) queue.Sort((a, b) => Dist2(a, pc).CompareTo(Dist2(b, pc)));
			int n = Mathf.Min(MAX_PER_FRAME, queue.Count);
			for (int i = 0; i < n; i++)
			{
				GenChunk(queue[0]);
				queue.RemoveAt(0);
			}
		}
		// 补全初始块(实体/矿物/液体)
		if (pendingFull.Count > 0)
		{
			int n = Mathf.Min(MAX_PER_FRAME, pendingFull.Count);
			for (int i = 0; i < n; i++)
			{
				Vector2Int c = pendingFull[0];
				pendingFull.RemoveAt(0);
				try
				{
					GenChunkOres(c);
					GenChunkLiquids(c);
					GenChunkEntities(c);
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("chunk full gen failed " + c + ": " + e);
				}
			}
		}
		// 卸载:超出卸载半径的区块关闭 collider;回到半径内恢复。
		// 仅玩家块变化时才扫描(静止时距离关系不变,结果必相同)
		int unloadCount = 0;
		if (pc != lastScanChunk)
		{
			lastScanChunk = pc;
			for (int x = 0; x < 16; x++)
			{
				for (int y = 0; y < 16; y++)
				{
					if (!genData[x, y]) continue;
					bool far = Dist2(new Vector2Int(x, y), pc) > UNLOAD_RADIUS * UNLOAD_RADIUS;
					if (far == colliderOn[x, y])
					{
						Tilemap tm = CH[x, y];
						if (tm == null) continue;
						var col = tm.GetComponent<TilemapCollider2D>();
						if (col == null) continue;
						col.enabled = !far;
						colliderOn[x, y] = !far;
						unloadCount++;
					}
				}
			}
		}
		if (Time.unscaledTime > nextLogTime)
		{
			nextLogTime = Time.unscaledTime + 5f;
			int genCount = 0;
			for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) if (genData[x, y]) genCount++;
			Plugin.Log.LogInfo("CS: generated " + genCount + "/256, queue " + queue.Count + ", collider toggles " + unloadCount);
		}
	}

	static int Dist2(Vector2Int a, Vector2Int b)
	{
		int dx = a.x - b.x, dy = a.y - b.y;
		return dx * dx + dy * dy;
	}

	// ===== 区块生成:地形 + 矿物 + 液体 + 实体 =====
	public static void GenChunk(Vector2Int c) => GenChunk(c, true);

	static void GenChunk(Vector2Int c, bool full)
	{
		if (genData[c.x, c.y]) return;
		genData[c.x, c.y] = true;
		inQueue[c.x, c.y] = false;
		try
		{
			GenChunkTerrain(c);
			if (full)
			{
				GenChunkOres(c);
				GenChunkLiquids(c);
				GenChunkEntities(c);
			}
			else
			{
				pendingFull.Add(c);
			}
			RenderChunk(c);
			GenChunkStructures(c);
			RefreshAround(c);
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning("chunk gen failed " + c + ": " + e);
		}
	}

static void RenderChunk(Vector2Int c)
	{
		Tilemap tm = CH[c.x, c.y];
		if (tm == null) return;
		int bx = c.x * CS, by = c.y * CS;
		int n = CS * CS;
		int half = W.HALFCHUNKSIZE;
		for (int i = 0; i < CS; i++)
		{
			for (int j = 0; j < CS; j++)
			{
				int idx = j * CS + i;
				renderPos[idx] = new Vector3Int(i - half, j - half, 0);
				renderTiles[idx] = W.tiles[WB[bx + i, by + j]];
			}
		}
		tm.SetTiles(renderPos, renderTiles);
	}

	// ===== 结构生成(照搬原版 WorldGenerateStructures/GenerateDropCapsules/
	// GenerateCollapsedPods/GenerateLifePods,按区块归属) =====
	// 原版:全图循环次数 = chunkWidth*chunkHeight*amt*rarity = 256*amt*rarity
	// 每块期望 λ = amt*rarity;用 while(Random.value<λ) 掷次数(几何分布,期望=λ)
	static int Poisson(float lambda)
	{
		int k = 0;
		while (UnityEngine.Random.value < lambda) k++;
		return k;
	}

	// 从世界坐标 pos 向下扫找第一个实心格(替代原版 Physics2D.Raycast,
	// 因为分块生成时 collider 未更新)。原版 raycast 命中点落在实心格表面,
	// WorldToBlockPos 取整后即实心格本身(结构"嵌"在石头里,与观感一致)
	static bool GroundAbove(Vector2 pos, out Vector2Int ground, float maxDist = 400f)
	{
		Vector2Int p = W.WorldToBlockPos(pos);
		for (int y = p.y; y >= 0 && p.y - y < maxDist; y--)
		{
			if (WB[p.x, y] > 0)
			{
				ground = new Vector2Int(p.x, y);
				return true;
			}
		}
		ground = p;
		return false;
	}

	// 块内随机世界坐标,偏向块中心(原版为全图随机;限制在中心 ±22 格内使
	// 半径 ≤20 的结构不跨块边界,避免分块生成时结构边缘被邻块地形覆盖)
	static Vector2 RandPosInChunk(Vector2Int c)
	{
		return new Vector2(c.x * CS - W.halfWidth + UnityEngine.Random.Range(10, CS - 10),
			c.y * CS - W.halfHeight + UnityEngine.Random.Range(10, CS - 10));
	}

	// 结构写入后刷新本块+已生成邻块(结构只写 worldBlocks,靠刷新显示)
	static void RefreshAround(Vector2Int c)
	{
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				int x = c.x + dx, y = c.y + dy;
				if (x < 0 || y < 0 || x > 15 || y > 15) continue;
				if (!genData[x, y]) continue;
				RenderChunk(new Vector2Int(x, y));
			}
		}
	}

	// 照搬原版 WorldGenerateStructures(按 biomeDepth 分层;原版结构密度与块无关)
	static void GenChunkStructures(Vector2Int c)
	{
		if (W.biomeOverride != WorldGeneration.OverrideSceneType.None) return;
		int d = W.biomeDepth;
		if (d > 4) return;
		try
		{
			float lrm = W.totalLootRarity;
			int n;
			// case1:空投舱+塌陷舱(所有层,密度按层,无 rarity 乘数)
			n = Poisson(UnityEngine.Random.Range(0.12f, 0.13f));
			for (int i = 0; i < n; i++) DropCapsuleAt(c);
			float cap;
			if (d == 2) cap = UnityEngine.Random.Range(0.066f, 0.077f);
			else if (d == 3) cap = UnityEngine.Random.Range(0.066f, 0.077f) * 2.5f;
			else if (d == 4) cap = UnityEngine.Random.Range(0.066f, 0.077f);
			else cap = UnityEngine.Random.Range(0.055f, 0.066f);
			n = Poisson(cap);
			for (int i = 0; i < n; i++) CollapsedPodAt(c);
			if (d <= 1)
			{
				// case2:depth>0 才有 BioContainer/SteelBridge(SteelBridge 无 raycast,位置=随机点)
				if (d > 0)
				{
					BioContainerAt(c, 0.05f, 0.07f, 1f);
					BridgeAt(c, 0.09f, 0.12f, "Structures/SteelBridge", 0.85f, raycast: false);
				}
				PodAt(c, 0.06f, 0.08f, "Structures/CratePod", 0.82f);
				PodAt(c, 0.06f, 0.08f, "Structures/MiniPod", 0.88f);
				PodAt(c, 0.045f, 0.05f, "Structures/SteelThing", 0.9f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodCross", 0.94f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodHorizontal", 0.94f, entity: false);
			}
			else if (d == 2 || d == 3)
			{
				// case3
				BioContainerAt(c, 0.05f, 0.07f, 1f);
				PodAt(c, 0.04f, 0.05f, "Structures/MedicalBuilding", 0.98f);
				BridgeAt(c, 0.09f, 0.12f, "Structures/SteelBridge", 0.95f, raycast: false);
				PodAt(c, 0.06f, 0.08f, "Structures/MiniPod", 0.88f);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodCross", 0.94f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodHorizontal", 0.94f, entity: false);
			}
			else
			{
				// case4:大树(raycast 距离=CHUNKSIZE=64,无 rarity)
				n = Poisson(UnityEngine.Random.Range(0.9f, 1.1f));
				for (int i = 0; i < n; i++)
				{
					Vector2Int p;
					if (GroundAbove(RandPosInChunk(c), out p, 64f))
						W.GenerateTree(p);
				}
				PodAt(c, 0.06f, 0.08f, "Structures/CratePod", 0.82f);
				PodAt(c, 0.06f, 0.08f, "Structures/MiniPod", 0.88f);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodCross", 0.95f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodHorizontal", 0.95f, entity: false);
				PodAt(c, 0.04f, 0.05f, "Structures/BrickLoot", 0.925f);
				BioContainerAt(c, 0.03f, 0.04f, 0.975f);
				IronVein(c, 1);
				IronVein(c, 2);
			}
			// 生命舱(所有层,无 rarity)
			n = Poisson(UnityEngine.Random.Range(0.088f, 0.1f));
			for (int i = 0; i < n; i++) LifePodAt(c);
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning("chunk structures failed " + c + ": " + e);
		}
	}

	static void DropCapsuleAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		Vector2Int p;
		if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
		((GameObject)UnityEngine.Object.Instantiate(GetStructObj("dropcapsule"), pos,
			Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f)))).GetComponent<AudioSource>().pitch = UnityEngine.Random.Range(0.9f, 1.1f);
		W.GenerateBlockCircle(pos, 32, 3, 0.7f, 0f);
		W.GenerateBlockCircle(pos, 30, 6, 0.04f, 0.04f);
		W.GenerateBlockCircle(pos, 4, 0, 1f, 0.9f);
	}

	static void CollapsedPodAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		Vector2Int p;
		if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
		Vector2Int vp = W.WorldToBlockPos(pos);
		CraterAt(pos, vp);
		W.GenerateObjectAtPos(vp, GetStruct("LifepodCollapsed").transform.GetChild(0).GetComponent<Tilemap>(), 0.88f, true);
		if (UnityEngine.Random.value < 0.9f)
		{
			AmmoScript component = ((GameObject)UnityEngine.Object.Instantiate(GetStructObj(Utils.PickRandom(W.spawnableMagazines)), pos,
				Quaternion.Euler(0f, 0f, UnityEngine.Random.value * 360f))).GetComponent<AmmoScript>();
			component.rounds = Mathf.RoundToInt(component.maxRounds * UnityEngine.Random.value);
		}
		for (int l = 0; l < 3; l++)
		{
			if (UnityEngine.Random.Range(0f, 1f) < 0.3f)
				UnityEngine.Object.Instantiate(GetStructObj("experimentflesh"), pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f), Quaternion.identity);
		}
		if (UnityEngine.Random.Range(0f, 1f) < 0.8f)
			UnityEngine.Object.Instantiate(GetStructObj("internalorgans"), pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f), Quaternion.identity);
	}

	static void LifePodAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		Vector2Int p;
		if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
		Vector2Int vp = W.WorldToBlockPos(pos);
		CraterAt(pos, vp);
		W.GenerateObjectAtPos(vp, GetStruct("Lifepod").transform.GetChild(0).GetComponent<Tilemap>(), 0.95f, true);
		W.GenerateEntityAtPos(W.BlockToWorldPos(vp), GetStruct("Lifepod"));
		if (UnityEngine.Random.value < WorldGeneration.GetRunSettingFloat("traderchance") * 0.01f)
		{
			int num3 = UnityEngine.Random.Range(-4, 4);
			TraderScript component = ((GameObject)UnityEngine.Object.Instantiate(GetStructObj("trader" + UnityEngine.Random.Range(1, 4)),
				W.BlockToWorldPos(vp + Vector2Int.down * 7 + Vector2Int.right * num3) - Vector2.one * 0.5f, Quaternion.identity)).GetComponent<TraderScript>();
			if (Mathf.Abs(num3) > 1.5f) component.farEnoughToMove = true;
			component.MoveRange = new RangeF(W.BlockToWorldPos(vp - Vector2Int.right * 5).x, W.BlockToWorldPos(vp + Vector2Int.right * 5).x);
		}
		else
		{
			UnityEngine.Object.Instantiate(GetStructObj("lifepodchest"), W.BlockToWorldPos(vp + Vector2Int.down * 6) - Vector2.one * 0.5f, Quaternion.identity);
		}
		for (int l = 0; l < 3; l++)
		{
			if (UnityEngine.Random.Range(0f, 1f) < 0.05f)
				UnityEngine.Object.Instantiate(GetStructObj("experimentflesh"), pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f), Quaternion.identity);
		}
		if (UnityEngine.Random.Range(0f, 1f) < 0.05f)
			UnityEngine.Object.Instantiate(GetStructObj("internalorgans"), pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f), Quaternion.identity);
		if (UnityEngine.Random.Range(0f, 1f) < 0.5f)
			UnityEngine.Object.Instantiate(GetStructObj("LoreNote"), pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f) + Vector2.up * UnityEngine.Random.Range(-1f, -6f), Quaternion.identity);
		if (UnityEngine.Random.Range(0f, 1f) < 0.285f)
			Utils.Create("epda", pos + Vector2.right * UnityEngine.Random.Range(-3f, 3f), UnityEngine.Random.value * 360f);
		if (UnityEngine.Random.value < 0.2f)
		{
			Vector2 pos2 = pos + Vector2.right * UnityEngine.Random.Range(-1.5f, 1.5f);
			Utils.Create("Special/defibrack", pos2, 0f);
			bool num4 = UnityEngine.Random.value < 0.5f;
			float value = UnityEngine.Random.value;
			GameObject go = null;
			if (UnityEngine.Random.value < 0.75f)
			{
				go = Utils.Create("manualdefibrillator", pos2, 0f);
				go.AddComponent<ItemLock>();
			}
			else
			{
				go = Utils.Create("aed", pos2, 0f);
				go.AddComponent<ItemLock>();
			}
			if (!num4) go.GetComponent<Item>().battery.UnloadBattery(true);
			else go.GetComponent<Item>().condition = value;
		}
	}

	// 塌陷坑:中心 90x90 范围内把实心块随机打碎(照搬原版)
	static void CraterAt(Vector2 pos, Vector2Int vp)
	{
		for (int j = 0; j < 90; j++)
		{
			for (int k = 0; k < 90; k++)
			{
				float num2 = Vector2.Distance(pos + Vector2.up * k + Vector2.right * j - Vector2.one * 45f, pos);
				if (num2 < 45f * UnityEngine.Random.Range(0f, 12f / (num2 * 0.8f)) && UnityEngine.Random.Range(0f, 1f) < 0.7f)
				{
					Vector2Int v2 = new Vector2Int(Mathf.Clamp(vp.x - 45 + j, 0, (int)(W.width - 1)), Mathf.Clamp(vp.y - 45 + k + 2, 0, (int)(W.height - 1)));
					if (WB[v2.x, v2.y] > 0)
						WB[v2.x, v2.y] = (ushort)UnityEngine.Random.Range(0, 5);
				}
			}
		}
	}

	static void BioContainerAt(Vector2Int c, float lo, float hi, float chance)
	{
		int n = Poisson(UnityEngine.Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			Vector2Int p;
			if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
			W.GenerateBlockCircle(pos, 16, 3, 0.8f, 0f);
			W.GenerateBlockCircle(pos, 20, 4, 0.3f, 0f);
			W.GenerateBlockCircle(pos, 16, 0, 0.15f, 0f);
			W.GenerateObjectAtPos(p, GetStruct("BioContainer").transform.GetChild(0).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(W.BlockToWorldPos(p), GetStruct("BioContainer"));
		}
	}

	// raycast=false 时位置=随机点本身(原版 SteelBridge 无 raycast)
	static void BridgeAt(Vector2Int c, float lo, float hi, string res, float chance, bool raycast = true)
	{
		int n = Poisson(UnityEngine.Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			if (raycast)
			{
				Vector2Int p;
				if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
			}
			W.GenerateObjectAtPos(W.WorldToBlockPos(pos), GetStruct(res).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(pos, GetStruct(res));
		}
	}

	static void PodAt(Vector2Int c, float lo, float hi, string res, float chance, bool entity = true)
	{
		int n = Poisson(UnityEngine.Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			Vector2Int p;
			if (GroundAbove(pos, out p)) pos = W.BlockToWorldPos(p);
			W.GenerateBlockCircle(pos, 16, 3, 0.5f, 0f);
			W.GenerateBlockCircle(pos, 20, 4, 0.2f, 0f);
			W.GenerateObjectAtPos(p, GetStruct(res).GetComponent<Tilemap>(), chance, true);
			if (entity) W.GenerateEntityAtPos(W.BlockToWorldPos(p), GetStruct(res));
		}
	}

	static void IronVein(Vector2Int c, int width)
	{
		Vector2Int v = new Vector2Int(UnityEngine.Random.Range(c.x * CS, c.x * CS + CS), UnityEngine.Random.Range(c.y * CS, c.y * CS + CS));
		int len = UnityEngine.Random.Range(1, 5);
		for (int a = 0; a < len; a++)
		{
			for (int b = 0; b < width; b++)
			{
				if (v.x + a < W.width && v.y + b < W.height)
					WB[v.x + a, v.y + b] = 5;
			}
		}
	}

	// ===== 地形(照搬原版 WorldGenerateTerrain 循环,范围=区块) =====
	static void GenChunkTerrain(Vector2Int c)
	{
		int bx = c.x * CS, by = c.y * CS;
		ushort[,] wb = WB;
		if (biome <= 1)
		{
			for (int x = bx; x < bx + CS; x++)
			{
				for (int y = by; y < by + CS; y++)
				{
					caveNoise.SetFrequency(0.06f + frequencyMap.GetNoise(x, y) * 0.01f);
					ushort block = (ushort)(caveNoise.GetNoise(x, y) > -0.715f ? 1 : 0);
					float noise = dirtPerlin.GetNoise(x, y);
					if (block > 0 && noise < -0.1f)
						block = (ushort)(noise < -0.33f ? 16 : 2);
					if (block > 0 && UnityEngine.Random.Range(0f, 1f) > 0.99f)
						block = (ushort)UnityEngine.Random.Range(1, 5);
					if (biomeMap.GetNoise(x, y) > 0.1f)
						block = (ushort)UnityEngine.Random.Range(3, 5);
					if (block > 0 && biomeMap.GetNoise(x, y) < -0.8f)
						block = 15;
					if (biome == 1 && y < W.height * 0.5f)
					{
						float num3 = y / (float)W.height * 2f;
						if (UnityEngine.Random.Range(0f, 1f) > num3 && block == 2)
							block = 12;
						if (y < W.height * 0.33f && UnityEngine.Random.Range(0f, 1f) > num3 * 3f && block == 1)
							block = 13;
					}
					wb[x, y] = block;
				}
			}
		}
		else if (biome == 2 || biome == 3)
		{
			for (int x = bx; x < bx + CS; x++)
			{
				for (int y = by; y < by + CS; y++)
				{
					float noise2 = biomeMap.GetNoise(x, y);
					float num16 = frequencyMap.GetNoise(x, y) * 0.25f + 0.1f;
					if (marbleMap.GetNoise(x, y) <= minMarble)
					{
						ushort block = (ushort)((noise2 > num16 && dirtPerlin.GetNoise(x, y) < -0.4f)
							? (noise2 < num16 + 0.1f ? 12 : 13) : 0);
						if (caveNoise.GetNoise(x, y) > 0.65f)
							block = 17;
						if (noise2 > 0.75f)
							block = 15;
						if (biomeMap2.GetNoise(x, y) > 0.1f)
							block = (ushort)UnityEngine.Random.Range(3, 5);
						if (biome == 3 && block > 0 && UnityEngine.Random.value < 0.1f)
							block = (ushort)(15 + UnityEngine.Random.Range(0, 2));
						wb[x, y] = block;
					}
					else
					{
						wb[x, y] = (ushort)((noise2 > num16) ? (dirtPerlin.GetNoise(x, y) < -0.1f ? 18 : 19) : 0);
					}
					if (biome == 3 && toxicNoise.GetNoise(x, y) < -0.8f && UnityEngine.Random.value > 0.5f)
						wb[x, y] = 22;
					if (biome == 3 && wb[x, y] > 0 && UnityEngine.Random.value > (y + W.halfHeight) / (float)W.height)
						wb[x, y] = 23;
				}
			}
		}
		else
		{
			for (int x = bx; x < bx + CS; x++)
			{
				for (int y = by; y < by + CS; y++)
				{
					marbleMap.SetFrequency(0.0189f - y / (float)W.height * 0.002f);
					float num31 = marbleMap.GetNoise(x, y) + UnityEngine.Random.Range(-0.1f, 0.1f);
					ushort block;
					if (num31 > 0.15f && num31 < 0.25f) block = 23;
					else if (num31 >= 0.25f && num31 < 0.45f) block = 16;
					else if (num31 >= 0.45f && num31 < 0.66f) block = 15;
					else if (num31 >= 0.66f) block = 19;
					else block = 0;
					if (biomeMap2.GetNoise(x, y) < -0.735f)
						block = 0;
					wb[x, y] = block;
				}
			}
		}
	}

	// ===== 矿物(照搬 GenerateOres,按区块换算密度) =====
	static void GenChunkOres(Vector2Int c)
	{
		ushort[,] wb = WB;
		if (biome == 4)
		{
			for (int i = 0; i < 4; i++)
			{
				if (UnityEngine.Random.value < 0.00025f)
				{
					int x = c.x * CS + UnityEngine.Random.Range(0, CS);
					int y = c.y * CS + UnityEngine.Random.Range(0, CS);
					if (wb[x, y] > 0) wb[x, y] = 35;
				}
			}
			return;
		}
		if (UnityEngine.Random.value >= 0.5f) return;
		int ox = c.x * CS + UnityEngine.Random.Range(0, CS);
		int oy = c.y * CS + UnityEngine.Random.Range(0, CS);
		if (wb[ox, oy] == 0) return;
		for (int s = UnityEngine.Random.Range(1, 26); s > 0; s--)
		{
			if (wb[ox, oy] > 0) wb[ox, oy] = 34;
			ox += UnityEngine.Random.value > 0.5f ? (UnityEngine.Random.value > 0.5f ? 1 : -1) : 0;
			oy += UnityEngine.Random.value > 0.5f ? (UnityEngine.Random.value > 0.5f ? 1 : -1) : 0;
			if (ox < 0 || oy < 0 || ox >= W.width || oy >= W.height) break;
		}
		// 块状矿脉(照搬原版地形循环 1811-1881,按 biomeDepth 分支:
		// biomeDepth>0 银矿(5),biomeDepth==0 铜矿(11))
		if (W.biomeDepth > 0)
		{
			VeinChunk(c, UnityEngine.Random.Range(0.35f, 0.5f), 5, 3, 6, 64, true);
			VeinChunk(c, UnityEngine.Random.Range(0.35f, 0.5f), 5, 3, 6, 60, false);
		}
		else
		{
			VeinChunk(c, UnityEngine.Random.Range(0.35f, 0.4f), 11, 2, 6, 48, true);
			VeinChunk(c, UnityEngine.Random.Range(0.35f, 0.4f), 11, 2, 6, 48, false);
		}
	}

	// 每条矿脉:起点块内随机,长 len(横/竖),宽 w,直接写 WB
	static void VeinChunk(Vector2Int c, float amt, ushort block, int w, int lenMin, int lenMax, bool horizontal)
	{
		int n = Poisson(amt);
		for (int i = 0; i < n; i++)
		{
			Vector2Int v = new Vector2Int(c.x * CS + UnityEngine.Random.Range(0, CS), c.y * CS + UnityEngine.Random.Range(0, CS));
			int len = UnityEngine.Random.Range(lenMin, lenMax);
			for (int a = 0; a < len; a++)
			{
				for (int b = 0; b < w; b++)
				{
					int X = horizontal ? v.x + a : v.x + b;
					int Y = horizontal ? v.y + b : v.y + a;
					if (X < W.width && Y < W.height) WB[X, Y] = block;
				}
			}
		}
	}

	// ===== 液体(照搬 PlaceLiquids,按区块换算密度) =====
	static void GenChunkLiquids(Vector2Int c)
	{
		int bx = c.x * CS + CS / 2, by = c.y * CS + CS / 2;
		if (biome == 0)
			PlaceLiquidsChunk(128f, 1, 32, bx, by);
		else if (biome == 1)
		{
			PlaceLiquidsChunk(10f, 1, 400, bx, by);
			PlaceLiquidsChunk(18f, 2, 128, bx, by);
		}
		else if (biome == 2 || biome == 3)
		{
			PlaceLiquidsChunk(50f, 1, 26, bx, by);
			PlaceLiquidsChunk(15f, 3, 128, bx, by);
		}
		else
		{
			PlaceLiquidsChunk(30f, 1, 128, bx, by);
			PlaceLiquidsChunk(10f, 2, 50, bx, by);
		}
	}

	static void PlaceLiquidsChunk(float perChunk, byte type, int maxFill, int cx, int cy)
	{
		float num = perChunk;
		for (int i = 0; i < (int)num; i++)
		{
			Vector2 pos = new Vector2(cx - CS / 2 + UnityEngine.Random.Range(0f, CS), cy - CS / 2 + UnityEngine.Random.Range(0f, CS));
			FluidManager.main.StartFill(WorldToBlockPos(pos), type, maxFill);
		}
	}

	static Vector2Int WorldToBlockPos(Vector2 pos) => new Vector2Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));

	// ===== 实体(照搬原版 WorldPlaceEntities 密度与检查,按区块换算) =====
	static readonly string[] Crystals = { "BloodCrystal", "SoothingCrystal", "ReliefCrystal", "TurbulentCrystal", "OxygenCrystal", "EmissiveCrystal", "DigestionCrystal" };

	static void GenChunkEntities(Vector2Int c)
	{
		float tlr = W.totalLootRarity;
		float ttr = W.totalTrapRarity;
		float lrm = W.lootRarityMultiplier;
		for (int i = 0; i < 5; i++)
		{
			if (UnityEngine.Random.value < 0.015f)
				D(c, Crystals[UnityEngine.Random.Range(0, Crystals.Length)], 1f, 1f, 2f, flip: true);
		}
		if (biome <= 1)
		{
			D(c, "glowplant", 2.7f, 3.5f, 1.25f, 10f, 0.25f, flip: true, check: SoftCheck);
			D(c, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, flip: true, check: SoftCheck);
			D(c, "ceilingrye", 0.3f, 0.4f, 1f, 10f, 0.5f, flip: true, check: SoftCheck, dir: Vector2.up);
			D(c, "medcrate", 0.18f * tlr, 0.2f * tlr, 3f, 180f);
			D(c, "containercrate", 0.05f * tlr, 0.07f * tlr, 3f, 180f);
			D(c, "foodbox", 0.1f * tlr, 0.13f * tlr, 3f, 180f);
			D(c, "spikestabber", 0.4f * ttr, 0.5f * ttr);
			D(c, "shadecrawler", 0.4f * ttr, 0.42f * ttr, 2f, 180f);
			D(c, "corpse", 1f * lrm, 1.1f * lrm, check: CorpseCheck);
			D(c, "animalcorpse", 0.6f * lrm, 0.7f * lrm, check: CorpseCheck);
			D(c, "drillpod", 0.09f, 0.1f, inGround: true, flip: true);
			if (biome > 0)
			{
				D(c, "barbedwirefence", 0.6f * ttr, 0.8f * ttr, 4.8f);
				D(c, "beartrap", 0.2f * ttr, 0.25f * ttr, 1f);
				D(c, "CaveTicks", 0.15f * ttr, 0.2f * ttr, 4f, 0f, 3f);
				D(c, "geyser", 1.6f, 1.8f, 0.6f, check: SoftCheck);
			}
			else
			{
				D(c, "geyser", 0.7f, 0.8f, 0.6f, check: SoftCheck);
			}
			D(c, "jumppad", 0.6f * ttr, 0.8f * ttr);
			D(c, "geotree", 2.7f, 3f, 3f, 6f, 0.15f, flip: true, check: SoilCheck);
			D(c, "hydreed", 1.4f, 1.6f, 2.6f, 6f, 0.4f, flip: true, check: SoilCheck);
			D(c, "leadbush", 2f, 2.2f, 0.6f, 6f, 0.1f, flip: true, check: SoilCheck);
		}
		else if (biome == 2 || biome == 3)
		{
			float m = biome == 2 ? 1f : 2f;
			D(c, "glowplant", 2.4f * m, 2.5f * m, 1.25f, 10f, 0.25f, flip: true, check: SandCheck);
			D(c, "stoneplant", 0.4f * (biome == 2 ? 1f : 3f), 0.5f * (biome == 2 ? 1f : 3f), 1.9f, 10f, 0.1f, flip: true, check: SandCheck);
			D(c, "cactus", 1.4f, 1.6f, 2.1f, 10f, 0.3f, flip: true, check: SandCheck);
			D(c, "sandrose", 1.3f, 1.4f, 1.5f, 10f, flip: true, check: SandCheck);
			D(c, "drybush", 6f, 7f, 2f, 20f, flip: true, check: SandCheck);
			D(c, "brownshroom", 4f, 5f, 0.9f, 10f, flip: true, check: SandCheck);
			D(c, "stalagmite", 10f, 15f, 2.8f, 0f, 0.15f, flip: true, check: StoneCheck);
			D(c, "jumppad", 0.25f * ttr, 0.35f * ttr);
			D(c, "landmine", 0.13f * ttr, 0.16f * ttr, 0.4f);
			D(c, "ceilingrye", 0.08f, 0.1f, 1f, 10f, 0.5f, flip: true, check: SoftCheck, dir: Vector2.up);
			if (biome == 3)
			{
				D(c, "spentfuel", 0.3f * ttr, 0.35f * ttr, 1.875f);
				D(c, "soundcannon", 0.4f * ttr, 0.45f * ttr, 1f);
				D(c, "foodbox", 0.1f * tlr, 0.13f * tlr, 3f, 180f);
				D(c, "pop", 3f * tlr, 4f * tlr, 2f, 20f, 0.2f, flip: true, check: SandCheck);
				D(c, "coil", 0.2f * ttr, 0.3f * ttr, 2f);
			}
			else
			{
				D(c, "wallbiter", 0.12f * ttr, 0.13f * ttr, 4.8f);
				D(c, "shadecrawler", 0.2f * ttr, 0.2f * ttr, 4.8f);
				D(c, "droppings", 0.75f, 0.82f);
				D(c, "beartrap", 0.1f * ttr, 0.2f * ttr, 1f);
				D(c, "barbedwirefence", 0.7f * ttr, 0.8f * ttr, 4.8f);
			}
			D(c, "rag", 0.12f * lrm * (biome == 2 ? 1f : 2.5f), 0.2f * lrm * (biome == 2 ? 1f : 2.5f), 1f);
			D(c, "corpse", 0.75f * lrm * (biome == 2 ? 1f : 2f), 0.82f * lrm * (biome == 2 ? 1f : 2f), check: CorpseCheck);
		}
		else
		{
			D(c, "glowplant", 0.2f, 0.3f, 1.25f, 10f, 0.25f, flip: true, check: SoilCheck);
			D(c, "shadecrawler", 0.45f * ttr, 0.5f * ttr, 2f, 180f);
			D(c, "wallbiter", 0.1f * ttr, 0.11f * ttr, 4.8f);
			D(c, "thornbackyoung", 0.24f * ttr, 0.26f * ttr, 4.8f);
			D(c, "overgrowntick", 0.1f * ttr, 0.12f * ttr, 4.8f);
			D(c, "caveticks", 0.15f * ttr, 0.16f * ttr, 4.8f);
			if (UnityEngine.Random.value < 0.012f) D(c, "thornbackelder", 1f, 1f);
			D(c, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, flip: true, check: SoilCheck);
			D(c, "ceilingrye", 0.65f, 0.8f, 1f, 10f, 0.5f, flip: true, check: SoftCheck, dir: Vector2.up);
			D(c, "medcrate", 0.18f * tlr, 0.2f * tlr, 3f, 180f);
			D(c, "containercrate", 0.05f * tlr, 0.07f * tlr, 3f, 180f);
			D(c, "foodbox", 0.1f * tlr, 0.13f * tlr, 3f, 180f);
			D(c, "corpse", 1.1f * lrm, 1.2f * lrm, check: CorpseCheck);
			D(c, "animalcorpse", 0.9f * lrm, 0.95f * lrm, check: CorpseCheck);
			D(c, "geotree", 0.4f, 0.5f, 3f, 6f, 0.15f, flip: true, check: SoilCheck);
			D(c, "browncap", 0.4f, 0.5f, 3f, 6f, 0.15f, flip: true, check: SoilCheck);
			D(c, "hydreed", 0.6f, 0.7f, 2.6f, 6f, 0.4f, flip: true, check: SoilCheck);
			D(c, "leadbush", 1.1f, 1.2f, 0.6f, 6f, 0.1f, flip: true, check: SoilCheck);
			D(c, "droppings", 3.7f, 4f);
			D(c, "pop", 1f * tlr, 1.1f * tlr, 2f, 20f, 0.2f, flip: true, check: SoilCheck);
			D(c, "bananaplant", 1.9f * ttr, 2f * ttr, 0.4f, 15f, 0.1f, flip: true, check: SoilCheck);
			D(c, "coil", 0.2f * ttr, 0.3f * ttr, 2f);
			D(c, "beartrap", 0.1f * ttr, 0.2f * ttr, 1f);
			D(c, "jumppad", 0.25f * ttr, 0.35f * ttr);
			D(c, "spikestabber", 0.4f * ttr, 0.5f * ttr);
			D(c, "grabberplant", 0.4f * ttr, 0.5f * ttr);
			D(c, "geyser", 0.7f, 0.8f, 0.6f, check: SoftCheck);
			D(c, "skullcrusher", 1.1f, 1.2f, 1f, 10f, flip: true, dir: Vector2.up);
		}
	}

	// 数据层"向下/指定方向找地面"(模拟原版 Physics2D.Raycast,不依赖 collider 时序)
	static bool FindSurface(int wx, int wy, Vector2 dir, out int hx, out int hy)
	{
		hx = wx; hy = wy;
		int sx = (int)Mathf.Sign(dir.x), sy = (int)Mathf.Sign(dir.y);
		for (int i = 0; i < 16; i++)
		{
			int bx = wx + sx * i, by = wy + sy * i;
			if (bx < 0 || by < 0 || bx >= W.width || by >= W.height) return false;
			if (WB[bx, by] > 0) { hx = bx; hy = by; return true; }
		}
		return false;
	}

	// 检查委托(原版 WorldPlaceEntities 的 PlaceCheckDelegate 逻辑)
	static bool SoftCheck(int bx, int by) => WB[bx, by] < 3 || IsSoil(bx, by);
	static bool SoilCheck(int bx, int by) => IsSoil(bx, by);
	static bool SandCheck(int bx, int by) => WB[bx, by] == 12 || WB[bx, by] == 13 || IsSoil(bx, by);
	static bool StoneCheck(int bx, int by) => WB[bx, by] == 17 || WB[bx, by] == 18 || WB[bx, by] == 19;
	static bool CorpseCheck(int bx, int by) => WB[bx, by] > 0 && WB[bx - 1, by] > 0 && WB[bx + 1, by] > 0;

	static bool IsSoil(int bx, int by)
	{
		ushort b = WB[bx, by];
		return b == 2 || b == 15 || b == 16 || b == 23 || (b > 30 && b < 34);
	}

	// 放置一个实体(模拟原版 DistributeEntities 对单点)
	static void D(Vector2Int c, string name, float min, float max, float yOff = 0f, float rot = 0f, float yDev = 0f, bool inGround = false, bool flip = false, Func<int, int, bool> check = null, Vector2 dir = default(Vector2))
	{
		float num = UnityEngine.Random.Range(min, max);
		int count = (int)num;
		if (UnityEngine.Random.value < num - count) count++;
		int bx = c.x * CS, by = c.y * CS;
		for (int n = 0; n < count; n++)
		{
			int wx = bx + UnityEngine.Random.Range(0, CS);
			int wy = by + UnityEngine.Random.Range(0, CS);
			if (WB[wx, wy] > 0) continue;
			int hx, hy;
			if (!FindSurface(wx, wy, dir == default(Vector2) ? Vector2.down : dir, out hx, out hy)) continue;
			if (check != null && !check(hx, hy)) continue;
			GameObject prefab = GetStructObj(name);
			if (prefab == null) continue;
			Vector2 point = new Vector2(hx + 0.5f, hy + 1f);
			float off = UnityEngine.Random.Range(yOff - yDev, yOff + yDev);
			GameObject go = UnityEngine.Object.Instantiate(prefab, point - dir * off, Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-rot, rot)));
			BuildingEntity be = go.GetComponent<BuildingEntity>();
			if (be != null)
			{
				be.blockPlacedOn = new Vector2Int(hx, hy);
				if (inGround && W.ChunkUpdated[c.x, c.y] != null)
					W.ChunkUpdated[c.x, c.y].AddListener(be.CheckSeating);
			}
			if (flip && UnityEngine.Random.value < 0.5f)
			{
				Vector3 s = go.transform.localScale;
				s.x *= -1f;
				go.transform.localScale = s;
			}
		}
	}
}