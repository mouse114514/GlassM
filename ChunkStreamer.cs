using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Tilemaps;
using Random = UnityEngine.Random;
using Object = UnityEngine.Object;
using NoiseType = FastNoiseLite.NoiseType;
using FractalType = FastNoiseLite.FractalType;
using CellularDistanceFunction = FastNoiseLite.CellularDistanceFunction;
using CellularReturnType = FastNoiseLite.CellularReturnType;
using RotationType3D = FastNoiseLite.RotationType3D;
using DomainWarpType = FastNoiseLite.DomainWarpType;

namespace GlassM;

public static class ChunkStreamer
{
	public static readonly int CS = WorldGeneration.CHUNKSIZE;

	public static WorldGeneration W;

	public static bool Active;

	private static readonly FieldInfo f_worldBlocks = typeof(WorldGeneration).GetField("worldBlocks", BindingFlags.Instance | BindingFlags.NonPublic);

	private static readonly FieldInfo f_chunks = typeof(WorldGeneration).GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic);

	private static FastNoiseLite caveNoise;

	private static FastNoiseLite dirtPerlin;

	private static FastNoiseLite frequencyMap;

	private static FastNoiseLite biomeMap;

	private static FastNoiseLite toxicNoise;

	private static FastNoiseLite biomeMap2;

	private static FastNoiseLite marbleMap;

	private static float minMarble;

	private static int biome;

	public static readonly bool[,] genData = new bool[16, 16];

	private static readonly bool[,] colliderOn = new bool[16, 16];

	private static readonly bool[,] inQueue = new bool[16, 16];

	private static readonly bool[,] genFull = new bool[16, 16];

	private static readonly bool[,] genApplied = new bool[16, 16];

	private static readonly bool[,] dirtyRender = new bool[16, 16];

	private static readonly List<Vector2Int> queue = new List<Vector2Int>();

	private static readonly List<Vector2Int> pendingFull = new List<Vector2Int>();

	private static readonly TileBase[] renderTiles = (TileBase[])new TileBase[CS * CS];

	private static readonly object terrLock = new object();

	private static readonly Queue<Vector2Int> terrJobs = new Queue<Vector2Int>();

	private static readonly Queue<KeyValuePair<Vector2Int, ushort[,]>> terrDone = new Queue<KeyValuePair<Vector2Int, ushort[,]>>();

	private static Thread terrThread;

	private static volatile bool terrStop;

	private static readonly Dictionary<string, GameObject> structRes = new Dictionary<string, GameObject>();

	private static long diagApplyMs;

	private static long diagRenderMs;

	private static long diagStructMs;

	private static long diagRefreshMs;

	private static long diagOreMs;

	private static int diagApplyCount;

	private static readonly System.Random terrainRng = new System.Random(12345);

	public const int GEN_RADIUS = 5;

	public const int UNLOAD_RADIUS = 7;

	public const int INIT_RADIUS = 1;

	public const int MAX_PER_FRAME = 4;

	public const float PHYSICS_DT = 0.03f;

	public static Vector2Int PlayerChunk = new Vector2Int(8, 8);

	private static Vector2Int lastScanChunk = new Vector2Int(int.MinValue, int.MinValue);

	private static Vector2Int lastEnqueueChunk = new Vector2Int(int.MinValue, int.MinValue);

	private static Vector2Int lastPlayerChunk;

	private static Vector2Int moveDir;

	private static float nextLogTime;

	public static readonly bool StreamOn = true;

	private static readonly string[] Crystals = new string[7] { "BloodCrystal", "SoothingCrystal", "ReliefCrystal", "TurbulentCrystal", "OxygenCrystal", "EmissiveCrystal", "DigestionCrystal" };

	public static ushort[,] WB => (ushort[,])f_worldBlocks.GetValue(W);

	public static Tilemap[,] CH => (Tilemap[,])f_chunks.GetValue(W);

	public static int QueueCount => queue.Count;

	private static void EnsureTerrainThread()
	{
		if (terrThread == null)
		{
			terrStop = false;
			terrThread = new Thread(TerrainWorkerLoop);
			terrThread.IsBackground = true;
			terrThread.Start();
		}
	}

	private static void TerrainWorkerLoop()
	{
		while (!terrStop)
		{
			Vector2Int jobChunk = default(Vector2Int);
			bool hasJob;
			lock (terrLock)
			{
				hasJob = terrJobs.Count > 0;
				if (hasJob)
				{
					jobChunk = terrJobs.Dequeue();
				}
			}
			if (!hasJob)
			{
				Thread.Sleep(1);
				continue;
			}
			ushort[,] data = new ushort[CS, CS];
			try
			{
				GenChunkTerrainInto(jobChunk, data);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("terrain worker failed " + jobChunk + ": " + ex);
				lock (terrLock)
				{
					terrDone.Enqueue(new KeyValuePair<Vector2Int, ushort[,]>(jobChunk, data));
				}
				continue;
			}
			lock (terrLock)
			{
				terrDone.Enqueue(new KeyValuePair<Vector2Int, ushort[,]>(jobChunk, data));
			}
		}
	}

	private static GameObject GetStruct(string name)
	{
		if (!structRes.TryGetValue(name, out var value))
		{
			value = Resources.Load<GameObject>(name);
			structRes[name] = value;
		}
		return value;
	}

	private static GameObject GetStructObj(string name)
	{
		if (!structRes.TryGetValue(name, out var value))
		{
			value = (GameObject)Resources.Load(name);
			structRes[name] = value;
		}
		return value;
	}

	private static float R(float min, float max)
	{
		return (float)((double)min + (double)(max - min) * terrainRng.NextDouble());
	}

	private static float RV()
	{
		return (float)terrainRng.NextDouble();
	}

	private static int RI(int min, int max)
	{
		return terrainRng.Next(min, max);
	}

	public static void OnNewWorld(WorldGeneration w)
	{
		W = w;
		Active = true;
		Array.Clear(genData, 0, genData.Length);
		Array.Clear(colliderOn, 0, colliderOn.Length);
		Array.Clear(inQueue, 0, inQueue.Length);
		Array.Clear(genFull, 0, genFull.Length);
		Array.Clear(genApplied, 0, genApplied.Length);
		Array.Clear(dirtyRender, 0, dirtyRender.Length);
		queue.Clear();
	}

	public static void OnClear()
	{
		Array.Clear(genData, 0, genData.Length);
		Array.Clear(colliderOn, 0, colliderOn.Length);
		Array.Clear(inQueue, 0, inQueue.Length);
		Array.Clear(genFull, 0, genFull.Length);
		Array.Clear(genApplied, 0, genApplied.Length);
		Array.Clear(dirtyRender, 0, dirtyRender.Length);
		queue.Clear();
		pendingFull.Clear();
		lock (terrLock)
		{
			terrJobs.Clear();
			terrDone.Clear();
		}
	}

	private static bool InWorld(Vector2Int c)
	{
		return c.x >= 0 && c.y >= 0 && c.x < 16 && c.y < 16;
	}

	public static void InitTerrain(WorldGeneration w)
	{
		W = w;
		biome = w.biomeDepth;
		if (biome <= 1)
		{
			caveNoise = NewNoise();
			caveNoise.SetNoiseType(NoiseType.Cellular);
			caveNoise.SetFrequency(0.06f);
			caveNoise.SetFractalOctaves(3);
			caveNoise.SetFractalType(FractalType.FBm);
			caveNoise.SetFractalLacunarity(1.5f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType(NoiseType.Perlin);
			dirtPerlin.SetFractalType(FractalType.FBm);
			dirtPerlin.SetFractalOctaves(7);
			dirtPerlin.SetFrequency(0.035f);
			frequencyMap = NewNoise();
			frequencyMap.SetNoiseType(NoiseType.Perlin);
			frequencyMap.SetFrequency(0.00037f);
			biomeMap = NewNoise();
			biomeMap.SetNoiseType(NoiseType.Cellular);
			biomeMap.SetFrequency(0.04f);
			biomeMap.SetCellularDistanceFunction(CellularDistanceFunction.EuclideanSq);
			biomeMap.SetCellularReturnType(CellularReturnType.Distance);
			biomeMap.SetCellularJitter(1f);
			biomeMap.SetFractalType(FractalType.Ridged);
			biomeMap.SetFractalLacunarity(1.5f);
		}
		else if (biome == 2 || biome == 3)
		{
			biomeMap = NewNoise();
			biomeMap.SetNoiseType(NoiseType.Value);
			biomeMap.SetFrequency(0.086f);
			biomeMap.SetFractalType(FractalType.FBm);
			biomeMap.SetFractalOctaves((biome == 2) ? 2 : 3);
			biomeMap.SetFractalGain(0.49f);
			biomeMap.SetFractalWeightedStrength(2.34f);
			biomeMap.SetDomainWarpType(DomainWarpType.OpenSimplex2);
			biomeMap.SetDomainWarpAmp(22f);
			frequencyMap = NewNoise();
			frequencyMap.SetFrequency(0.006f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType(NoiseType.Cellular);
			dirtPerlin.SetFrequency(0.02f);
			dirtPerlin.SetFractalType(FractalType.Ridged);
			dirtPerlin.SetFractalGain(0.65f);
			caveNoise = NewNoise();
			caveNoise.SetFrequency(0.005f);
			caveNoise.SetFractalType(FractalType.PingPong);
			caveNoise.SetFractalGain(0.35f);
			caveNoise.SetDomainWarpType(DomainWarpType.BasicGrid);
			caveNoise.SetDomainWarpAmp(40f);
			toxicNoise = NewNoise();
			toxicNoise.SetFrequency(0.012f);
			toxicNoise.SetFractalType(FractalType.PingPong);
			toxicNoise.SetFractalGain(0.3f);
			toxicNoise.SetDomainWarpType(DomainWarpType.BasicGrid);
			toxicNoise.SetDomainWarpAmp(50f);
			biomeMap2 = NewNoise();
			biomeMap2.SetNoiseType(NoiseType.Cellular);
			biomeMap2.SetFrequency(0.05f);
			biomeMap2.SetCellularDistanceFunction(CellularDistanceFunction.EuclideanSq);
			biomeMap2.SetCellularReturnType(CellularReturnType.Distance);
			biomeMap2.SetCellularJitter(1f);
			biomeMap2.SetFractalType(FractalType.Ridged);
			biomeMap2.SetFractalLacunarity(1.5f);
			marbleMap = NewNoise();
			marbleMap.SetFrequency((biome == 2) ? 0.007f : 0.035f);
			marbleMap.SetNoiseType(NoiseType.Perlin);
			marbleMap.SetDomainWarpType(DomainWarpType.OpenSimplex2);
			marbleMap.SetDomainWarpAmp(100f);
			minMarble = ((biome == 2) ? 0.45f : 1f);
		}
		else
		{
			marbleMap = NewNoise();
			marbleMap.SetNoiseType(NoiseType.Value);
			marbleMap.SetFractalType(FractalType.Ridged);
			marbleMap.SetFractalOctaves(3);
			marbleMap.SetFractalLacunarity(2.29f);
			marbleMap.SetFractalGain(4f);
			marbleMap.SetFractalWeightedStrength(1.2f);
			marbleMap.SetDomainWarpType(DomainWarpType.OpenSimplex2);
			marbleMap.SetDomainWarpAmp(41f);
			biomeMap2 = NewNoise();
			biomeMap2.SetFrequency(0.02f);
			biomeMap2.SetDomainWarpType(DomainWarpType.OpenSimplex2);
			biomeMap2.SetDomainWarpAmp(25f);
		}
	}

	private static FastNoiseLite NewNoise()
	{
		return new FastNoiseLite(Random.Range(0, int.MaxValue));
	}

	public static void GenerateInitial()
	{
		Vector2Int center = new Vector2Int((int)((long)(W.width / 2u) / (long)CS), (int)((long)(W.height / 2u) / (long)CS));
		PlayerChunk = center;
		EnqueueAround(center, 1, genNow: true);
		GenSpawnCavity();
		if (StreamOn)
		{
			EnqueueAround(center, 5, genNow: false);
		}
	}

	private static void GenSpawnCavity()
	{
		if (W == null || WB == null)
		{
			return;
		}
		int colStart = 508 / CS;
		int colEnd = 516 / CS;
		int spawnRow = 1011 / CS;
		for (int i = colStart; i <= colEnd; i++)
		{
			SyncGenAndWait(new Vector2Int(i, spawnRow));
		}
		for (int j = 508; j <= 516; j++)
		{
			for (int k = 1012; k <= 1022; k++)
			{
				WB[j, k] = 0;
			}
			WB[j, 1011] = 1;
			WB[j, 1023] = 0;
		}
		for (int l = 506; l <= 518; l++)
		{
			WB[l, 1023] = 0;
		}
		for (int m = colStart; m <= colEnd; m++)
		{
			RenderChunk(new Vector2Int(m, spawnRow));
		}
		Plugin.Log.LogInfo(("CS: spawn cavity done, cols=" + colStart + "-" + colEnd + " row=" + spawnRow));
	}

	private static void EnqueueAround(Vector2Int c, int radius, bool genNow)
	{
		for (int i = c.x - radius; i <= c.x + radius; i++)
		{
			for (int j = c.y - radius; j <= c.y + radius; j++)
			{
				Vector2Int chunk = new Vector2Int(i, j);
				if (!InWorld(chunk) || genData[chunk.x, chunk.y])
				{
					continue;
				}
				if (genNow)
				{
					if (!genApplied[chunk.x, chunk.y])
					{
						SyncGenAndWait(chunk);
					}
				}
				else
				{
					queue.Add(chunk);
					inQueue[chunk.x, chunk.y] = true;
				}
			}
		}
	}

	private static void SyncGenAndWait(Vector2Int cc)
	{
		if (genData[cc.x, cc.y] || genApplied[cc.x, cc.y])
		{
			return;
		}
		genData[cc.x, cc.y] = true;
		genFull[cc.x, cc.y] = false;
		EnsureTerrainThread();
		lock (terrLock)
		{
			terrJobs.Enqueue(cc);
		}
		for (int i = 0; i < 200; i++)
		{
			bool hasDone;
			KeyValuePair<Vector2Int, ushort[,]> done;
			lock (terrLock)
			{
				hasDone = terrDone.Count > 0;
				done = ((!hasDone) ? default(KeyValuePair<Vector2Int, ushort[,]>) : terrDone.Dequeue());
			}
			if (hasDone)
			{
				if (done.Key == cc)
				{
					ApplyChunk(cc, done.Value);
					pendingFull.Add(cc);
					break;
				}
				ApplyChunk(done.Key, done.Value);
				pendingFull.Add(done.Key);
			}
			else
			{
				Thread.Sleep(1);
			}
		}
	}

	public static void Tick()
	{
		if (!Active || W == null || WB == null || CH == null)
		{
			return;
		}
		if (Time.fixedDeltaTime != 0.03f)
		{
			Time.fixedDeltaTime = 0.03f;
		}
		Vector2Int pc = PlayerChunk;
		try
		{
			Vector3 camPos = ((WorldGeneration.world != null && PlayerCamera.main != null && PlayerCamera.main.body != null) ? PlayerCamera.main.body.transform.position : Vector3.zero);
			pc = new Vector2Int(Mathf.Clamp((int)(camPos.x + 512f) / CS, 0, 15), Mathf.Clamp((int)(camPos.y + 512f) / CS, 0, 15));
			if (pc != lastPlayerChunk)
			{
				Vector2Int delta = pc - lastPlayerChunk;
				moveDir = new Vector2Int((delta.x > 0) ? 1 : ((delta.x < 0) ? (-1) : 0), (delta.y > 0) ? 1 : ((delta.y < 0) ? (-1) : 0));
				lastPlayerChunk = pc;
			}
			PlayerChunk = pc;
		}
		catch
		{
		}
		bool enqueued = false;
		if (StreamOn && pc != lastEnqueueChunk)
		{
			lastEnqueueChunk = pc;
			for (int i = pc.x - 5; i <= pc.x + 5; i++)
			{
				for (int j = pc.y - 5; j <= pc.y + 5; j++)
				{
					if (i >= 0 && j >= 0 && i <= 15 && j <= 15 && !genData[i, j] && !inQueue[i, j])
					{
						queue.Add(new Vector2Int(i, j));
						inQueue[i, j] = true;
						enqueued = true;
					}
				}
			}
		}
		if (pendingFull.Count > 0)
		{
			int pendingN = Mathf.Min(4, pendingFull.Count);
			for (int k = 0; k < pendingN; k++)
			{
				Vector2Int chunk = pendingFull[0];
				pendingFull.RemoveAt(0);
				try
				{
					GenChunkOres(chunk);
					GenChunkLiquids(chunk);
					GenChunkEntities(chunk);
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning("chunk full gen failed " + chunk + ": " + ex);
				}
			}
		}
		if (queue.Count > 0)
		{
			if (enqueued)
			{
				queue.Sort((Vector2Int a, Vector2Int b) => GenPriority(a, pc).CompareTo(GenPriority(b, pc)));
			}
			int genN = Mathf.Min(4, queue.Count);
			for (int l = 0; l < genN; l++)
			{
				GenChunk(queue[0], full: true);
				queue.RemoveAt(0);
			}
		}
		ApplyDoneChunks(4);
		int toggleCount = 0;
		if (pc != lastScanChunk)
		{
			lastScanChunk = pc;
			for (int m = 0; m < 16; m++)
			{
				for (int n = 0; n < 16; n++)
				{
					if (!genData[m, n])
					{
						continue;
					}
					bool shouldOff = Dist2(new Vector2Int(m, n), pc) > 49;
					if (shouldOff != colliderOn[m, n])
					{
						continue;
					}
					Tilemap tilemap = CH[m, n];
					if (!(tilemap == null))
					{
						Collider2D[] colliders = tilemap.GetComponents<Collider2D>();
						foreach (Collider2D col in colliders)
						{
							col.enabled = !shouldOff;
						}
						Rigidbody2D rb = tilemap.GetComponent<Rigidbody2D>();
						if (rb != null)
						{
							rb.simulated = !shouldOff;
						}
						colliderOn[m, n] = !shouldOff;
						toggleCount++;
						if (!shouldOff && dirtyRender[m, n])
						{
							RenderChunk(new Vector2Int(m, n));
						}
					}
				}
			}
		}
		if (!(Time.unscaledTime > nextLogTime))
		{
			return;
		}
		nextLogTime = Time.unscaledTime + 5f;
		int genCount = 0;
		for (int i = 0; i < 16; i++)
		{
			for (int j = 0; j < 16; j++)
			{
				if (genData[i, j])
				{
					genCount++;
				}
			}
		}
		string perfText = "";
		if (diagApplyCount > 0)
		{
			perfText = $" | apply {(float)diagApplyMs / (float)diagApplyCount:N2}ms (ore {(float)diagOreMs / (float)diagApplyCount:N2} render {(float)diagRenderMs / (float)diagApplyCount:N2} struct {(float)diagStructMs / (float)diagApplyCount:N2} refresh {(float)diagRefreshMs / (float)diagApplyCount:N2}, {diagApplyCount})";
			diagApplyMs = (diagOreMs = (diagRenderMs = (diagStructMs = (diagRefreshMs = 0L))));
			diagApplyCount = 0;
		}
		int rendererOn = 0;
		int collidersOn = 0;
		int rbSim = 0;
		if (CH != null)
		{
			for (int i = 0; i < 16; i++)
			{
				for (int j = 0; j < 16; j++)
				{
					Tilemap tilemap = CH[i, j];
					if (tilemap == null)
					{
						continue;
					}
					TilemapRenderer renderer = tilemap.GetComponent<TilemapRenderer>();
					if (renderer != null && renderer.enabled)
					{
						rendererOn++;
					}
					Collider2D[] colliders = tilemap.GetComponents<Collider2D>();
					foreach (Collider2D col in colliders)
					{
						if (col.enabled)
						{
							collidersOn++;
						}
					}
					Rigidbody2D rb = tilemap.GetComponent<Rigidbody2D>();
					if (rb != null && rb.simulated)
					{
						rbSim++;
					}
				}
			}
		}
		Plugin.Log.LogInfo(("CS: generated " + genCount + "/256, queue " + queue.Count + ", collider toggles " + toggleCount + " | rendererOn=" + rendererOn + " colliders=" + collidersOn + " rbSim=" + rbSim + perfText));
	}

	private static int Dist2(Vector2Int a, Vector2Int b)
	{
		int dx = a.x - b.x;
		int dy = a.y - b.y;
		return dx * dx + dy * dy;
	}

	private static int GenPriority(Vector2Int a, Vector2Int b)
	{
		int dx = a.x - b.x;
		int dy = a.y - b.y;
		int pri = dx * dx + dy * dy;
		if (moveDir.x != 0 && dx != 0 && dx > 0 == moveDir.x > 0)
		{
			pri -= 16;
		}
		if (moveDir.y != 0 && dy != 0 && dy > 0 == moveDir.y > 0)
		{
			pri -= 16;
		}
		return pri;
	}

	private static void GenChunk(Vector2Int c, bool full)
	{
		if (genData[c.x, c.y] || genApplied[c.x, c.y])
		{
			return;
		}
		genData[c.x, c.y] = true;
		genFull[c.x, c.y] = full;
		inQueue[c.x, c.y] = false;
		EnsureTerrainThread();
		lock (terrLock)
		{
			terrJobs.Enqueue(c);
		}
	}

	private static void ApplyChunk(Vector2Int c, ushort[,] data)
	{
		if (genApplied[c.x, c.y])
		{
			return;
		}
		genApplied[c.x, c.y] = true;
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			int bx = c.x * CS;
			int by = c.y * CS;
			for (int i = 0; i < CS; i++)
			{
				for (int j = 0; j < CS; j++)
				{
					WB[bx + i, by + j] = data[i, j];
				}
			}
			Stopwatch swOre = Stopwatch.StartNew();
			if (genFull[c.x, c.y])
			{
				GenChunkOres(c);
				GenChunkLiquids(c);
				GenChunkEntities(c);
			}
			else
			{
				pendingFull.Add(c);
			}
			diagOreMs += swOre.ElapsedMilliseconds;
			Stopwatch swRender = Stopwatch.StartNew();
			RenderChunk(c);
			diagRenderMs += swRender.ElapsedMilliseconds;
			Stopwatch swStruct = Stopwatch.StartNew();
			GenChunkStructures(c);
			diagStructMs += swStruct.ElapsedMilliseconds;
			Stopwatch swRefresh = Stopwatch.StartNew();
			RefreshAround(c);
			diagRefreshMs += swRefresh.ElapsedMilliseconds;
			diagApplyMs += sw.ElapsedMilliseconds;
			diagApplyCount++;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("chunk apply failed " + c + ": " + ex);
		}
	}

	private static void ApplyDoneChunks(int max)
	{
		for (int i = 0; i < max; i++)
		{
			bool hasDone;
			KeyValuePair<Vector2Int, ushort[,]> done;
			lock (terrLock)
			{
				hasDone = terrDone.Count > 0;
				done = ((!hasDone) ? default(KeyValuePair<Vector2Int, ushort[,]>) : terrDone.Dequeue());
			}
			if (!hasDone)
			{
				break;
			}
			ApplyChunk(done.Key, done.Value);
		}
	}

	private static void RenderChunk(Vector2Int c)
	{
		Tilemap tm = CH[c.x, c.y];
		if (tm == null)
		{
			return;
		}
		int bx = c.x * CS;
		int by = c.y * CS;
		int halfChunk = W.HALFCHUNKSIZE;
		if (Dist2(c, PlayerChunk) > 49)
		{
			dirtyRender[c.x, c.y] = true;
			return;
		}
		int idx = 0;
		for (int i = 0; i < CS; i++)
		{
			for (int j = 0; j < CS; j++)
			{
				renderTiles[idx++] = W.tiles[WB[bx + j, by + i]];
			}
		}
		tm.SetTilesBlock(new BoundsInt(-halfChunk, -halfChunk, 0, CS, CS, 1), renderTiles);
		dirtyRender[c.x, c.y] = false;
	}

	private static int Poisson(float lambda)
	{
		int count = 0;
		while (Random.value < lambda)
		{
			count++;
		}
		return count;
	}

	private static bool GroundAbove(Vector2 pos, out Vector2Int ground, float maxDist = 400f)
	{
		Vector2Int start = W.WorldToBlockPos(pos);
		int y = start.y;
		while (y >= 0 && (float)(start.y - y) < maxDist)
		{
			if (WB[start.x, y] > 0)
			{
				ground = new Vector2Int(start.x, y);
				return true;
			}
			y--;
		}
		ground = start;
		return false;
	}

	private static Vector2 RandPosInChunk(Vector2Int c)
	{
		return new Vector2((float)(c.x * CS - W.halfWidth + Random.Range(10, CS - 10)), (float)(c.y * CS - W.halfHeight + Random.Range(10, CS - 10)));
	}

	private static void RefreshAround(Vector2Int c)
	{
		if (genData[c.x, c.y])
		{
			RenderChunk(c);
		}
	}

	private static void GenChunkStructures(Vector2Int c)
	{
		if ((int)W.biomeOverride > 0)
		{
			return;
		}
		int biomeDepth = W.biomeDepth;
		if (biomeDepth > 4)
		{
			return;
		}
		try
		{
			float totalLootRarity = W.totalLootRarity;
			int n = Poisson(Random.Range(0.12f, 0.13f));
			for (int i = 0; i < n; i++)
			{
				DropCapsuleAt(c);
			}
			n = Poisson(biomeDepth switch
			{
				2 => Random.Range(0.066f, 0.077f), 
				3 => Random.Range(0.066f, 0.077f) * 2.5f, 
				4 => Random.Range(0.066f, 0.077f), 
				_ => Random.Range(0.055f, 0.066f), 
			});
			for (int j = 0; j < n; j++)
			{
				CollapsedPodAt(c);
			}
			if (biomeDepth <= 1)
			{
				if (biomeDepth > 0)
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
			else if (biomeDepth == 2 || biomeDepth == 3)
			{
				BioContainerAt(c, 0.05f, 0.07f, 1f);
				PodAt(c, 0.04f, 0.05f, "Structures/MedicalBuilding", 0.98f);
				BridgeAt(c, 0.09f, 0.12f, "Structures/SteelBridge", 0.95f, raycast: false);
				PodAt(c, 0.06f, 0.08f, "Structures/MiniPod", 0.88f);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodCross", 0.94f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodHorizontal", 0.94f, entity: false);
			}
			else
			{
				n = Poisson(Random.Range(0.9f, 1.1f));
				for (int k = 0; k < n; k++)
				{
					if (GroundAbove(RandPosInChunk(c), out var ground, 64f))
					{
						W.GenerateTree(ground);
					}
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
			n = Poisson(Random.Range(0.088f, 0.1f));
			for (int l = 0; l < n; l++)
			{
				LifePodAt(c);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("chunk structures failed " + c + ": " + ex);
		}
	}

	private static void DropCapsuleAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		if (GroundAbove(pos, out var ground))
		{
			pos = W.BlockToWorldPos(ground);
		}
		Object.Instantiate<GameObject>(GetStructObj("dropcapsule"), pos, Quaternion.Euler(0f, 0f, Random.Range(0f, 360f))).GetComponent<AudioSource>().pitch = Random.Range(0.9f, 1.1f);
		W.GenerateBlockCircle(pos, 32, (ushort)3, 0.7f, 0f, false, false, false);
		W.GenerateBlockCircle(pos, 30, (ushort)6, 0.04f, 0.04f, false, false, false);
		W.GenerateBlockCircle(pos, 30, (ushort)6, 0.04f, 0.04f, false, false, false);
		W.GenerateBlockCircle(pos, 4, (ushort)0, 1f, 0.9f, false, false, false);
	}

	private static void CollapsedPodAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		if (GroundAbove(pos, out var ground))
		{
			pos = W.BlockToWorldPos(ground);
		}
		Vector2Int vp = W.WorldToBlockPos(pos);
		CraterAt(pos, vp);
		W.GenerateObjectAtPos(vp, GetStruct("LifepodCollapsed").transform.GetChild(0).GetComponent<Tilemap>(), 0.88f, true);
		if (Random.value < 0.9f)
		{
			AmmoScript ammo = Object.Instantiate<GameObject>(GetStructObj(Utils.PickRandom<string>(W.spawnableMagazines)), pos, Quaternion.Euler(0f, 0f, Random.value * 360f)).GetComponent<AmmoScript>();
			ammo.rounds = Mathf.RoundToInt((float)ammo.maxRounds * Random.value);
		}
		for (int i = 0; i < 3; i++)
		{
			if (Random.Range(0f, 1f) < 0.3f)
			{
				Object.Instantiate<GameObject>(GetStructObj("experimentflesh"), pos + Vector2.right * Random.Range(-3f, 3f), Quaternion.identity);
			}
		}
		if (Random.Range(0f, 1f) < 0.8f)
		{
			Object.Instantiate<GameObject>(GetStructObj("internalorgans"), pos + Vector2.right * Random.Range(-3f, 3f), Quaternion.identity);
		}
	}

	private static void LifePodAt(Vector2Int c)
	{
		Vector2 pos = RandPosInChunk(c);
		if (GroundAbove(pos, out var ground))
		{
			pos = W.BlockToWorldPos(ground);
		}
		Vector2Int vp = W.WorldToBlockPos(pos);
		CraterAt(pos, vp);
		W.GenerateObjectAtPos(vp, GetStruct("Lifepod").transform.GetChild(0).GetComponent<Tilemap>(), 0.95f, true);
		W.GenerateEntityAtPos(W.BlockToWorldPos(vp), GetStruct("Lifepod"));
		if (Random.value < WorldGeneration.GetRunSettingFloat("traderchance") * 0.01f)
		{
			int traderOff = Random.Range(-4, 4);
			TraderScript trader = Object.Instantiate<GameObject>(GetStructObj("trader" + Random.Range(1, 4)), W.BlockToWorldPos(vp + Vector2Int.down * 7 + Vector2Int.right * traderOff) - Vector2.one * 0.5f, Quaternion.identity).GetComponent<TraderScript>();
			if ((float)Mathf.Abs(traderOff) > 1.5f)
			{
				trader.farEnoughToMove = true;
			}
			trader.MoveRange = new RangeF(W.BlockToWorldPos(vp - Vector2Int.right * 5).x, W.BlockToWorldPos(vp + Vector2Int.right * 5).x);
		}
		else
		{
			Object.Instantiate<GameObject>(GetStructObj("lifepodchest"), W.BlockToWorldPos(vp + Vector2Int.down * 6) - Vector2.one * 0.5f, Quaternion.identity);
		}
		for (int i = 0; i < 3; i++)
		{
			if (Random.Range(0f, 1f) < 0.05f)
			{
				Object.Instantiate<GameObject>(GetStructObj("experimentflesh"), pos + Vector2.right * Random.Range(-3f, 3f), Quaternion.identity);
			}
		}
		if (Random.Range(0f, 1f) < 0.05f)
		{
			Object.Instantiate<GameObject>(GetStructObj("internalorgans"), pos + Vector2.right * Random.Range(-3f, 3f), Quaternion.identity);
		}
		if (Random.Range(0f, 1f) < 0.5f)
		{
			Object.Instantiate<GameObject>(GetStructObj("LoreNote"), pos + Vector2.right * Random.Range(-3f, 3f), Quaternion.identity);
		}
		if (Random.Range(0f, 1f) < 0.285f)
		{
			Utils.Create("epda", pos + Vector2.right * Random.Range(-3f, 3f), Random.value * 360f);
		}
		if (Random.value < 0.2f)
		{
			Vector2 rackPos = pos + Vector2.right * Random.Range(-1.5f, 1.5f);
			Utils.Create("Special/defibrack", rackPos, 0f);
			bool hasBattery = Random.value < 0.5f;
			float batteryValue = Random.value;
			GameObject defib = null;
			if (Random.value < 0.75f)
			{
				defib = Utils.Create("manualdefibrillator", rackPos, 0f);
				defib.AddComponent<ItemLock>();
			}
			else
			{
				defib = Utils.Create("aed", rackPos, 0f);
				defib.AddComponent<ItemLock>();
			}
			if (!hasBattery)
			{
				defib.GetComponent<Item>().battery.UnloadBattery(true);
			}
			else
			{
				defib.GetComponent<Item>().condition = batteryValue;
			}
		}
	}

	private static void CraterAt(Vector2 pos, Vector2Int vp)
	{
		for (int i = 0; i < 90; i++)
		{
			for (int j = 0; j < 90; j++)
			{
				float dist = Vector2.Distance(pos + Vector2.up * (float)j + Vector2.right * (float)i - Vector2.one * 45f, pos);
				if (dist < 45f * Random.Range(0f, 12f / (dist * 0.8f)) && Random.Range(0f, 1f) < 0.7f)
				{
					Vector2Int cell = new Vector2Int(Mathf.Clamp(vp.x - 45 + i, 0, (int)(W.width - 1)), Mathf.Clamp(vp.y - 45 + j + 2, 0, (int)(W.height - 1)));
					if (WB[cell.x, cell.y] > 0)
					{
						WB[cell.x, cell.y] = (ushort)Random.Range(0, 5);
					}
				}
			}
		}
	}

	private static void BioContainerAt(Vector2Int c, float lo, float hi, float chance)
	{
		int n = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			if (GroundAbove(pos, out var ground))
			{
				pos = W.BlockToWorldPos(ground);
			}
			W.GenerateBlockCircle(pos, 16, (ushort)3, 0.8f, 0f, false, false, false);
			W.GenerateBlockCircle(pos, 20, (ushort)4, 0.3f, 0f, false, false, false);
			W.GenerateBlockCircle(pos, 16, (ushort)0, 0.15f, 0f, false, false, false);
			W.GenerateObjectAtPos(ground, GetStruct("BioContainer").transform.GetChild(0).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(W.BlockToWorldPos(ground), GetStruct("BioContainer"));
		}
	}

	private static void BridgeAt(Vector2Int c, float lo, float hi, string res, float chance, bool raycast = true)
	{
		int n = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			if (raycast && GroundAbove(pos, out var ground))
			{
				pos = W.BlockToWorldPos(ground);
			}
			W.GenerateObjectAtPos(W.WorldToBlockPos(pos), GetStruct(res).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(pos, GetStruct(res));
		}
	}

	private static void PodAt(Vector2Int c, float lo, float hi, string res, float chance, bool entity = true)
	{
		int n = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < n; i++)
		{
			Vector2 pos = RandPosInChunk(c);
			if (GroundAbove(pos, out var ground))
			{
				pos = W.BlockToWorldPos(ground);
			}
			W.GenerateBlockCircle(pos, 16, (ushort)3, 0.5f, 0f, false, false, false);
			W.GenerateBlockCircle(pos, 20, (ushort)4, 0.2f, 0f, false, false, false);
			W.GenerateObjectAtPos(ground, GetStruct(res).GetComponent<Tilemap>(), chance, true);
			if (entity)
			{
				W.GenerateEntityAtPos(W.BlockToWorldPos(ground), GetStruct(res));
			}
		}
	}

	private static void IronVein(Vector2Int c, int width)
	{
		Vector2Int start = new Vector2Int(Random.Range(c.x * CS, c.x * CS + CS), Random.Range(c.y * CS, c.y * CS + CS));
		int len = Random.Range(1, 5);
		for (int i = 0; i < len; i++)
		{
			for (int j = 0; j < width; j++)
			{
				if (start.x + i < W.width && start.y + j < W.height)
				{
					WB[start.x + i, start.y + j] = 5;
				}
			}
		}
	}

	private static void GenChunkTerrainInto(Vector2Int c, ushort[,] wb)
	{
		int bx = c.x * CS;
		int by = c.y * CS;
		if (biome <= 1)
		{
			for (int i = bx; i < bx + CS; i++)
			{
				for (int j = by; j < by + CS; j++)
				{
					caveNoise.SetFrequency(0.06f + frequencyMap.GetNoise((float)i, (float)j) * 0.01f);
					ushort block = ((caveNoise.GetNoise((float)i, (float)j) > -0.715f) ? ((ushort)1) : ((ushort)0));
					float dirt = dirtPerlin.GetNoise((float)i, (float)j);
					if (block > 0 && dirt < -0.1f)
					{
						block = (ushort)((dirt < -0.33f) ? 16u : 2u);
					}
					if (block > 0 && R(0f, 1f) > 0.99f)
					{
						block = (ushort)RI(1, 5);
					}
					float biomeV = biomeMap.GetNoise((float)i, (float)j);
					if (biomeV > 0.1f)
					{
						block = (ushort)RI(3, 5);
					}
					if (block > 0 && biomeV < -0.8f)
					{
						block = 15;
					}
					if (biome == 1 && (float)j < (float)W.height * 0.5f)
					{
						float depthF = (float)j / (float)W.height * 2f;
						if (R(0f, 1f) > depthF && block == 2)
						{
							block = 12;
						}
						if ((float)j < (float)W.height * 0.33f && R(0f, 1f) > depthF * 3f && block == 1)
						{
							block = 13;
						}
					}
					wb[i - bx, j - by] = block;
				}
			}
			return;
		}
		if (biome == 2 || biome == 3)
		{
			for (int k = bx; k < bx + CS; k++)
			{
				for (int l = by; l < by + CS; l++)
				{
					float biomeV = biomeMap.GetNoise((float)k, (float)l);
					float freq = frequencyMap.GetNoise((float)k, (float)l) * 0.25f + 0.1f;
					if (marbleMap.GetNoise((float)k, (float)l) <= minMarble)
					{
						ushort block = (ushort)((biomeV > freq && dirtPerlin.GetNoise((float)k, (float)l) < -0.4f) ? ((biomeV < freq + 0.1f) ? 12u : 13u) : 0u);
						if (caveNoise.GetNoise((float)k, (float)l) > 0.65f)
						{
							block = 17;
						}
						if (biomeV > 0.75f)
						{
							block = 15;
						}
						if (biomeMap2.GetNoise((float)k, (float)l) > 0.1f)
						{
							block = (ushort)RI(3, 5);
						}
						if (biome == 3 && block > 0 && RV() < 0.1f)
						{
							block = (ushort)(15 + RI(0, 2));
						}
						wb[k - bx, l - by] = block;
					}
					else
					{
						wb[k - bx, l - by] = (ushort)((biomeV > freq) ? ((dirtPerlin.GetNoise((float)k, (float)l) < -0.1f) ? 18u : 19u) : 0u);
					}
					if (biome == 3 && toxicNoise.GetNoise((float)k, (float)l) < -0.8f && RV() > 0.5f)
					{
						wb[k - bx, l - by] = 22;
					}
					if (biome == 3 && wb[k - bx, l - by] > 0 && RV() > (float)(l + W.halfHeight) / (float)W.height)
					{
						wb[k - bx, l - by] = 23;
					}
				}
			}
			return;
		}
		float lastFreq = float.NaN;
		for (int m = bx; m < bx + CS; m++)
		{
			for (int n = by; n < by + CS; n++)
			{
				float freq = 0.0189f - (float)n / (float)W.height * 0.002f;
				if (freq != lastFreq)
				{
					marbleMap.SetFrequency(freq);
					lastFreq = freq;
				}
				float marble = marbleMap.GetNoise((float)m, (float)n) + R(-0.1f, 0.1f);
				ushort block = (ushort)((marble > 0.15f && marble < 0.25f) ? 23 : ((marble >= 0.25f && marble < 0.45f) ? 16 : ((marble >= 0.45f && marble < 0.66f) ? 15 : ((marble >= 0.66f) ? 19 : 0))));
				if (biomeMap2.GetNoise((float)m, (float)n) < -0.735f)
				{
					block = 0;
				}
				wb[m - bx, n - by] = block;
			}
		}
	}

	private static void GenChunkOres(Vector2Int c)
	{
		ushort[,] wB = WB;
		if (biome == 4)
		{
			for (int i = 0; i < 4; i++)
			{
				if (Random.value < 0.00025f)
				{
					int x = c.x * CS + Random.Range(0, CS);
					int y = c.y * CS + Random.Range(0, CS);
					if (wB[x, y] > 0)
					{
						wB[x, y] = 35;
					}
				}
			}
		}
		else
		{
			if (Random.value >= 0.5f)
			{
				return;
			}
			int x = c.x * CS + Random.Range(0, CS);
			int y = c.y * CS + Random.Range(0, CS);
			if (wB[x, y] == 0)
			{
				return;
			}
			for (int steps = Random.Range(1, 26); steps > 0; steps--)
			{
				if (wB[x, y] > 0)
				{
					wB[x, y] = 34;
				}
				x += ((Random.value > 0.5f) ? ((Random.value > 0.5f) ? 1 : (-1)) : 0);
				y += ((Random.value > 0.5f) ? ((Random.value > 0.5f) ? 1 : (-1)) : 0);
				if (x < 0 || y < 0 || x >= W.width || y >= W.height)
				{
					break;
				}
			}
			if (W.biomeDepth > 0)
			{
				VeinChunk(c, Random.Range(0.35f, 0.5f), 5, 3, 6, 64, horizontal: true);
				VeinChunk(c, Random.Range(0.35f, 0.5f), 5, 3, 6, 60, horizontal: false);
			}
			else
			{
				VeinChunk(c, Random.Range(0.35f, 0.4f), 11, 2, 6, 48, horizontal: true);
				VeinChunk(c, Random.Range(0.35f, 0.4f), 11, 2, 6, 48, horizontal: false);
			}
		}
	}

	private static void VeinChunk(Vector2Int c, float amt, ushort block, int w, int lenMin, int lenMax, bool horizontal)
	{
		int n = Poisson(amt);
		for (int i = 0; i < n; i++)
		{
			Vector2Int start = new Vector2Int(c.x * CS + Random.Range(0, CS), c.y * CS + Random.Range(0, CS));
			int len = Random.Range(lenMin, lenMax);
			for (int j = 0; j < len; j++)
			{
				for (int k = 0; k < w; k++)
				{
					int x = (horizontal ? (start.x + j) : (start.x + k));
					int y = (horizontal ? (start.y + k) : (start.y + j));
					if (x < W.width && y < W.height)
					{
						WB[x, y] = block;
					}
				}
			}
		}
	}

	private static void GenChunkLiquids(Vector2Int c)
	{
		int cx = c.x * CS + CS / 2;
		int cy = c.y * CS + CS / 2;
		if (biome == 0)
		{
			PlaceLiquidsChunk(128f, 1, 32, cx, cy);
		}
		else if (biome == 1)
		{
			PlaceLiquidsChunk(10f, 1, 400, cx, cy);
			PlaceLiquidsChunk(18f, 2, 128, cx, cy);
		}
		else if (biome == 2 || biome == 3)
		{
			PlaceLiquidsChunk(50f, 1, 26, cx, cy);
			PlaceLiquidsChunk(15f, 3, 128, cx, cy);
		}
		else
		{
			PlaceLiquidsChunk(30f, 1, 128, cx, cy);
			PlaceLiquidsChunk(10f, 2, 50, cx, cy);
		}
	}

	private static void PlaceLiquidsChunk(float perChunk, byte type, int maxFill, int cx, int cy)
	{
		Vector2 pos = default(Vector2);
		for (int i = 0; i < (int)perChunk; i++)
		{
			pos = new Vector2((float)(cx - CS / 2) + Random.Range(0f, (float)CS), (float)(cy - CS / 2) + Random.Range(0f, (float)CS));
			FluidManager.main.StartFill(WorldToBlockPos(pos), type, maxFill);
		}
	}

	private static Vector2Int WorldToBlockPos(Vector2 pos)
	{
		return new Vector2Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
	}

	private static void GenChunkEntities(Vector2Int c)
	{
		float totalLootRarity = W.totalLootRarity;
		float totalTrapRarity = W.totalTrapRarity;
		float lootRarityMultiplier = W.lootRarityMultiplier;
		for (int i = 0; i < 5; i++)
		{
			if (Random.value < 0.015f)
			{
				D(c, Crystals[Random.Range(0, Crystals.Length)], 1f, 1f, 2f, 0f, 0f, inGround: false, flip: true);
			}
		}
		if (biome <= 1)
		{
			D(c, "glowplant", 2.7f, 3.5f, 1.25f, 10f, 0.25f, inGround: false, flip: true, SoftCheck);
			D(c, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, inGround: false, flip: true, SoftCheck);
			D(c, "ceilingrye", 0.3f, 0.4f, 1f, 10f, 0.5f, inGround: false, flip: true, SoftCheck, Vector2.up);
			D(c, "medcrate", 0.18f * totalLootRarity, 0.2f * totalLootRarity, 3f, 180f);
			D(c, "containercrate", 0.05f * totalLootRarity, 0.07f * totalLootRarity, 3f, 180f);
			D(c, "foodbox", 0.1f * totalLootRarity, 0.13f * totalLootRarity, 3f, 180f);
			D(c, "spikestabber", 0.4f * totalTrapRarity, 0.5f * totalTrapRarity);
			D(c, "shadecrawler", 0.4f * totalTrapRarity, 0.42f * totalTrapRarity, 2f, 180f);
			D(c, "corpse", 1f * lootRarityMultiplier, 1.1f * lootRarityMultiplier, 0f, 0f, 0f, inGround: false, flip: false, CorpseCheck);
			D(c, "animalcorpse", 0.6f * lootRarityMultiplier, 0.7f * lootRarityMultiplier, 0f, 0f, 0f, inGround: false, flip: false, CorpseCheck);
			D(c, "drillpod", 0.09f, 0.1f, 0f, 0f, 0f, inGround: true, flip: true);
			if (biome > 0)
			{
				D(c, "barbedwirefence", 0.6f * totalTrapRarity, 0.8f * totalTrapRarity, 4.8f);
				D(c, "beartrap", 0.2f * totalTrapRarity, 0.25f * totalTrapRarity, 1f);
				D(c, "CaveTicks", 0.15f * totalTrapRarity, 0.2f * totalTrapRarity, 4f, 0f, 3f);
				D(c, "geyser", 1.6f, 1.8f, 0.6f, 0f, 0f, inGround: false, flip: false, SoftCheck);
			}
			else
			{
				D(c, "geyser", 0.7f, 0.8f, 0.6f, 0f, 0f, inGround: false, flip: false, SoftCheck);
			}
			D(c, "jumppad", 0.6f * totalTrapRarity, 0.8f * totalTrapRarity);
			D(c, "geotree", 2.7f, 3f, 3f, 6f, 0.15f, inGround: false, flip: true, SoilCheck);
			D(c, "hydreed", 1.4f, 1.6f, 2.6f, 6f, 0.4f, inGround: false, flip: true, SoilCheck);
			D(c, "leadbush", 2f, 2.2f, 0.6f, 6f, 0.1f, inGround: false, flip: true, SoilCheck);
			return;
		}
		if (biome == 2 || biome == 3)
		{
			float biomeMult = ((biome == 2) ? 1f : 2f);
			D(c, "glowplant", 2.4f * biomeMult, 2.5f * biomeMult, 1.25f, 10f, 0.25f, inGround: false, flip: true, SandCheck);
			D(c, "stoneplant", 0.4f * ((biome == 2) ? 1f : 3f), 0.5f * ((biome == 2) ? 1f : 3f), 1.9f, 10f, 0.1f, inGround: false, flip: true, SandCheck);
			D(c, "cactus", 1.4f, 1.6f, 2.1f, 10f, 0.3f, inGround: false, flip: true, SandCheck);
			D(c, "sandrose", 1.3f, 1.4f, 1.5f, 10f, 0f, inGround: false, flip: true, SandCheck);
			D(c, "drybush", 6f, 7f, 2f, 20f, 0f, inGround: false, flip: true, SandCheck);
			D(c, "brownshroom", 4f, 5f, 0.9f, 10f, 0f, inGround: false, flip: true, SandCheck);
			D(c, "stalagmite", 10f, 15f, 2.8f, 0f, 0.15f, inGround: false, flip: true, StoneCheck);
			D(c, "jumppad", 0.25f * totalTrapRarity, 0.35f * totalTrapRarity);
			D(c, "landmine", 0.13f * totalTrapRarity, 0.16f * totalTrapRarity, 0.4f);
			D(c, "ceilingrye", 0.08f, 0.1f, 1f, 10f, 0.5f, inGround: false, flip: true, SoftCheck, Vector2.up);
			if (biome == 3)
			{
				D(c, "spentfuel", 0.3f * totalTrapRarity, 0.35f * totalTrapRarity, 1.875f);
				D(c, "soundcannon", 0.4f * totalTrapRarity, 0.45f * totalTrapRarity, 1f);
				D(c, "foodbox", 0.1f * totalLootRarity, 0.13f * totalLootRarity, 3f, 180f);
				D(c, "pop", 3f * totalLootRarity, 4f * totalLootRarity, 2f, 20f, 0.2f, inGround: false, flip: true, SandCheck);
				D(c, "coil", 0.2f * totalTrapRarity, 0.3f * totalTrapRarity, 2f);
			}
			else
			{
				D(c, "wallbiter", 0.12f * totalTrapRarity, 0.13f * totalTrapRarity, 4.8f);
				D(c, "shadecrawler", 0.2f * totalTrapRarity, 0.2f * totalTrapRarity, 4.8f);
				D(c, "droppings", 0.75f, 0.82f);
				D(c, "beartrap", 0.1f * totalTrapRarity, 0.2f * totalTrapRarity, 1f);
				D(c, "barbedwirefence", 0.7f * totalTrapRarity, 0.8f * totalTrapRarity, 4.8f);
			}
			D(c, "rag", 0.12f * lootRarityMultiplier * ((biome == 2) ? 1f : 2.5f), 0.2f * lootRarityMultiplier * ((biome == 2) ? 1f : 2.5f), 1f);
			D(c, "corpse", 0.75f * lootRarityMultiplier * ((biome == 2) ? 1f : 2f), 0.82f * lootRarityMultiplier * ((biome == 2) ? 1f : 2f), 0f, 0f, 0f, inGround: false, flip: false, CorpseCheck);
			return;
		}
		D(c, "glowplant", 0.2f, 0.3f, 1.25f, 10f, 0.25f, inGround: false, flip: true, SoilCheck);
		D(c, "shadecrawler", 0.45f * totalTrapRarity, 0.5f * totalTrapRarity, 2f, 180f);
		D(c, "wallbiter", 0.1f * totalTrapRarity, 0.11f * totalTrapRarity, 4.8f);
		D(c, "thornbackyoung", 0.24f * totalTrapRarity, 0.26f * totalTrapRarity, 4.8f);
		D(c, "overgrowntick", 0.1f * totalTrapRarity, 0.12f * totalTrapRarity, 4.8f);
		D(c, "caveticks", 0.15f * totalTrapRarity, 0.16f * totalTrapRarity, 4.8f);
		if (Random.value < 0.012f)
		{
			D(c, "thornbackelder", 1f, 1f);
		}
		D(c, "stoneplant", 0.4f, 0.5f, 1.9f, 10f, 0.1f, inGround: false, flip: true, SoilCheck);
		D(c, "ceilingrye", 0.65f, 0.8f, 1f, 10f, 0.5f, inGround: false, flip: true, SoftCheck, Vector2.up);
		D(c, "medcrate", 0.18f * totalLootRarity, 0.2f * totalLootRarity, 3f, 180f);
		D(c, "containercrate", 0.05f * totalLootRarity, 0.07f * totalLootRarity, 3f, 180f);
		D(c, "foodbox", 0.1f * totalLootRarity, 0.13f * totalLootRarity, 3f, 180f);
		D(c, "corpse", 1.1f * lootRarityMultiplier, 1.2f * lootRarityMultiplier, 0f, 0f, 0f, inGround: false, flip: false, CorpseCheck);
		D(c, "animalcorpse", 0.9f * lootRarityMultiplier, 0.95f * lootRarityMultiplier, 0f, 0f, 0f, inGround: false, flip: false, CorpseCheck);
		D(c, "geotree", 0.4f, 0.5f, 3f, 6f, 0.15f, inGround: false, flip: true, SoilCheck);
		D(c, "browncap", 0.4f, 0.5f, 3f, 6f, 0.15f, inGround: false, flip: true, SoilCheck);
		D(c, "hydreed", 0.6f, 0.7f, 2.6f, 6f, 0.4f, inGround: false, flip: true, SoilCheck);
		D(c, "leadbush", 1.1f, 1.2f, 0.6f, 6f, 0.1f, inGround: false, flip: true, SoilCheck);
		D(c, "droppings", 3.7f, 4f);
		D(c, "pop", 1f * totalLootRarity, 1.1f * totalLootRarity, 2f, 20f, 0.2f, inGround: false, flip: true, SoilCheck);
		D(c, "bananaplant", 1.9f * totalTrapRarity, 2f * totalTrapRarity, 0.4f, 15f, 0.1f, inGround: false, flip: true, SoilCheck);
		D(c, "coil", 0.2f * totalTrapRarity, 0.3f * totalTrapRarity, 2f);
		D(c, "beartrap", 0.1f * totalTrapRarity, 0.2f * totalTrapRarity, 1f);
		D(c, "jumppad", 0.25f * totalTrapRarity, 0.35f * totalTrapRarity);
		D(c, "spikestabber", 0.4f * totalTrapRarity, 0.5f * totalTrapRarity);
		D(c, "grabberplant", 0.4f * totalTrapRarity, 0.5f * totalTrapRarity);
		D(c, "geyser", 0.7f, 0.8f, 0.6f, 0f, 0f, inGround: false, flip: false, SoftCheck);
		D(c, "skullcrusher", 1.1f, 1.2f, 1f, 10f, 0f, inGround: false, flip: true, null, Vector2.up);
	}

	private static bool FindSurface(int wx, int wy, Vector2 dir, out int hx, out int hy)
	{
		hx = wx;
		hy = wy;
		int sx = (int)Mathf.Sign(dir.x);
		int sy = (int)Mathf.Sign(dir.y);
		for (int i = 0; i < 16; i++)
		{
			int bx = wx + sx * i;
			int by = wy + sy * i;
			if (bx < 0 || by < 0 || bx >= W.width || by >= W.height)
			{
				return false;
			}
			if (WB[bx, by] > 0)
			{
				hx = bx;
				hy = by;
				return true;
			}
		}
		return false;
	}

	private static bool SoftCheck(int bx, int by)
	{
		return WB[bx, by] < 3 || IsSoil(bx, by);
	}

	private static bool SoilCheck(int bx, int by)
	{
		return IsSoil(bx, by);
	}

	private static bool SandCheck(int bx, int by)
	{
		return WB[bx, by] == 12 || WB[bx, by] == 13 || IsSoil(bx, by);
	}

	private static bool StoneCheck(int bx, int by)
	{
		return WB[bx, by] == 17 || WB[bx, by] == 18 || WB[bx, by] == 19;
	}

	private static bool CorpseCheck(int bx, int by)
	{
		return WB[bx, by] > 0 && WB[bx - 1, by] > 0 && WB[bx + 1, by] > 0;
	}

	private static bool IsSoil(int bx, int by)
	{
		ushort b = WB[bx, by];
		return b == 2 || b == 15 || b == 16 || b == 23 || (b > 30 && b < 34);
	}

	private static void D(Vector2Int c, string name, float min, float max, float yOff = 0f, float rot = 0f, float yDev = 0f, bool inGround = false, bool flip = false, Func<int, int, bool> check = null, Vector2 dir = default(Vector2))
	{
		float rnd = Random.Range(min, max);
		int count = (int)rnd;
		if (Random.value < rnd - (float)count)
		{
			count++;
		}
		int bx = c.x * CS;
		int by = c.y * CS;
		for (int i = 0; i < count; i++)
		{
			int wx = bx + Random.Range(0, CS);
			int wy = by + Random.Range(0, CS);
			if (WB[wx, wy] > 0 || !FindSurface(wx, wy, (dir == default(Vector2)) ? Vector2.down : dir, out var hx, out var hy) || (check != null && !check(hx, hy)))
			{
				continue;
			}
			GameObject structObj = GetStructObj(name);
			if (structObj == null)
			{
				continue;
			}
			Vector2 point = new Vector2((float)hx + 0.5f, (float)hy + 1f);
			float off = Random.Range(yOff - yDev, yOff + yDev);
			GameObject go = Object.Instantiate<GameObject>(structObj, point - dir * off, Quaternion.Euler(0f, 0f, Random.Range(0f - rot, rot)));
			BuildingEntity comp = go.GetComponent<BuildingEntity>();
			if (comp != null)
			{
				comp.blockPlacedOn = new Vector2Int(hx, hy);
				if (inGround && W.ChunkUpdated[c.x, c.y] != null)
				{
					W.ChunkUpdated[c.x, c.y].AddListener(comp.CheckSeating);
				}
			}
			if (flip && Random.value < 0.5f)
			{
				Vector3 localScale = go.transform.localScale;
				localScale.x *= -1f;
				go.transform.localScale = localScale;
			}
		}
	}
}









