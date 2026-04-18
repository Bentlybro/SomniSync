using System;
using UnityEngine;
using UnityEngine.Events;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Networked timer — authority-driven countdown synced to all clients.
    /// 
    /// The authority (master or claimed owner) runs the countdown and syncs
    /// the remaining time via NetworkVariables. Clients display the value
    /// and receive events (started, tick, finished).
    /// 
    /// Survives master transfers — the new master picks up from the synced value.
    /// 
    /// Usage:
    ///   timer.StartTimer(60f);     // 60 second countdown
    ///   timer.OnFinished.AddListener(() => OpenDoor());
    ///   timer.OnTick.AddListener((remaining) => UpdateUI(remaining));
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkTimer : MonoBehaviour
    {
        [Header("Timer Settings")]
        [Tooltip("Duration in seconds (can also be set via StartTimer())")]
        public float duration = 60f;

        [Tooltip("Loop the timer when it finishes")]
        public bool loop = false;

        [Tooltip("Auto-start when the scene loads (authority only)")]
        public bool autoStart = false;

        [Tooltip("How often to sync remaining time (Hz). Lower = less bandwidth.")]
        public float syncRate = 2f;

        [Header("Events")]
        public UnityEvent OnStarted;
        public UnityEvent OnFinished;
        public UnityEvent<float> OnTick; // remaining seconds

        private NetworkObject _netObj;
        private NetworkVariables _vars;

        private bool _running;
        private float _remaining;
        private float _syncTimer;
        private float _lastSyncedValue;

        private const string VAR_REMAINING = "_timer_remaining";
        private const string VAR_RUNNING = "_timer_running";
        private const string VAR_DURATION = "_timer_duration";

        protected virtual void Awake()
        {
            _netObj = GetComponent<NetworkObject>();
        }

        protected virtual void Start()
        {
            _vars = GetComponent<NetworkVariables>();
            if (_vars == null)
                _vars = gameObject.AddComponent<NetworkVariables>();

            _vars.DefineVariable(VAR_REMAINING, NetworkVariables.VarType.Float);
            _vars.DefineVariable(VAR_RUNNING, NetworkVariables.VarType.Bool);
            _vars.DefineVariable(VAR_DURATION, NetworkVariables.VarType.Float);
            _vars.OnValueChanged += OnVarChanged;

            if (autoStart && _netObj.HasAuthority())
            {
                StartTimer(duration);
            }
        }

        protected virtual void Update()
        {
            if (_netObj.HasAuthority())
                AuthorityUpdate();
            else
                SpectatorUpdate();
        }

        private void AuthorityUpdate()
        {
            if (!_running) return;

            _remaining -= Time.deltaTime;
            OnTick?.Invoke(_remaining);

            if (_remaining <= 0f)
            {
                _remaining = 0f;
                _running = false;

                _vars.SetFloat(VAR_REMAINING, 0f);
                _vars.SetBool(VAR_RUNNING, false);

                OnFinished?.Invoke();

                if (loop)
                    StartTimer(duration);

                return;
            }

            // Sync at configured rate
            _syncTimer += Time.deltaTime;
            if (_syncTimer >= 1f / syncRate)
            {
                _syncTimer = 0f;
                if (Mathf.Abs(_remaining - _lastSyncedValue) > 0.05f)
                {
                    _lastSyncedValue = _remaining;
                    _vars.SetFloat(VAR_REMAINING, _remaining);
                }
            }
        }

        private void SpectatorUpdate()
        {
            if (!_running) return;

            // Locally tick down between syncs for smooth display
            _remaining -= Time.deltaTime;
            if (_remaining < 0f) _remaining = 0f;
            OnTick?.Invoke(_remaining);
        }

        private void OnVarChanged(string name, NetworkVariables.VarType type)
        {
            if (_netObj.HasAuthority()) return;

            switch (name)
            {
                case VAR_REMAINING:
                    _remaining = _vars.GetFloat(VAR_REMAINING);
                    break;
                case VAR_RUNNING:
                    bool nowRunning = _vars.GetBool(VAR_RUNNING);
                    if (nowRunning && !_running)
                    {
                        _running = true;
                        OnStarted?.Invoke();
                    }
                    else if (!nowRunning && _running)
                    {
                        _running = false;
                        OnFinished?.Invoke();
                    }
                    break;
                case VAR_DURATION:
                    duration = _vars.GetFloat(VAR_DURATION);
                    break;
            }
        }

        // --- Public API ---

        /// <summary>Start a countdown. Authority only — syncs to all clients.</summary>
        public void StartTimer(float seconds)
        {
            if (!_netObj.HasAuthority())
            {
                Debug.LogWarning("[NetworkTimer] Only authority can start timer");
                return;
            }

            duration = seconds;
            _remaining = seconds;
            _running = true;
            _lastSyncedValue = seconds;

            _vars.SetFloat(VAR_DURATION, seconds);
            _vars.SetFloat(VAR_REMAINING, seconds);
            _vars.SetBool(VAR_RUNNING, true);

            OnStarted?.Invoke();
        }

        /// <summary>Stop the timer. Authority only.</summary>
        public void StopTimer()
        {
            if (!_netObj.HasAuthority()) return;

            _running = false;
            _vars.SetBool(VAR_RUNNING, false);
            _vars.SetFloat(VAR_REMAINING, _remaining);
        }

        /// <summary>Reset to full duration without starting. Authority only.</summary>
        public void ResetTimer()
        {
            if (!_netObj.HasAuthority()) return;

            _remaining = duration;
            _running = false;
            _vars.SetFloat(VAR_REMAINING, duration);
            _vars.SetBool(VAR_RUNNING, false);
        }

        /// <summary>Get remaining seconds.</summary>
        public float Remaining => _remaining;

        /// <summary>Is the timer currently running?</summary>
        public bool IsRunning => _running;

        /// <summary>Progress from 0 (just started) to 1 (finished).</summary>
        public float Progress => duration > 0 ? 1f - (_remaining / duration) : 1f;
    }
}
