#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

using SomniumSpace.Worlds.bently.networking;

namespace SomniumSpace.Worlds.bently.networking.Samples.Editor
{
    /// <summary>
    /// Generates the Quick Start sample scene programmatically.
    ///
    /// We ship this as code rather than a binary .unity asset for two
    /// reasons: (1) UPM samples live outside Assets/ so binary assets
    /// are fiddly to version, and (2) a code-generated scene is clear
    /// about what each object is for — the user can read this file,
    /// see exactly which components get added with which settings, and
    /// copy the parts they need into their own scene instead of blindly
    /// using a black-box prefab.
    ///
    /// Access: Tools &gt; SomniSync &gt; Create Quick Start Scene.
    /// </summary>
    public static class QuickStartBuilder
    {
        private const string DefaultServerUrl = "ws://localhost:9090";

        [MenuItem("Tools/SomniSync/Create Quick Start Scene")]
        public static void CreateScene()
        {
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Save Quick Start scene",
                "QuickStart",
                "unity",
                "Choose where to save the generated sample scene.",
                "Assets");

            if (string.IsNullOrEmpty(savePath))
                return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(savePath) != null)
            {
                Debug.LogError("[SomniSync Quick Start] Scene already exists: " + savePath);
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            BuildScene();

            if (!EditorSceneManager.SaveScene(scene, savePath))
            {
                Debug.LogError("[SomniSync Quick Start] Could not save scene: " + savePath);
                return;
            }
            Debug.Log($"[SomniSync Quick Start] Scene saved to {savePath}. Press Play, with the relay server running on ws://localhost:9090.");
        }

        private static void BuildScene()
        {
            // ── Ground ────────────────────────────────────────────────────
            // BoxCollider is supported by the relay; a Unity Plane's
            // MeshCollider would be skipped during scene initialization.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(40f, 1f, 40f);

            // ── NetworkManager ────────────────────────────────────────────
            var managerGo = new GameObject("NetworkManager");
            var mgr = managerGo.AddComponent<NetworkManager>();
            mgr.serverUrl = DefaultServerUrl;
            mgr.roomId = "quickstart";

            // ── Player ────────────────────────────────────────────────────
            var player = new GameObject("Player");
            player.transform.position = new Vector3(0f, 1f, -4f);
            var cc = player.AddComponent<CharacterController>();
            cc.radius = 0.3f;
            cc.height = 1.8f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            var cam = new GameObject("PlayerCamera");
            cam.transform.SetParent(player.transform, false);
            cam.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            cam.AddComponent<Camera>();
            cam.AddComponent<AudioListener>();
            // Remove the default Main Camera — we just made a new one on the player.
            var defaultCam = GameObject.Find("Main Camera");
            if (defaultCam != null) Object.DestroyImmediate(defaultCam);
            cam.tag = "MainCamera";

            // The player controller is a runtime script — see
            // Samples~/QuickStart/Runtime/QuickStartPlayerController.cs.
            player.AddComponent<SomniumSpace.Worlds.bently.networking.Samples.QuickStartPlayerController>();
            mgr.localPlayer = player.transform;

            // ── Grabbable cube ────────────────────────────────────────────
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "GrabbableCube";
            cube.transform.position = new Vector3(2f, 0.5f, 0f);
            // Render it red so it stands out as "the one you can grab".
            var cubeRenderer = cube.GetComponent<Renderer>();
            if (cubeRenderer != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.color = Color.red;
                cubeRenderer.sharedMaterial = mat;
            }
            cube.AddComponent<Rigidbody>();
            cube.AddComponent<NetworkObject>();

            // ── Wandering NPC ─────────────────────────────────────────────
            var npc = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            npc.name = "WanderingNPC";
            npc.transform.position = new Vector3(-3f, 1f, 0f);
            var npcRenderer = npc.GetComponent<Renderer>();
            if (npcRenderer != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.color = new Color(0.2f, 0.5f, 1f);
                npcRenderer.sharedMaterial = mat;
            }
            var npcRb = npc.AddComponent<Rigidbody>();
            npcRb.isKinematic = true;
            npc.AddComponent<NetworkObject>();
            var npcBrain = npc.AddComponent<NetworkNPC>();
            npcBrain.wanderRadius = 5f;
            npcBrain.wanderSpeed = 1.5f;

            // ── Spawn hotkey tester ──────────────────────────────────────
            var spawnerGo = new GameObject("SpawnKeyTester");
            spawnerGo.AddComponent<SpawnKeyTester>();

            // ── Light ────────────────────────────────────────────────────
            // The default Directional Light from NewSceneSetup.DefaultGameObjects is fine.
        }
    }

}
#endif
