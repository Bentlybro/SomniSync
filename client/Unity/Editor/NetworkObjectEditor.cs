#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace SomniumSpace.Worlds.bently.networking
{
    [CustomEditor(typeof(NetworkObject))]
    [CanEditMultipleObjects]
    public class NetworkObjectEditor : Editor
    {
        // Identity
        SerializedProperty entityId;
        SerializedProperty entityType;
        
        // Sync
        SerializedProperty syncRate;
        SerializedProperty smoothing;
        SerializedProperty positionThreshold;
        SerializedProperty rotationThreshold;
        SerializedProperty syncPosition;
        SerializedProperty syncRotation;
        
        // Options
        SerializedProperty spawnOnStart;
        SerializedProperty destroyOnDisconnect;
        
        // Runtime
        SerializedProperty _isOwned;
        SerializedProperty _isMaster;
        SerializedProperty _isSpawned;
        
        private bool showAdvanced = false;
        
        private void OnEnable()
        {
            entityId = serializedObject.FindProperty("entityId");
            entityType = serializedObject.FindProperty("entityType");
            syncRate = serializedObject.FindProperty("syncRate");
            smoothing = serializedObject.FindProperty("smoothing");
            positionThreshold = serializedObject.FindProperty("positionThreshold");
            rotationThreshold = serializedObject.FindProperty("rotationThreshold");
            syncPosition = serializedObject.FindProperty("syncPosition");
            syncRotation = serializedObject.FindProperty("syncRotation");
            spawnOnStart = serializedObject.FindProperty("spawnOnStart");
            destroyOnDisconnect = serializedObject.FindProperty("destroyOnDisconnect");
            _isOwned = serializedObject.FindProperty("_isOwned");
            _isMaster = serializedObject.FindProperty("_isMaster");
            _isSpawned = serializedObject.FindProperty("_isSpawned");
        }
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            var netObj = (NetworkObject)target;
            
            // Header
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Add this to any GameObject to sync it across all players.\n" +
                "Rigidbody is auto-detected — physics objects just work.",
                MessageType.Info
            );
            EditorGUILayout.Space(4);
            
            // === IDENTITY ===
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            
            EditorGUI.BeginDisabledGroup(Application.isPlaying);
            EditorGUILayout.PropertyField(entityType, new GUIContent("Type", 
                "Used to match this object with its prefab on remote clients.\n" +
                "Objects with the same type in the scene will be linked automatically."));
            EditorGUI.EndDisabledGroup();
            
            if (Application.isPlaying && !string.IsNullOrEmpty(entityId.stringValue))
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Network ID", entityId.stringValue);
                EditorGUI.EndDisabledGroup();
            }
            
            EditorGUILayout.Space(8);
            
            // === SYNC ===
            EditorGUILayout.LabelField("Sync", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(syncPosition, new GUIContent("Position"));
            EditorGUILayout.PropertyField(syncRotation, new GUIContent("Rotation"));
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.PropertyField(smoothing, new GUIContent("Smoothing", 
                "How smoothly this object moves on remote clients.\n\n" +
                "None — Instant snap to server position\n" +
                "Low — Fast response, may jitter slightly\n" +
                "Medium — Balanced (recommended for most objects)\n" +
                "High — Very smooth but adds perceived latency"));
            
            EditorGUILayout.Space(8);
            
            // === ADVANCED ===
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced Settings", true);
            
            if (showAdvanced)
            {
                EditorGUI.indentLevel++;
                
                EditorGUILayout.PropertyField(syncRate, new GUIContent("Sync Rate (Hz)", 
                    "How many updates per second to send.\n" +
                    "Default 60 matches the server tick rate.\n" +
                    "Lower = less bandwidth but choppier movement."));
                
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Delta Thresholds", EditorStyles.miniLabel);
                
                EditorGUILayout.PropertyField(positionThreshold, new GUIContent("Position (m)", 
                    "Minimum movement in meters before sending an update.\n" +
                    "Saves bandwidth for objects that barely move."));
                
                EditorGUILayout.PropertyField(rotationThreshold, new GUIContent("Rotation (°)", 
                    "Minimum rotation in degrees before sending an update."));
                
                EditorGUILayout.Space(4);
                
                EditorGUILayout.PropertyField(spawnOnStart, new GUIContent("Auto Register", 
                    "Automatically register with the server on Start.\n" +
                    "Disable if you want to register manually via script."));
                
                EditorGUILayout.PropertyField(destroyOnDisconnect, new GUIContent("Destroy on Disconnect", 
                    "Remove this object when the connection drops."));
                
                EditorGUI.indentLevel--;
            }
            
            // === RUNTIME STATUS ===
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
                
                EditorGUILayout.BeginHorizontal();
                
                DrawStatusBadge("Owned", _isOwned.boolValue, Color.green, Color.gray);
                DrawStatusBadge("Master", _isMaster.boolValue, new Color(1f, 0.8f, 0f), Color.gray);
                DrawStatusBadge("Spawned", _isSpawned.boolValue, Color.cyan, Color.gray);
                
                EditorGUILayout.EndHorizontal();
                
                // Rigidbody status
                var rb = netObj.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    EditorGUILayout.Space(4);
                    string physicsStatus = rb.isKinematic ? "Kinematic (receiving)" : "Simulating (sending)";
                    EditorGUILayout.HelpBox($"Physics: {physicsStatus}", MessageType.None);
                }
            }
            
            serializedObject.ApplyModifiedProperties();
        }
        
        private void DrawStatusBadge(string label, bool active, Color activeColor, Color inactiveColor)
        {
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = active ? activeColor : inactiveColor;
            
            GUIStyle style = new GUIStyle(EditorStyles.miniButton);
            style.fontStyle = active ? FontStyle.Bold : FontStyle.Normal;
            
            GUILayout.Button(active ? $"● {label}" : $"○ {label}", style);
            
            GUI.backgroundColor = originalColor;
        }
    }
}
#endif
