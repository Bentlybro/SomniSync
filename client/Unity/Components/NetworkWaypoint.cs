using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Deterministic waypoint pathing for NPCs — no network broadcasts needed.
    ///
    /// Every client computes the same position from the shared <see cref="NetworkManager.ServerTime"/>
    /// clock, the waypoint list, and the speed. NPCs walk patrols without
    /// consuming any bandwidth at all; for state changes (death, attack,
    /// aggro switch) use NetworkEvents and NetworkVariables instead.
    ///
    /// Why this is better than networked positions for NPCs:
    ///   - Zero bandwidth regardless of NPC count
    ///   - No desync caused by dropped position packets
    ///   - No interpolation jitter
    ///   - Scales to hundreds of NPCs per room
    ///
    /// Attach to any GameObject. No NetworkObject required (position isn't synced).
    ///
    /// Usage:
    ///   1. Add child empty GameObjects for each waypoint and assign to the list
    ///      (or leave empty and use transform.position as a stationary point).
    ///   2. Pick a loop mode (PingPong, Loop, Once).
    ///   3. Set speed. Done.
    ///
    /// For combat NPCs that chase players, pair this with a script that sets
    /// <see cref="overridePath"/> to a runtime-chosen target.
    /// </summary>
    public class NetworkWaypoint : MonoBehaviour
    {
        public enum LoopMode { Loop, PingPong, Once }

        [Header("Path")]
        [Tooltip("Waypoints in order. If empty, the starting transform position is used.")]
        public Transform[] waypoints;
        public LoopMode loopMode = LoopMode.Loop;

        [Header("Motion")]
        [Tooltip("Movement speed in meters per second")]
        public float speed = 2f;
        [Tooltip("Seconds to pause at each waypoint (0 = no pause)")]
        public float pauseAtEach = 0f;
        [Tooltip("Rotate to face the direction of motion")]
        public bool faceDirection = true;
        [Tooltip("Smoothing when turning to face the next waypoint (deg/s, 0 = instant)")]
        public float turnSpeed = 360f;

        [Header("Offset")]
        [Tooltip("Add this many seconds to the shared clock when computing this NPC's position. Useful for staggering a group of NPCs so they don't all move in sync.")]
        public float phaseOffset = 0f;

        private Vector3[] _points;
        private float[] _segmentTimes;   // cumulative travel time to the end of each segment
        private float _totalMotionTime;  // time to traverse the whole path once (excluding pauses)
        private float _totalCycleTime;   // full cycle including pauses and (for PingPong) the return trip
        private bool _valid;
        private Vector3 _startPos;

        private void Awake()
        {
            _startPos = transform.position;
            BuildPath();
        }

        private void BuildPath()
        {
            if (waypoints == null || waypoints.Length == 0)
            {
                _valid = false;
                return;
            }

            _points = new Vector3[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
                _points[i] = waypoints[i] != null ? waypoints[i].position : _startPos;

            if (speed <= 0f) { _valid = false; return; }

            _segmentTimes = new float[_points.Length];
            float cumulative = 0f;
            for (int i = 0; i < _points.Length; i++)
            {
                int next = (i + 1) % _points.Length;
                // For Once/PingPong, no segment from last→first.
                if (loopMode != LoopMode.Loop && i == _points.Length - 1)
                {
                    _segmentTimes[i] = cumulative; // no further travel from last
                    break;
                }
                float segLen = Vector3.Distance(_points[i], _points[next]);
                cumulative += segLen / speed;
                _segmentTimes[i] = cumulative;
            }

            _totalMotionTime = cumulative;
            float pauses = pauseAtEach * _points.Length;
            _totalCycleTime = _totalMotionTime + pauses;

            if (loopMode == LoopMode.PingPong)
                _totalCycleTime *= 2f; // add the return leg

            _valid = _totalCycleTime > 0.0001f && _points.Length >= 1;
        }

        private void Update()
        {
            if (!_valid) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsNetworkReady) return;

            double t = NetworkManager.Instance.ServerTime + phaseOffset;
            Vector3 pos = SampleAt(t, out Vector3 forward);
            transform.position = pos;

            if (faceDirection && forward.sqrMagnitude > 0.0001f)
            {
                Quaternion target = Quaternion.LookRotation(forward);
                if (turnSpeed <= 0f)
                    transform.rotation = target;
                else
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
            }
        }

        /// <summary>
        /// Compute the deterministic position and forward direction for time t.
        /// </summary>
        private Vector3 SampleAt(double t, out Vector3 forward)
        {
            forward = Vector3.zero;
            if (_points.Length == 1)
                return _points[0];

            double cycle = t;
            if (_totalCycleTime > 0f)
                cycle = t - System.Math.Floor(t / _totalCycleTime) * _totalCycleTime;

            if (loopMode == LoopMode.Once && t >= _totalCycleTime)
            {
                int last = _points.Length - 1;
                return _points[last];
            }

            bool reversing = false;
            if (loopMode == LoopMode.PingPong)
            {
                double halfCycle = _totalCycleTime * 0.5;
                if (cycle > halfCycle)
                {
                    cycle -= halfCycle;
                    reversing = true;
                }
            }

            // March through segments (with optional pause at the end of each).
            float pauseDur = pauseAtEach;
            float cursor = 0f;

            int segCount = loopMode == LoopMode.Loop ? _points.Length : _points.Length - 1;
            for (int i = 0; i < segCount; i++)
            {
                int fromIdx = reversing ? (_points.Length - 1 - i) : i;
                int toIdx;
                if (reversing) toIdx = _points.Length - 2 - i;
                else           toIdx = (i + 1) % _points.Length;
                if (toIdx < 0) toIdx = 0;

                float segLen = Vector3.Distance(_points[fromIdx], _points[toIdx]);
                float segTime = segLen / speed;

                if (cycle < cursor + segTime)
                {
                    float segT = (float)(cycle - cursor) / segTime;
                    forward = (_points[toIdx] - _points[fromIdx]).normalized;
                    return Vector3.Lerp(_points[fromIdx], _points[toIdx], segT);
                }
                cursor += segTime;

                if (pauseDur > 0f)
                {
                    if (cycle < cursor + pauseDur)
                        return _points[toIdx];
                    cursor += pauseDur;
                }
            }

            // Safety fallback at end of cycle.
            return _points[_points.Length - 1];
        }

        private void OnDrawGizmosSelected()
        {
            if (waypoints == null) return;
            Gizmos.color = Color.cyan;
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] == null) continue;
                Gizmos.DrawSphere(waypoints[i].position, 0.15f);
                int next = (i + 1) % waypoints.Length;
                if (loopMode != LoopMode.Loop && i == waypoints.Length - 1) continue;
                if (waypoints[next] == null) continue;
                Gizmos.DrawLine(waypoints[i].position, waypoints[next].position);
            }
        }
    }
}
