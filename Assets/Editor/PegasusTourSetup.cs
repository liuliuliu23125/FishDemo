using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Pegasus;

namespace FishDemo.EditorTools
{
    /// <summary>
    /// 在世界中心外围布置一条 Pegasus 环绕巡游路线，用于实时观察：
    /// 1. SECTR chunk 随相机移动的加载 / 卸载；
    /// 2. 植被（Terrain 树 + BRG 草）的远近距离 LOD 切换；
    /// 3. 地形 mesh 的整体形态。
    ///
    /// 为什么放在 Assets/Editor（默认程序集）而不是 FishDemo.Editor（有 asmdef）：
    /// Pegasus 与 SECTR 都没有 asmdef，编在 Assembly-CSharp / Assembly-CSharp-Editor。
    /// 有 asmdef 的程序集引用不到默认程序集（依赖方向是反的），
    /// 同 Assets/Editor/AGENTS.md §1 的结论。
    ///
    /// 为什么高度在编辑期烘焙、运行时关掉高度校正：
    /// Pegasus 的高度校验走 Terrain.activeTerrains（PegasusManager.cs:1138），
    /// 而本工程的 chunk 是运行时由 SECTR 异步加载的。若让 Pegasus 在运行时采样，
    /// 路径高度会随「当时恰好加载了哪几块」而漂移，同一圈路线的海拔每次都不同，
    /// 也就无法用来判断 LOD 距离是否稳定。
    /// 这里在编辑期把 36 个 chunk 的真实 TerrainData 采一遍，把世界坐标 Y 直接写进 POI，
    /// 运行时设 PoiHeightCheckType.None，路线就完全确定。
    ///
    /// 幂等：重复执行会先删掉旧的 Pegasus 节点再重建。
    /// 注意 World.unity 是流水线产物，重跑 WorldPipeline 后需要再执行本菜单。
    ///
    /// 用法：菜单 FishDemo/World/② 布置 Pegasus 环绕巡游
    /// </summary>
    public static class PegasusTourSetup
    {
        private const string Tag = "[PegasusTour]";
        private const string WorldScenePath = "Assets/Scenes/World.unity";
        private const string ManagerRootName = "Pegasus Tour";

        /// <summary>
        /// 巡游半径（米）。
        ///
        /// 取 1100 而不是更"安全"的 1300，是因为它决定能覆盖多少 chunk：
        /// 世界 3072m = 6×6 个 512m chunk，RegionLoader.LoadSize=768（半宽 384）。
        /// 按「圆 ± 加载半宽」求并集，几何计算得：
        ///   半径 1300 → 覆盖 32/36（外围四角够不到）
        ///   半径 1100 → 覆盖 36/36（全部）
        ///   半径  900 → 覆盖 32/36（内圈够不到外沿）
        ///   半径  700 → 覆盖 24/36
        /// 要"绕整个地形看 chunk 动态变化"，就必须覆盖全部，故选 1100。
        /// </summary>
        private const float RouteRadius = 1100f;

        /// <summary>POI 数量。一圈约 6.9km，16 个点间隔约 430m，样条足够平滑。</summary>
        private const int PoiCount = 16;

        /// <summary>
        /// 贴地漫游的离地高度区间（米）。
        ///
        /// 为什么是 3~8m：BRG 草的 LOD 阈值按屏幕像素定（100/25/8 px），
        /// 1080p + 60° FOV 换算：1m 高的草在 9.4m 内是 LOD0、117m 外直接剔除。
        ///   ？5m → 187px（LOD0 满细节，看得到叶片）
        ///    8m → 117px（LOD0 边界）
        ///   15m →  62px（LOD1）
        ///   30m →  31px（LOD2，只剩色块）
        /// 要"看到地表细节和草"，就必须全程待在 8m 以内，故取 3~8m。
        /// 高度在这个区间内缓慢起伏，避免完全平飞显得呆板。
        /// </summary>
        private const float AltLow = 3f;
        private const float AltHigh = 8f;

        /// <summary>
        /// 巡航速度（米/秒）。8m/s ≈ 快步走，能看清草从 LOD0→1→2 逐级切换。
        /// 一圈 6.9km 约 14 分钟——慢，但这是观察工具，看清细节优先于走完。
        /// 想快点可用滚轮调速度，或按 F 切手动飞行。
        /// </summary>
        private const float CruiseSpeed = 8f;

        /// <summary>视线前推距离（米）。贴地时 25m 已有明显俯角，能同时看到前方地表与脚下草。</summary>
        private const float LookAhead = 25f;

        [MenuItem("FishDemo/World/② 布置 Pegasus 环绕巡游", false, 21)]
        public static void Run()
        {
            var sb = new StringBuilder();
            void W(string s) { Debug.Log($"{Tag} {s}"); sb.AppendLine(s); }

            var scene = EditorSceneManager.OpenScene(WorldScenePath, OpenSceneMode.Single);

            var camera = Object.FindObjectsOfType<Camera>()
                .FirstOrDefault(c => c.CompareTag("MainCamera")) ?? Object.FindObjectOfType<Camera>();
            if (camera == null)
            {
                W("主场景里没有相机，先跑 WorldPipeline 生成世界");
                Flush(sb);
                return;
            }

            // ── 1. 采样地形高度（编辑期烘焙） ──
            var route = BuildRoute();
            W($"路线：半径 {RouteRadius}m，{PoiCount} 个 POI，周长约 {2f * Mathf.PI * RouteRadius / 1000f:F1}km");
            if (!BakeGroundHeights(scene, route, W))
            {
                W("地形高度采样失败");
                Flush(sb);
                return;
            }

            // ── 2. 清掉旧的 Pegasus 节点，保证幂等 ──
            RemoveExistingManagers();

            // ── 3. 建 Manager ──
            var root = new GameObject(ManagerRootName);
            var manager = root.AddComponent<PegasusManager>();

            manager.m_target = camera.gameObject;
            // Looped：无限循环，观察场景时不用手动重启。
            manager.m_flythroughType = PegasusConstants.FlythroughType.Looped;
            // LeaveAlone：不要让 Pegasus 改 Application.targetFrameRate，
            // 否则观测到的帧率是被人为钉死的，失去性能参考意义。
            manager.m_targetFramerateType = PegasusConstants.TargetFrameRate.LeaveAlone;
            // None：高度已烘焙进 POI，运行期再采样会因 chunk 异步加载而漂移。
            manager.m_heightCheckType = PegasusConstants.HeightCheckType.None;
            manager.m_autoStartAtRuntime = true;
            manager.m_displayDebug = false;
            // 阻尼调小，相机跟得更干脆，便于观察近景细节。
            manager.m_positionDamping = 0.1f;
            manager.m_rotationDamping = 0.1f;

            // ── 4. 建 POI ──
            // 每个 POI 必须是 Manager 的子物体：InitialiseFlythrough 按
            // transform 子物体顺序重建 m_poiList（PegasusManager.cs:194-197），
            // 顺序即巡游顺序。
            for (int i = 0; i < PoiCount; i++)
            {
                var p = route[i];
                var poiGo = new GameObject($"POI {i:00}");
                poiGo.transform.SetParent(root.transform, false);
                poiGo.transform.position = p.Position;

                var poi = poiGo.AddComponent<PegasusPoi>();
                poi.m_manager = manager;

                // 看向前下方：既能看到迎面加载的 chunk，也能看到脚下/近处的植被 LOD。
                poi.m_lookatType = PegasusConstants.LookatType.Target;
                poi.m_lookatLocation = p.LookAt;

                // 速度用自定义值：标准档位（8 / 25 / 70 m/s）都不适合这个尺度。
                // 注意 PegasusPoi.Initialise 的 switch 没有 Custom 分支，
                // m_startSpeed 会保留我们写入的值。
                poi.m_startSpeedType = PegasusConstants.SpeedType.Custom;
                poi.m_startSpeed = CruiseSpeed;

                poi.m_velocityEasingType = PegasusConstants.EasingType.Linear;
                poi.m_positionEasingType = PegasusConstants.EasingType.Linear;
                poi.m_rotationEasingType = PegasusConstants.EasingType.Linear;
                poi.m_autoRollOn = false;
                // 已烘焙绝对高度，POI 自身不要再做高度校正。
                poi.m_heightCheckType = PegasusConstants.PoiHeightCheckType.None;
            }

            // 让 Manager 重新收集子物体并计算段长 / 时长 / 旋转。
            manager.InitialiseFlythrough();

            // ── 5. 运行时模式切换（F 键：自动巡游 ↔ 手动飞行） ──
            EnsureTourController(camera.gameObject);

            W($"已生成 {PoiCount} 个 POI，总里程 {manager.m_totalDistance / 1000f:F2}km，" +
              $"预计 {manager.m_totalDuration.TotalMinutes:F1} 分钟/圈");
            W($"高度范围（离地）{AltLow}m ~ {AltHigh}m；地面 Y 区间 " +
              $"[{route.Min(r => r.GroundY):F1}, {route.Max(r => r.GroundY):F1}]");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            W("已保存 World.unity");
            Flush(sb);
        }

        /// <summary>路线上一个采样点的几何数据。</summary>
        private struct RoutePoint
        {
            public Vector3 Position;
            public Vector3 LookAt;
            public float GroundY;
        }

        /// <summary>
        /// 生成环形路线。高度用 2 个周期的余弦：θ=0 处最低，θ=π/2 处最高，
        /// 一圈得到 2 段低空 + 2 段高空，两种观察需求都能覆盖。
        ///
        /// 返回的 Position.y 暂存的是「离地高度」（米），不是世界 Y。
        /// BakeGroundHeights 会把地面高度采出来再换算成绝对世界 Y。
        /// 这样分两步是为了让几何布置与地形采样解耦，采样失败时路线几何仍然成立。
        /// </summary>
        private static List<RoutePoint> BuildRoute()
        {
            var list = new List<RoutePoint>(PoiCount);
            for (int i = 0; i < PoiCount; i++)
            {
                float theta = 2f * Mathf.PI * i / PoiCount;
                float cos = Mathf.Cos(theta);
                float sin = Mathf.Sin(theta);

                // 逆时针：切线方向是半径方向逆时针旋转 90°。
                var tangent = new Vector3(-sin, 0f, cos);

                // 0..1 的周期量：θ=0 时为 0（贴地），θ=π/2 时为 1（略高）。
                float t = 0.5f - 0.5f * Mathf.Cos(2f * theta);
                float altitudeAboveGround = Mathf.Lerp(AltLow, AltHigh, t);

                var basePos = new Vector3(cos * RouteRadius, altitudeAboveGround, sin * RouteRadius);

                list.Add(new RoutePoint
                {
                    Position = basePos,
                    // 视线沿切线前推并下压。
                    //
                    // 俯角不能按离地高度算（现在只有 3~8m，那样几乎平视，看不到脚下的草）。
                    // 改成固定下压量：前推 25m 时压 9m → 约 20° 俯角。
                    // 这个角度能同时覆盖：
                    //   - 画面下缘：脚下 3~8m 的草（LOD0，满细节）
                    //   - 画面中部：15~40m 的草（LOD1→2，能看切换）
                    //   - 画面上缘：远处地表起伏与植被轮廓
                    LookAt = new Vector3(basePos.x, basePos.y, basePos.z)
                             + tangent * LookAhead
                             - Vector3.up * 9f,
                    GroundY = float.NaN,
                });
            }
            return list;
        }

        /// <summary>
        /// 逐 chunk 打开（一次只开一个，避免 36 块 TerrainData + GTS mesh 同时进内存），
        /// 用 Terrain.SampleHeight 取真实地表高度，再把离地高度换算成世界 Y。
        /// 返回 false 表示存在完全没采到高度的点。
        /// </summary>
        private static bool BakeGroundHeights(Scene scene, List<RoutePoint> route, System.Action<string> w)
        {
            var chunks = Object.FindObjectsOfType<SECTR_Chunk>().OrderBy(c => c.ScenePath).ToList();
            if (chunks.Count == 0)
            {
                w("主场景里没有 SECTR_Chunk");
                return false;
            }

            var heights = new float[route.Count];
            for (int i = 0; i < heights.Length; i++)
                heights[i] = float.NaN;

            int opened = 0;
            foreach (var c in chunks)
            {
                if (string.IsNullOrEmpty(c.NodeName) || !File.Exists(c.NodeName))
                    continue;

                var chunkScene = EditorSceneManager.OpenScene(c.NodeName, OpenSceneMode.Additive);
                opened++;

                // 只取本 chunk 场景里的 Terrain，避免把已打开场景的也算进来。
                var terrains = Object.FindObjectsOfType<Terrain>()
                    .Where(t => t.gameObject.scene == chunkScene)
                    .ToArray();

                for (int i = 0; i < route.Count; i++)
                {
                    if (!float.IsNaN(heights[i]))
                        continue;

                    var rp = route[i];
                    foreach (var t in terrains)
                    {
                        if (t.terrainData == null)
                            continue;

                        // 先做 XZ 命中判断：一个 chunk 有 4 个 subTile，路线点只属于其中一个。
                        Vector3 origin = t.transform.position;
                        Vector3 size = t.terrainData.size;
                        if (rp.Position.x < origin.x || rp.Position.x > origin.x + size.x)
                            continue;
                        if (rp.Position.z < origin.z || rp.Position.z > origin.z + size.z)
                            continue;

                        // Terrain.SampleHeight 接收世界 XZ，但返回的是**相对该地形本地原点**
                        // 的高度，必须加 transform.position.y 才是世界 Y。
                        // 依据：本工程既有写法 GrassSceneSetup.cs:135、WorldProbe.cs:94 同样处理。
                        heights[i] = t.SampleHeight(new Vector3(rp.Position.x, 0f, rp.Position.z))
                                     + origin.y;
                        break;
                    }
                }

                EditorSceneManager.CloseScene(chunkScene, true);
            }

            int missing = 0;
            for (int i = 0; i < route.Count; i++)
            {
                if (float.IsNaN(heights[i]))
                {
                    missing++;
                    w($"POI {i} 未落在任何地形上（x={route[i].Position.x:F0}, z={route[i].Position.z:F0}）");
                    continue;
                }

                var rp = route[i];
                rp.GroundY = heights[i];

                // 平移而不是重算：BuildRoute 已把视线定为「比相机低 9m」，
                // 这里只需把整条视线跟着相机一起抬到真实地面高度上，
                // 保持那个相对关系不变（否则会重复下压，俯角翻倍）。
                float dy = heights[i];
                rp.Position.y += dy;
                rp.LookAt.y += dy;
                route[i] = rp;
            }

            w($"扫描 {opened} 个 chunk 场景采高度，未命中 {missing} 个点");
            return missing == 0;
        }

        /// <summary>删掉旧的 Pegasus 管理器（及其 POI 子物体），保证重复执行不叠加。</summary>
        private static void RemoveExistingManagers()
        {
            var existing = Object.FindObjectsOfType<PegasusManager>();
            foreach (var m in existing)
            {
                Object.DestroyImmediate(m.gameObject);
            }
            if (existing.Length > 0)
            {
                Debug.Log($"{Tag} 已移除 {existing.Length} 个旧 Pegasus 节点");
            }
        }

        /// <summary>确保相机上挂着模式切换脚本（幂等）。</summary>
        private static void EnsureTourController(GameObject cameraGo)
        {
            var existing = cameraGo.GetComponent<FishDemo.Gameplay.PegasusTourController>();
            if (existing == null)
            {
                cameraGo.AddComponent<FishDemo.Gameplay.PegasusTourController>();
            }
        }

        private static void Flush(StringBuilder sb)
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/pegasus-tour-setup.txt", sb.ToString());
        }
    }
}
