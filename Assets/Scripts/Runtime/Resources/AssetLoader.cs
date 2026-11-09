using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace FishDemo.AssetManagement
{
    /// <summary>
    /// 资源加载结果：把资源对象和句柄一起交给调用方。
    ///
    /// 为什么必须同时交出句柄：
    /// YooAsset 每次 LoadAssetAsync 都会创建新句柄并让 Provider 的 RefCount +1
    /// （ProviderBase.cs:288-291），只有句柄能释放。若封装层只返回资源对象，
    /// 调用方永远拿不到句柄，资源就再也卸不掉。所以这里必须成对给出。
    /// </summary>
    public readonly struct AssetLease<T> where T : Object
    {
        /// <summary>资源对象本体。</summary>
        public readonly T Asset;

        /// <summary>底层句柄，用于释放。</summary>
        public readonly AssetHandle Handle;

        public AssetLease(T asset, AssetHandle handle)
        {
            Asset = asset;
            Handle = handle;
        }

        /// <summary>是否加载成功。</summary>
        public bool IsValid => Asset != null && Handle != null;
    }

    /// <summary>
    /// 资源加载器：集中管理 YooAsset 句柄的获取与释放。
    ///
    /// 设计要点：
    /// - 每次 Load 都向 YooAsset 申请新句柄（库内部有 Provider 复用，重复加载成本低），
    ///   句柄与调用方一一对应，避免共享句柄导致的悬空与二次释放。
    /// - 以 location 为键记录未释放句柄，便于监控与统一回收。
    /// - 不缓存 AssetObject，避免与 YooAsset 自身的资源缓存重复。
    ///
    /// 注意：本类只管理 Handle 生命周期，不负责 GameObject 池化（那是 Streaming 层的事）。
    /// </summary>
    public sealed class AssetLoader
    {
        /// <summary>location → 该路径当前未释放的句柄列表。同一路径可能被多处同时引用。</summary>
        private readonly Dictionary<string, List<AssetHandle>> _handles = new(64);

        /// <summary>
        /// 异步加载资源，同时返回资源与句柄。调用方用完必须 Release。
        /// </summary>
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

        /// <summary>
        /// 异步加载 GameObject 并实例化。实例化后句柄仍需保留，直到实例销毁。
        /// </summary>
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

        /// <summary>
        /// 释放一个句柄。调用方必须传入加载时拿到的那个句柄。
        /// </summary>
        public void Release(string location, AssetHandle handle)
        {
            if (handle == null || !_handles.TryGetValue(location, out var list))
            {
                return;
            }

            if (!list.Remove(handle))
            {
                // 不在本类登记表中，说明不是通过本类加载的，交给调用方自行处理
                return;
            }

            handle.Release();

            if (list.Count == 0)
            {
                _handles.Remove(location);
            }
        }

        /// <summary>
        /// 释放全部句柄。用于关卡切换或内存告急。
        ///
        /// 调用方必须确保没有对象仍在使用这些资源，否则会拿到已销毁的 Asset。
        /// 释放后建议调用 Package.UnloadUnusedAssetsAsync() 让 bundle 真正卸载。
        /// </summary>
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

        /// <summary>当前未释放的句柄总数，用于内存与泄漏监控。</summary>
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

        /// <summary>登记句柄，保证 ReleaseAll 能覆盖到。</summary>
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
