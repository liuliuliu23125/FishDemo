using UnityEngine;
using ProceduralWorlds.GTS;

/// <summary>
/// GTS mesh chunk 的运行时数据注入器。
///
/// ── 为什么需要它 ──
/// GTS 的 splat 混合依赖一组全局 Vector 数组（_UVData / _ColorData / _LayerDataA 等），
/// 由 GTSComponent.UpdateMaterial() 用 Shader.SetGlobalVectorArray 注入。
///
/// GTSMesh 标了 [ExecuteAlways]，在**编辑器**里能靠 Update/OnValidate 触发；
/// 但它在**真机运行时**不会自动跑初始化路径：
///   - GTSComponent.Update() 只在 m_isDirty 时 Refresh()
///   - m_isDirty 由 OnValidate 置位，而 OnValidate 只在编辑器生效
/// 结果：打包后全局数组未设置，地形贴图 UV / 混合全错。
///
/// 本组件在 Start 时显式调用一次 Refresh()，保证运行时数据就位。每帧开销为 0。
///
/// ── 为什么放这个目录 ──
/// GTS 没有 asmdef，编进默认的 Assembly-CSharp。
/// asmdef 程序集（如 FishDemo.Resources）引用不到 Assembly-CSharp，
/// 所以本文件必须留在默认程序集（Assets/ 下、且不在任何 asmdef 覆盖的目录里）。
/// 放在 Assets/Editor 又会被编进 Assembly-CSharp-Editor，打包后不存在。
/// 因此放在 Assets/GTSRuntime/ 这个无 asmdef 的独立目录。
///
/// ── 为什么挂在每个 subTile 上而不是场景单例 ──
/// GTSComponent.UpdateMaterial() 除了设全局数组，还会把本组件的
/// gtsControlTextures 等绑到材质上，而 Control 贴图是 per-chunk 的。
/// 挂每块代价很小（4 个组件/ chunk，只跑一次 Start）。
/// </summary>
[DisallowMultipleComponent]
public class GtsRuntimeDataBootstrap : MonoBehaviour
{
    /// <summary>与场景里 GTSMesh 使用同一个 Profile，保证全局数组内容一致。</summary>
    public GTSProfile profile;

    private void Start()
    {
        var gtsMesh = GetComponent<GTSMesh>();
        if (gtsMesh == null)
            return;

        if (gtsMesh.profile == null)
            gtsMesh.profile = profile;

        // Refresh → UpdateMaterial（注入全局数组）+ SetReferencedTextures（绑定 per-材质贴图）
        gtsMesh.Refresh();
    }
}
