using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FishDemo.Rendering;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 把 M1 的 BRG 草摆进 World.unity，让打开场景 / 进 Play Mode 就能直接看到效果。
    ///
    /// 摆在哪：主相机视线前方、相机贴地（实测相机在 (0,420,-1228.8)，脚下地面 327.2）。
    /// 用 probe 结果把草放在相机正前方 25m 处的地表上，1000 株 × 0.35m 间距 ≈ 11m 见方，
    /// 在第一人称视角里正好占满近景。
    ///
    /// 幂等：重复执行会先删掉旧的 GrassBRG 节点，不会堆积。
    ///
    /// 用法：-executeMethod FishDemo.EditorTools.GrassSceneSetup.Run
    /// 菜单：FishDemo/Rendering/⑤ 把 M1 草放进 World 场景
    /// </summary>
    public static class GrassSceneSetup
    {
        private const string Tag = "[GrassScene]";
        private const string ResultPath = "Logs/Grass/scene-setup.txt";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string RootName = "GrassBRG (M1)";

        [MenuItem("FishDemo/Rendering/⑤ 把 M1 草放进 World 场景", false, 105)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            // 幂等：清掉旧节点
            foreach (var existing in scene.GetRootGameObjects())
            {
                if (existing.name == RootName)
                {
                    Object.DestroyImmediate(existing);
                    W($"已移除旧节点 {RootName}");
                }
            }

            var cam = Camera.main;
            if (cam == null)
            {
                W("找不到 MainCamera，中止");
                Flush(sb);
                return;
            }

            Vector3 fwd = cam.transform.forward;

            // ── 位置 ──
            // 实测相机 (0, 420, -1228.8)，地面约 327（离地 93m），forward=(0,-0.42,0.91)，
            // 即俯角 asin(0.42) ≈ 25°，FOV 60° → 视锥下边缘只到 -55°。
            //
            // 两个都试过、都不可行：
            //   · 视线与地面交点（200m 外）：1m 草在 512px 下只有 2 像素，看不见。
            //   · 相机正下方（94m 距离）：-90° 在视锥外，剔除后 0 像素（实测命中 0）。
            //
            // 可行位置：沿视锥「下边缘内侧」放。取俯角 -50°（离下边界 -55° 留 5° 余量），
            // 从 93m 高落到地面需要水平距离 93 / tan(50°) ≈ 78m。
            // 该处距相机约 121m，512px/60° 下约 0.27 m/px，1m 草 ≈ 4 像素——
            // 偏小，所以 M1 用 3 倍缩放让它 ≈ 12 像素，肉眼可辨。
            const float lookDownAngleDeg = 50f;
            float horizontal = cam.transform.position.y / Mathf.Tan(lookDownAngleDeg * Mathf.Deg2Rad);
            // 用相机水平朝向作为前方，避免写死 Z 轴。
            Vector3 flatFwd = new Vector3(fwd.x, 0f, fwd.z).normalized;
            Vector3 probeXZ = cam.transform.position + flatFwd * horizontal;

            float groundY = SampleGround(probeXZ.x, probeXZ.z, cam.transform.position.y - 93f);
            // 用真实落差重算一次水平距离，保证落点就在地面上。
            float actualDrop = cam.transform.position.y - groundY;
            horizontal = actualDrop / Mathf.Tan(lookDownAngleDeg * Mathf.Deg2Rad);
            probeXZ = cam.transform.position + flatFwd * horizontal;
            groundY = SampleGround(probeXZ.x, probeXZ.z, groundY);

            Vector3 anchorPos = new Vector3(probeXZ.x, groundY, probeXZ.z);

            var root = new GameObject(RootName);
            root.transform.position = anchorPos;

            var mgr = root.AddComponent<GrassBRGManager>();
            // 1000 株、间距 0.6m → 约 19m 见方，在 121m 处约占画面 1/4 宽。
            mgr.ConfigureForValidation(1000, 0.6f, 0.6f);
            mgr.SetAnchor(root.transform, true);

            // 8 倍缩放：118m 处 1m 草约 4 像素，放大到 ~32 像素才一眼看清。
            // 这是 M1 的可见性权宜，不是最终视觉比例——M2 会用真实尺度 + 近距离。
            var so = new SerializedObject(mgr);
            so.FindProperty("_scaleMultiplier").floatValue = 8f;
            so.ApplyModifiedPropertiesWithoutUndo();

            W($"相机 = {cam.transform.position}, forward = {fwd}");
            W($"草锚点（视锥下边缘内侧）= {anchorPos}，地面高 {groundY:F1}");
            W($"锚点距相机 = {Vector3.Distance(cam.transform.position, anchorPos):F1} m");
            W("已挂 GrassBRGManager：1000 实例 / 间距 0.6m / 缩放 ×8 / 贴地");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            W($"已保存 {WorldScenePath}");
            Flush(sb);
        }

        /// <summary>
        /// 取 (x,z) 处的地面高度。
        ///
        /// 为什么必须先把 chunk 打开：编辑期 World.unity 里没有 Terrain（地形都在 chunk 场景，
        /// 运行时由 SECTR 加载）。不打开的话 SampleHeight 拿不到任何东西。
        /// </summary>
        private static float SampleGround(float x, float z, float fallbackY)
        {
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (!s.enabled || s.path.EndsWith("World.unity")) continue;
                if (!File.Exists(s.path)) continue;
                EditorSceneManager.OpenScene(s.path, OpenSceneMode.Additive);
            }

            var terrains = Object.FindObjectsOfType<Terrain>();
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 tp = t.transform.position;
                Vector3 ts = t.terrainData.size;
                if (x < tp.x || x > tp.x + ts.x || z < tp.z || z > tp.z + ts.z)
                    continue;
                // SampleHeight 是相对地形原点的局部高度，要加 transform.position.y。
                return t.SampleHeight(new Vector3(x, 0f, z)) + tp.y;
            }

            return fallbackY;
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
            File.WriteAllText(ResultPath, sb.ToString());
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
