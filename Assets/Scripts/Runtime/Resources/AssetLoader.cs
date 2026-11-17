using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace FishDemo.AssetManagement
{
    public readonly struct AssetLease<T> where T : Object
    {
        public readonly T Asset;

        public readonly AssetHandle Handle;

        public AssetLease(T asset, AssetHandle handle)
        {
            Asset = asset;
            Handle = handle;
        }

        public bool IsValid => Asset != null && Handle != null;
    }

    public sealed class AssetLoader
    {
        private readonly Dictionary<string, List<AssetHandle>> _handles = new(64);

        public async Task<AssetLease<T>> LoadAsync<T>(string location) where T : Object
        {
            var handle = YooAssetInitializer.Package.LoadAssetAsync(location);
            await handle;

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[AssetLoader] 加载失败: {location}, {handle.Error}");
                handle.Release();
                return default;
            }

            Track(location, handle);
            return new AssetLease<T>(handle.GetAssetObject<T>(), handle);
        }

        public async Task<AssetLease<GameObject>> InstantiateAsync(string location)
        {
            var handle = YooAssetInitializer.Package.LoadAssetAsync(location);
            await handle;

            if (handle.Status != EOperationStatus.Succeeded)
            {
                Debug.LogError($"[AssetLoader] 实例化失败: {location}, {handle.Error}");
                handle.Release();
                return default;
            }

            Track(location, handle);
            var go = handle.InstantiateSync();
            return new AssetLease<GameObject>(go, handle);
        }

        public void Release(string location, AssetHandle handle)
        {
            if (handle == null || !_handles.TryGetValue(location, out var list))
            {
                return;
            }

            if (!list.Remove(handle))
            {
                return;
            }

            handle.Release();

            if (list.Count == 0)
            {
                _handles.Remove(location);
            }
        }

        public void ReleaseAll()
        {
            foreach (var list in _handles.Values)
            {
                foreach (var handle in list)
                {
                    handle.Release();
                }
            }

            _handles.Clear();
        }

        public int HandleCount
        {
            get
            {
                int count = 0;
                foreach (var list in _handles.Values)
                {
                    count += list.Count;
                }

                return count;
            }
        }

        private void Track(string location, AssetHandle handle)
        {
            if (!_handles.TryGetValue(location, out var list))
            {
                list = new List<AssetHandle>(1);
                _handles[location] = list;
            }

            list.Add(handle);
        }
    }
}
