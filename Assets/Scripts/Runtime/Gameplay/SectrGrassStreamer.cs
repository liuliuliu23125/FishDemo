using System.Collections.Generic;
using FishDemo.Rendering;
using UnityEngine;

namespace FishDemo.Gameplay
{
    public sealed class SectrGrassStreamer : MonoBehaviour
    {
        public static int MaxResidentChunks = 24;

        public static float PollInterval = 0.1f;

        public static float ChunkScanInterval = 5f;

        private static SectrGrassStreamer s_instance;

        private GrassBRGManager _manager;
        private SECTR_Chunk[] _chunks;
        private float _nextPoll;
        private float _nextChunkScan;

        private readonly HashSet<string> _resident = new HashSet<string>();
        private readonly List<string> _order = new List<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Ensure()
        {
            GrassBRGManager.RequestStreaming();

            if (!Application.isPlaying || s_instance != null)
                return;

            var go = new GameObject("~SectrGrassStreamer");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<SectrGrassStreamer>();
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll)
                return;
            _nextPoll = now + PollInterval;

            if (_manager == null)
                _manager = FindObjectOfType<GrassBRGManager>();
            if (_manager == null || !_manager.IsStreaming)
                return;

            if (_chunks == null || now >= _nextChunkScan)
            {
                _chunks = FindObjectsOfType<SECTR_Chunk>();
                _nextChunkScan = now + ChunkScanInterval;
            }

            for (int i = _order.Count - 1; i >= 0; i--)
            {
                string name = _order[i];
                if (IsChunkLoaded(name))
                    continue;

                _manager.RemoveGrassChunk(name);
                _order.RemoveAt(i);
                _resident.Remove(name);
                break;
            }

            while (_order.Count > MaxResidentChunks)
            {
                string oldest = _order[0];
                _manager.RemoveGrassChunk(oldest);
                _resident.Remove(oldest);
                _order.RemoveAt(0);
            }

            for (int i = 0; i < _chunks.Length; i++)
            {
                var chunk = _chunks[i];
                if (chunk == null || !chunk.IsLoaded())
                    continue;

                string name = chunk.ScenePath;
                if (string.IsNullOrEmpty(name) || _resident.Contains(name) || _failed.Contains(name))
                    continue;

                if (_manager.AddGrassChunk(name))
                {
                    _resident.Add(name);
                    _order.Add(name);
                }
                else
                {
                    _failed.Add(name);
                }
                break;
            }
        }

        public static void FlushAll()
        {
            if (s_instance == null)
                return;
            if (s_instance._manager != null)
                s_instance._manager.ClearGrassChunks();
            GrassBRGManager.ReleaseBufferPool();
            s_instance._resident.Clear();
            s_instance._order.Clear();
            s_instance._failed.Clear();
        }

        private bool IsChunkLoaded(string scenePath)
        {
            for (int i = 0; i < _chunks.Length; i++)
            {
                var c = _chunks[i];
                if (c != null && c.IsLoaded() && c.ScenePath == scenePath)
                    return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }
    }
}
