using UnityEngine;

namespace Pegasus
{
    /// <summary>
    /// Simple free-fly camera controller for capture work.
    /// - Hold RMB to look around
    /// - WASD to move, QE up/down
    /// - Shift to go faster, Ctrl to go slower
    /// - Mouse wheel to adjust base speed
    ///
    /// Supports both legacy input and the new Input System via PegasusInputCompat.
    /// </summary>
    [DisallowMultipleComponent]
    public class PegasusFlyingCamera : MonoBehaviour
    {
        [Header("Movement")]
        public float m_moveSpeed = 30f;
        public float m_shiftMultiplier = 4f;
        public float m_ctrlMultiplier = 0.25f;
        public float m_scrollSpeedStep = 1.5f;
        public float m_minMoveSpeed = 0.25f;
        public float m_maxMoveSpeed = 200f;

        [Header("Look")]
        public int m_lookMouseButton = 1; // RMB
        public float m_lookSensitivity = 1.5f;
        public bool m_invertY = false;
        public float m_pitchMin = -89f;
        public float m_pitchMax = 89f;

        [Header("Options")]
        public bool m_requireRmbToMove = true;
        public bool m_lockCursorWhileLooking = true;

        private float m_yaw;
        private float m_pitch;
        private bool m_wasCursorLocked;

        private void Awake()
        {
            var euler = transform.rotation.eulerAngles;
            m_yaw = euler.y;
            m_pitch = NormalizePitch(euler.x);
        }

        private void OnDisable()
        {
            if (m_wasCursorLocked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                m_wasCursorLocked = false;
            }
        }

        private void Update()
        {
            // Speed adjust (mouse wheel)
            var scrollY = PegasusInputCompat.GetMouseScrollY();
            if (Mathf.Abs(scrollY) > 0.0001f)
            {
                // Legacy returns small values, new input system can be larger; normalize a bit.
                var normalized = Mathf.Clamp(scrollY, -120f, 120f) / 120f;
                m_moveSpeed = Mathf.Clamp(m_moveSpeed + (normalized * m_scrollSpeedStep), m_minMoveSpeed, m_maxMoveSpeed);
            }

            bool looking = PegasusInputCompat.GetMouseButton(m_lookMouseButton);
            UpdateCursorLock(looking);

            if (looking)
            {
                var delta = PegasusInputCompat.GetMouseDelta();

                // Legacy delta is already relatively small; new input system delta is pixels.
                // Scale pixels down a bit to feel similar.
                if (delta.sqrMagnitude > 0f)
                {
                    delta *= 0.1f;
                }

                float invert = m_invertY ? 1f : -1f;
                m_yaw += delta.x * m_lookSensitivity;
                m_pitch += delta.y * m_lookSensitivity * invert;
                m_pitch = Mathf.Clamp(m_pitch, m_pitchMin, m_pitchMax);

                transform.rotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
            }

            if (m_requireRmbToMove && !looking)
            {
                return;
            }

            Vector3 move = Vector3.zero;
            if (PegasusInputCompat.GetKey(KeyCode.W)) move += Vector3.forward;
            if (PegasusInputCompat.GetKey(KeyCode.S)) move += Vector3.back;
            if (PegasusInputCompat.GetKey(KeyCode.A)) move += Vector3.left;
            if (PegasusInputCompat.GetKey(KeyCode.D)) move += Vector3.right;
            if (PegasusInputCompat.GetKey(KeyCode.E)) move += Vector3.up;
            if (PegasusInputCompat.GetKey(KeyCode.Q)) move += Vector3.down;

            if (move.sqrMagnitude < 0.0001f)
            {
                return;
            }

            float speed = m_moveSpeed;
            if (PegasusInputCompat.GetKey(KeyCode.LeftShift) || PegasusInputCompat.GetKey(KeyCode.RightShift))
            {
                speed *= Mathf.Max(0.01f, m_shiftMultiplier);
            }
            if (PegasusInputCompat.GetKey(KeyCode.LeftControl) || PegasusInputCompat.GetKey(KeyCode.RightControl))
            {
                speed *= Mathf.Max(0.01f, m_ctrlMultiplier);
            }

            // Move relative to current orientation.
            Vector3 worldMove =
                (transform.right * move.x) +
                (transform.up * move.y) +
                (transform.forward * move.z);

            transform.position += worldMove.normalized * (speed * Time.unscaledDeltaTime);
        }

        private void UpdateCursorLock(bool looking)
        {
            if (!m_lockCursorWhileLooking)
            {
                return;
            }

            if (looking && !m_wasCursorLocked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                m_wasCursorLocked = true;
            }
            else if (!looking && m_wasCursorLocked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                m_wasCursorLocked = false;
            }
        }

        private static float NormalizePitch(float pitch)
        {
            // Convert Unity's 0..360 pitch to -180..180 so clamps work intuitively.
            if (pitch > 180f) pitch -= 360f;
            return pitch;
        }
    }
}

