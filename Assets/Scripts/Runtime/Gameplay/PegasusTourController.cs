using UnityEngine;
using Pegasus;

namespace FishDemo.Gameplay
{
    /// <summary>
    /// 演示用：在「Pegasus 自动巡游」与「WASD 手动飞行」之间按 F 切换。
    ///
    /// 为什么这个脚本没有 asmdef（编进 Assembly-CSharp）：
    /// Pegasus 自身没有 asmdef，类型编在默认程序集 Assembly-CSharp 里。
    /// 有 asmdef 的程序集（如 FishDemo.Rendering）无法引用默认程序集，
    /// 依赖方向是反的。所以凡是要直接用 Pegasus 类型的运行时脚本，
    /// 只能放在没有 asmdef 的目录里。同 [Assets/GTSRuntime] 的处理方式。
    ///
    /// 为什么手动模式每次重建 PegasusFlyingCamera：
    /// 它把 yaw/pitch 缓存在私有字段里，且只在 Awake 时从 transform.rotation 取一次。
    /// 若复用同一个实例，第二次进入手动模式时会沿用上一次（飞行结束后已被 Pegasus
    /// 改过的）相机朝向记录，一按右键视角就会突跳。每次重新 AddComponent 让 Awake
    /// 用当前朝向重新初始化，代价是极少量分配——这是调试/观察工具，正确性优先。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PegasusTourController : MonoBehaviour
    {
        [Tooltip("Pegasus 管理器。为空时在场景里自动查找。")]
        [SerializeField] private PegasusManager _manager;

        [Tooltip("切换键。默认 F。")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F;

        [Tooltip("手动模式下的基础移动速度（米/秒）。滚轮可实时调整。")]
        [SerializeField] private float _manualMoveSpeed = 120f;

        /// <summary>当前是否处于手动飞行模式。</summary>
        public bool IsManual => _manual != null;

        /// <summary>手动模式下的飞行控制组件；非手动模式为 null。</summary>
        private PegasusFlyingCamera _manual;

        private void Awake()
        {
            if (_manager == null)
            {
                _manager = FindObjectOfType<PegasusManager>();
            }
        }

        private void Update()
        {
            // 用 Pegasus 自带的输入兼容层：工程当前是旧 Input Manager，
            // 但该层同时兼容新 Input System，将来切过去不用改这里。
            if (!PegasusInputCompat.GetKeyDown(_toggleKey))
            {
                return;
            }

            if (_manual != null)
            {
                EnterAutoMode();
            }
            else
            {
                EnterManualMode();
            }
        }

        /// <summary>
        /// 进入手动飞行。暂停 Pegasus 后它的 LateUpdate 会直接返回，不再写相机，
        /// 主控权完全交给我们，不存在两者互抢 transform 的问题。
        /// </summary>
        private void EnterManualMode()
        {
            if (_manager != null)
            {
                _manager.PauseFlythrough();
            }

            _manual = gameObject.AddComponent<PegasusFlyingCamera>();
            _manual.m_moveSpeed = _manualMoveSpeed;
            // 允许 WASD 直接移动，不必先按住右键；视角仍用右键拖动。
            _manual.m_requireRmbToMove = false;
            _manual.m_maxMoveSpeed = 1000f;

            Debug.Log("[PegasusTour] 已切换到手动飞行（WASD 移动 / 右键转视角 / 滚轮调速度）");
        }

        /// <summary>
        /// 回到自动巡游。不是从暂停点硬飞回去，而是先找路径上离相机最近的点，
        /// 再把巡游进度定位到那里，最后恢复播放。
        ///
        /// 理由：手动飞行可能离原暂停点几百米，若直接 Resume，Pegasus 会用阻尼把
        /// 相机一路拖回去，看到的是「倒退飞行」而不是「继续巡游」，也会把沿途
        /// chunk 反复加载一遍，干扰观察。
        /// </summary>
        private void EnterAutoMode()
        {
            if (_manual != null)
            {
                Destroy(_manual);
                _manual = null;
            }

            if (_manager != null)
            {
                SnapToNearestPathPoint();
                // Pause 过才允许 Resume（ResumeFlythrough 内部会校验状态）。
                _manager.ResumeFlythrough();
            }

            Debug.Log("[PegasusTour] 已切回自动巡游（沿路径从最近点继续）");
        }

        /// <summary>
        /// 在整条路径上采样，把巡游进度定位到离当前相机位置最近的点。
        /// 采样 256 份：一圈约 7.5km，平均每 30m 一个候选点，足够接近，
        /// 重新定位的偏差在肉眼阈值以下，也不必做解析求解。
        /// </summary>
        private void SnapToNearestPathPoint()
        {
            float total = _manager.m_totalDistance;
            if (total <= 0f)
            {
                return;
            }

            const int Samples = 256;
            float bestDist = 0f;
            float bestSqr = float.MaxValue;
            Vector3 cameraPos = transform.position;

            for (int i = 0; i < Samples; i++)
            {
                float d = total * i / Samples;
                _manager.CalculateTargetAtDistance(d, out Vector3 pos, out _, out _);
                float sqr = (pos - cameraPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestDist = d;
                }
            }

            // 把进度写回管理器后 MoveTargetTo 会按百分比重新求位置。
            // 它同时会把相机 transform 立刻摆到路径点上（之后由阻尼平滑）。
            _manager.m_totalDistanceTravelled = bestDist;
            _manager.MoveTargetTo(bestDist / total);
        }
    }
}
