// Copyright (c) 2014 Make Code Now! LLC

using UnityEngine;
using System.Collections.Generic;
using FishDemo.Gameplay;

/// \ingroup Stream
/// (Un)loads Chunks within a given volume. Can be set to optionally not touch Sectors
/// that are not part of the terrain grid.
[AddComponentMenu("Procedural Worlds/SECTR/Stream/SECTR Region Loader")]
public class SECTR_RegionLoader : SECTR_Loader 
{
	#region Private Details
	private List<SECTR_Sector> sectors = new List<SECTR_Sector>(16);
	private List<SECTR_Sector> loadSectors = new List<SECTR_Sector>(16);
	private List<SECTR_Sector> unloadSectors = new List<SECTR_Sector>(16);
	private bool updated = false;
	#endregion

	#region Public Interface
	[SECTR_ToolTip("The dimensions of the volume in which terrain chunks should be loaded.")]
	public Vector3 LoadSize = new Vector3(20f, 10f, 20f);
	[SECTR_ToolTip("The distance from the load size that you need to move for a Sector to unload (as a percentage).", 0f, 1f)]
	public float UnloadBuffer = 0.1f;
	[SECTR_ToolTip("If set, will only load Sectors in matching layers.")]
	public LayerMask LayersToLoad = -1;

	public override string ToString()
	{
		return "Region Loader";
	}

	/// Returns true if all referenced Chunks in region are loaded. False, otherwise.
	public override bool Loaded
	{
		get
		{
			int numSectors = sectors.Count;
			bool loaded = updated || numSectors == 0;
			for(int sectorIndex = 0; sectorIndex < numSectors && loaded; ++sectorIndex)
			{
				SECTR_Sector sector = sectors[sectorIndex];
				if(sector.Frozen)
				{
					SECTR_Chunk chunk = sector.GetComponent<SECTR_Chunk>();
					if(chunk && !chunk.IsLoaded())
					{
						loaded = false;
						break;
					}
				}
			}
			return loaded;
		}
	}
	#endregion

	#region Unity Interface
	void Start()
	{
		LockSelf(true);
	}

	void OnDisable()
	{
		int numSectors = sectors.Count;
		for(int sectorIndex = 0; sectorIndex < numSectors; ++sectorIndex)
		{
			SECTR_Sector sector = sectors[sectorIndex];
			if(sector)
			{
				SECTR_Chunk chunk = sector.GetComponent<SECTR_Chunk>();
				if(chunk)
				{
					// [FishDemo] 不立即卸载：引用转交缓存，切相机/边界回走时可原地复用。
					SectrChunkCache.Release(chunk);
				}
			}
		}
		sectors.Clear();
		updated = false;
	}

	void Update()
	{
		Vector3 position = transform.position;
		Bounds loadBounds = new Bounds(position, LoadSize);
		Bounds unloadBounds = new Bounds(position, LoadSize * (1f + UnloadBuffer));

		SECTR_Sector.GetContaining(ref loadSectors, loadBounds);
		SECTR_Sector.GetContaining(ref unloadSectors, unloadBounds);

		// [FishDemo] 卸载/加载限流：原版在一帧内对所有离开/进入范围的 chunk 做
		// RemoveReference / AddReference，切相机时会一次触发 9 个场景卸载或加载。
		// 超出的部分留在列表里，下一帧继续；loadSectors 每帧重算，不会漏。
		int releasesThisFrame = 0;
		int sectorIndex = 0;
		int numSectors = sectors.Count;
		while(sectorIndex < numSectors)
		{
			SECTR_Sector oldSector = sectors[sectorIndex];
			if(loadSectors.Contains(oldSector))
			{
				loadSectors.Remove(oldSector);
				++sectorIndex;
			}
			else if(!unloadSectors.Contains(oldSector))
			{
				if(SectrChunkCache.MaxReleasesPerFrame > 0 &&
				   releasesThisFrame >= SectrChunkCache.MaxReleasesPerFrame)
				{
					++sectorIndex;
					continue;
				}

				SECTR_Chunk oldChunk = oldSector != null ? oldSector.GetComponent<SECTR_Chunk>() : null;
				if(oldChunk)
				{
					SectrChunkCache.Release(oldChunk);
				}
				sectors.RemoveAt(sectorIndex);
				--numSectors;
				++releasesThisFrame;
			}
			else
			{
				++sectorIndex;
			}
		}

		numSectors = loadSectors.Count;
		int layerMaskValue = LayersToLoad.value;
		int loadsThisFrame = 0;
		if(numSectors > 0)
		{
			for(sectorIndex = 0; sectorIndex < numSectors; ++sectorIndex)
			{
				SECTR_Sector newSector = loadSectors[sectorIndex];
				SECTR_Chunk newChunk = newSector != null ? newSector.GetComponent<SECTR_Chunk>() : null;
				if(newSector && newChunk != null &&
				   (newSector.Frozen || newChunk.IsLoaded()) &&
				   ((layerMaskValue & (1 << newSector.gameObject.layer)) != 0))
				{
					// 缓存命中直接接管引用；未命中才走 AddReference（真正 LoadSceneAsync）。
					if(!SectrChunkCache.TryAcquire(newChunk))
					{
						if(SectrChunkCache.MaxLoadsPerFrame > 0 &&
						   loadsThisFrame >= SectrChunkCache.MaxLoadsPerFrame)
						{
							// 留给下一帧；本 sector 没加入 sectors，Update 会重新算到它。
							continue;
						}
						// 按 wall-clock 间隔发起 LoadSceneAsync。只限帧数不够：
						// batchmode/高帧率下 2 个/帧仍会在几毫秒内全部发起，激活又会挤在一起。
						if(!SectrChunkCache.CanStartLoad())
						{
							continue;
						}
						newChunk.AddReference();
						++loadsThisFrame;
					}
					sectors.Add(newSector);
				}
			}
		}

		if(locked && Loaded)
		{
			LockSelf(false);
		}

		updated = true;
	}

	#if UNITY_EDITOR
	void OnDrawGizmosSelected()
	{
		Gizmos.color = Color.yellow;
		Gizmos.DrawWireCube(transform.position, LoadSize);
		Gizmos.color = Color.red;
		Gizmos.DrawWireCube(transform.position, LoadSize * (1f + UnloadBuffer));
	}
	#endif
	#endregion
}
