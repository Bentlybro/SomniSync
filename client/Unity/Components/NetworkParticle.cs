using System;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Syncs ParticleSystem play/stop/properties across clients.
    /// Attach to any NetworkObject with a ParticleSystem.
    /// 
    /// Two sync modes:
    /// - Events only (default): Just play/stop — lightweight
    /// - Full sync: Also syncs emission rate, start color, start speed
    /// 
    /// Uses NetworkEvents for play/stop triggers (non-persistent),
    /// and NetworkVariables for properties (persistent for late joiners).
    /// 
    /// No custom protocol needed — built on existing 0x04 and 0x06 messages.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkParticle : MonoBehaviour
    {
        [Header("What to Sync")]
        [Tooltip("Sync play/stop state")]
        public bool syncPlayState = true;

        [Tooltip("Sync emission rate over time")]
        public bool syncEmissionRate = false;

        [Tooltip("Sync start color")]
        public bool syncStartColor = false;

        [Tooltip("Sync start speed")]
        public bool syncStartSpeed = false;

        [Tooltip("Sync start size")]
        public bool syncStartSize = false;

        [Header("Settings")]
        [Tooltip("How often to check for property changes (Hz)")]
        public float syncRate = 5f;

        private NetworkObject _netObj;
        private NetworkEvents _events;
        private NetworkVariables _vars;
        private ParticleSystem _ps;
        private ParticleSystem.EmissionModule _emission;
        private ParticleSystem.MainModule _main;

        private float _syncTimer;
        private bool _lastPlaying;
        private float _lastEmissionRate;
        private Color _lastColor;
        private float _lastSpeed;
        private float _lastSize;

        private void Awake()
        {
            _netObj = GetComponent<NetworkObject>();
            _ps = GetComponent<ParticleSystem>();

            if (_ps == null)
            {
                Debug.LogWarning($"[NetworkParticle] No ParticleSystem on {name}, disabling");
                enabled = false;
                return;
            }

            _emission = _ps.emission;
            _main = _ps.main;
        }

        private void Start()
        {
            // Get or add required components
            _events = GetComponent<NetworkEvents>();
            if (_events == null)
                _events = gameObject.AddComponent<NetworkEvents>();

            _vars = GetComponent<NetworkVariables>();
            if (_vars == null && (syncEmissionRate || syncStartColor || syncStartSpeed || syncStartSize))
                _vars = gameObject.AddComponent<NetworkVariables>();

            // Register event handlers
            _events.On("_particle_play", OnRemotePlay);
            _events.On("_particle_stop", OnRemoteStop);

            // Register variable change handlers
            if (_vars != null)
            {
                if (syncEmissionRate)
                    _vars.DefineVariable("_ps_emission", NetworkVariables.VarType.Float);
                if (syncStartSpeed)
                    _vars.DefineVariable("_ps_speed", NetworkVariables.VarType.Float);
                if (syncStartSize)
                    _vars.DefineVariable("_ps_size", NetworkVariables.VarType.Float);
                if (syncStartColor)
                {
                    _vars.DefineVariable("_ps_color_r", NetworkVariables.VarType.Int);
                    _vars.DefineVariable("_ps_color_g", NetworkVariables.VarType.Int);
                    _vars.DefineVariable("_ps_color_b", NetworkVariables.VarType.Int);
                    _vars.DefineVariable("_ps_color_a", NetworkVariables.VarType.Int);
                }
                _vars.OnValueChanged += OnVarChanged;
            }

            // Cache initial state
            _lastPlaying = _ps.isPlaying;
            _lastEmissionRate = _emission.rateOverTime.constant;
            _lastColor = _main.startColor.color;
            _lastSpeed = _main.startSpeed.constant;
            _lastSize = _main.startSize.constant;
        }

        private void Update()
        {
            if (!_netObj.HasAuthority()) return;

            _syncTimer += Time.deltaTime;

            // Check play state every frame (important to catch)
            if (syncPlayState)
            {
                bool playing = _ps.isPlaying;
                if (playing != _lastPlaying)
                {
                    _lastPlaying = playing;
                    if (playing)
                        _events.Send("_particle_play");
                    else
                        _events.Send("_particle_stop");
                }
            }

            // Check properties at sync rate
            if (_syncTimer < 1f / syncRate) return;
            _syncTimer = 0f;

            if (_vars == null) return;

            if (syncEmissionRate)
            {
                float rate = _emission.rateOverTime.constant;
                if (Mathf.Abs(rate - _lastEmissionRate) > 0.01f)
                {
                    _lastEmissionRate = rate;
                    _vars.SetFloat("_ps_emission", rate);
                }
            }

            if (syncStartSpeed)
            {
                float speed = _main.startSpeed.constant;
                if (Mathf.Abs(speed - _lastSpeed) > 0.01f)
                {
                    _lastSpeed = speed;
                    _vars.SetFloat("_ps_speed", speed);
                }
            }

            if (syncStartSize)
            {
                float size = _main.startSize.constant;
                if (Mathf.Abs(size - _lastSize) > 0.01f)
                {
                    _lastSize = size;
                    _vars.SetFloat("_ps_size", size);
                }
            }

            if (syncStartColor)
            {
                Color c = _main.startColor.color;
                if (c != _lastColor)
                {
                    _lastColor = c;
                    _vars.SetInt("_ps_color_r", (int)(c.r * 255));
                    _vars.SetInt("_ps_color_g", (int)(c.g * 255));
                    _vars.SetInt("_ps_color_b", (int)(c.b * 255));
                    _vars.SetInt("_ps_color_a", (int)(c.a * 255));
                }
            }
        }

        // --- Remote event handlers ---

        private void OnRemotePlay()
        {
            if (!_netObj.HasAuthority() && _ps != null)
                _ps.Play();
        }

        private void OnRemoteStop()
        {
            if (!_netObj.HasAuthority() && _ps != null)
                _ps.Stop();
        }

        private void OnVarChanged(string name, NetworkVariables.VarType type)
        {
            if (_netObj.HasAuthority()) return;
            if (_ps == null || _vars == null) return;

            switch (name)
            {
                case "_ps_emission":
                    var em = _ps.emission;
                    em.rateOverTime = _vars.GetFloat("_ps_emission");
                    break;
                case "_ps_speed":
                    var main = _ps.main;
                    main.startSpeed = _vars.GetFloat("_ps_speed");
                    break;
                case "_ps_size":
                    var main2 = _ps.main;
                    main2.startSize = _vars.GetFloat("_ps_size");
                    break;
                case "_ps_color_r":
                case "_ps_color_g":
                case "_ps_color_b":
                case "_ps_color_a":
                    ApplyColor();
                    break;
            }
        }

        private void ApplyColor()
        {
            if (_vars == null) return;
            float r = _vars.GetInt("_ps_color_r") / 255f;
            float g = _vars.GetInt("_ps_color_g") / 255f;
            float b = _vars.GetInt("_ps_color_b") / 255f;
            float a = _vars.GetInt("_ps_color_a") / 255f;
            var main = _ps.main;
            main.startColor = new Color(r, g, b, a);
        }

        // --- Public API ---

        /// <summary>Play the particle system and sync to all clients.</summary>
        public void Play()
        {
            if (_ps != null) _ps.Play();
            if (_events != null) _events.Send("_particle_play");
        }

        /// <summary>Stop the particle system and sync to all clients.</summary>
        public void Stop()
        {
            if (_ps != null) _ps.Stop();
            if (_events != null) _events.Send("_particle_stop");
        }
    }
}
