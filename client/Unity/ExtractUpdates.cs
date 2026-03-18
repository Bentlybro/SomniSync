using UnityEngine;
using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.bently.networking
{
    public static class UpdatesParser
    {
        /// <summary>
        /// Manually parse EntityUpdate[] from JSON array string.
        /// Bypasses Unity JsonUtility's array limitations by deserializing each object individually.
        /// </summary>
        public static EntityUpdate[] ParseUpdatesArray(string json)
        {
            try
            {
                // Find the Updates array in JSON
                string searchPattern = "\"Updates\":";
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
                
                // Should be at '['
                if (json[startIndex] != '[')
                    return null;
                
                startIndex++; // Skip '['
                
                // Find matching ']'
                int depth = 1;
                int endIndex = startIndex;
                while (endIndex < json.Length && depth > 0)
                {
                    if (json[endIndex] == '[' || json[endIndex] == '{')
                        depth++;
                    else if (json[endIndex] == ']' || json[endIndex] == '}')
                        depth--;
                    endIndex++;
                }
                
                // Extract array contents
                string arrayContents = json.Substring(startIndex, endIndex - startIndex - 1).Trim();
                
                if (string.IsNullOrWhiteSpace(arrayContents))
                    return new EntityUpdate[0];
                
                // Manually split by JSON objects
                var updates = new List<EntityUpdate>();
                int pos = 0;
                
                while (pos < arrayContents.Length)
                {
                    // Skip whitespace/commas
                    while (pos < arrayContents.Length && 
                           (char.IsWhiteSpace(arrayContents[pos]) || arrayContents[pos] == ','))
                        pos++;
                    
                    if (pos >= arrayContents.Length)
                        break;
                    
                    // Should be at '{'
                    if (arrayContents[pos] != '{')
                        break;
                    
                    // Find matching '}'
                    int objDepth = 1;
                    int objEnd = pos + 1;
                    while (objEnd < arrayContents.Length && objDepth > 0)
                    {
                        if (arrayContents[objEnd] == '{')
                            objDepth++;
                        else if (arrayContents[objEnd] == '}')
                            objDepth--;
                        objEnd++;
                    }
                    
                    // Extract and deserialize this object
                    string objectJson = arrayContents.Substring(pos, objEnd - pos);
                    
                    try
                    {
                        EntityUpdate update = JsonUtility.FromJson<EntityUpdate>(objectJson);
                        if (update != null)
                            updates.Add(update);
                    }
                    catch
                    {
                        // Skip malformed objects
                    }
                    
                    pos = objEnd;
                }
                
                Debug.Log($"[UpdatesParser] Parsed {updates.Count} EntityUpdates");
                return updates.ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UpdatesParser] Parse failed: {ex.Message}");
                return null;
            }
        }
    }
}
