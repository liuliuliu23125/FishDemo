# YooAsset · Runtime/DownloadSystem 规约

> 第三方源码，不改。三层并发与重试细节见 [yooasset_3.0.7 §3.2](../../../../Docs/yooasset_3.0.7.md)。

## 1. 分层

```
ResourceDownloaderOperation      业务层：一组 bundle 的下载/进度/中止
  └─ DownloadSchedulerOperation  调度层：并发数 + 每帧启动数 + 引用计数
       └─ DownloadFileBaseOperation  单文件：请求/重试/断点续传/看门狗
            └─ IDownloadBackend / IDownloadRequest  传输层（UnityWebRequest / UnityWebBackend）
```

`DownloadSchedulerOperation` 由文件系统（Sandbox/WebNetwork）创建，配置来自 `EFileSystemParameter`。

## 2. 并发控制

`DownloadMaxConcurrency` / `DownloadMaxRequestPerFrame` 的项目值见包 [AGENTS.md §6](../../AGENTS.md)。`BundleLoadingMaxConcurrency` 是**另一层**（bundle 装载），不要和下载并发混为一谈。

## 3. 重试 / 断点续传 / 看门狗

- `IDownloadRetryPolicy`：`CanRetry` + 退避时长；`DownloadRetryController` 管理次数与等待，不执行传输。
- `IDownloadURLPolicy`：在多个远端 URL 间选择/回退。
- `DownloadWatchdogTimeout`：秒；0 表示禁用。移动端必须 > 0，否则请求卡死无感知。
- `DownloadResumeMinimumSize`：小于该大小的文件不尝试断点续传。
- 下载任务的 `ReferenceCount <= 0` 时调度器会主动 `Abort`，避免无人等待的任务占槽。

## 4. 业务入口

下载/解压/导入/预算 API 见 [ResourcePackage §5](../ResourcePackage/AGENTS.md)；下载器可按标签、全部或指定资源创建。

## 5. 硬约束

- 不要在业务层自己开 `UnityWebRequest` 下载 bundle；走 `ResourceDownloaderOperation`，否则 catalog/缓存状态不一致。
- 不要随意 `Abort` 下载器后立刻重建同名 bundle 下载；让调度器清理引用计数。
- `Paused=true` 只停止启动新任务，已启动的继续；暂停不是取消。
- 远端 URL 变更用 `IRemoteService`，不要在下载层硬编码域名。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §3.2](../../../../Docs/yooasset_3.0.7.md) | 并发、重试、续传、看门狗、URL 选择 |
| [../FileSystem/AGENTS.md](../FileSystem/AGENTS.md) | 参数落在哪个文件系统 |
| [../../AGENTS.md](../../AGENTS.md) | 项目参数表 |
