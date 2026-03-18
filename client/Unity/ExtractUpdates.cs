using UnityEngine;
using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Manual JSON parser for EntityUpdate arrays.
    /// Completely bypasses Unity's JsonUtility which has known limitations
    /// with arrays, nested objects, and nullable types.
    /// </summary>
    public static class UpdatesParser
    {
        /// <summary>
        /// Parse EntityUpdate[] from the raw JSON message.
        /// Does NOT use JsonUtility at all - pure string parsing.
        /// </summary>
        public static EntityUpdate[] ParseUpdatesArray(string json)
        {
            try
            {
                // Find "Updates":[ in the JSON
                int idx = json.IndexOf("\"Updates\":");
                if (idx == -1) return null;
                
                idx += "\"Updates\":".Length;
                idx = SkipWhitespace(json, idx);
                
                // Check for null
                if (idx + 4 <= json.Length && json.Substring(idx, 4) == "null")
                    return null;
                
                // Must be '['
                if (json[idx] != '[') return null;
                idx++; // skip '['
                
                // Find matching ']'
                int arrayEnd = FindMatchingBracket(json, idx - 1);
                if (arrayEnd == -1) return null;
                
                // Extract each {...} object from the array
                var updates = new List<EntityUpdate>();
                
                while (idx < arrayEnd)
                {
                    idx = SkipWhitespace(json, idx);
                    if (idx >= arrayEnd) break;
                    
                    // Skip commas
                    if (json[idx] == ',') { idx++; continue; }
                    
                    // Must be '{'
                    if (json[idx] != '{') break;
                    
                    int objEnd = FindMatchingBrace(json, idx);
                    if (objEnd == -1) break;
                    
                    string objJson = json.Substring(idx, objEnd - idx + 1);
                    
                    // Parse this EntityUpdate manually (no JsonUtility!)
                    EntityUpdate update = ParseEntityUpdate(objJson);
                    if (update != null)
                        updates.Add(update);
                    
                    idx = objEnd + 1;
                }
                
                if (updates.Count > 0)
                    Debug.Log($"[UpdatesParser] Parsed {updates.Count} updates");
                
                return updates.Count > 0 ? updates.ToArray() : null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UpdatesParser] Parse error: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Parse a single EntityUpdate from JSON string - NO JsonUtility.
        /// Expected format: {"Id":"uuid","Position":{"X":1.0,"Y":2.0,"Z":3.0},"Rotation":{"X":4.0,"Y":5.0,"Z":6.0}}
        /// </summary>
        private static EntityUpdate ParseEntityUpdate(string json)
        {
            try
            {
                var update = new EntityUpdate();
                
                // Extract Id
                update.Id = ExtractStringValue(json, "Id");
                if (string.IsNullOrEmpty(update.Id))
                {
                    Debug.LogWarning($"[UpdatesParser] EntityUpdate has no Id");
                    return null;
                }
                
                // Extract Position object
                update.Position = ExtractVector3Data(json, "Position");
                
                // Extract Rotation object
                update.Rotation = ExtractVector3Data(json, "Rotation");
                
                return update;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UpdatesParser] Failed to parse EntityUpdate: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Extract a Vector3Data ({"X":f,"Y":f,"Z":f}) from a JSON string by field name.
        /// </summary>
        private static Vector3Data ExtractVector3Data(string json, string fieldName)
        {
            string pattern = $"\"{fieldName}\":";
            int idx = json.IndexOf(pattern);
            if (idx == -1) return null;
            
            idx += pattern.Length;
            idx = SkipWhitespace(json, idx);
            
            // Check for null
            if (idx + 4 <= json.Length && json.Substring(idx, 4) == "null")
                return null;
            
            // Must be '{'
            if (json[idx] != '{') return null;
            
            int objEnd = FindMatchingBrace(json, idx);
            if (objEnd == -1) return null;
            
            string objJson = json.Substring(idx, objEnd - idx + 1);
            
            var v = new Vector3Data();
            v.X = ExtractFloatValue(objJson, "X");
            v.Y = ExtractFloatValue(objJson, "Y");
            v.Z = ExtractFloatValue(objJson, "Z");
            
            return v;
        }
        
        /// <summary>
        /// Extract a string value: "fieldName":"value"
        /// </summary>
        private static string ExtractStringValue(string json, string fieldName)
        {
            string pattern = $"\"{fieldName}\":\"";
            int idx = json.IndexOf(pattern);
            if (idx == -1) return null;
            
            idx += pattern.Length;
            int endIdx = json.IndexOf("\"", idx);
            if (endIdx == -1) return null;
            
            return json.Substring(idx, endIdx - idx);
        }
        
        /// <summary>
        /// Extract a float value: "fieldName":123.456
        /// </summary>
        private static float ExtractFloatValue(string json, string fieldName)
        {
            string pattern = $"\"{fieldName}\":";
            int idx = json.IndexOf(pattern);
            if (idx == -1) return 0f;
            
            idx += pattern.Length;
            idx = SkipWhitespace(json, idx);
            
            // Find end of number (next comma, brace, bracket, or whitespace)
            int endIdx = idx;
            while (endIdx < json.Length)
            {
                char c = json[endIdx];
                if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\n' || c == '\r' || c == '\t')
                    break;
                endIdx++;
            }
            
            string numStr = json.Substring(idx, endIdx - idx);
            
            if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, 
                               System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            
            return 0f;
        }
        
        private static int SkipWhitespace(string s, int idx)
        {
            while (idx < s.Length && char.IsWhiteSpace(s[idx]))
                idx++;
            return idx;
        }
        
        /// <summary>
        /// Find the matching '}' for a '{' at the given index.
        /// </summary>
        private static int FindMatchingBrace(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
        
        /// <summary>
        /// Find the matching ']' for a '[' at the given index.
        /// </summary>
        private static int FindMatchingBracket(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
