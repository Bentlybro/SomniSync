using UnityEngine;
using System;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// JSON parsing helper that works around Unity JsonUtility limitations.
    /// Handles nullable types and delegates array parsing to UpdatesParser.
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Deserialize JSON string to NetworkMessage with proper nullable handling.
        /// </summary>
        public static NetworkMessage DeserializeNetworkMessage(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            
            try
            {
                // First pass: Use JsonUtility for basic flat fields (Type, EntityId, etc.)
                var message = JsonUtility.FromJson<NetworkMessage>(json);
                
                if (message == null)
                {
                    Debug.LogError("[JsonHelper] Failed to deserialize NetworkMessage");
                    return null;
                }
                
                // Second pass: Manually extract fields that JsonUtility breaks on
                message.IsMasterClient = ExtractBool(json, "IsMasterClient");
                
                // Third pass: Parse Updates array completely manually (no JsonUtility)
                message.Updates = UpdatesParser.ParseUpdatesArray(json);
                
                return message;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonHelper] Deserialization error: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract a boolean value from JSON string by field name.
        /// </summary>
        public static bool? ExtractBool(string json, string fieldName)
        {
            try
            {
                string searchPattern = $"\"{fieldName}\":";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null;
                
                startIndex += searchPattern.Length;
                
                while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
                    startIndex++;
                
                if (startIndex + 4 <= json.Length && json.Substring(startIndex, 4) == "null")
                    return null;
                if (startIndex + 4 <= json.Length && json.Substring(startIndex, 4) == "true")
                    return true;
                if (startIndex + 5 <= json.Length && json.Substring(startIndex, 5) == "false")
                    return false;
                
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract bool '{fieldName}': {ex.Message}");
                return null;
            }
        }
    }
}
