using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Dev utility: press a configurable key to spawn one of everything
    /// in <see cref="NetworkManager.networkedPrefabs"/>, arranged in a row
    /// in front of the local player.
    ///
    /// Drop this on any GameObject in the scene while iterating on
    /// content. Leaves the rest of the runtime untouched otherwise.
    /// </summary>
    public class SpawnKeyTester : MonoBehaviour
    {
        [Header("Trigger")]
        [Tooltip("Key to press. Default P.")]
        public KeyCode spawnKey = KeyCode.P;

        [Header("Layout")]
        [Tooltip("Meters in front of the player to place the first prefab.")]
        public float distanceInFront = 3f;
        [Tooltip("Meters between each prefab in the row.")]
        public float spacing = 1.5f;
        [Tooltip("Height offset above the player's pivot — raise for avatars whose origin is at feet.")]
        public float heightOffset = 1.0f;

        [Header("Scope")]
        [Tooltip("If set, only spawn this one prefab by name (must match a GameObject in networkedPrefabs). Leave empty to spawn one of every prefab in the list.")]
        public string onlySpawnName = "";

        private void Update()
        {
            if (!Input.GetKeyDown(spawnKey)) return;

            var mgr = NetworkManager.Instance;
            if (mgr == null || !mgr.IsConnected)
            {
                Debug.LogWarning("[SpawnKeyTester] Not connected — ignored.");
                return;
            }
            if (!mgr.IsPhysicsReady)
            {
                Debug.LogWarning("[SpawnKeyTester] Physics not ready yet — ignored.");
                return;
            }
            if (mgr.networkedPrefabs == null || mgr.networkedPrefabs.Count == 0)
            {
                Debug.LogWarning("[SpawnKeyTester] NetworkManager.networkedPrefabs is empty — add prefabs in the inspector.");
                return;
            }

            var player = mgr.LocalPlayer;
            Vector3 origin = player != null ? player.position + Vector3.up * heightOffset : transform.position;
            Vector3 forward = player != null ? player.forward : transform.forward;
            Vector3 right   = player != null ? player.right   : transform.right;
            forward.y = 0f; forward.Normalize();
            right.y   = 0f; right.Normalize();

            if (!string.IsNullOrEmpty(onlySpawnName))
            {
                int idx = mgr.GetPrefabIndex(onlySpawnName);
                if (idx < 0)
                {
                    Debug.LogError($"[SpawnKeyTester] No prefab named '{onlySpawnName}' in networkedPrefabs.");
                    return;
                }
                Vector3 pos = origin + forward * distanceInFront;
                mgr.Spawn(idx, pos, Quaternion.LookRotation(forward));
                Debug.Log($"[SpawnKeyTester] Spawned '{onlySpawnName}' (idx={idx}) at {pos}");
                return;
            }

            int count = mgr.networkedPrefabs.Count;
            float rowWidth = (count - 1) * spacing;
            Vector3 rowStart = origin + forward * distanceInFront - right * (rowWidth * 0.5f);

            for (int i = 0; i < count; i++)
            {
                var prefab = mgr.networkedPrefabs[i];
                if (prefab == null) continue;
                Vector3 pos = rowStart + right * (i * spacing);
                mgr.Spawn(i, pos, Quaternion.LookRotation(-forward)); // face the player
                Debug.Log($"[SpawnKeyTester] Spawned '{prefab.name}' (idx={i}) at {pos}");
            }
        }
    }
}
