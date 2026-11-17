# YooAsset · Runtime/AsyncOperation 规约

> 第三方源码，不改。调度与时间片细节见 [yooasset_3.0.7 §3.1](../../../../Docs/yooasset_3.0.7.md)。

## 1. 三层结构

```
AsyncOperationSystem          全局：时间片预算 + 调度器列表
  └─ AsyncOperationScheduler  每个 ResourcePackage 一个（全局调度器固定存在）
       └─ AsyncOperationBase   每个初始化/版本/清单/加载/下载操作
```

- 调度器按 `Priority` 降序，同优先级按创建顺序稳定排序。
- 每帧 `YooAssetsDriver.Update()` → `AsyncOperationSystem.Update()`：先刷新帧起点，再依次更新调度器；`IsBusy` 为 true 即停止。
- `MaxTimeSlice` 的项目值与钳制规则见包 [AGENTS.md §6](../../AGENTS.md)。

## 2. 操作生命周期

`AsyncOperationBase` 的公开状态：`Status`（None/Processing/Succeeded/Failed）、`Error`、`Progress`、`IsDone`、`Priority`。

- `StartOperation` → `InternalStart`；之后每帧 `UpdateOperation` → `InternalUpdate`，直到 `IsDone`。
- 子操作通过 `AddChildOperation` 挂载，父操作在子操作完成前保持 Processing。
- 子类异常会被捕获并转成 `Failed`，不会让调度器停摆。
- `WaitForAsyncComplete` 只在编辑器/同步加载使用；移动端不要在主线程循环等待。

## 3. `await` 的陷阱

`OperationAwaiter.GetResult()` **不抛业务异常**：

```csharp
await op;                 // Status == Failed 也不会抛
if (op.Status != EOperationStatus.Succeeded) { ... }
```

因此所有 `await` 后必须检查 `Status` / `Error`（项目 `YooAssetInitializer` 就是这么做的）。`HandleBase.GetAwaiter()` 在句柄失效时会抛 `InvalidOperationException`，那是另一种情况。

## 4. 扩展

`CustomAsyncOperation` 可提交自定义操作，属于扩展禁区（见包 AGENTS §8）；注册入口是 `ResourcePackage.StartOperation`。

## 5. 硬约束

- 所有 API 只在 Unity 主线程调用（`ResourceManager` 明确非线程安全）。
- 不要在 `Update` 里 `while (!op.IsDone)`；用 `await`、`Completed` 回调或 `IEnumerator`。
- 时间片只约束「每帧总预算」，单个内部步骤（如一次同步 IO）仍可能超时；拆细操作是包内的事，不要在业务层假设每帧一定推进 N 个。

## 6. 相关

| 文档 | 何时读 |
|---|---|
| [yooasset_3.0.7 §3.1](../../../../Docs/yooasset_3.0.7.md) | 调度、时间片、优先级、`await` 实现 |
| [../ResourcePackage/AGENTS.md](../ResourcePackage/AGENTS.md) | 操作由谁创建 |
| [../../AGENTS.md](../../AGENTS.md) | 项目时间片设置 |
