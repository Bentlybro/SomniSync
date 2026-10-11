#if UNITY_EDITOR
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking.Editor
{
    /// <summary>A short setup path for creators adding SomniSync to a scene.</summary>
    public sealed class SomniSyncSetupWindow : EditorWindow
    {
        private const string DefaultUrl = "ws://localhost:9090";
        private string _serverUrl = DefaultUrl;
        private string _roomId = "default";
        private string _sceneVersion = "1";
        private string _connectionResult = "";
        private string _installResult = "";
        private string _creatorId = "";
        private Vector2 _scroll;
        private bool _checkingConnection;

        private static string ShownKey => "SomniSync.SetupShown.1." + Application.dataPath;

        [InitializeOnLoadMethod]
        private static void ShowOnFirstImport()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode || EditorPrefs.GetBool(ShownKey, false)) return;
                Open();
            };
        }

        [MenuItem("Tools/SomniSync Package/Setup Wizard", priority = 0)]
        public static void Open()
        {
            EditorPrefs.SetBool(ShownKey, true);
            var window = GetWindow<SomniSyncSetupWindow>("SomniSync Setup");
            window.minSize = new Vector2(470, 520);
            window.LoadFromManager();
            window.Show();
        }

        private void OnEnable()
        {
            _creatorId = EditorPrefs.GetString("SomniSync.CreatorId." + Application.dataPath, "");
            LoadFromManager();
        }

        private void LoadFromManager()
        {
            var manager = FindManager();
            if (manager == null) return;
            _serverUrl = manager.serverUrl;
            _roomId = string.IsNullOrWhiteSpace(manager.roomId) ? "default" : manager.roomId;
            _sceneVersion = manager.sceneVersion;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Set up SomniSync", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Choose a relay and room, set up this scene, then press Play. Every player in the same room sees its networked objects.", MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Somnium world upload", EditorStyles.boldLabel);
            var installerScript = MonoScript.FromScriptableObject(this);
            bool worldCopy = AssetDatabase.GetAssetPath(installerScript).StartsWith("Assets/#User/", StringComparison.Ordinal);
            if (worldCopy)
            {
                EditorGUILayout.HelpBox("This is the creator-specific world copy. Its components and scripts are ready to use in this scene.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("For a Somnium upload, prepare the world scripts FIRST. Then remove the Package Manager copy and reopen this wizard from the copied scripts before adding components to your scene.", MessageType.Info);
                _creatorId = EditorGUILayout.TextField(new GUIContent("Creator ID", "The creator part of SomniumSpace.Worlds.<creator>.somnisync"), _creatorId);
                using (new EditorGUI.DisabledScope(!SomniumWorldInstaller.IsValidCreator(_creatorId) || EditorApplication.isPlaying))
                {
                    if (GUILayout.Button("Prepare world scripts", GUILayout.Height(28)))
                    {
                        EditorPrefs.SetString("SomniSync.CreatorId." + Application.dataPath, _creatorId.Trim());
                        SomniumWorldInstaller.Export(_creatorId, installerScript, out _installResult);
                    }
                }
                if (!SomniumWorldInstaller.IsValidCreator(_creatorId))
                    EditorGUILayout.HelpBox("Enter your Somnium creator ID to prepare world scripts.", MessageType.None);
                if (!string.IsNullOrEmpty(_installResult)) EditorGUILayout.HelpBox(_installResult, MessageType.Info);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("1. Connection", EditorStyles.boldLabel);
            _serverUrl = EditorGUILayout.TextField(new GUIContent("Server URL", "WebSocket address of your SomniSync relay"), _serverUrl);
            _roomId = EditorGUILayout.TextField(new GUIContent("Room", "Players using the same room share a world"), _roomId);
            _sceneVersion = EditorGUILayout.TextField(new GUIContent("Scene version", "Increase after changing networking behavior or unsupported geometry"), _sceneVersion);

            var urlValid = TryGetUrl(out var url, out var validationError);
            if (!urlValid) EditorGUILayout.HelpBox(validationError, MessageType.Error);
            else if (url.IsLoopback)
                EditorGUILayout.HelpBox("This relay address works only on this computer. Set a public ws:// or wss:// address before uploading the world.", MessageType.Warning);
            if (!ValidRoom())
                EditorGUILayout.HelpBox("Use 1-64 letters, numbers, _, - or . for the room.", MessageType.Error);
            bool versionValid = !string.IsNullOrWhiteSpace(_sceneVersion) && _sceneVersion.Length <= 64;
            if (!versionValid)
                EditorGUILayout.HelpBox("Set a scene version of 1-64 characters. Increase it when network behavior changes.", MessageType.Error);

            using (new EditorGUI.DisabledScope(!urlValid || !ValidRoom() || !versionValid || EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Create or update Network Manager", GUILayout.Height(30)))
                    ApplyManager();
            }

            using (new EditorGUI.DisabledScope(!urlValid || !ValidRoom() || _checkingConnection))
            {
                if (GUILayout.Button(_checkingConnection ? "Testing connection..." : "Test relay connection"))
                    TestConnection(url);
            }
            if (!string.IsNullOrEmpty(_connectionResult))
                EditorGUILayout.HelpBox(_connectionResult, _connectionResult.StartsWith("Connected") ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("2. Networked objects", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select an object with a Box, Sphere, or Capsule collider, then add NetworkObject. A Rigidbody enables server physics and grabbing.", MessageType.None);
            var selected = Selection.activeGameObject;
            using (new EditorGUI.DisabledScope(selected == null || EditorApplication.isPlaying))
            {
                if (GUILayout.Button(selected == null ? "Select an object first" : "Add NetworkObject to " + selected.name))
                    AddSelectedObject(selected);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("3. Check and run", EditorStyles.boldLabel);
            var managers = FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var objects = FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            EditorGUILayout.LabelField(managers.Length == 1 ? "Manager: ready" : "Manager: " + managers.Length + " found (need exactly one)");
            EditorGUILayout.LabelField("Networked scene objects: " + objects.Length);
            if (GUILayout.Button("Validate scene")) SceneValidator.Open();
            using (new EditorGUI.DisabledScope(managers.Length != 1 || !urlValid || !ValidRoom() || !versionValid || EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Save scene and Play", GUILayout.Height(28)))
                {
                    ApplyManager();
                    var scene = managers[0].gameObject.scene;
                    if (string.IsNullOrEmpty(scene.path))
                        _connectionResult = "Save this scene as an asset before entering Play mode.";
                    else if (EditorSceneManager.SaveScene(scene))
                        EditorApplication.isPlaying = true;
                    else
                        _connectionResult = "Could not save the scene. Check the Unity Console.";
                }
            }
            if (GUILayout.Button("Select Network Manager") && managers.Length > 0)
            {
                Selection.activeGameObject = managers[0].gameObject;
                EditorGUIUtility.PingObject(managers[0].gameObject);
            }
            EditorGUILayout.HelpBox("Save the scene before uploading it. The relay server must be running and reachable by your players.", MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private static NetworkManager FindManager()
        {
            var managers = FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return managers.Length == 1 ? managers[0] : null;
        }

        private bool ValidRoom()
        {
            var value = _roomId.Trim();
            if (value.Length == 0 || value.Length > 64) return false;
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-' && c != '.') return false;
            return true;
        }

        private bool TryGetUrl(out Uri url, out string error)
        {
            if (!Uri.TryCreate(_serverUrl.Trim(), UriKind.Absolute, out url) ||
                (url.Scheme != "ws" && url.Scheme != "wss") ||
                !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
                !string.IsNullOrEmpty(url.Fragment) || url.AbsolutePath != "/")
            {
                error = "Use a relay base URL such as ws://localhost:9090 or wss://relay.example.com (no path, query or credentials).";
                return false;
            }
            error = "";
            return true;
        }

        private void ApplyManager()
        {
            var managers = FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (managers.Length > 1)
            {
                EditorUtility.DisplayDialog("SomniSync", "This scene has multiple NetworkManagers. Remove extras before setup.", "OK");
                return;
            }

            NetworkManager manager;
            if (managers.Length == 0)
            {
                var go = new GameObject("SomniSync Network Manager");
                Undo.RegisterCreatedObjectUndo(go, "Create SomniSync Network Manager");
                manager = Undo.AddComponent<NetworkManager>(go);
            }
            else manager = managers[0];

            Undo.RecordObject(manager, "Configure SomniSync Network Manager");
            manager.serverUrl = _serverUrl.Trim().TrimEnd('/');
            manager.roomId = _roomId.Trim();
            manager.sceneVersion = _sceneVersion.Trim();
#if SOMNIUM_SDK
            if (manager.GetComponent<SomniumBridgeIntegration>() == null)
                Undo.AddComponent<SomniumBridgeIntegration>(manager.gameObject);
#endif
            EditorUtility.SetDirty(manager);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            Selection.activeGameObject = manager.gameObject;
            _connectionResult = "Manager configured. Save the scene, then press Play.";
        }

        private static void AddSelectedObject(GameObject go)
        {
            if (go.GetComponent<NetworkObject>() != null)
            {
                Selection.activeGameObject = go;
                return;
            }
            var collider = go.GetComponent<Collider>();
            if (collider == null || collider is MeshCollider)
            {
                EditorUtility.DisplayDialog("SomniSync", "Add a Box, Sphere, or Capsule collider to this object first.", "OK");
                return;
            }
            Undo.AddComponent<NetworkObject>(go);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
        }

        private async void TestConnection(Uri baseUrl)
        {
            _checkingConnection = true;
            _connectionResult = "";
            Repaint();
            try
            {
                // Use a disposable room: a connection check must not collide
                // with a live world's scene fingerprint or bootstrap state.
                var endpoint = new Uri(baseUrl.ToString().TrimEnd('/') +
                    "/setup-check-" + Guid.NewGuid().ToString("N") + "?scene=0000000000000001");
                using (var socket = new ClientWebSocket())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    await socket.ConnectAsync(endpoint, timeout.Token);
                    _connectionResult = "Connected to the relay. WebSocket handshake succeeded.";
                }
            }
            catch (Exception ex)
            {
                _connectionResult = "Connection failed: " + ex.GetBaseException().Message;
            }
            finally
            {
                _checkingConnection = false;
                if (this != null) Repaint();
            }
        }
    }
    /// <summary>Builds a separate, lightweight scene for real client load tests.</summary>
    /// <remarks>The visual markers are sparse; every generated body still has a collider.</remarks>
    public static class SomniSyncLoadTestBuilder
    {
        [MenuItem("Tools/SomniSync Package/Create 4,000 Object Test Scene")]
        public static void CreateFromMenu()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save SomniSync load test scene", "SomniSync4000LoadTest", "unity",
                "Choose a new scene path for the test fixture.");
            if (!string.IsNullOrEmpty(path)) CreateSceneAtPath(path, 4000);
        }

        public static bool CreateSceneAtPath(string path, int objectCount)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                objectCount < 1 || objectCount >= Protocol.MAX_SCENE_OBJECTS)
            {
                Debug.LogError("[SomniSync] Choose a new .unity path under Assets and 1-16,383 objects.");
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null)
            {
                Debug.LogError("[SomniSync] Test scene already exists: " + path);
                return false;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var managerGo = new GameObject("SomniSync Network Manager");
            var manager = managerGo.AddComponent<NetworkManager>();
            manager.serverUrl = "ws://127.0.0.1:9090";
            manager.roomId = "somnisync-load-test";
            manager.sceneVersion = "1";

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -1f, 0f);
            ground.transform.localScale = new Vector3(160f, 2f, 160f);

            var player = new GameObject("Test Player Position");
            player.transform.position = new Vector3(0f, 2f, 0f);
            manager.localPlayer = player.transform;

            var camera = Camera.main;
            if (camera != null)
            {
                camera.transform.position = new Vector3(0f, 75f, -95f);
                camera.transform.LookAt(Vector3.zero);
            }

            var parent = new GameObject("Load Objects").transform;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(objectCount));
            try
            {
                for (int i = 0; i < objectCount; i++)
                {
                    if (i % 128 == 0)
                        EditorUtility.DisplayProgressBar("SomniSync load test",
                            "Creating network objects", (float)i / objectCount);
                    // Render only a small sample so a graphics bottleneck does
                    // not hide networking or server-physics performance.
                    var go = i % 64 == 0
                        ? GameObject.CreatePrimitive(PrimitiveType.Sphere)
                        : new GameObject();
                    go.name = "LoadSphere_" + i.ToString("D5");
                    go.transform.SetParent(parent, false);
                    go.transform.localPosition = new Vector3(
                        (i % columns - (columns - 1) * 0.5f) * 1.5f,
                        2f + i % 7 * 0.65f,
                        (i / columns - (columns - 1) * 0.5f) * 1.5f);
                    go.transform.localScale = Vector3.one * 0.5f;
                    if (go.GetComponent<SphereCollider>() == null)
                        go.AddComponent<SphereCollider>();
                    go.AddComponent<Rigidbody>();
                    go.AddComponent<NetworkObject>();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!EditorSceneManager.SaveScene(scene, path))
            {
                Debug.LogError("[SomniSync] Could not save load test scene: " + path);
                return false;
            }
            Debug.Log($"[SomniSync] Created {objectCount} networked bodies at {path}. Set the relay URL in the Setup Wizard, then test two clients.");
            return true;
        }
    }
}
#endif
