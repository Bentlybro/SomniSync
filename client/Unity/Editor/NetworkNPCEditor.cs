#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking.Editor
{
    /// <summary>
    /// Custom inspector for NetworkNPC — hides wander/chase sub-fields when
    /// their toggle is off so the panel isn't a wall of inapplicable options.
    /// </summary>
    [CustomEditor(typeof(NetworkNPC))]
    public class NetworkNPCEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawSection("Wander", "canWander", new[]
            {
                "wanderRadius", "wanderSpeed", "pauseMin", "pauseMax"
            });

            DrawSection("Chase", "canChase", new[]
            {
                "aggroRadius", "deaggroRadius", "chaseSpeed",
                "minContactDistance", "reclaimCooldown"
            });

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Facing", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("faceDirection"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("turnSpeed"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Determinism", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seedOverride"));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSection(string title, string toggleProp, string[] subProps)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            var toggle = serializedObject.FindProperty(toggleProp);
            EditorGUILayout.PropertyField(toggle);

            if (!toggle.boolValue) return;

            EditorGUI.indentLevel++;
            foreach (var name in subProps)
            {
                var prop = serializedObject.FindProperty(name);
                if (prop != null)
                    EditorGUILayout.PropertyField(prop);
            }
            EditorGUI.indentLevel--;
        }
    }
}
#endif
