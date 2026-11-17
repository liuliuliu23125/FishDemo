using System.Collections;
using UnityEngine;

namespace FishDemo.Gameplay
{
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
            RequestAssetScan();
        }

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
            yield return new WaitForEndOfFrame();
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
            SectrChunkCache.Flush(unload: false);
        }

        private void OnLowMemory()
        {
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
            SectrChunkCache.Flush(unload: false);
        }
    }
}
