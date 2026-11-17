using System;
using UnityEngine;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// 环湖轨道相机：绕中心湖做匀速圆周运动，朝向湖心并缓慢俯仰。
    ///
    /// ── 为什么不用 Cinemachine（虚拟相机）──
    /// 1. 工程没有装 Cinemachine（`Packages/manifest.json` 里没有 `com.unity.cinemachine`），
    ///    为一个固定圆周运动引入整套依赖不划算。
    /// 2. 真正要处理的三件事它都帮不上：
    ///    - Crest 的 Viewer（`WaterRenderer.Viewer` 必须是当前渲染相机，用于水下/反射；
    ///      LOD 中心已固定为 `Lake Viewpoint`，不随相机漂移）；
    ///    - SECTR chunk 加载（`SECTR_RegionLoader` 按**自身 transform** 判定范围，
    ///      `SECTR_RegionLoader.cs:83-84`，所以轨道相机也得有自己的 loader）；
    ///    - 相机启用/禁用切换。
    /// 3. 轨道运动本身就是 20 行三角函数，参数化反而更清楚。
    ///
    /// ── 为什么半径比 `LakeCarve.WaterHalfExtent`（280m）略大 ──
    /// LOD 中心固定在湖心，水面数据始终覆盖整个湖盆；半径 300m 时相机在岸外 20m，
    /// 俯角 12° 能把 380m 直径的湖完整收进 60° FOV。
    ///
    /// ── 高度为什么不能是固定值（2026-11-17 修正）──
    /// 旧实现用「水位 + 26m」= 290m 的固定高度。但 R=300m 一圈的真实地形高度是
    /// **269m ~ 323.5m**（`Logs/Lake/height-grid.csv` 实测）：北偏西 270°~315° 一带地形到
    /// 312~320m，相机固定 290m 会**埋进地里**，出现穿模/看不见湖。
    /// 现在改为**跟随地形**：每帧取相机 XZ 处的地表高度，抬高 `_groundClearance`，
    /// 并与「水位 + `_minHeightAboveWater`」取较大值（保证低洼处不贴水面）。
    /// 高度做指数平滑，避免按 32m 网格采样的地形高度让镜头一顿一顿。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LakeOrbitCamera : MonoBehaviour
    {
        [Tooltip("轨道半径（米）。应大于湖盆半径 190m，才能在岸外看全湖面。")]
        [SerializeField] private float _radius = 300f;

        [Tooltip("离地高度（米）。相机高度 = max(地表高度 + 该值, 水位 + 最低水上高度)。")]
        [SerializeField] private float _groundClearance = 18f;

        [Tooltip("相对水面的最低高度（米）。防止相机在低洼处贴到水面。")]
        [SerializeField] private float _minHeightAboveWater = 26f;

        [Tooltip("高度平滑系数（1/秒）。越大跟随地形越快，越小越平稳。")]
        [SerializeField] private float _heightSmoothing = 2.5f;

        [Tooltip("绕一圈的秒数。120s ≈ 3.9m/s 的线速度，慢到能看清水面而不晕。")]
        [SerializeField] private float _secondsPerRevolution = 120f;

        [Tooltip("起始角度（度）。0 = 湖心正南方（-Z 方向）。")]
        [SerializeField] private float _startAngle = 0f;

        [Tooltip("俯仰角（度）。正数表示低头看湖。")]
        [SerializeField] private float _pitch = 12f;

        [Tooltip("俯仰角的缓慢摆动幅度（度）。0 = 固定俯仰。")]
        [SerializeField] private float _pitchSway = 3f;

        [Tooltip("俯仰摆动的周期（秒）。")]
        [SerializeField] private float _pitchSwayPeriod = 24f;

        /// <summary>湖心 XZ 与水位。与 LakeCarve 的常量保持一致（Editor 程序集不可引用）。</summary>
        [SerializeField] private Vector3 _lakeCenter = new Vector3(192f, 264f, 96f);

        /// <summary>累计角度（弧度）。不用 Time.time，避免切换相机回来时相位跳变。</summary>
        private float _angle;

        /// <summary>累计时间（秒），用于俯仰摆动。与 <see cref="_angle"/> 同步推进。</summary>
        private float _elapsed;

        /// <summary>平滑后的相机高度。第一帧直接取目标值，不做平滑，避免从 0 升上来。</summary>
        private float _smoothedY = float.NaN;

        /// <summary>
        /// 时间源。默认走 `Time.deltaTime`；自动化验收会替换成固定步长，
        /// 因为 batchmode `-nographics` 下 `Time.deltaTime` 不推进（实测 120 帧只走 0.6°）。
        /// </summary>
        private Func<float> _deltaTimeProvider = () => Time.deltaTime;

        /// <summary>外层 rig 用它决定何时重建相机；切换状态由 <see cref="LakeCameraRig"/> 负责。</summary>
        public Vector3 LakeCenter => _lakeCenter;

        /// <summary>
        /// 是否暂停轨道驱动。
        ///
        /// 为什么要这个开关：按 F 进入 WASD 自由飞行后，`PegasusFlyingCamera` 会每帧写
        /// transform，而本脚本在 `LateUpdate` 里也每帧写 transform —— 后者更晚执行，
        /// 会把飞行位移直接覆盖掉（表现为"按 F 没反应，镜头还在自己转"）。
        /// 自由飞行期间必须置 true，让本脚本彻底让出 transform 控制权。
        /// </summary>
        public bool Paused { get; set; }

        /// <summary>
        /// 替换时间源，仅供自动化验收使用（正式逻辑永远用 `Time.deltaTime`）。
        /// 传 null 恢复默认。
        /// </summary>
        public void OverrideDeltaTime(Func<float> provider)
        {
            _deltaTimeProvider = provider ?? (() => Time.deltaTime);
        }

        /// <summary>把角度重置到起始位置。切换回本相机时调用，保证每次进入从同一机位开始。</summary>
        public void ResetPose()
        {
            _angle = _startAngle * Mathf.Deg2Rad;
            _elapsed = 0f;
            // 重置高度平滑：切回轨道时直接落到目标高度，而不是从上一次的位置慢慢飘过去。
            _smoothedY = float.NaN;
            ApplyPose(_elapsed);
        }

        /// <summary>
        /// 按**当前**相机位置反解轨道角度，再续接驱动。
        ///
        /// 用于「自由飞行结束、交还轨道控制」：直接 `ResetPose` 会瞬移回起始角度，
        /// 观感是镜头突然一跳。这里保持 XZ 方位角连续，只把半径/高度拉回轨道值，
        /// 于是画面平滑地"落回"圆周上。
        /// </summary>
        public void ResumeFromCurrentPosition()
        {
            var p = transform.position;
            float dx = p.x - _lakeCenter.x;
            float dz = p.z - _lakeCenter.z;
            // 用 atan2(sin, cos) 与 ApplyPose 的 (sin, cos) 排布对应，保证角度语义一致。
            _angle = Mathf.Atan2(dx, dz);
            // 俯仰摆动相位无法从位置反解，直接归零让摆动从现在开始，避免跳变。
            _elapsed = 0f;
            ApplyPose(_elapsed);
        }

        private void OnEnable()
        {
            // 重新启用时不要沿用禁用期间的静默时间，从当前位置平滑继续即可。
            // 自由飞行结束后由 rig 显式清 Paused 并调用 ResetPose（见 LakeCameraRig）。
            ResetPose();
        }

        private void LateUpdate()
        {
            if (Paused) return;

            float dt = _deltaTimeProvider();
            _elapsed += dt;

            if (_secondsPerRevolution > 0.01f)
            {
                _angle += dt * Mathf.PI * 2f / _secondsPerRevolution;
            }
            else
            {
                _angle += dt;
            }

            ApplyPose(_elapsed);
        }

        private void ApplyPose(float t)
        {
            float cos = Mathf.Cos(_angle);
            float sin = Mathf.Sin(_angle);

            float x = _lakeCenter.x + _radius * sin;
            float z = _lakeCenter.z + _radius * cos;

            float y = ResolveHeight(x, z);

            var pos = new Vector3(x, y, z);
            transform.position = pos;

            // 俯仰随时间缓慢摆动，避免全程固定角度显得呆板。
            float pitch = _pitch;
            if (_pitchSway > 0.001f && _pitchSwayPeriod > 0.01f)
            {
                pitch += _pitchSway * Mathf.Sin(t * Mathf.PI * 2f / _pitchSwayPeriod);
            }

            // 朝向湖心：先算水平朝向，再压低 pitch。
            var toCenter = new Vector3(_lakeCenter.x - pos.x, 0f, _lakeCenter.z - pos.z);
            var look = Quaternion.LookRotation(toCenter.normalized, Vector3.up)
                       * Quaternion.Euler(pitch, 0f, 0f);
            transform.rotation = look;
        }

        /// <summary>
        /// 取该 XZ 处的相机目标高度：地表高度 + 离地间隙，且不低于水位 + 最低水上高度。
        ///
        /// ── 为什么用 Terrain.SampleHeight ──
        /// 地形虽然已被 GTS 转成 mesh 渲染，但 `Terrain` 组件与 `TerrainData` 仍然保留
        /// （见 [world_implementation] §4.1），`SampleHeight` 给的是权威高度，
        /// 与 `LakeCarve` / `PegasusTourSetup` 用的是同一套换算
        /// （`terrain.SampleHeight(worldXZ) + terrain.transform.position.y`）。
        /// 不用 Physics.Raycast：水面/草/树都有碰撞体，会打到非地形物体上。
        ///
        /// ── 为什么要平滑 ──
        /// chunk 场景是流式加载的，刚起步时目标点可能还没有 Terrain；
        /// 且高度场本身在草坡上起伏。指数平滑让高度缓变，避免镜头踏步。
        /// </summary>
        private float ResolveHeight(float x, float z)
        {
            float waterFloor = _lakeCenter.y + _minHeightAboveWater;
            float target = waterFloor;

            float ground = SampleGroundHeight(x, z);
            if (!float.IsNaN(ground))
            {
                target = Mathf.Max(waterFloor, ground + _groundClearance);
            }
            else if (!float.IsNaN(_smoothedY))
            {
                // 目标点的 chunk 还没被 SECTR 加载（流式延迟）。
                // 这时不要回落到水位高度：地形可能比水位高得多，回落会瞬间穿模。
                // 保持当前高度，等 Terrain 到位再按真实地表调整。
                target = Mathf.Max(waterFloor, _smoothedY);
            }

            if (float.IsNaN(_smoothedY))
            {
                _smoothedY = target;   // 首次直接到位
            }
            else
            {
                // 用真实帧时间做平滑：固定步长注入（验收）时也保持一致手感。
                float dt = _deltaTimeProvider();
                float k = _heightSmoothing > 0.01f
                    ? 1f - Mathf.Exp(-_heightSmoothing * dt)
                    : 1f;
                _smoothedY = Mathf.Lerp(_smoothedY, target, k);
            }

            return _smoothedY;
        }

        /// <summary>
        /// 世界 XZ 处的地表高度。找不到覆盖该点的 Terrain 时返回 NaN（调用方保留原高度）。
        ///
        /// 注意 `Terrain.SampleHeight` 的语义：它接收**世界 XZ**，但返回的是
        /// **相对该地形 transform.position.y 的高度**，所以必须加上 `position.y`。
        /// 这是本工程多处踩过的点（`PegasusTourSetup.cs:288` 有相同注释）。
        /// </summary>
        private static float SampleGroundHeight(float x, float z)
        {
            var terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                var t = terrains[i];
                if (t == null || t.terrainData == null) continue;

                var p = t.transform.position;
                var s = t.terrainData.size;
                if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;

                return p.y + t.SampleHeight(new Vector3(x, 0f, z));
            }
            return float.NaN;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑期摆到当前角度的位姿，方便在 Scene 视图里核对轨道是否合理。
        /// 只在组件被选中（或 Inspector 改参数）时执行，不影响运行时。
        /// </summary>
        private void OnValidate()
        {
            if (!Application.isPlaying)
            {
                _angle = _startAngle * Mathf.Deg2Rad;
                ApplyPose(0f);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            const int Segments = 96;
            var prev = Vector3.zero;
            for (int i = 0; i <= Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                float x = _lakeCenter.x + _radius * Mathf.Sin(a);
                float z = _lakeCenter.z + _radius * Mathf.Cos(a);
                // 画真实高度（含地形跟随），而不是一条固定高度的圆——
                // 固定高度的圆正是旧版穿模的原因，画出来会误导。
                var p = new Vector3(x, ResolveHeightForGizmo(x, z), z);
                if (i > 0) Gizmos.DrawLine(prev, p);
                prev = p;
            }

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 1f);
            Gizmos.DrawLine(transform.position, _lakeCenter);
        }

        /// <summary>Gizmo 用：不写 _smoothedY，避免在 Scene 视图里污染运行期状态。</summary>
        private float ResolveHeightForGizmo(float x, float z)
        {
            float target = _lakeCenter.y + _minHeightAboveWater;
            float ground = SampleGroundHeight(x, z);
            if (!float.IsNaN(ground))
                target = Mathf.Max(target, ground + _groundClearance);
            return target;
        }
#endif
    }
}
