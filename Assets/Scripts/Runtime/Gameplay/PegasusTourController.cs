using UnityEngine;
using Pegasus;

namespace FishDemo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class PegasusTourController : MonoBehaviour
    {
        [Tooltip("Pegasus 管理器。为空时在场景里自动查找。")]
        [SerializeField] private PegasusManager _manager;

        [Tooltip("切换键。默认 F。")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F;

        [Tooltip("手动模式下的基础移动速度（米/秒）。滚轮可实时调整。")]
        [SerializeField] private float _manualMoveSpeed = 120f;

        public bool IsManual => _manual != null;

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

        private void EnterManualMode()
        {
            if (_manager != null)
            {
                _manager.PauseFlythrough();
            }

            _manual = gameObject.AddComponent<PegasusFlyingCamera>();
            _manual.m_moveSpeed = _manualMoveSpeed;
            _manual.m_requireRmbToMove = false;
            _manual.m_maxMoveSpeed = 1000f;

            Debug.Log("[PegasusTour] 已切换到手动飞行（WASD 移动 / 右键转视角 / 滚轮调速度）");
        }

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
                _manager.ResumeFlythrough();
            }

            Debug.Log("[PegasusTour] 已切回自动巡游（沿路径从最近点继续）");
        }

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

            _manager.m_totalDistanceTravelled = bestDist;
            _manager.MoveTargetTo(bestDist / total);
        }
    }
}
