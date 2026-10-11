#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking.Editor
{
    /// <summary>
    /// Scans the currently-open scene for common SomniSync setup mistakes and
    /// surfaces them in a dedicated editor window. Catches the 80% of
    /// "why isn't this syncing?" issues before they become support threads.
    ///
    /// Access via: Tools &gt; SomniSync &gt; Validate Scene.
    /// </summary>
    public class SceneValidator : EditorWindow
    {
        private enum Severity { Ok, Info, Warn, Error }

        private struct Finding
        {
            public Severity Severity;
            public string Category;
            public string Message;
            public Object Context;
        }

        private readonly List<Finding> _findings = new List<Finding>();
        private Vector2 _scroll;
        private int _errorCount;
        private int _warnCount;

        [MenuItem("Tools/SomniSync Package/Validate Scene")]
        public static void Open()
        {
            var w = GetWindow<SceneValidator>("SomniSync Validator");
            w.minSize = new Vector2(480, 320);
            w.Run();
        }

        [MenuItem("Tools/SomniSync Package/Open Documentation")]
        public static void OpenDocs()
        {
            Application.OpenURL("https://bentlybro.github.io/SomniSync/");
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Re-scan scene", GUILayout.Height(24)))
            {
                Run();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                _findings.Count == 0
                    ? "No scan yet."
                    : $"{_errorCount} errors · {_warnCount} warnings · {_findings.Count} total",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();

            if (_findings.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Click \"Re-scan scene\" to check the currently-open scene for SomniSync setup issues.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var f in _findings)
            {
                DrawFinding(f);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawFinding(Finding f)
        {
            var style = new GUIStyle(EditorStyles.helpBox) { richText = true, wordWrap = true };
            var prefix = f.Severity switch
            {
                Severity.Ok    => "<color=#4caf50>[ OK ]</color>",
                Severity.Info  => "<color=#2196f3>[INFO]</color>",
                Severity.Warn  => "<color=#ffb74d>[WARN]</color>",
                Severity.Error => "<color=#e57373>[FAIL]</color>",
                _              => "[?]"
            };

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"{prefix}  <b>{f.Category}</b>\n{f.Message}", style);
            if (f.Context != null && GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(40)))
            {
                Selection.activeObject = f.Context;
                EditorGUIUtility.PingObject(f.Context);
            }
            EditorGUILayout.EndHorizontal();
        }

        // =====================================================================
        // Validation
        // =====================================================================

        private void Run()
        {
            _findings.Clear();
            _errorCount = 0;
            _warnCount = 0;

            CheckNetworkManager();
            CheckPrefabList();
            CheckSceneNetworkObjects();
            CheckSiblingRequirements();

            Repaint();
        }

        private void CheckNetworkManager()
        {
            var managers = Object.FindObjectsByType<NetworkManager>(FindObjectsSortMode.None);
            if (managers.Length == 0)
            {
                Fail("NetworkManager", "No NetworkManager component in the open scene. Add one to an empty GameObject to enable networking.", null);
                return;
            }
            if (managers.Length > 1)
            {
                Fail("NetworkManager",
                    $"Found {managers.Length} NetworkManager components — exactly one is expected. Remove the extras.",
                    managers[0]);
                return;
            }

            var mgr = managers[0];
            Ok("NetworkManager", $"Found on '{mgr.gameObject.name}'.", mgr);
            if (string.IsNullOrWhiteSpace(mgr.sceneVersion) || mgr.sceneVersion.Length > 64)
                Fail("NetworkManager.sceneVersion", "Set a scene version of 1-64 characters. Increase it after networking behavior or unsupported geometry changes.", mgr);

            if (string.IsNullOrWhiteSpace(mgr.serverUrl))
            {
                Fail("NetworkManager.serverUrl", "Server URL is empty. Set it to e.g. ws://localhost:9090 or wss://your-relay.example.com:9090.", mgr);
            }
            else if (!System.Uri.TryCreate(mgr.serverUrl, System.UriKind.Absolute, out var serverUri) ||
                (serverUri.Scheme != "ws" && serverUri.Scheme != "wss") ||
                serverUri.AbsolutePath != "/" || !string.IsNullOrEmpty(serverUri.Query) ||
                !string.IsNullOrEmpty(serverUri.Fragment) || !string.IsNullOrEmpty(serverUri.UserInfo))
            {
                Fail("NetworkManager.serverUrl", "Use a ws:// or wss:// relay base URL without a room path, query, or credentials.", mgr);
            }
            else
            {
                Ok("NetworkManager.serverUrl", $"URL looks valid: {mgr.serverUrl}", mgr);
                if (serverUri.IsLoopback)
                    Warn("NetworkManager.serverUrl", "This loopback URL only works on your own PC. Set a reachable relay URL before uploading the world.", mgr);
            }

            string roomId = string.IsNullOrWhiteSpace(mgr.roomId) ? "default" : mgr.roomId.Trim();
            bool roomValid = roomId.Length <= 64;
            foreach (char c in roomId)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-' && c != '.') roomValid = false;
            if (!roomValid)
                Fail("NetworkManager.roomId", "Use at most 64 letters, numbers, _, - or . for the room ID.", mgr);

            if (mgr.localPlayer == null)
            {
                Warn("NetworkManager.localPlayer",
                    "Local Player transform is not assigned. AOI filtering + PlayerPushDetector auto-attach will fall back to runtime detection (works for Somnium rigs, unreliable elsewhere).",
                    mgr);
            }
        }

        private void CheckPrefabList()
        {
            var mgr = Object.FindAnyObjectByType<NetworkManager>();
            if (mgr == null) return;

            if (mgr.networkedPrefabs == null || mgr.networkedPrefabs.Count == 0)
            {
                Info("networkedPrefabs",
                    "List is empty. Runtime spawning via NetworkManager.Spawn() will not work until you add prefabs here. Skip this if you don't spawn anything at runtime.",
                    mgr);
                return;
            }

            var seenNames = new HashSet<string>();
            for (int i = 0; i < mgr.networkedPrefabs.Count; i++)
            {
                var p = mgr.networkedPrefabs[i];
                if (p == null)
                {
                    Fail("networkedPrefabs",
                        $"Entry at index {i} is null. Every index must map to a real prefab — shifting indices breaks existing callers and the server's index-based spawn broadcasts.",
                        mgr);
                    continue;
                }

                if (!seenNames.Add(p.name))
                {
                    Warn("networkedPrefabs",
                        $"Duplicate prefab name '{p.name}' at index {i}. Spawn(string) name-lookup returns first match only — the later entry will never spawn via name.",
                        p);
                }

                var netObj = p.GetComponent<NetworkObject>();
                if (netObj == null)
                {
                    Fail("networkedPrefabs",
                        $"'{p.name}' (index {i}) has no NetworkObject component on its root. Spawn requests for this prefab will fail.",
                        p);
                    continue;
                }

                var col = p.GetComponent<Collider>();
                if (col == null)
                {
                    Fail("networkedPrefabs",
                        $"'{p.name}' (index {i}) has no Collider on the root. Runtime spawning requires a Box, Sphere, or Capsule collider so the server can build a Rapier body.",
                        p);
                    continue;
                }
                if (col is MeshCollider)
                {
                    Fail("networkedPrefabs",
                        $"'{p.name}' (index {i}) uses a MeshCollider. Server-side Rapier only supports Box/Sphere/Capsule. Swap to one of those on the root; keep the MeshCollider on children if you need visual collision.",
                        p);
                    continue;
                }

                Ok("networkedPrefabs", $"'{p.name}' (index {i}) OK.", p);
            }
        }

        private void CheckSceneNetworkObjects()
        {
            var objects = Object.FindObjectsByType<NetworkObject>(FindObjectsSortMode.None);
            if (objects.Length == 0)
            {
                Info("NetworkObject", "No NetworkObject components in the scene. Add them to any GameObject you want synced.", null);
                return;
            }
            if (objects.Length > Protocol.MAX_SCENE_OBJECTS)
                Fail("NetworkObject count",
                    $"Found {objects.Length} NetworkObjects. The relay accepts at most {Protocol.MAX_SCENE_OBJECTS} scene physics objects, including static colliders.",
                    objects[0]);

            int staticColliders = 0;
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (col == null || col.isTrigger || col is MeshCollider || col.GetComponent<NetworkObject>() != null) continue;
                var rb = col.GetComponent<Rigidbody>();
                if (col.gameObject.isStatic || rb == null) staticColliders++;
            }
            if (objects.Length + staticColliders > Protocol.MAX_SCENE_OBJECTS)
                Fail("Scene physics count", $"At least {objects.Length + staticColliders} scene objects and static colliders may be sent. The relay limit is {Protocol.MAX_SCENE_OBJECTS}.", objects[0]);

            int meshColliderCount = 0;
            int noColliderCount = 0;
            var sortKeys = new HashSet<(string, string, Vector3)>();

            foreach (var obj in objects)
            {
                if (!sortKeys.Add((obj.entityType, obj.gameObject.name, obj.transform.position)))
                    Fail("NetworkObject identity",
                        $"'{obj.name}' shares its type, name, and position with another NetworkObject. Give each object a unique name so clients assign the same IDs.",
                        obj);
                var col = obj.GetComponent<Collider>();
                if (col == null)
                {
                    noColliderCount++;
                    Warn("NetworkObject",
                        $"'{obj.name}' has no Collider. Will not be included in the server physics scene — OK for pure-variable/event-only objects, but no physics sync.",
                        obj);
                    continue;
                }
                if (col is MeshCollider)
                {
                    meshColliderCount++;
                    Warn("NetworkObject",
                        $"'{obj.name}' uses MeshCollider — server skips these. Add a Box/Sphere/Capsule collider on the root for physics sync.",
                        obj);
                }
            }

            if (noColliderCount == 0 && meshColliderCount == 0)
            {
                Ok("NetworkObject", $"All {objects.Length} NetworkObject(s) have supported colliders.", null);
            }
        }

        private void CheckSiblingRequirements()
        {
            // Components that require a NetworkObject on the same GameObject.
            CheckSibling<NetworkAnimator>();
            CheckSibling<NetworkEvents>();
            CheckSibling<NetworkVariables>();
            CheckSibling<NetworkNPC>();
            CheckSibling<NetworkParticle>();
            CheckSibling<NetworkTimer>();
        }

        private void CheckSibling<T>() where T : Component
        {
            var instances = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            foreach (var inst in instances)
            {
                if (inst.GetComponent<NetworkObject>() == null)
                {
                    Fail(typeof(T).Name,
                        $"'{inst.gameObject.name}' has a {typeof(T).Name} but no sibling NetworkObject. It will disable itself at runtime.",
                        inst);
                }
            }
        }

        // =====================================================================
        // Logging helpers
        // =====================================================================

        private void Ok(string category, string message, Object context)
            => _findings.Add(new Finding { Severity = Severity.Ok, Category = category, Message = message, Context = context });

        private void Info(string category, string message, Object context)
            => _findings.Add(new Finding { Severity = Severity.Info, Category = category, Message = message, Context = context });

        private void Warn(string category, string message, Object context)
        {
            _findings.Add(new Finding { Severity = Severity.Warn, Category = category, Message = message, Context = context });
            _warnCount++;
        }

        private void Fail(string category, string message, Object context)
        {
            _findings.Add(new Finding { Severity = Severity.Error, Category = category, Message = message, Context = context });
            _errorCount++;
        }
    }
}
#endif
