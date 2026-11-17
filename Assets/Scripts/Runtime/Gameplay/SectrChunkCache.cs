using System.Collections.Generic;
using UnityEngine;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// SECTR chunk 的短期保留缓存。
    ///
    /// ── 为什么需要 ──
    /// SECTR 原版是纯引用计数：RegionLoader 离开范围就 RemoveReference，
    /// 引用到 0 立刻 UnloadSceneAsync。切相机 / 在 chunk 边界来回走时，
    /// 同一批 chunk 会被反复卸载再加载（每块约 12MB 资产），
    /// 既卡顿又制造原生/托管堆碎片。
    ///
    /// 本缓存把“离开范围”的引用**转交**给缓存持有，chunk 保持 loaded 状态；
    /// 在 TTL 内重新进入范围时可以原地复用，不再走 LoadSceneAsync。
    ///
    /// ── 引用语义 ──
    /// 缓存只接管**已经存在的引用**，不额外 AddReference：
    /// - Release(chunk)：RegionLoader 的引用转给缓存（refCount 不变）
    /// - TryAcquire(chunk)：缓存的引用转回 RegionLoader（refCount 不变）
    /// - 淘汰：调用 Count 次 RemoveReference，refCount 到 0 时 SECTR 才会真正卸载
    /// 因此不会和 RegionLoader 的引用计数打架。
    ///
    /// ── 容量 ──
    /// 按 chunk 数计（不是引用数）。两个 loader 同时引用同一块时 Count=2，仍只占 1 个名额。
    /// editor 默认 6 块（约 75MB）、真机默认 4 块（约 50MB），按真机内存再调。
    /// </summary>
    public static class SectrChunkCache
    {
        private struct Entry
        {
            public SECTR_Chunk Chunk;
            public int Count;
            public float LastUse;
        }

        /// <summary>缓存最多保留多少块 chunk。0 = 关闭缓存（退化为原版立即卸载）。</summary>
        public static int MaxCachedChunks =
#if UNITY_EDITOR
            6;
#else
            4;
#endif

        /// <summary>缓存保留秒数。超过后由 Tick 逐块淘汰。</summary>
        public static float TtlSeconds =
#if UNITY_EDITOR
            15f;
#else
            10f;
#endif

        /// <summary>两次真正卸载之间的最小间隔，避免淘汰时一次性掉 9 个 chunk。</summary>
        public static float EvictInterval = 0.1f;

        /// <summary>每帧最多新增多少个 chunk 引用；其余留到下一帧，摊平加载 I/O。</summary>
        public static int MaxLoadsPerFrame = 2;

        /// <summary>
        /// 两次真正 LoadSceneAsync 之间的最小 wall-clock 间隔。
        /// 只限“每帧 N 个”在高帧率下无效（几毫秒就能发完），所以用时间间隔。
        /// </summary>
        public static float LoadInterval = 0.05f;

        /// <summary>每帧最多释放多少个引用；缓存本身很便宜，主要防止一次清空 9 块。</summary>
        public static int MaxReleasesPerFrame = 4;

        /// <summary>资产清理的最小间隔（editor 下扫描 AssetDatabase 更贵）。</summary>
        public static float AssetScanCooldown =
#if UNITY_EDITOR
            30f;
#else
            10f;
#endif

        // Entry 是 struct + List，避免每次 Release 都 new 一个 class 对象（缓存本身也会制造碎片）。
        private static readonly List<Entry> s_entries = new List<Entry>(8);
        private static float s_nextEvictTime;
        private static float s_nextLoadTime;

        /// <summary>RegionLoader 在真正 AddReference 前调用；返回 true 表示本次可以发起加载。</summary>
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
        /// <summary>诊断用：缓存命中次数（省掉的 LoadSceneAsync 次数）。</summary>
        public static int TotalHits { get; private set; }
        /// <summary>诊断用：真正被淘汰卸载的 chunk 次数。</summary>
        public static int TotalEvictions { get; private set; }

        /// <summary>
        /// 把 RegionLoader 的一个引用转交给缓存。chunk 保持 loaded，不触发卸载。
        /// 非 Play Mode 直接走原版 RemoveReference，避免编辑器里留下悬挂引用。
        /// </summary>
        public static void Release(SECTR_Chunk chunk)
        {
            if (chunk == null)
                return;

            // 只缓存 **Active** 的 chunk。Loading/Unloading 的引用不能转交：
            // 下一次 TryAcquire 会拿到一个未就绪的 chunk，SECTR 的 _Load 又不会重启加载，
            // 结果会卡在 Loading/Unloading 并堵住整个 activationQueue。
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

        /// <summary>
        /// 尝试从缓存接管一个引用。返回 true 时调用方**不要**再 AddReference。
        /// </summary>
        public static bool TryAcquire(SECTR_Chunk chunk)
        {
            if (chunk == null || !Application.isPlaying || MaxCachedChunks <= 0)
                return false;

            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (e.Chunk != chunk)
                    continue;

                // 缓存只应持有 Active 的 chunk；状态异常时归还引用并丢弃条目，
                // 让调用方走正常的 AddReference/LoadSceneAsync 路径。
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

        /// <summary>每帧由 SectrStreamingRuntime 调用；节流淘汰过期/超量的 chunk。</summary>
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

        /// <summary>清空缓存。unload=false 用于退出播放，避免在 shutdown 里启动卸载协程。</summary>
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
            // Count 是缓存持有的引用数，逐个归还原引用计数；到 0 时 SECTR 才会卸载。
            for (int i = 0; i < e.Count; i++)
                e.Chunk.RemoveReference();
        }
    }
}
