using System.Collections.Generic;
using FishDemo.Rendering;
using UnityEngine;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// 让草数据跟随 SECTR chunk 的加载/卸载。
    ///
    /// ── 为什么需要 ──
    /// 树是 Terrain 的 native 数据，跟着 chunk 场景走（chunk 缓存已经覆盖）；
    /// 草不在 chunk 场景里，它是一份独立的 .bytes，原实现启动时把 36 块全读进内存
    /// （435 万实例，托管堆 260MB+ / GPU 133MB），与相机在哪无关。
    ///
    /// 这里把草的驻留集合对齐到「SECTR 当前已加载的 chunk」：
    /// 相机离开 → chunk 卸载 → 对应草数据释放；进入 → 重新读入。
    /// 驻留量由 SECTR 的加载范围天然封顶，另外再给一个硬上限兜底。
    ///
    /// ── 为什么放在 Gameplay ──
    /// `FishDemo.Rendering` 是 asmdef，引用不到无 asmdef 的 SECTR；
    /// Gameplay 编在 Assembly-CSharp，两边都能调。
    ///
    /// ── 为什么轮询而不是事件 ──
    /// SECTR 的加载完成回调散在第三方代码里，改它要维护补丁；
    /// 0.1s 轮询 36 个组件的 IsLoaded 是常数级开销，且天然容忍异步加载的中间态。
    /// </summary>
    public sealed class SectrGrassStreamer : MonoBehaviour
    {
        /// <summary>草数据硬上限（chunk 数）。SECTR 本身约驻留 12~15 块 + 缓存 4~6 块。</summary>
        public static int MaxResidentChunks = 24;

        /// <summary>轮询间隔（秒）。加载/卸载本来就有 50ms 限流，更密没有意义。</summary>
        public static float PollInterval = 0.1f;

        /// <summary>每隔多久重新扫描一次 SECTR_Chunk 组件（场景切换/动态生成时）。</summary>
        public static float ChunkScanInterval = 5f;

        private static SectrGrassStreamer s_instance;

        private GrassBRGManager _manager;
        private SECTR_Chunk[] _chunks;
        private float _nextPoll;
        private float _nextChunkScan;

        private readonly HashSet<string> _resident = new HashSet<string>();
        private readonly List<string> _order = new List<string>();
        /// <summary>加载失败过的 chunk（缺文件/解析失败）。避免每 tick 重试同一块，饿死后面的块。</summary>
        private readonly HashSet<string> _failed = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Ensure()
        {
            // 必须在场景加载前告诉 GrassBRGManager：不要一次性读全量草数据。
            // 该静态标志由本方法写入，时间早于任何 MonoBehaviour.OnEnable。
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
            // 非流式（全量加载 / 打包后回退）时不参与；每帧都检查，
            // 避免验收脚本切回全量后本组件还去打 AddChunk。
            if (_manager == null || !_manager.IsStreaming)
                return;

            if (_chunks == null || now >= _nextChunkScan)
            {
                _chunks = FindObjectsOfType<SECTR_Chunk>();
                _nextChunkScan = now + ChunkScanInterval;
            }

            // 1) 卸载：驻留集合里、但对应 chunk 已经不在已加载状态。
            //    每 tick 最多卸一个：每次移除都要重建整张剔除表（复制其余 chunk 的
            //    positions），一次卸 9 块会把这些复制叠在同一帧。
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

            // 2) 容量兜底：超上限时先丢最早加入的（SECTR 缓存命中的块也会被丢，
            //    再进入视野时重新读盘；这是显存上限与命中率之间的取舍）。
            while (_order.Count > MaxResidentChunks)
            {
                // 无论 Remove 是否命中，都要把本地记录减掉，否则这里会死循环。
                string oldest = _order[0];
                _manager.RemoveGrassChunk(oldest);
                _resident.Remove(oldest);
                _order.RemoveAt(0);
            }

            // 3) 加载：每 tick 最多一个，避免读盘 + 排序 + 上传与场景激活挤在同一帧。
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

        /// <summary>低内存应急：清空全部草驻留。下一帧起按 SECTR 状态重新加载。</summary>
        public static void FlushAll()
        {
            if (s_instance == null)
                return;
            if (s_instance._manager != null)
                s_instance._manager.ClearGrassChunks();
            // 池化 buffer 也是显存，低内存时一并归还。
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
