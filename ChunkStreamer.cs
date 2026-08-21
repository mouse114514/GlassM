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

	private static readonly TileBase[] renderTiles = (TileBase[])(object)new TileBase[CS * CS];

	private static readonly Dictionary<string, GameObject> structRes = new Dictionary<string, GameObject>();

	private static long diagApplyMs;

	private static long diagRenderMs;

	private static long diagStructMs;

	private static long diagRefreshMs;

	private static long diagOreMs;

	private static int diagApplyCount;

	private static int diagEntityCount;

	private static bool structPlaced;

	private static readonly List<Vector2Int> mpPlayerChunks = new List<Vector2Int>();

	private static readonly List<Vector2Int> mpLastEnqueued = new List<Vector2Int>();

	private static System.Reflection.FieldInfo f_netBodyAll;

	private static bool mpNetBodyChecked;

	private static readonly System.Random terrainRng = new System.Random(12345);

	public const int GEN_RADIUS = 3;

	public const int UNLOAD_RADIUS = 3;

	public const int RENDER_RADIUS = 1;

	public const int INIT_RADIUS = 1;

	public const int MAX_PER_FRAME = 4;

	public const float PHYSICS_DT = 0.03f;

	public static Vector2Int PlayerChunk = new Vector2Int(8, 8);

	private static int lastScanKey = int.MinValue;

	private static Vector2Int lastPlayerChunk;

	private static Vector2Int moveDir;

	private static int diagTickCount;

	private static int diagGenCount;

	private static int diagRenderCount;

	private static int diagRenderFixed;

	private static float diagLastLogTime;
	private static float diagBlockLogTime;

	private static bool spawnProtected;

	private static Vector2Int spawnCenter;

	private static bool[,] protectedCell;

	public static readonly bool StreamOn = true;

	private static readonly string[] Crystals = new string[7] { "BloodCrystal", "SoothingCrystal", "ReliefCrystal", "TurbulentCrystal", "OxygenCrystal", "EmissiveCrystal", "DigestionCrystal" };

	public static ushort[,] WB => (ushort[,])f_worldBlocks.GetValue(W);

	public static Tilemap[,] CH => (Tilemap[,])f_chunks.GetValue(W);

	public static int QueueCount => queue.Count;

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
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Expected O, but got Unknown
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
		Diag.Log("[CSDIAG] new world w=" + w.width + " h=" + w.height + " biome=" + w.biomeDepth + " fwb=" + (f_worldBlocks != null) + " fch=" + (f_chunks != null));
		foreach (string r in Patches.Report)
		{
			Diag.Log("[CSDIAG] " + r);
		}
		Diag.Log("[CSDIAG] report end, patches=" + Patches.Report.Count);
		Array.Clear(genData, 0, genData.Length);
		Array.Clear(colliderOn, 0, colliderOn.Length);
		Array.Clear(inQueue, 0, inQueue.Length);
		Array.Clear(genFull, 0, genFull.Length);
		Array.Clear(genApplied, 0, genApplied.Length);
		Array.Clear(dirtyRender, 0, dirtyRender.Length);
		queue.Clear();
		protectedCell = new bool[(int)w.width, (int)w.height];
		spawnProtected = false;
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
		protectedCell = null;
		spawnProtected = false;
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
			caveNoise.SetNoiseType((NoiseType)2);
			caveNoise.SetFrequency(0.06f);
			caveNoise.SetFractalOctaves(3);
			caveNoise.SetFractalType((FractalType)1);
			caveNoise.SetFractalLacunarity(1.5f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType((NoiseType)3);
			dirtPerlin.SetFractalType((FractalType)1);
			dirtPerlin.SetFractalOctaves(7);
			dirtPerlin.SetFrequency(0.035f);
			frequencyMap = NewNoise();
			frequencyMap.SetNoiseType((NoiseType)3);
			frequencyMap.SetFrequency(0.00037f);
			biomeMap = NewNoise();
			biomeMap.SetNoiseType((NoiseType)2);
			biomeMap.SetFrequency(0.04f);
			biomeMap.SetCellularDistanceFunction((CellularDistanceFunction)1);
			biomeMap.SetCellularReturnType((CellularReturnType)1);
			biomeMap.SetCellularJitter(1f);
			biomeMap.SetFractalType((FractalType)2);
			biomeMap.SetFractalLacunarity(1.5f);
		}
		else if (biome == 2 || biome == 3)
		{
			biomeMap = NewNoise();
			biomeMap.SetNoiseType((NoiseType)5);
			biomeMap.SetFrequency(0.086f);
			biomeMap.SetFractalType((FractalType)1);
			biomeMap.SetFractalOctaves((biome == 2) ? 2 : 3);
			biomeMap.SetFractalGain(0.49f);
			biomeMap.SetFractalWeightedStrength(2.34f);
			biomeMap.SetDomainWarpType((DomainWarpType)0);
			biomeMap.SetDomainWarpAmp(22f);
			frequencyMap = NewNoise();
			frequencyMap.SetFrequency(0.006f);
			dirtPerlin = NewNoise();
			dirtPerlin.SetNoiseType((NoiseType)2);
			dirtPerlin.SetFrequency(0.02f);
			dirtPerlin.SetFractalType((FractalType)2);
			dirtPerlin.SetFractalGain(0.65f);
			caveNoise = NewNoise();
			caveNoise.SetFrequency(0.005f);
			caveNoise.SetFractalType((FractalType)3);
			caveNoise.SetFractalGain(0.35f);
			caveNoise.SetDomainWarpType((DomainWarpType)2);
			caveNoise.SetDomainWarpAmp(40f);
			toxicNoise = NewNoise();
			toxicNoise.SetFrequency(0.012f);
			toxicNoise.SetFractalType((FractalType)3);
			toxicNoise.SetFractalGain(0.3f);
			toxicNoise.SetDomainWarpType((DomainWarpType)2);
			toxicNoise.SetDomainWarpAmp(50f);
			biomeMap2 = NewNoise();
			biomeMap2.SetNoiseType((NoiseType)2);
			biomeMap2.SetFrequency(0.05f);
			biomeMap2.SetCellularDistanceFunction((CellularDistanceFunction)1);
			biomeMap2.SetCellularReturnType((CellularReturnType)1);
			biomeMap2.SetCellularJitter(1f);
			biomeMap2.SetFractalType((FractalType)2);
			biomeMap2.SetFractalLacunarity(1.5f);
			marbleMap = NewNoise();
			marbleMap.SetFrequency((biome == 2) ? 0.007f : 0.035f);
			marbleMap.SetNoiseType((NoiseType)3);
			marbleMap.SetDomainWarpType((DomainWarpType)0);
			marbleMap.SetDomainWarpAmp(100f);
			minMarble = ((biome == 2) ? 0.45f : 1f);
		}
		else
		{
			marbleMap = NewNoise();
			marbleMap.SetNoiseType((NoiseType)5);
			marbleMap.SetFractalType((FractalType)2);
			marbleMap.SetFractalOctaves(3);
			marbleMap.SetFractalLacunarity(2.29f);
			marbleMap.SetFractalGain(4f);
			marbleMap.SetFractalWeightedStrength(1.2f);
			marbleMap.SetDomainWarpType((DomainWarpType)0);
			marbleMap.SetDomainWarpAmp(41f);
			biomeMap2 = NewNoise();
			biomeMap2.SetFrequency(0.02f);
			biomeMap2.SetDomainWarpType((DomainWarpType)0);
			biomeMap2.SetDomainWarpAmp(25f);
		}
	}

	private static FastNoiseLite NewNoise()
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Expected O, but got Unknown
		return new FastNoiseLite(Random.Range(0, int.MaxValue));
	}

	public static void GenerateInitial()
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		Vector2Int val = default(Vector2Int);
		val = new Vector2Int((int)((long)(W.width / 2u) / (long)CS), (int)((long)(W.height / 2u) / (long)CS));
		PlayerChunk = val;
		for (int i = val.x; i <= val.x + 1; i++)
		{
			for (int j = val.y; j <= val.y + 1; j++)
			{
				if (i >= 0 && j >= 0 && i <= 15 && j <= 15 && !genData[i, j] && !genApplied[i, j])
				{
					SyncGenAndWait(new Vector2Int(i, j));
				}
			}
		}
		GenSpawnCavity();
		if (StreamOn)
		{
			EnqueueAround(val, GEN_RADIUS, genNow: false);
		}
	}

	private static void GenSpawnCavity()
	{
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)W == (Object)null || WB == null)
		{
			return;
		}
		int num = 508 / CS;
		int num2 = 516 / CS;
		int num3 = 1011 / CS;
		for (int i = num; i <= num2; i++)
		{
			SyncGenAndWait(new Vector2Int(i, num3));
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
		for (int m = num; m <= num2; m++)
		{
			RenderChunk(new Vector2Int(m, num3));
		}
		Plugin.Log.LogInfo((object)("CS: spawn cavity done, cols=" + num + "-" + num2 + " row=" + num3));
	}

	private static void EnqueueAround(Vector2Int c, int radius, bool genNow)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		Vector2Int val = default(Vector2Int);
		for (int i = c.x - radius; i <= c.x + radius; i++)
		{
			for (int j = c.y - radius; j <= c.y + radius; j++)
			{
				val = new Vector2Int(i, j);
				if (!InWorld(val) || genData[val.x, val.y])
				{
					continue;
				}
				if (genNow)
				{
					if (!genApplied[val.x, val.y])
					{
						SyncGenAndWait(val);
					}
				}
				else
				{
					queue.Add(val);
					inQueue[val.x, val.y] = true;
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
		ushort[,] array = new ushort[CS, CS];
		try
		{
			GenChunkTerrainInto(cc, array);
			ApplyBordersInto(array, cc.x * CS, cc.y * CS);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("sync gen failed " + cc.ToString() + ": " + ex));
			return;
		}
		ApplyChunk(cc, array);
	}

	private static void CollectPlayers()
	{
		mpPlayerChunks.Clear();
		try
		{
			if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			{
				Vector3 val = PlayerCamera.main.body.transform.position;
				mpPlayerChunks.Add(new Vector2Int(Mathf.Clamp((int)(val.x + 512f) / CS, 0, 15), Mathf.Clamp((int)(val.y + 512f) / CS, 0, 15)));
			}
		}
		catch
		{
		}
		try
		{
			if (!mpNetBodyChecked)
			{
				mpNetBodyChecked = true;
				f_netBodyAll = Type.GetType("KrokoshaCasualtiesMP.NetBody, KrokoshaCasualtiesMP")?.GetField("all_instances", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
			}
			if (f_netBodyAll != null)
			{
				System.Collections.IEnumerable list = f_netBodyAll.GetValue(null) as System.Collections.IEnumerable;
				if (list != null)
				{
					foreach (object obj in list)
					{
						Component component = obj as Component;
						if ((Object)(object)component != (Object)null && (Object)(object)component.transform != (Object)null)
						{
							Vector3 val2 = component.transform.position;
							mpPlayerChunks.Add(new Vector2Int(Mathf.Clamp((int)(val2.x + 512f) / CS, 0, 15), Mathf.Clamp((int)(val2.y + 512f) / CS, 0, 15)));
						}
					}
				}
			}
		}
		catch
		{
		}
	}

	public static void Tick()
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0223: Unknown result type (might be due to invalid IL or missing references)
		//IL_02be: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02da: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0393: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_041f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0425: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ff: Unknown result type (might be due to invalid IL or missing references)
		bool wbOk = false;
		bool chOk = false;
		try
		{
			wbOk = WB != null;
		}
		catch (Exception)
		{
		}
		try
		{
			chOk = CH != null;
		}
		catch (Exception)
		{
		}
		if (!Active || (Object)(object)W == (Object)null || !wbOk || !chOk)
		{
			if (Time.realtimeSinceStartup - diagBlockLogTime > 5f)
			{
				diagBlockLogTime = Time.realtimeSinceStartup;
				string wState = ((Object)(object)W == (Object)null) ? "0" : "1";
				Diag.Log("[CSDIAG] tick blocked Active=" + (Active ? 1 : 0) + " W=" + wState + " WB=" + (wbOk ? 1 : 0) + " CH=" + (chOk ? 1 : 0));
			}
			return;
		}
		if (Time.fixedDeltaTime != 0.03f)
		{
			Time.fixedDeltaTime = 0.03f;
		}
		CollectPlayers();
		TryProtectSpawn();
		Vector2Int pc = PlayerChunk;
		try
		{
			Vector3 val = (((Object)(object)WorldGeneration.world != (Object)null && (Object)(object)PlayerCamera.main != (Object)null && (Object)(object)PlayerCamera.main.body != (Object)null) ? ((Component)PlayerCamera.main.body).transform.position : Vector3.zero);
			pc = new Vector2Int(Mathf.Clamp((int)(val.x + 512f) / CS, 0, 15), Mathf.Clamp((int)(val.y + 512f) / CS, 0, 15));
			if (pc != lastPlayerChunk)
			{
				Vector2Int val2 = pc - lastPlayerChunk;
				moveDir = new Vector2Int((val2.x > 0) ? 1 : ((val2.x < 0) ? (-1) : 0), (val2.y > 0) ? 1 : ((val2.y < 0) ? (-1) : 0));
				lastPlayerChunk = pc;
			}
			PlayerChunk = pc;
		}
		catch
		{
		}
		bool flag = false;
		if (StreamOn)
		{
			foreach (Vector2Int ppc in mpPlayerChunks)
			{
				for (int i = ppc.x - GEN_RADIUS; i <= ppc.x + GEN_RADIUS; i++)
				{
					for (int j = ppc.y - GEN_RADIUS; j <= ppc.y + GEN_RADIUS; j++)
					{
						if (i >= 0 && j >= 0 && i <= 15 && j <= 15 && !genData[i, j] && !inQueue[i, j])
						{
							queue.Add(new Vector2Int(i, j));
							inQueue[i, j] = true;
							flag = true;
						}
					}
				}
			}
		}
		if (pendingFull.Count > 0)
		{
			int num = Mathf.Min(4, pendingFull.Count);
			for (int k = 0; k < num; k++)
			{
				Vector2Int val3 = pendingFull[0];
				pendingFull.RemoveAt(0);
				try
				{
					GenChunkOres(val3);
					GenChunkLiquids(val3);
					GenChunkEntities(val3);
				}
				catch (Exception ex)
				{
					ManualLogSource log = Plugin.Log;
					Vector2Int val4 = val3;
					log.LogWarning((object)("chunk full gen failed " + val4.ToString() + ": " + ex));
				}
			}
		}
		if (queue.Count > 0)
		{
			if (flag)
			{
				queue.Sort((Vector2Int a, Vector2Int b) => GenPriority(a, pc).CompareTo(GenPriority(b, pc)));
			}
			int num2 = Mathf.Min(4, queue.Count);
			for (int l = 0; l < num2; l++)
			{
				GenChunk(queue[0], full: true);
				queue.RemoveAt(0);
			}
		}
		int num3 = 0;
		int scanKey = 0;
		foreach (Vector2Int ppc2 in mpPlayerChunks)
		{
			scanKey = scanKey * 397 + ppc2.x * 31 + ppc2.y;
		}
		if (scanKey != lastScanKey)
		{
			lastScanKey = scanKey;
			for (int m = 0; m < 16; m++)
			{
				for (int n = 0; n < 16; n++)
				{
					if (!genData[m, n])
					{
						continue;
					}
					bool flag2 = true;
					foreach (Vector2Int ppc in mpPlayerChunks)
					{
						if (Math.Max(Math.Abs(m - ppc.x), Math.Abs(n - ppc.y)) <= UNLOAD_RADIUS)
						{
							flag2 = false;
							break;
						}
					}
					if (flag2 != colliderOn[m, n])
					{
						continue;
					}
					Tilemap val5 = CH[m, n];
					if (!((Object)(object)val5 == (Object)null))
					{
						Collider2D[] components = ((Component)val5).GetComponents<Collider2D>();
						foreach (Collider2D val6 in components)
						{
							((Behaviour)val6).enabled = !flag2;
						}
						Rigidbody2D component = ((Component)val5).GetComponent<Rigidbody2D>();
						if ((Object)(object)component != (Object)null)
						{
							component.simulated = !flag2;
						}
						colliderOn[m, n] = !flag2;
						num3++;
						if (!flag2 && dirtyRender[m, n])
						{
							RenderChunk(new Vector2Int(m, n));
						}
					}
				}
			}
		}
		for (int m2 = pc.x - RENDER_RADIUS; m2 <= pc.x + RENDER_RADIUS; m2++)
		{
			for (int n2 = pc.y - RENDER_RADIUS; n2 <= pc.y + RENDER_RADIUS; n2++)
			{
				if (m2 >= 0 && n2 >= 0 && m2 < 16 && n2 < 16 && dirtyRender[m2, n2] && genApplied[m2, n2])
				{
					RenderChunk(new Vector2Int(m2, n2));
					diagRenderFixed++;
				}
			}
		}
		diagTickCount++;
		if (Time.realtimeSinceStartup - diagLastLogTime > 5f)
		{
			diagLastLogTime = Time.realtimeSinceStartup;
			int dirtyTotal = 0;
			for (int m3 = 0; m3 < 16; m3++)
			{
				for (int n3 = 0; n3 < 16; n3++)
				{
					if (dirtyRender[m3, n3])
					{
						dirtyTotal++;
					}
				}
			}
			string diagLine = string.Concat(new object[]
			{
				"[CSDIAG] ticks=", diagTickCount, " plrs=", mpPlayerChunks.Count, " pc=", pc, " q=", queue.Count, " pf=", pendingFull.Count, " gen=", diagGenCount, " ren=", diagRenderCount, " fix=", diagRenderFixed, " dirty=", dirtyTotal, " colFlip=", num3, " ore=", diagOreMs, "ms ren=", diagRenderMs, "ms struct=", diagStructMs, "ms refresh=", diagRefreshMs, "ms apply=", diagApplyMs, "ms"
			});
			Diag.Log(diagLine);
			diagTickCount = 0;
			diagGenCount = 0;
			diagRenderCount = 0;
			diagRenderFixed = 0;
			diagOreMs = 0L;
			diagRenderMs = 0L;
			diagStructMs = 0L;
			diagRefreshMs = 0L;
			diagApplyMs = 0L;
			diagApplyCount = 0;
		}
	}

	private static int Dist2(Vector2Int a, Vector2Int b)
	{
		int num = a.x - b.x;
		int num2 = a.y - b.y;
		return num * num + num2 * num2;
	}

	private static int GenPriority(Vector2Int a, Vector2Int b)
	{
		int num = a.x - b.x;
		int num2 = a.y - b.y;
		int num3 = num * num + num2 * num2;
		if (moveDir.x != 0 && num != 0 && num > 0 == moveDir.x > 0)
		{
			num3 -= 16;
		}
		if (moveDir.y != 0 && num2 != 0 && num2 > 0 == moveDir.y > 0)
		{
			num3 -= 16;
		}
		return num3;
	}

	private static void ApplyBordersInto(ushort[,] wb, int ox, int oy)
	{
		int x0 = ox;
		int x1 = ox + CS;
		if (x0 < 8)
		{
			int right = Math.Min(x1, 8);
			for (int i = x0; i < right; i++)
			{
				for (int j = 0; j < CS; j++)
				{
					if (Random.Range(0f, 1f) > (float)i * 0.125f)
					{
						wb[i - ox, j] = 14;
					}
				}
			}
		}
		int width = (int)W.width;
		if (x1 > width - 8)
		{
			int left = Math.Max(x0, width - 8);
			for (int k = left; k < x1; k++)
			{
				for (int l = 0; l < CS; l++)
				{
					if (Random.Range(0f, 1f) > (float)(width - 1 - k) * 0.125f)
					{
						wb[k - ox, l] = 14;
					}
				}
			}
		}
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
		ushort[,] array = new ushort[CS, CS];
		try
		{
			GenChunkTerrainInto(c, array);
			ApplyBordersInto(array, c.x * CS, c.y * CS);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("chunk gen failed " + c.ToString() + ": " + ex));
			genData[c.x, c.y] = false;
			return;
		}
		ApplyChunk(c, array);
		diagGenCount++;
	}

	private static void ApplyChunk(Vector2Int c, ushort[,] data)
	{
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0133: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		if (genApplied[c.x, c.y])
		{
			return;
		}
		genApplied[c.x, c.y] = true;
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			int num = c.x * CS;
			int num2 = c.y * CS;
			for (int i = 0; i < CS; i++)
			{
				for (int j = 0; j < CS; j++)
				{
					WB[num + i, num2 + j] = data[i, j];
				}
			}
			Stopwatch stopwatch2 = Stopwatch.StartNew();
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
			diagOreMs += stopwatch2.ElapsedMilliseconds;
			Stopwatch stopwatch3 = Stopwatch.StartNew();
			RenderChunk(c);
			diagRenderMs += stopwatch3.ElapsedMilliseconds;
			Stopwatch stopwatch4 = Stopwatch.StartNew();
			bool flagStruct = GenChunkStructures(c);
			diagStructMs += stopwatch4.ElapsedMilliseconds;
			Stopwatch stopwatch5 = Stopwatch.StartNew();
			if (flagStruct)
			{
				RefreshAround(c);
			}
			diagRefreshMs += stopwatch5.ElapsedMilliseconds;
			diagApplyMs += stopwatch.ElapsedMilliseconds;
			diagApplyCount++;
		}
		catch (Exception ex)
		{
			ManualLogSource log = Plugin.Log;
			Vector2Int val = c;
			log.LogWarning((object)("chunk apply failed " + val.ToString() + ": " + ex));
			genApplied[c.x, c.y] = false;
			genData[c.x, c.y] = false;
		}
	}

	private static void TryProtectSpawn()
	{
		if (spawnProtected || !Active || W == null)
		{
			return;
		}
		Body body = (PlayerCamera.main != null) ? PlayerCamera.main.body : null;
		if ((Object)(object)body == (Object)null)
		{
			return;
		}
		Vector2Int b = W.WorldToBlockPos(((Component)body).transform.position);
		if (b.x < 0 || b.y < 0 || b.x >= (int)W.width || b.y >= (int)W.height)
		{
			return;
		}
		int dist = Math.Max(Math.Abs(b.x - (int)W.halfWidth), Math.Abs(b.y - (int)W.halfHeight));
		if (dist < 8)
		{
			return;
		}
		spawnProtected = true;
		spawnCenter = b;
		Plugin.Log.LogInfo((object)("CS: spawn recorded at block " + b.ToString()));
	}

	public static void ProtectObjectArea(Vector2Int pos, Tilemap tilemap)
	{
		if (protectedCell == null || (Object)(object)tilemap == (Object)null)
		{
			return;
		}
		BoundsInt bounds = tilemap.cellBounds;
		for (int i = bounds.xMin; i < bounds.xMax; i++)
		{
			for (int j = bounds.yMin; j < bounds.yMax; j++)
			{
				if (!tilemap.HasTile(new Vector3Int(i, j)))
				{
					continue;
				}
				int gx = pos.x + i;
				int gy = pos.y + j;
				if (gx >= 0 && gy >= 0 && gx < protectedCell.GetLength(0) && gy < protectedCell.GetLength(1))
				{
					protectedCell[gx, gy] = true;
				}
			}
		}
	}

	public static void RefreshChunkAtBlock(Vector2Int blockPos)
	{
		int cx = blockPos.x / CS;
		int cy = blockPos.y / CS;
		if (InWorld(new Vector2Int(cx, cy)) && genApplied[cx, cy])
		{
			RenderChunk(new Vector2Int(cx, cy), true);
		}
	}

	private static void ApplyProtected(ushort[,] wb, int ox, int oy)
	{
		if (protectedCell != null)
		{
			int width = protectedCell.GetLength(0);
			int height = protectedCell.GetLength(1);
			for (int i = 0; i < CS; i++)
			{
				for (int j = 0; j < CS; j++)
				{
					int gx = ox + i;
					int gy = oy + j;
					if (gx < width && gy < height && protectedCell[gx, gy])
					{
						wb[i, j] = WB[gx, gy];
					}
				}
			}
		}
		if (spawnProtected)
		{
			int minX = Mathf.Max(ox, spawnCenter.x - 16);
			int maxX = Mathf.Min(ox + CS - 1, spawnCenter.x + 16);
			int minY = Mathf.Max(oy, spawnCenter.y - 16);
			int maxY = Mathf.Min(oy + CS - 1, spawnCenter.y + 16);
			if (minX <= maxX && minY <= maxY)
			{
				for (int i = minX; i <= maxX; i++)
				{
					for (int j = minY; j <= maxY; j++)
					{
						wb[i - ox, j - oy] = WB[i, j];
					}
				}
			}
		}
	}

	private static void RenderChunk(Vector2Int c, bool force = false)
	{
		Tilemap val = CH[c.x, c.y];
		if ((Object)(object)val == (Object)null)
		{
			return;
		}
		diagRenderCount++;
		int num = c.x * CS;
		int num2 = c.y * CS;
		int hALFCHUNKSIZE = W.HALFCHUNKSIZE;
		if (!force && Math.Max(Math.Abs(c.x - PlayerChunk.x), Math.Abs(c.y - PlayerChunk.y)) > RENDER_RADIUS)
		{
			dirtyRender[c.x, c.y] = true;
			return;
		}
		int num3 = 0;
		for (int i = 0; i < CS; i++)
		{
			for (int j = 0; j < CS; j++)
			{
				renderTiles[num3++] = W.tiles[WB[num + j, num2 + i]];
			}
		}
		val.SetTilesBlock(new BoundsInt(-hALFCHUNKSIZE, -hALFCHUNKSIZE, 0, CS, CS, 1), renderTiles);
		dirtyRender[c.x, c.y] = false;
	}

	private static int Poisson(float lambda)
	{
		float num = (float)Math.Exp(0f - lambda);
		int num2 = 0;
		float num3 = 1f;
		do
		{
			num2++;
			num3 *= Random.value;
		}
		while (num3 > num && num2 < 1000);
		return num2 - 1;
	}

	private static bool GroundAbove(Vector2 pos, out Vector2Int ground, float maxDist = 400f)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		Vector2Int val = W.WorldToBlockPos(pos);
		int num = val.y;
		while (num >= 0 && (float)(val.y - num) < maxDist)
		{
			if (WB[val.x, num] > 0)
			{
				ground = new Vector2Int(val.x, num);
				return true;
			}
			num--;
		}
		ground = val;
		return false;
	}

	private static Vector2 RandPosInChunk(Vector2Int c)
	{
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		return new Vector2((float)(c.x * CS - W.halfWidth + Random.Range(10, CS - 10)), (float)(c.y * CS - W.halfHeight + Random.Range(10, CS - 10)));
	}

	private static void RefreshAround(Vector2Int c)
	{
		for (int i = c.x - 2; i <= c.x + 2; i++)
		{
			for (int j = c.y - 2; j <= c.y + 2; j++)
			{
				if (i >= 0 && j >= 0 && i <= 15 && j <= 15 && genApplied[i, j])
				{
					RenderChunk(new Vector2Int(i, j), force: true);
				}
			}
		}
	}

	private static bool GenChunkStructures(Vector2Int c)
	{
		structPlaced = false;
		if ((int)W.biomeOverride > 0)
		{
			return false;
		}
		int biomeDepth = W.biomeDepth;
		if (biomeDepth > 4)
		{
			return false;
		}
		try
		{
			float totalLootRarity = W.totalLootRarity;
			int num = Poisson(Random.Range(0.12f, 0.13f));
			for (int i = 0; i < num; i++)
			{
				DropCapsuleAt(c);
			}
			num = Poisson(biomeDepth switch
			{
				2 => Random.Range(0.066f, 0.077f), 
				3 => Random.Range(0.066f, 0.077f) * 2.5f, 
				4 => Random.Range(0.066f, 0.077f), 
				_ => Random.Range(0.055f, 0.066f), 
			});
			for (int j = 0; j < num; j++)
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
				num = Poisson(Random.Range(0.9f, 1.1f));
				for (int k = 0; k < num; k++)
				{
					if (GroundAbove(RandPosInChunk(c), out var ground, 64f))
					{
						structPlaced = true;
						W.GenerateTree(ground);
					}
				}
				PodAt(c, 0.06f, 0.08f, "Structures/CratePod", 0.82f);
				PodAt(c, 0.06f, 0.08f, "Structures/MiniPod", 0.88f);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodCross", 0.95f, entity: false);
				PodAt(c, 0.03f, 0.05f, "Structures/WoodHorizontal", 0.95f, entity: false);
				PodAt(c, 0.04f, 0.05f, "Structures/BrickLoot", 0.925f);
				BioContainerAt(c, 0.03f, 0.04f, 0.975f);
			}
			num = Poisson(Random.Range(0.088f, 0.1f));
			for (int l = 0; l < num; l++)
			{
				LifePodAt(c);
			}
		}
		catch (Exception ex)
		{
			ManualLogSource log = Plugin.Log;
			Vector2Int val = c;
			log.LogWarning((object)("chunk structures failed " + val.ToString() + ": " + ex));
		}
		return structPlaced;
	}

	private static void DropCapsuleAt(Vector2Int c)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		Vector2 val = RandPosInChunk(c);
		if (GroundAbove(val, out var ground))
		{
			val = W.BlockToWorldPos(ground);
		}
		structPlaced = true;
		Object.Instantiate<GameObject>(GetStructObj("dropcapsule"), (Vector2)(val), Quaternion.Euler(0f, 0f, Random.Range(0f, 360f))).GetComponent<AudioSource>().pitch = Random.Range(0.9f, 1.1f);
		W.GenerateBlockCircle(val, 32, (ushort)3, 0.7f, 0f, false, false, false);
		W.GenerateBlockCircle(val, 30, (ushort)6, 0.04f, 0.04f, false, false, false);
		W.GenerateBlockCircle(val, 4, (ushort)0, 1f, 0.9f, false, false, false);
	}

	private static void CollapsedPodAt(Vector2Int c)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		Vector2 val = RandPosInChunk(c);
		if (GroundAbove(val, out var ground))
		{
			val = W.BlockToWorldPos(ground);
		}
		Vector2Int val2 = W.WorldToBlockPos(val);
		CraterAt(val, val2);
		structPlaced = true;
		W.GenerateObjectAtPos(val2, ((Component)GetStruct("LifepodCollapsed").transform.GetChild(0)).GetComponent<Tilemap>(), 0.88f, true);
		if (Random.value < 0.9f)
		{
			AmmoScript component = Object.Instantiate<GameObject>(GetStructObj(Utils.PickRandom<string>(W.spawnableMagazines)), (Vector2)(val), Quaternion.Euler(0f, 0f, Random.value * 360f)).GetComponent<AmmoScript>();
			component.rounds = Mathf.RoundToInt((float)component.maxRounds * Random.value);
		}
		for (int i = 0; i < 3; i++)
		{
			if (Random.Range(0f, 1f) < 0.3f)
			{
				Object.Instantiate<GameObject>(GetStructObj("experimentflesh"), (Vector2)(val + Vector2.right * Random.Range(-3f, 3f)), Quaternion.identity);
			}
		}
		if (Random.Range(0f, 1f) < 0.8f)
		{
			Object.Instantiate<GameObject>(GetStructObj("internalorgans"), (Vector2)(val + Vector2.right * Random.Range(-3f, 3f)), Quaternion.identity);
		}
	}

	private static void LifePodAt(Vector2Int c)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0208: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_026f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0270: Unknown result type (might be due to invalid IL or missing references)
		//IL_0284: Unknown result type (might be due to invalid IL or missing references)
		//IL_0289: Unknown result type (might be due to invalid IL or missing references)
		//IL_028e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0293: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02de: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0301: Unknown result type (might be due to invalid IL or missing references)
		//IL_0306: Unknown result type (might be due to invalid IL or missing references)
		//IL_0332: Unknown result type (might be due to invalid IL or missing references)
		//IL_0333: Unknown result type (might be due to invalid IL or missing references)
		//IL_0347: Unknown result type (might be due to invalid IL or missing references)
		//IL_034c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0378: Unknown result type (might be due to invalid IL or missing references)
		//IL_0379: Unknown result type (might be due to invalid IL or missing references)
		//IL_038d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0392: Unknown result type (might be due to invalid IL or missing references)
		//IL_0397: Unknown result type (might be due to invalid IL or missing references)
		//IL_039e: Unknown result type (might be due to invalid IL or missing references)
		//IL_03db: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fa: Unknown result type (might be due to invalid IL or missing references)
		Vector2 val = RandPosInChunk(c);
		if (GroundAbove(val, out var ground))
		{
			val = W.BlockToWorldPos(ground);
		}
		Vector2Int val2 = W.WorldToBlockPos(val);
		CraterAt(val, val2);
		structPlaced = true;
		W.GenerateObjectAtPos(val2, ((Component)GetStruct("Lifepod").transform.GetChild(0)).GetComponent<Tilemap>(), 0.95f, true);
		W.GenerateEntityAtPos(W.BlockToWorldPos(val2), GetStruct("Lifepod"));
		if (Random.value < WorldGeneration.GetRunSettingFloat("traderchance") * 0.01f)
		{
			int num = Random.Range(-4, 4);
			TraderScript component = Object.Instantiate<GameObject>(GetStructObj("trader" + Random.Range(1, 4)), (Vector2)(W.BlockToWorldPos(val2 + Vector2Int.down * 7 + Vector2Int.right * num) - Vector2.one * 0.5f), Quaternion.identity).GetComponent<TraderScript>();
			if ((float)Mathf.Abs(num) > 1.5f)
			{
				component.farEnoughToMove = true;
			}
			component.MoveRange = new RangeF(W.BlockToWorldPos(val2 - Vector2Int.right * 5).x, W.BlockToWorldPos(val2 + Vector2Int.right * 5).x);
		}
		else
		{
			Object.Instantiate<GameObject>(GetStructObj("lifepodchest"), (Vector2)(W.BlockToWorldPos(val2 + Vector2Int.down * 6) - Vector2.one * 0.5f), Quaternion.identity);
		}
		for (int i = 0; i < 3; i++)
		{
			if (Random.Range(0f, 1f) < 0.05f)
			{
				Object.Instantiate<GameObject>(GetStructObj("experimentflesh"), (Vector2)(val + Vector2.right * Random.Range(-3f, 3f)), Quaternion.identity);
			}
		}
		if (Random.Range(0f, 1f) < 0.05f)
		{
			Object.Instantiate<GameObject>(GetStructObj("internalorgans"), (Vector2)(val + Vector2.right * Random.Range(-3f, 3f)), Quaternion.identity);
		}
		if (Random.Range(0f, 1f) < 0.5f)
		{
			Object.Instantiate<GameObject>(GetStructObj("LoreNote"), (Vector2)(val + Vector2.right * Random.Range(-3f, 3f) + Vector2.up * Random.Range(-1f, -6f)), Quaternion.identity);
		}
		if (Random.Range(0f, 1f) < 0.285f)
		{
			Utils.Create("epda", val + Vector2.right * Random.Range(-3f, 3f), Random.value * 360f);
		}
		if (Random.value < 0.2f)
		{
			Vector2 val3 = val + Vector2.right * Random.Range(-1.5f, 1.5f);
			Utils.Create("Special/defibrack", val3, 0f);
			bool flag = Random.value < 0.5f;
			float value = Random.value;
			GameObject val4 = null;
			if (Random.value < 0.75f)
			{
				val4 = Utils.Create("manualdefibrillator", val3, 0f);
				val4.AddComponent<ItemLock>();
			}
			else
			{
				val4 = Utils.Create("aed", val3, 0f);
				val4.AddComponent<ItemLock>();
			}
			if (!flag)
			{
				val4.GetComponent<Item>().battery.UnloadBattery(true);
			}
			else
			{
				val4.GetComponent<Item>().condition = value;
			}
		}
	}

	private static void CraterAt(Vector2 pos, Vector2Int vp)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		Vector2Int val = default(Vector2Int);
		for (int i = 0; i < 90; i++)
		{
			for (int j = 0; j < 90; j++)
			{
				float num = Vector2.Distance(pos + Vector2.up * (float)j + Vector2.right * (float)i - Vector2.one * 45f, pos);
				if (num < 45f * Random.Range(0f, 12f / (num * 0.8f)) && Random.Range(0f, 1f) < 0.7f)
				{
					val = new Vector2Int(Mathf.Clamp(vp.x - 45 + i, 0, (int)(W.width - 1)), Mathf.Clamp(vp.y - 45 + j + 2, 0, (int)(W.height - 1)));
					if (WB[val.x, val.y] > 0)
					{
						WB[val.x, val.y] = (ushort)Random.Range(0, 5);
					}
				}
			}
		}
	}

	private static void BioContainerAt(Vector2Int c, float lo, float hi, float chance)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		int num = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < num; i++)
		{
			Vector2 val = RandPosInChunk(c);
			if (GroundAbove(val, out var ground))
			{
				val = W.BlockToWorldPos(ground);
			}
			W.GenerateBlockCircle(val, 16, (ushort)3, 0.8f, 0f, false, false, false);
			W.GenerateBlockCircle(val, 20, (ushort)4, 0.3f, 0f, false, false, false);
			W.GenerateBlockCircle(val, 16, (ushort)0, 0.15f, 0f, false, false, false);
			structPlaced = true;
		W.GenerateObjectAtPos(ground, ((Component)GetStruct("BioContainer").transform.GetChild(0)).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(W.BlockToWorldPos(ground), GetStruct("BioContainer"));
		}
	}

	private static void BridgeAt(Vector2Int c, float lo, float hi, string res, float chance, bool raycast = true)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		int num = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < num; i++)
		{
			Vector2 val = RandPosInChunk(c);
			if (raycast && GroundAbove(val, out var ground))
			{
				val = W.BlockToWorldPos(ground);
			}
			structPlaced = true;
		W.GenerateObjectAtPos(W.WorldToBlockPos(val), GetStruct(res).GetComponent<Tilemap>(), chance, true);
			W.GenerateEntityAtPos(val, GetStruct(res));
		}
	}

	private static void PodAt(Vector2Int c, float lo, float hi, string res, float chance, bool entity = true)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		int num = Poisson(Random.Range(lo, hi) * W.totalLootRarity);
		for (int i = 0; i < num; i++)
		{
			Vector2 val = RandPosInChunk(c);
			if (GroundAbove(val, out var ground))
			{
				val = W.BlockToWorldPos(ground);
			}
			W.GenerateBlockCircle(val, 16, (ushort)3, 0.5f, 0f, false, false, false);
			W.GenerateBlockCircle(val, 20, (ushort)4, 0.2f, 0f, false, false, false);
			structPlaced = true;
			W.GenerateObjectAtPos(ground, GetStruct(res).GetComponent<Tilemap>(), chance, true);
			if (entity)
			{
				W.GenerateEntityAtPos(W.BlockToWorldPos(ground), GetStruct(res));
			}
		}
	}

	private static void IronVein(Vector2Int c, int width)
	{
		Vector2Int val = default(Vector2Int);
		val = new Vector2Int(Random.Range(c.x * CS, c.x * CS + CS), Random.Range(c.y * CS, c.y * CS + CS));
		int num = Random.Range(1, 5);
		for (int i = 0; i < num; i++)
		{
			for (int j = 0; j < width; j++)
			{
				if (val.x + i < W.width && val.y + j < W.height)
				{
					WB[val.x + i, val.y + j] = 5;
				}
			}
		}
	}

	private static void GenChunkTerrainInto(Vector2Int c, ushort[,] wb)
	{
		int num = c.x * CS;
		int num2 = c.y * CS;
		if (biome <= 1)
		{
			for (int i = num; i < num + CS; i++)
			{
				for (int j = num2; j < num2 + CS; j++)
				{
					caveNoise.SetFrequency(0.06f + frequencyMap.GetNoise((float)i, (float)j) * 0.01f);
					ushort num3 = ((caveNoise.GetNoise((float)i, (float)j) > -0.715f) ? ((ushort)1) : ((ushort)0));
					float noise = dirtPerlin.GetNoise((float)i, (float)j);
					if (num3 > 0 && noise < -0.1f)
					{
						num3 = (ushort)((noise < -0.33f) ? 16u : 2u);
					}
					if (num3 > 0 && R(0f, 1f) > 0.99f)
					{
						num3 = (ushort)RI(1, 5);
					}
					float noise2 = biomeMap.GetNoise((float)i, (float)j);
					if (noise2 > 0.1f)
					{
						num3 = (ushort)RI(3, 5);
					}
					if (num3 > 0 && noise2 < -0.8f)
					{
						num3 = 15;
					}
					if (biome == 1 && (float)j < (float)W.height * 0.5f)
					{
						float num4 = (float)j / (float)W.height * 2f;
						if (R(0f, 1f) > num4 && num3 == 2)
						{
							num3 = 12;
						}
						if ((float)j < (float)W.height * 0.33f && R(0f, 1f) > num4 * 3f && num3 == 1)
						{
							num3 = 13;
						}
					}
					wb[i - num, j - num2] = num3;
				}
			}
			ApplyProtected(wb, num, num2);
			return;
		}
		if (biome == 2 || biome == 3)
		{
			for (int k = num; k < num + CS; k++)
			{
				for (int l = num2; l < num2 + CS; l++)
				{
					float noise3 = biomeMap.GetNoise((float)k, (float)l);
					float num5 = frequencyMap.GetNoise((float)k, (float)l) * 0.25f + 0.1f;
					if (marbleMap.GetNoise((float)k, (float)l) <= minMarble)
					{
						ushort num6 = (ushort)((noise3 > num5 && dirtPerlin.GetNoise((float)k, (float)l) < -0.4f) ? ((noise3 < num5 + 0.1f) ? 12u : 13u) : 0u);
						if (caveNoise.GetNoise((float)k, (float)l) > 0.65f)
						{
							num6 = 17;
						}
						if (noise3 > 0.75f)
						{
							num6 = 15;
						}
						if (biomeMap2.GetNoise((float)k, (float)l) > 0.1f)
						{
							num6 = (ushort)RI(3, 5);
						}
						if (biome == 3 && num6 > 0 && RV() < 0.1f)
						{
							num6 = (ushort)(15 + RI(0, 2));
						}
						wb[k - num, l - num2] = num6;
					}
					else
					{
						wb[k - num, l - num2] = (ushort)((noise3 > num5) ? ((dirtPerlin.GetNoise((float)k, (float)l) < -0.1f) ? 18u : 19u) : 0u);
					}
					if (biome == 3 && toxicNoise.GetNoise((float)k, (float)l) < -0.8f && RV() > 0.5f)
					{
						wb[k - num, l - num2] = 22;
					}
					if (biome == 3 && wb[k - num, l - num2] > 0 && RV() > (float)(l + W.halfHeight) / (float)W.height)
					{
						wb[k - num, l - num2] = 23;
					}
				}
			}
			ApplyProtected(wb, num, num2);
			return;
		}
		float num7 = float.NaN;
		for (int m = num; m < num + CS; m++)
		{
			for (int n = num2; n < num2 + CS; n++)
			{
				float num8 = 0.0189f - (float)n / (float)W.height * 0.002f;
				if (num8 != num7)
				{
					marbleMap.SetFrequency(num8);
					num7 = num8;
				}
				float num9 = marbleMap.GetNoise((float)m, (float)n) + R(-0.1f, 0.1f);
				ushort num10 = (ushort)((num9 > 0.15f && num9 < 0.25f) ? 23 : ((num9 >= 0.25f && num9 < 0.45f) ? 16 : ((num9 >= 0.45f && num9 < 0.66f) ? 15 : ((num9 >= 0.66f) ? 19 : 0))));
				if (biomeMap2.GetNoise((float)m, (float)n) < -0.735f)
				{
					num10 = 0;
				}
				wb[m - num, n - num2] = num10;
			}
		}
		ApplyProtected(wb, num, num2);
	}

	private static void GenChunkOres(Vector2Int c)
	{
		ushort[,] wB = WB;
		int num = Poisson(0.5f);
		for (int i = 0; i < num; i++)
		{
			int num2 = c.x * CS + Random.Range(0, CS);
			int num3 = c.y * CS + Random.Range(0, CS);
			for (int num4 = Random.Range(1, 26); num4 > 0; num4--)
			{
				if (num2 > 0 && num2 < W.width - 1 && num3 > 0 && num3 < W.height - 1 && wB[num2, num3] > 0)
				{
					wB[num2, num3] = 34;
				}
				num2 += (Random.value > 0.5f) ? ((Random.value > 0.5f) ? 1 : (-1)) : 0;
				num3 += (Random.value > 0.5f) ? ((Random.value > 0.5f) ? 1 : (-1)) : 0;
			}
		}
		if (biome == 0)
		{
			VeinChunk(c, Random.Range(0.35f, 0.4f), 11, 2, 6, 48, horizontal: true);
			VeinChunk(c, Random.Range(0.35f, 0.4f), 11, 2, 6, 48, horizontal: false);
			return;
		}
		if (biome == 1)
		{
			VeinChunk(c, Random.Range(0.35f, 0.5f), 5, 3, 6, 64, horizontal: true);
			VeinChunk(c, Random.Range(0.35f, 0.5f), 5, 3, 6, 60, horizontal: false);
			return;
		}
		if (biome == 2 || biome == 3)
		{
			VeinChunk(c, Random.Range(0.25f, 0.3f), 11, 2, 6, 48, horizontal: true);
			VeinChunk(c, Random.Range(0.24f, 0.3f), 11, 2, 6, 48, horizontal: false);
			if (biome == 3)
			{
				VeinChunkSquare(c, 0.5f, 0.6f, 20, 4, 16);
			}
			return;
		}
		if (biome == 4)
		{
			for (int i = 0; i < 4; i++)
			{
				if (Random.value < 0.00025f)
				{
					int num5 = c.x * CS + Random.Range(0, CS);
					int num6 = c.y * CS + Random.Range(0, CS);
					if (wB[num5, num6] > 0)
					{
						wB[num5, num6] = 35;
					}
				}
			}
		}
	}

	private static void VeinChunkSquare(Vector2Int c, float amtMin, float amtMax, ushort block, int sizeMin, int sizeMax)
	{
		int num = Poisson(Random.Range(amtMin, amtMax));
		for (int i = 0; i < num; i++)
		{
			int num2 = c.x * CS + Random.Range(0, CS);
			int num3 = c.y * CS + Random.Range(0, CS);
			int num4 = Random.Range(sizeMin, sizeMax);
			for (int j = 0; j < num4; j++)
			{
				for (int k = 0; k < num4; k++)
				{
					int num5 = num2 + j;
					int num6 = num3 + k;
					if (num5 < W.width && num6 < W.height)
					{
						WB[num5, num6] = block;
					}
				}
			}
		}
	}

	private static void VeinChunk(Vector2Int c, float amt, ushort block, int w, int lenMin, int lenMax, bool horizontal)
	{
		int num = Poisson(amt);
		Vector2Int val = default(Vector2Int);
		for (int i = 0; i < num; i++)
		{
			val = new Vector2Int(c.x * CS + Random.Range(0, CS), c.y * CS + Random.Range(0, CS));
			int num2 = Random.Range(lenMin, lenMax);
			for (int j = 0; j < num2; j++)
			{
				for (int k = 0; k < w; k++)
				{
					int num3 = (horizontal ? (val.x + j) : (val.x + k));
					int num4 = (horizontal ? (val.y + k) : (val.y + j));
					if (num3 < W.width && num4 < W.height)
					{
						WB[num3, num4] = block;
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
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		Vector2 pos = default(Vector2);
		for (int i = 0; i < (int)perChunk; i++)
		{
			pos = new Vector2((float)(cx - CS / 2) + Random.Range(0f, (float)CS), (float)(cy - CS / 2) + Random.Range(0f, (float)CS));
			FluidManager.main.StartFill(WorldToBlockPos(pos), type, maxFill);
		}
	}

	private static Vector2Int WorldToBlockPos(Vector2 pos)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		return new Vector2Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
	}

	private static void GenChunkEntities(Vector2Int c)
	{
		Diag.Log("[CE] start c=(" + c.x + "," + c.y + ") biome=" + biome);
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0177: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_024f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0255: Unknown result type (might be due to invalid IL or missing references)
		//IL_025d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0290: Unknown result type (might be due to invalid IL or missing references)
		//IL_0296: Unknown result type (might be due to invalid IL or missing references)
		//IL_029e: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0303: Unknown result type (might be due to invalid IL or missing references)
		//IL_0309: Unknown result type (might be due to invalid IL or missing references)
		//IL_0323: Unknown result type (might be due to invalid IL or missing references)
		//IL_034b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0351: Unknown result type (might be due to invalid IL or missing references)
		//IL_0359: Unknown result type (might be due to invalid IL or missing references)
		//IL_0381: Unknown result type (might be due to invalid IL or missing references)
		//IL_0387: Unknown result type (might be due to invalid IL or missing references)
		//IL_038f: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_0406: Unknown result type (might be due to invalid IL or missing references)
		//IL_0435: Unknown result type (might be due to invalid IL or missing references)
		//IL_043b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0444: Unknown result type (might be due to invalid IL or missing references)
		//IL_046c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0472: Unknown result type (might be due to invalid IL or missing references)
		//IL_047a: Unknown result type (might be due to invalid IL or missing references)
		//IL_04a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_04af: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_04e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0523: Unknown result type (might be due to invalid IL or missing references)
		//IL_0529: Unknown result type (might be due to invalid IL or missing references)
		//IL_056a: Unknown result type (might be due to invalid IL or missing references)
		//IL_059f: Unknown result type (might be due to invalid IL or missing references)
		//IL_05a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_0606: Unknown result type (might be due to invalid IL or missing references)
		//IL_060c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0614: Unknown result type (might be due to invalid IL or missing references)
		//IL_0643: Unknown result type (might be due to invalid IL or missing references)
		//IL_0649: Unknown result type (might be due to invalid IL or missing references)
		//IL_0651: Unknown result type (might be due to invalid IL or missing references)
		//IL_0680: Unknown result type (might be due to invalid IL or missing references)
		//IL_0686: Unknown result type (might be due to invalid IL or missing references)
		//IL_068e: Unknown result type (might be due to invalid IL or missing references)
		//IL_06bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_06c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_06cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_0700: Unknown result type (might be due to invalid IL or missing references)
		//IL_0708: Unknown result type (might be due to invalid IL or missing references)
		//IL_0737: Unknown result type (might be due to invalid IL or missing references)
		//IL_073d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0745: Unknown result type (might be due to invalid IL or missing references)
		//IL_076d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0773: Unknown result type (might be due to invalid IL or missing references)
		//IL_077b: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_07a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_07de: Unknown result type (might be due to invalid IL or missing references)
		//IL_07fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0823: Unknown result type (might be due to invalid IL or missing references)
		//IL_0829: Unknown result type (might be due to invalid IL or missing references)
		//IL_0831: Unknown result type (might be due to invalid IL or missing references)
		//IL_0859: Unknown result type (might be due to invalid IL or missing references)
		//IL_085f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0867: Unknown result type (might be due to invalid IL or missing references)
		//IL_088f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0895: Unknown result type (might be due to invalid IL or missing references)
		//IL_089d: Unknown result type (might be due to invalid IL or missing references)
		//IL_08d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_08d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_08de: Unknown result type (might be due to invalid IL or missing references)
		//IL_0906: Unknown result type (might be due to invalid IL or missing references)
		//IL_090c: Unknown result type (might be due to invalid IL or missing references)
		//IL_091b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0943: Unknown result type (might be due to invalid IL or missing references)
		//IL_0949: Unknown result type (might be due to invalid IL or missing references)
		//IL_0951: Unknown result type (might be due to invalid IL or missing references)
		//IL_0979: Unknown result type (might be due to invalid IL or missing references)
		//IL_097f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0987: Unknown result type (might be due to invalid IL or missing references)
		//IL_09ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_09b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_09e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_09e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_09ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a17: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a1d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a26: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a78: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a7e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a86: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ae3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ae9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0af8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b27: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b2d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b35: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b5d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b63: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b6b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b93: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b99: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ba1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0bc9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0bcf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0bd7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0bff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c05: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c0d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c35: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c3b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c55: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c79: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c7f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0c87: Unknown result type (might be due to invalid IL or missing references)
		//IL_0cb6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0cbc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0cc4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0cf1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0cfc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d24: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d2a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d32: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d5a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d60: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d68: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d90: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d96: Unknown result type (might be due to invalid IL or missing references)
		//IL_0d9e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0dd1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0dd7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ddf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e12: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e18: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e20: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e4f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e55: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e5d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e8c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e92: Unknown result type (might be due to invalid IL or missing references)
		//IL_0e9a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ec9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ecf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ed7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f06: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f0c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f14: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f38: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f3e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f46: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f79: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f7f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0f87: Unknown result type (might be due to invalid IL or missing references)
		//IL_0fba: Unknown result type (might be due to invalid IL or missing references)
		//IL_0fc0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0fc8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ff0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ff6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0ffe: Unknown result type (might be due to invalid IL or missing references)
		//IL_1026: Unknown result type (might be due to invalid IL or missing references)
		//IL_102c: Unknown result type (might be due to invalid IL or missing references)
		//IL_1034: Unknown result type (might be due to invalid IL or missing references)
		//IL_105c: Unknown result type (might be due to invalid IL or missing references)
		//IL_1062: Unknown result type (might be due to invalid IL or missing references)
		//IL_106a: Unknown result type (might be due to invalid IL or missing references)
		//IL_1092: Unknown result type (might be due to invalid IL or missing references)
		//IL_1098: Unknown result type (might be due to invalid IL or missing references)
		//IL_10a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_10c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_10ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_10d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_1105: Unknown result type (might be due to invalid IL or missing references)
		//IL_110b: Unknown result type (might be due to invalid IL or missing references)
		//IL_1113: Unknown result type (might be due to invalid IL or missing references)
		//IL_1135: Unknown result type (might be due to invalid IL or missing references)
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
			DistributeLoose(c, "bandage", 0.2f * totalLootRarity, 0.3f * totalLootRarity, true, Vector2.down, true, true);
			DistributeLoose(c, "droppings", 0.3f, 0.5f, true, Vector2.down, false, false);
			if (biome == 0)
			{
				DistributeLoose(c, "fleshchunk", 0.015f * totalLootRarity, 0.02f * totalLootRarity, true, Vector2.down, true, false);
			}
			Diag.Log("[CE] done b0 c=(" + c.x + "," + c.y + ") placed=" + diagEntityCount);
			diagEntityCount = 0;
			return;
		}
		if (biome == 2 || biome == 3)
		{
			float num = ((biome == 2) ? 1f : 2f);
			D(c, "glowplant", 2.4f * num, 2.5f * num, 1.25f, 10f, 0.25f, inGround: false, flip: true, SandCheck);
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
			DistributeLoose(c, "oilpipe", 0.3f, 0.4f, false, Vector2.down, true, false);
			PlaceTurret(c, 0.12f * totalTrapRarity * ((biome == 2) ? 1f : 0.66f), 0.15f * totalTrapRarity * ((biome == 2) ? 1f : 0.66f));
			PlaceStalactite(c, 1.5f * totalTrapRarity, 2f * totalTrapRarity);
			PlaceSandvine(c, 6f * ((biome == 2) ? 1f : 0.1f), 7f * ((biome == 2) ? 1f : 0.1f));
			Diag.Log("[CE] done b23 c=(" + c.x + "," + c.y + ") placed=" + diagEntityCount);
			diagEntityCount = 0;
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
		PlaceSandvine(c, 4f, 5f);
		DistributeLoose(c, "wallflower", 6f, 7f, false, Vector2.down, true, false);
		Diag.Log("[CE] done bX c=(" + c.x + "," + c.y + ") placed=" + diagEntityCount);
		diagEntityCount = 0;
	}

	private static bool FindSurface(int wx, int wy, Vector2 dir, out int hx, out int hy, int maxDist = 16)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		hx = wx;
		hy = wy;
		int num = (int)Mathf.Sign(dir.x);
		int num2 = (int)Mathf.Sign(dir.y);
		for (int i = 0; i < maxDist; i++)
		{
			int num3 = wx + num * i;
			int num4 = wy + num2 * i;
			if (num3 < 0 || num4 < 0 || num3 >= W.width || num4 >= W.height)
			{
				return false;
			}
			if (WB[num3, num4] > 0)
			{
				hx = num3;
				hy = num4;
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
		return bx > 0 && bx < W.width - 1 && WB[bx, by] > 0 && WB[bx - 1, by] > 0 && WB[bx + 1, by] > 0;
	}

	private static bool IsSoil(int bx, int by)
	{
		ushort num = WB[bx, by];
		return num == 2 || num == 15 || num == 16 || num == 23 || (num > 30 && num < 34);
	}

	private static void DistributeLoose(Vector2Int c, string name, float min, float max, bool surface, Vector2 dir, bool rotate, bool setCondition)
	{
		float num = Random.Range(min, max);
		int num2 = (int)num;
		if (Random.value < num - (float)num2)
		{
			num2++;
		}
		int num3 = c.x * CS;
		int num4 = c.y * CS;
		for (int i = 0; i < num2; i++)
		{
			int num5 = num3 + Random.Range(0, CS);
			int num6 = num4 + Random.Range(0, CS);
			int hx;
			int hy;
			if (surface)
			{
				if (WB[num5, num6] > 0 || !FindSurface(num5, num6, dir, out hx, out hy))
				{
					continue;
				}
				Vector2Int place = new Vector2Int(hx - (int)Mathf.Sign(dir.x), hy - (int)Mathf.Sign(dir.y));
				hx = place.x;
				hy = place.y;
			}
			else
			{
				hx = num5;
				hy = num6;
			}
			GameObject structObj = GetStructObj(name);
			if ((Object)(object)structObj == (Object)null)
			{
				Diag.Log("[CE] null-res " + name);
				continue;
			}
			Vector2 vector = W.BlockToWorldPos(new Vector2Int(hx, hy));
			GameObject val2 = Object.Instantiate<GameObject>(structObj, (Vector2)(vector), Quaternion.Euler(0f, 0f, rotate ? Random.Range(-180f, 180f) : 0f));
			diagEntityCount++;
			if (setCondition)
			{
				Item item = val2.GetComponent<Item>();
				if ((Object)(object)item != (Object)null)
				{
					item.condition = Random.Range(1, 4) * 0.33f;
				}
			}
		}
	}

	private static int RandCount(float min, float max)
	{
		float num = Random.Range(min, max);
		int num2 = (int)num;
		if (Random.value < num - (float)num2)
		{
			num2++;
		}
		return num2;
	}

	private static void PlaceTurret(Vector2Int c, float min, float max)
	{
		int num = RandCount(min, max);
		for (int i = 0; i < num; i++)
		{
			int num5 = c.x * CS + Random.Range(0, CS);
			int num6 = c.y * CS + Random.Range(0, CS);
			if (WB[num5, num6] > 0)
			{
				continue;
			}
			float num4 = (Random.value > 0.5f) ? 1f : (-1f);
			int hx;
			int hy;
			if (!FindSurface(num5, num6, Vector2.right * num4, out hx, out hy, 32))
			{
				continue;
			}
			GameObject structObj = GetStructObj("turret");
			if ((Object)(object)structObj == (Object)null)
			{
				Diag.Log("[CE] null-res turret");
				continue;
			}
			Vector2 place = W.BlockToWorldPos(new Vector2Int(hx - (int)num4, hy));
			GameObject val2 = Object.Instantiate<GameObject>(structObj, (Vector2)(place), Quaternion.identity);
			val2.transform.localScale = new Vector2(0f - num4, 1f);
			diagEntityCount++;
		}
	}

	private static void PlaceStalactite(Vector2Int c, float min, float max)
	{
		int num = RandCount(min, max);
		for (int i = 0; i < num; i++)
		{
			int num5 = c.x * CS + Random.Range(0, CS);
			int num6 = c.y * CS + Random.Range(0, CS);
			if (WB[num5, num6] > 0)
			{
				continue;
			}
			int hx;
			int hy;
			if (!FindSurface(num5, num6, Vector2.up, out hx, out hy, 64))
			{
				continue;
			}
			if ((float)(hy - (int)W.halfHeight) > (float)((int)W.halfHeight) - 5f)
			{
				continue;
			}
			GameObject structObj = GetStructObj("stalactite");
			if ((Object)(object)structObj == (Object)null)
			{
				Diag.Log("[CE] null-res stalactite");
				continue;
			}
			Vector2 top = W.BlockToWorldPos(new Vector2Int(hx, hy));
			GameObject val2 = Object.Instantiate<GameObject>(structObj, (Vector2)(top + Vector2.down * 2f), Quaternion.identity);
			val2.GetComponent<BuildingEntity>().blockPlacedOn = new Vector2Int(hx, hy);
			if (W.ChunkUpdated[c.x, c.y] != null)
			{
				W.ChunkUpdated[c.x, c.y].AddListener(new UnityAction(val2.GetComponent<StalactiteDropper>().CheckSeating));
			}
			val2.transform.localScale = new Vector3((Random.Range(0f, 1f) > 0.5f) ? (-1f) : 1f, 1f, 1f);
			diagEntityCount++;
		}
	}

	private static void PlaceSandvine(Vector2Int c, float min, float max)
	{
		int num = RandCount(min, max);
		for (int i = 0; i < num; i++)
		{
			int num5 = c.x * CS + Random.Range(0, CS);
			int num6 = c.y * CS + Random.Range(0, CS);
			if (WB[num5, num6] > 0)
			{
				continue;
			}
			int hx1;
			int hy1;
			if (!FindSurface(num5, num6, Vector2.up, out hx1, out hy1, 128))
			{
				continue;
			}
			int hx2;
			int hy2;
			if (!FindSurface(num5, num6, Vector2.down, out hx2, out hy2, 128))
			{
				continue;
			}
			if ((float)(hy2 - (int)W.halfHeight + 1) > (float)((int)W.halfHeight) - 5f)
			{
				continue;
			}
			GameObject structObj = GetStructObj("Special/sandvinehook");
			GameObject structObj2 = GetStructObj("Special/sandvinerope");
			if ((Object)(object)structObj == (Object)null || (Object)(object)structObj2 == (Object)null)
			{
				Diag.Log("[CE] null-res sandvine");
				continue;
			}
			Vector2 top = W.BlockToWorldPos(new Vector2Int(hx1, hy1));
			Vector2 bottom = new Vector2((float)hx2 + 0.5f, (float)(hy2 - (int)W.halfHeight + 1));
			Color color = Color.Lerp(Color.gray, Color.white, Random.value);
			GameObject gameObject3 = Object.Instantiate<GameObject>(structObj, (Vector2)(top), Quaternion.identity);
			GameObject obj = Object.Instantiate<GameObject>(structObj2, (Vector2)((top + bottom) * 0.5f), Quaternion.identity);
			obj.GetComponent<SpriteRenderer>().size = new Vector2(2.5f, Mathf.Abs(top.y - bottom.y));
			obj.GetComponent<SpriteRenderer>().color = color;
			gameObject3.GetComponent<SpriteRenderer>().color = color;
			obj.GetComponent<SpriteRenderer>().flipX = Random.value > 0.5f;
			gameObject3.GetComponent<SpriteRenderer>().flipX = Random.value > 0.5f;
			float num7 = Random.Range(0.15f, 1f);
			gameObject3.transform.localScale = new Vector3(num7, 1f);
			obj.transform.localScale = new Vector3(num7, 1f);
			Climbable component2 = obj.GetComponent<Climbable>();
			component2.points.Add(bottom);
			component2.points.Add(top);
			component2.downwardsVelocity = (1f - num7) * 16f;
			diagEntityCount++;
		}
	}

	private static void D(Vector2Int c, string name, float min, float max, float yOff = 0f, float rot = 0f, float yDev = 0f, bool inGround = false, bool flip = false, Func<int, int, bool> check = null, Vector2 dir = default(Vector2))
	{
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Expected O, but got Unknown
		//IL_01fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0218: Unknown result type (might be due to invalid IL or missing references)
		float num = Random.Range(min, max);
		int num2 = (int)num;
		if (Random.value < num - (float)num2)
		{
			num2++;
		}
		int num3 = c.x * CS;
		int num4 = c.y * CS;
		Vector2 val = default(Vector2);
		for (int i = 0; i < num2; i++)
		{
			int num5 = num3 + Random.Range(0, CS);
			int num6 = num4 + Random.Range(0, CS);
			Vector2 val2 = (dir == default(Vector2)) ? Vector2.down : dir;
			if (WB[num5, num6] > 0 || !FindSurface(num5, num6, val2, out var hx, out var hy) || (check != null && !check(hx, hy)))
			{
				continue;
			}
			GameObject structObj = GetStructObj(name);
			if ((Object)(object)structObj == (Object)null)
			{
				Diag.Log("[CE] null-res " + name);
				continue;
			}
			Vector2Int place = new Vector2Int(hx - (int)Mathf.Sign(val2.x), hy - (int)Mathf.Sign(val2.y));
			val = W.BlockToWorldPos(place);
			float num7 = Random.Range(yOff - yDev, yOff + yDev);
			GameObject val3 = Object.Instantiate<GameObject>(structObj, (Vector2)(val - val2 * num7), Quaternion.Euler(0f, 0f, Random.Range(0f - rot, rot)));
			diagEntityCount++;
			BuildingEntity component = val3.GetComponent<BuildingEntity>();
			if ((Object)(object)component != (Object)null)
			{
				component.blockPlacedOn = new Vector2Int(hx, hy);
				if (inGround && W.ChunkUpdated[c.x, c.y] != null)
				{
					W.ChunkUpdated[c.x, c.y].AddListener(new UnityAction(component.CheckSeating));
				}
			}
			if (flip && Random.value < 0.5f)
			{
				Vector3 localScale = val3.transform.localScale;
				localScale.x *= -1f;
				val3.transform.localScale = localScale;
			}
		}
	}
}
