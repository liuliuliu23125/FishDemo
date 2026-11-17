using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using FishDemo.Gameplay;

public static class LakeCameraSetup
{
    private const string Tag = "[LakeCamera]";
    private const string WorldScenePath = "Assets/Scenes/World.unity";
    private const string OutDir = "Logs/Lake";
    private const string ResultPath = OutDir + "/lake-camera.txt";

    private const string OrbitNodeName = "Lake Orbit Camera";

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

            foreach (var oldRig in UnityEngine.Object.FindObjectsOfType<LakeCameraRig>())
                UnityEngine.Object.DestroyImmediate(oldRig);

            if (mode == "remove")
            {
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

            var orbitGo = new GameObject(OrbitNodeName);
            var orbitCam = orbitGo.AddComponent<Camera>();
            CopyCameraSettings(tourCam, orbitCam);
            orbitCam.tag = "Untagged";
            orbitCam.enabled = false;

            var orbit = orbitGo.AddComponent<LakeOrbitCamera>();
            W($"已建 {OrbitNodeName}（半径 300m / 高度跟随地形（地表+18m，不低于水位+26m）/ 120s 一圈）");

            orbitGo.AddComponent<AudioListener>().enabled = false;

            var tourLoader = tourGo.GetComponent<SECTR_RegionLoader>();
            if (tourLoader == null) { W("FAIL：原相机上没有 SECTR_RegionLoader，无法拷贝加载参数"); Flush(sb, 2); return; }
            var orbitLoader = orbitGo.AddComponent<SECTR_RegionLoader>();
            orbitLoader.LoadSize = tourLoader.LoadSize;
            orbitLoader.UnloadBuffer = tourLoader.UnloadBuffer;
            orbitLoader.LayersToLoad = tourLoader.LayersToLoad;
            orbitLoader.enabled = false;
            W($"已挂 SECTR_RegionLoader（LoadSize={orbitLoader.LoadSize}，UnloadBuffer={orbitLoader.UnloadBuffer}）");

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
