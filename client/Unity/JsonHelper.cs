using UnityEngine;
using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// JSON parsing helper that works around Unity JsonUtility limitations.
    /// Handles nullable types, manual field extraction, and safe deserialization.
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
                // First pass: Use JsonUtility for basic structure
                var message = JsonUtility.FromJson<NetworkMessage>(json);
                
                if (message == null)
                {
                    Debug.LogError("[JsonHelper] Failed to deserialize NetworkMessage");
                    return null;
                }
                
                // Second pass: Manually extract problematic fields that JsonUtility fails on
                message.IsMasterClient = ExtractBool(json, "IsMasterClient");
                
                // Extract Updates array (JsonUtility can't handle arrays as class properties)
                message.Updates = ExtractUpdates(json);
                
                return message;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonHelper] Deserialization error: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract EntityUpdate[] array from JSON.
        /// Unity's JsonUtility cannot deserialize arrays/lists as class properties.
        /// </summary>
        private static EntityUpdate[] ExtractUpdates(string json)
        {
            try
            {
                // Find the Updates array in JSON
                string searchPattern = "\"Updates\":[";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null; // Field not found or null
                
                // Check if it's null
                if (json.Substring(startIndex + searchPattern.Length - 1, 4) == "null")
                    return null;
                
                startIndex += searchPattern.Length;
                
                // Find matching closing bracket
                int depth = 1;
                int endIndex = startIndex;
                while (endIndex < json.Length && depth > 0)
                {
                    if (json[endIndex] == '[')
                        depth++;
                    else if (json[endIndex] == ']')
                        depth--;
                    endIndex++;
                }
                
                // Extract array JSON
                string arrayJson = json.Substring(startIndex, endIndex - startIndex - 1);
                
                // Parse array manually (Unity can't do this directly)
                // Wrap in an object so JsonUtility can handle it
                string wrappedJson = "{\"items\":" + arrayJson + "}";
                var wrapper = JsonUtility.FromJson<EntityUpdateArrayWrapper>(wrappedJson);
                
                return wrapper?.items;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract Updates array: {ex.Message}");
                return null;
            }
        }
        
        // Helper wrapper class for array deserialization
        [System.Serializable]
        private class EntityUpdateArrayWrapper
        {
            public EntityUpdate[] items;
        }
        
        /// <summary>
        /// Extract a boolean value from JSON string by field name.
        /// Handles nullable bools that JsonUtility breaks on.
        /// </summary>
        public static bool? ExtractBool(string json, string fieldName)
        {
            try
            {
                // Find the field in JSON
                string searchPattern = $"\"{fieldName}\":";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null; // Field not found
                
                startIndex += searchPattern.Length;
                
                // Skip whitespace
                while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
                    startIndex++;
                
                // Check for null value
                if (json.Substring(startIndex, 4) == "null")
                    return null;
                
                // Extract value (true or false)
                if (json.Substring(startIndex, 4) == "true")
                    return true;
                
                if (json.Substring(startIndex, 5) == "false")
                    return false;
                
                // Couldn't parse
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract bool '{fieldName}': {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract a string value from JSON string by field name.
        /// </summary>
        public static string ExtractString(string json, string fieldName)
        {
            try
            {
                string searchPattern = $"\"{fieldName}\":\"";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null; // Field not found
                
                startIndex += searchPattern.Length;
                int endIndex = json.IndexOf("\"", startIndex);
                
                if (endIndex == -1)
                    return null; // Malformed JSON
                
                return json.Substring(startIndex, endIndex - startIndex);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract string '{fieldName}': {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract an integer value from JSON string by field name.
        /// </summary>
        public static int? ExtractInt(string json, string fieldName)
        {
            try
            {
                string searchPattern = $"\"{fieldName}\":";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null; // Field not found
                
                startIndex += searchPattern.Length;
                
                // Skip whitespace
                while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
                    startIndex++;
                
                // Check for null
                if (json.Substring(startIndex, 4) == "null")
                    return null;
                
                // Find end of number (next comma, brace, or bracket)
                int endIndex = startIndex;
                while (endIndex < json.Length && 
                       char.IsDigit(json[endIndex]) || 
                       json[endIndex] == '-' || 
                       json[endIndex] == '.')
                {
                    endIndex++;
                }
                
                string numberStr = json.Substring(startIndex, endIndex - startIndex);
                
                if (int.TryParse(numberStr, out int result))
                    return result;
                
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract int '{fieldName}': {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract a float value from JSON string by field name.
        /// </summary>
        public static float? ExtractFloat(string json, string fieldName)
        {
            try
            {
                string searchPattern = $"\"{fieldName}\":";
                int startIndex = json.IndexOf(searchPattern);
                
                if (startIndex == -1)
                    return null;
                
                startIndex += searchPattern.Length;
                
                // Skip whitespace
                while (startIndex < json.Length && char.IsWhiteSpace(json[startIndex]))
                    startIndex++;
                
                // Check for null
                if (startIndex + 4 <= json.Length && json.Substring(startIndex, 4) == "null")
                    return null;
                
                // Find end of number
                int endIndex = startIndex;
                while (endIndex < json.Length && 
                       (char.IsDigit(json[endIndex]) || 
                        json[endIndex] == '-' || 
                        json[endIndex] == '.' || 
                        json[endIndex] == 'e' || 
                        json[endIndex] == 'E'))
                {
                    endIndex++;
                }
                
                string numberStr = json.Substring(startIndex, endIndex - startIndex);
                
                if (float.TryParse(numberStr, out float result))
                    return result;
                
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JsonHelper] Failed to extract float '{fieldName}': {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if a JSON string contains a specific field.
        /// </summary>
        public static bool HasField(string json, string fieldName)
        {
            return json.Contains($"\"{fieldName}\":");
        }
        
        /// <summary>
        /// Safe JSON deserialization with fallback.
        /// </summary>
        public static T Deserialize<T>(string json) where T : new()
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonHelper] Failed to deserialize {typeof(T).Name}: {ex.Message}");
                return default(T);
            }
        }
        
        /// <summary>
        /// Safe JSON serialization with fallback.
        /// </summary>
        public static string Serialize<T>(T obj)
        {
            try
            {
                return JsonUtility.ToJson(obj);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JsonHelper] Failed to serialize {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }
    }
}
