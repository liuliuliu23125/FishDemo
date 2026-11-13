using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using FishDemo.Gameplay;

/// <summary>
/// 往 World.unity 里加一台「环湖轨道相机」和一套相机切换 rig，原 Pegasus 相机完全不动。
///
/// ── 第一性原理：为什么需要三样东西，而不是只加一台相机 ──
/// 拿掉原相机换成新的，只是渲染了一个新视角。但本工程有两处状态**不跟着相机走**：
///
///   1. **Crest 水面以「视点」为中心铺开**（`WaterRenderer.cs:99-112` 的 GetViewpoint
///      取 `_Camera`，即 public 的 `Viewer`）。视点还停在没渲染的相机上时，
///      水面的 clipmap 会铺在别处 —— 表现为「湖在这个方向，水却在那个方向」。
///      所以 rig 每次切换都重写 `water.Viewer`。
///
///   2. **SECTR 的 chunk 加载按 loader 自身 transform 判定**
///      （`SECTR_RegionLoader.cs:83-84` 用 `transform.position` 建 Bounds，不看相机）。
///      原 loader 挂在 Main Camera 上。新相机不配 loader 就没有 chunk 加载，
///      画面里只有水没有地形。所以给新相机也挂一个 loader，由 rig 只启用当台。
///
///   3. **`Camera.main` 依赖 tag + enabled**。两台都带 tag 时返回值不确定，
///      都不带时返回 null（GrassBRG 剔除兜底 `GrassBRGManager.cs:1831` 依赖它）。
///      所以 tag 由 rig 搬运。
///
/// ── 为什么轨道相机不用 Cinemachine ──
/// 工程没装（`Packages/manifest.json` 无 `com.unity.cinemachine`）。且上面三件事
/// Cinemachine 一件都解决不了，为它引入依赖不划算。轨道本身见 <see cref="LakeOrbitCamera"/>。
///
/// ── 幂等 ──
/// 重复执行先删旧的 `Lake Orbit Camera` 节点再重建；rig 组件重挂前先移除。
///
/// 用法：
///   -executeMethod LakeCameraSetup.Apply
///   -executeMethod LakeCameraSetup.Remove
///   -executeMethod LakeCameraSetup.Report
/// 输出 Logs/Lake/lake-camera.txt
/// </summary>
public static class LakeCameraSetup
{
    private const string Tag = "[LakeCamera]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-camera.txt";

    /// <summary>轨道相机节点名。重复执行时按这个名字找已有对象，保证幂等。</summary>
    private const string OrbitNodeName = "Lake Orbit Camera";

    /// <summary>rig 挂在哪个现有节点上。用场景里已有的权威根节点，不额外造容器。</summary>
    private const string RigHostName = "Crest Lake";

    public static void Apply() { Run("apply"); }
    public static void Remove() { Run("remove"); }
    public static void Report() { Run("report"); }

    private static void Run(string mode)
    {
        var sb = new StringBuilder();
        void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

        try
        {
            Directory.CreateDirectory(OutDir);
            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);
            W($"模式 = {mode}  场景 = {WorldScenePath}");

            var tourGo = GameObject.Find("Main Camera");
            if (tourGo == null) { W("FAIL：找不到 Main Camera"); Flush(sb, 2); return; }
            var tourCam = tourGo.GetComponent<Camera>();
            if (tourCam == null) { W("FAIL：Main Camera 上没有 Camera 组件"); Flush(sb, 2); return; }

            if (mode == "report") { Report(W, tourCam); Flush(sb, 0); return; }

            var stale = GameObject.Find(OrbitNodeName);
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale);

            // rig 可能挂在别的节点上（上一版实现），统一先清掉，避免出现两个 rig 抢相机。
            foreach (var oldRig in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>())
                UnityEngine.Object.DestroyImmediate(oldRig);

            if (mode == "remove")
            {
                // 轨道相机的 loader 随节点一起删；tag / enabled 状态复原交给巡游相机。
                tourCam.enabled = true;
                tourGo.tag = "MainCamera";
                var tourListenerOnCam = tourGo.GetComponent<AudioListener>();
                if (tourListenerOnCam != null) tourListenerOnCam.enabled = true;
                var tourLoaderOnCam = tourGo.GetComponent<SECTR_RegionLoader>();
                if (tourLoaderOnCam != null) tourLoaderOnCam.enabled = true;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                W($"已删除 {OrbitNodeName} 与 rig，原相机状态复原");
                W("==== 结论：PASS ====");
                Flush(sb, 0);
                return;
            }

            // ── 1. 建轨道相机 ──
            var orbitGo = new GameObject(OrbitNodeName);
            var orbitCam = orbitGo.AddComponent<Camera>();
            // 从原相机拷渲染配置：URP 下相机上的这些项必须一致，
            // 否则会得到「换相机后风格突变」的假象（后处理、HDR、剔层）。
            CopyCameraSettings(tourCam, orbitCam);
            orbitCam.tag = "Untagged";          // 运行时由 rig 搬 tag
            orbitCam.enabled = false;           // 运行时由 rig 打开

            var orbit = orbitGo.AddComponent<LakeOrbitCamera>();
            W($"已建 {OrbitNodeName}（半径 300m / 高度 水位+26m / 120s 一圈）");

            // ── 2. 轨道相机自己的 AudioListener ──
            // 两台相机各带一个，由 rig 精确开关，避免出现「没有监听器」告警。
            orbitGo.AddComponent<AudioListener>().enabled = false;

            // ── 3. 轨道相机自己的 SECTR loader ──
            // 参数照抄原相机的（LoadSize / UnloadBuffer / LayersToLoad）。
            var tourLoader = tourGo.GetComponent<SECTR_RegionLoader>();
            if (tourLoader == null) { W("FAIL：原相机上没有 SECTR_RegionLoader，无法拷贝加载参数"); Flush(sb, 2); return; }
            var orbitLoader = orbitGo.AddComponent<SECTR_RegionLoader>();
            orbitLoader.LoadSize = tourLoader.LoadSize;
            orbitLoader.UnloadBuffer = tourLoader.UnloadBuffer;
            orbitLoader.LayersToLoad = tourLoader.LayersToLoad;
            orbitLoader.enabled = false;
            W($"已挂 SECTR_RegionLoader（LoadSize={orbitLoader.LoadSize}，UnloadBuffer={orbitLoader.UnloadBuffer}）");

            // ── 4. rig ──
            var host = GameObject.Find(RigHostName);
            if (host == null)
            {
                W($"FAIL：找不到 rig 宿主节点 {RigHostName}（先跑 LakeCrestSetup.Apply）");
                Flush(sb, 2);
                return;
            }
            var rig = host.AddComponent<LakeCameraRig>();
            var so = new SerializedObject(rig);
            so.FindProperty("_tourCamera").objectReferenceValue = tourCam;
            so.FindProperty("_orbitCamera").objectReferenceValue = orbitCam;
            so.FindProperty("_toggleKey").intValue = (int)KeyCode.C;
            so.FindProperty("_flyToggleKey").intValue = (int)KeyCode.F;
            so.FindProperty("_flyMoveSpeed").floatValue = 120f;
            so.FindProperty("_startOnOrbit").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            W($"已在 {RigHostName} 上挂 LakeCameraRig（C 切相机 / F 自由飞行，启动即环湖轨道）");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            W("");
            W("=== 校验 ===");
            bool ok = Report(W, tourCam);
            W($"==== 结论：{(ok ? "PASS" : "FAIL")} ====");
            Flush(sb, ok ? 0 : 2);
        }
        catch (Exception ex)
        {
            W($"异常：{ex}");
            W("==== 结论：FAIL ====");
            Flush(sb, 1);
        }
    }

    /// <summary>
    /// 复制相机的渲染相关配置。**不复制** transform（轨道脚本自己摆位）与 enabled/tag。
    ///
    /// 复制项的依据：这些都是「换了镜头但画面风格必须不变」的项。
    /// 尤其 `cullingMask` —— Crest 的水面 chunk 在 Water 层（layer 4），
    /// 新相机漏掉这一层就一滴水都看不到，且**不报错**。
    /// </summary>
    private static void CopyCameraSettings(Camera src, Camera dst)
    {
        dst.clearFlags = src.clearFlags;
        dst.backgroundColor = src.backgroundColor;
        dst.cullingMask = src.cullingMask;
        dst.fieldOfView = src.fieldOfView;
        dst.nearClipPlane = src.nearClipPlane;
        dst.farClipPlane = src.farClipPlane;
        dst.allowHDR = src.allowHDR;
        dst.allowMSAA = src.allowMSAA;
        dst.allowDynamicResolution = src.allowDynamicResolution;
        dst.orthographic = src.orthographic;
        dst.renderingPath = src.renderingPath;
        dst.useOcclusionCulling = src.useOcclusionCulling;
        dst.depth = src.depth;

        // URP 的相机附加数据（后处理、渲染器序号）挂在 UniversalAdditionalCameraData 上，
        // 不随 AddComponent<Camera> 自动生成。用反射取类型，避免 AssetEditor 程序集
        // 硬依赖 URP 的 Internal 命名空间（本工程 UrpPipelineSetup 也是这么做的）。
        var srcData = src.GetComponent("UniversalAdditionalCameraData");
        if (srcData != null)
        {
            var type = srcData.GetType();
            var dstData = dst.GetComponent(type) ?? dst.gameObject.AddComponent(type);
            var so = new SerializedObject(dstData);
            var srcSo = new SerializedObject(srcData);
            var it = srcSo.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference) continue;
                if (it.name == "m_Script") continue;
                var dstProp = so.FindProperty(it.name);
                if (dstProp == null) continue;
                if (dstProp.propertyType != it.propertyType) continue;
                so.CopyFromSerializedProperty(it);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private static bool Report(Action<string> W, Camera tourCam)
    {
        bool ok = true;

        var orbitGo = GameObject.Find(OrbitNodeName);
        if (orbitGo == null) { W("  FAIL：找不到轨道相机节点"); return false; }
        var orbitCam = orbitGo.GetComponent<Camera>();
        var orbit = orbitGo.GetComponent<LakeOrbitCamera>();
        var listener = orbitGo.GetComponent<AudioListener>();
        var loader = orbitGo.GetComponent<SECTR_RegionLoader>();
        var rig = UnityEngine.Object.FindObjectOfType<LakeCameraRig>();

        W($"  轨道相机 = {(orbitCam != null ? "有" : "缺")}, " +
          $"LakeOrbitCamera = {(orbit != null ? "有" : "缺")}, " +
          $"AudioListener = {(listener != null ? "有" : "缺")}, " +
          $"SECTR_RegionLoader = {(loader != null ? "有" : "缺")}");
        if (orbitCam == null || orbit == null || listener == null || loader == null) ok = false;

        W($"  rig = {(rig != null ? "有（挂在 " + rig.gameObject.name + "）" : "缺")}");
        if (rig == null) ok = false;

        if (orbitCam != null)
        {
            // 剔层必须与原相机一致：Water 层（bit 4）漏掉就看不到水面，且不报错。
            bool sameMask = orbitCam.cullingMask == tourCam.cullingMask;
            bool hasWater = (orbitCam.cullingMask & (1 << 4)) != 0;
            W($"  cullingMask 与原相机一致 = {sameMask}（Water 层={hasWater}）");
            if (!sameMask || !hasWater) ok = false;

            bool sameFov = Mathf.Approximately(orbitCam.fieldOfView, tourCam.fieldOfView);
            W($"  FOV = {orbitCam.fieldOfView}（原相机 {tourCam.fieldOfView}，一致={sameFov}）");
            if (!sameFov) ok = false;
        }

        if (loader != null)
        {
            var tourLoader = tourCam.GetComponent<SECTR_RegionLoader>();
            bool same = tourLoader != null && loader.LoadSize == tourLoader.LoadSize;
            W($"  SECTR LoadSize = {loader.LoadSize}（与原相机一致={same}）");
            if (!same) ok = false;
        }

        // 组件级开关状态：轨道相机在场景里应是「未启用」，由 rig 在运行时打开。
        W($"  场景内初始状态：轨道 Camera.enabled={orbitCam?.enabled}，" +
          $"loader.enabled={loader?.enabled}（期望都是 False，运行时由 rig 接管）");
        W($"  原相机 Camera.enabled={tourCam.enabled}，tag={tourCam.tag}（保持不变）");

        return ok;
    }

    private static void Flush(StringBuilder sb, int code)
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(ResultPath, sb.ToString());
        EditorApplication.Exit(code);
    }
}
