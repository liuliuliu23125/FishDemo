using System.Collections.Generic;
using UnityEngine;

namespace FishDemo.Gameplay
{
    public static class SectrChunkCache
    {
        private struct Entry
        {
            public SECTR_Chunk Chunk;
            public int Count;
            public float LastUse;
        }

        public static int MaxCachedChunks =
#if UNITY_EDITOR
            6;
#else
            4;
#endif

        public static float TtlSeconds =
#if UNITY_EDITOR
            15f;
#else
            10f;
#endif

        public static float EvictInterval = 0.1f;

        public static int MaxLoadsPerFrame = 2;

        public static float LoadInterval = 0.05f;

        public static int MaxReleasesPerFrame = 4;

        public static float AssetScanCooldown =
#if UNITY_EDITOR
            30f;
#else
            10f;
#endif

        private static readonly List<Entry> s_entries = new List<Entry>(8);
        private static float s_nextEvictTime;
        private static float s_nextLoadTime;

        public static bool CanStartLoad()
        {
            if (!Application.isPlaying || LoadInterval <= 0f)
                return true;
            float now = Time.unscaledTime;
            if (now < s_nextLoadTime)
                return false;
            s_nextLoadTime = now + LoadInterval;
            return true;
        }

        public static int Count => s_entries.Count;
        public static int TotalHits { get; private set; }
        public static int TotalEvictions { get; private set; }

        public static void Release(SECTR_Chunk chunk)
        {
            if (chunk == null)
                return;

            if (!Application.isPlaying || MaxCachedChunks <= 0 || !chunk.IsLoaded())
            {
                chunk.RemoveReference();
                return;
            }

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < s_entries.Count; i++)
            {
                if (s_entries[i].Chunk == chunk)
                {
                    Entry e = s_entries[i];
                    e.Count++;
                    e.LastUse = now;
                    s_entries[i] = e;
                    return;
                }
            }

            s_entries.Add(new Entry { Chunk = chunk, Count = 1, LastUse = now });
        }

        public static bool TryAcquire(SECTR_Chunk chunk)
        {
            if (chunk == null || !Application.isPlaying || MaxCachedChunks <= 0)
                return false;

            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (e.Chunk != chunk)
                    continue;

                if (e.Chunk == null)
                {
                    s_entries.RemoveAt(i);
                    return false;
                }
                if (!e.Chunk.IsLoaded())
                {
                    s_entries.RemoveAt(i);
                    for (int n = 0; n < e.Count; n++)
                        e.Chunk.RemoveReference();
                    return false;
                }

                e.Count--;
                e.LastUse = Time.realtimeSinceStartup;
                if (e.Count <= 0)
                    s_entries.RemoveAt(i);
                else
                    s_entries[i] = e;
                TotalHits++;
                return true;
            }

            return false;
        }

        public static void Tick()
        {
            if (!Application.isPlaying || s_entries.Count == 0)
                return;
            if (Time.realtimeSinceStartup < s_nextEvictTime)
                return;
            s_nextEvictTime = Time.realtimeSinceStartup + EvictInterval;

            float now = Time.realtimeSinceStartup;
            int oldestIndex = -1;
            float oldestTime = float.MaxValue;
            bool overCapacity = s_entries.Count > MaxCachedChunks;

            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (e.Chunk == null)
                {
                    s_entries.RemoveAt(i);
                    i--;
                    continue;
                }

                bool expired = now - e.LastUse > TtlSeconds;
                bool eligible = expired || overCapacity;
                if (eligible && e.LastUse < oldestTime)
                {
                    oldestTime = e.LastUse;
                    oldestIndex = i;
                }
            }

            if (oldestIndex >= 0)
                EvictAt(oldestIndex);
        }

        public static void Flush(bool unload)
        {
            if (s_entries.Count == 0)
                return;

            Entry[] entries = s_entries.ToArray();
            s_entries.Clear();
            if (!unload)
                return;

            for (int i = 0; i < entries.Length; i++)
            {
                SECTR_Chunk chunk = entries[i].Chunk;
                if (chunk == null)
                    continue;
                for (int n = 0; n < entries[i].Count; n++)
                    chunk.RemoveReference();
            }
        }

        private static void EvictAt(int index)
        {
            Entry e = s_entries[index];
            s_entries.RemoveAt(index);
            if (e.Chunk == null)
                return;

            TotalEvictions++;
            for (int i = 0; i < e.Count; i++)
                e.Chunk.RemoveReference();
        }
    }
}
