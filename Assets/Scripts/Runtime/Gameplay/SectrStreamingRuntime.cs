using System.Collections;
using UnityEngine;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// SECTR 流式补丁的运行时宿主。
    ///
    /// 负责两件 SECTR 原版没有的事：
    /// 1. 每帧驱动 <see cref="SectrChunkCache.Tick"/>；
    /// 2. 把同步的 `Resources.UnloadUnusedAssets()` 改成**等卸载完成 + 全局冷却 + 异步**。
    ///
    /// 为什么需要独立宿主：SECTR_Chunk 挂在主场景的 sector 上，可能在 OnDisable/退出时
    /// 无法可靠地启动协程；用一个 DontDestroyOnLoad 的宿主更稳。
    /// </summary>
    public sealed class SectrStreamingRuntime : MonoBehaviour
    {
        private static SectrStreamingRuntime s_instance;
        private static int s_pendingUnloads;
        private static bool s_scanRunning;
        private static bool s_forceScanPending;
        private static float s_nextScanTime;

        public static int PendingUnloads => s_pendingUnloads;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Ensure()
        {
            if (!Application.isPlaying || s_instance != null)
                return;

            var go = new GameObject("~SectrStreamingRuntime");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<SectrStreamingRuntime>();
        }

        private void Update()
        {
            SectrChunkCache.Tick();
        }

        /// <summary>
        /// 跟踪一次 UnloadSceneAsync。卸载完成前不做资产扫描，避免扫到半卸载的资产。
        /// </summary>
        public static void TrackUnload(AsyncOperation op)
        {
            if (op == null || !Application.isPlaying)
                return;

            Ensure();
            if (s_instance == null)
                return;

            s_pendingUnloads++;
            s_instance.StartCoroutine(WaitUnload(op));
        }

        private static IEnumerator WaitUnload(AsyncOperation op)
        {
            while (op != null && !op.isDone)
                yield return null;

            s_pendingUnloads = Mathf.Max(0, s_pendingUnloads - 1);
            // 卸载都结束后再尝试一次；有 force 请求时即使冷却未到也会扫。
            RequestAssetScan();
        }

        /// <summary>
        /// 请求一次资产清理。默认受冷却限制；force 会越过冷却（低内存时用）。
        /// 若还有卸载未完成，请求会保留到卸载结束后执行。
        /// </summary>
        public static void RequestAssetScan(bool force = false)
        {
            if (!Application.isPlaying)
                return;

            if (force)
                s_forceScanPending = true;

            Ensure();
            if (s_instance == null || s_scanRunning || s_pendingUnloads > 0)
                return;

            if (!s_forceScanPending && Time.realtimeSinceStartup < s_nextScanTime)
                return;

            s_forceScanPending = false;
            s_scanRunning = true;
            s_nextScanTime = Time.realtimeSinceStartup + SectrChunkCache.AssetScanCooldown;
            s_instance.StartCoroutine(ScanUnusedAssets());
        }

        private static IEnumerator ScanUnusedAssets()
        {
            // 先等一帧，让本帧的 SceneManager.UnloadSceneAsync 完成对象销毁。
            yield return new WaitForEndOfFrame();
            // 异步版本仍会做全量扫描，但不会像同步版那样把整段扫描压在一帧里。
            yield return Resources.UnloadUnusedAssets();
            s_scanRunning = false;
        }

        private void OnApplicationQuit()
        {
            if (SectrChunkCache.TotalHits + SectrChunkCache.TotalEvictions > 0)
            {
                Debug.Log($"[SectrStreaming] 缓存命中 {SectrChunkCache.TotalHits} 次，" +
                          $"淘汰 {SectrChunkCache.TotalEvictions} 次，剩余 {SectrChunkCache.Count} 块");
            }
            // 退出播放时只清引用，不在 shutdown 里启动 UnloadSceneAsync。
            SectrChunkCache.Flush(unload: false);
        }

        private void OnLowMemory()
        {
            // 低内存时优先保命：清空草驻留、chunk 缓存，并强制做一次资产清理。
            // 草是自管内存（NativeArray + GraphicsBuffer），不会随 UnloadUnusedAssets 回收，
            // 必须显式释放。
            SectrGrassStreamer.FlushAll();
            SectrChunkCache.Flush(unload: true);
            RequestAssetScan(force: true);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            s_scanRunning = false;
            s_pendingUnloads = 0;
            // 退出播放/域重载时清掉缓存引用；此时不应再启动卸载。
            SectrChunkCache.Flush(unload: false);
        }
    }
}
