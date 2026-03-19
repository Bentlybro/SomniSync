using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Spawns a debug capsule on each player (Original Player / Simulator Player).
    /// Only visible in Unity Scene view — hidden in Game view and builds.
    /// Attach to any GameObject in your scene.
    /// </summary>
    public class PlayerDebugMarker : MonoBehaviour
    {
        [Header("Marker Settings")]
        public Color localPlayerColor = Color.green;
        public Color remotePlayerColor = Color.cyan;
        public float capsuleHeight = 1.8f;
        public float capsuleRadius = 0.3f;

        private HashSet<Transform> _tracked = new HashSet<Transform>();
        private float _nextScan;

        private void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 1f; // Scan once per second

            // Find all player roots
            foreach (var cc in FindObjectsByType<CharacterController>(FindObjectsSortMode.None))
            {
                Transform body = cc.transform;
                if (body.name != "NonXR.Body" && body.name != "XR.Body") continue;

                // Get root (Original Player: or Simulator Player:)
                Transform root = body;
                while (root.parent != null) root = root.parent;
                if (!root.name.StartsWith("Original Player:") && !root.name.StartsWith("Simulator Player:"))
                    continue;

                if (_tracked.Contains(root)) continue;
                _tracked.Add(root);

                bool isLocal = root.name.StartsWith("Original Player:");
                CreateMarker(body, isLocal);
            }
        }

        private void CreateMarker(Transform parent, bool isLocal)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            marker.name = "[DEBUG] Player Marker";
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = new Vector3(0, capsuleHeight / 2f, 0);
            marker.transform.localScale = new Vector3(capsuleRadius * 2, capsuleHeight / 2f, capsuleRadius * 2);

            // Remove collider so it doesn't interfere with physics
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Editor-only visibility: set to EditorOnly tag + wireframe material
            marker.tag = "EditorOnly";

            // Semi-transparent material
            var renderer = marker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard"));
                Color c = isLocal ? localPlayerColor : remotePlayerColor;
                c.a = 0.3f;
                mat.color = c;
                mat.SetFloat("_Mode", 3); // Transparent
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
                renderer.material = mat;

                // Hide in game view, only show in scene view
                renderer.enabled = true;
                marker.layer = 0; // Default layer, visible in scene
            }

            // Add component to hide in game camera
            marker.AddComponent<SceneViewOnly>();

            Debug.Log($"[Net] Debug marker added to {(isLocal ? "local" : "remote")} player: {parent.name}");
        }
    }

    /// <summary>
    /// Hides this object from all cameras except Scene view.
    /// </summary>
    public class SceneViewOnly : MonoBehaviour
    {
        private void OnWillRenderObject()
        {
            // Hide from game cameras, only show in Scene view
            if (Camera.current != null && Camera.current.cameraType != CameraType.SceneView)
            {
                GetComponent<Renderer>().enabled = false;
            }
            else
            {
                GetComponent<Renderer>().enabled = true;
            }
        }
    }
}
