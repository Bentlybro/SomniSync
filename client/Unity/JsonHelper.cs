using UnityEngine;
using Newtonsoft.Json;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// JSON helper using Newtonsoft Json.NET (already installed in project).
    /// Replaces all manual parsing and JsonUtility workarounds.
    /// </summary>
    public static class JsonHelper
    {
        public static NetworkMessage DeserializeNetworkMessage(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            
            try
            {
                return JsonConvert.DeserializeObject<NetworkMessage>(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[JsonHelper] Deserialization error: {ex.Message}");
                return null;
            }
        }
    }
}
