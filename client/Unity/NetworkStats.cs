using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Displays network performance stats. Add to any GameObject in the scene.
    /// Shows FPS, messages sent/received per second, and bandwidth.
    /// </summary>
    public class NetworkStats : MonoBehaviour
    {
        [Header("Display")]
        public bool showStats = true;
        public KeyCode toggleKey = KeyCode.F3;
        
        // Counters (static so NetworkManager can increment them)
        public static int MessagesSent = 0;
        public static int MessagesReceived = 0;
        public static long BytesSent = 0;
        public static long BytesReceived = 0;
        
        // Per-second snapshots
        private int _msgSentPerSec = 0;
        private int _msgRecvPerSec = 0;
        private long _bytesSentPerSec = 0;
        private long _bytesRecvPerSec = 0;
        private float _fps = 0;
        
        private float _timer = 0;
        private int _frameCount = 0;
        
        // Previous frame values
        private int _prevSent = 0;
        private int _prevRecv = 0;
        private long _prevBytesSent = 0;
        private long _prevBytesRecv = 0;
        
        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
                showStats = !showStats;
            
            _frameCount++;
            _timer += Time.unscaledDeltaTime;
            
            if (_timer >= 1f)
            {
                _fps = _frameCount / _timer;
                _msgSentPerSec = MessagesSent - _prevSent;
                _msgRecvPerSec = MessagesReceived - _prevRecv;
                _bytesSentPerSec = BytesSent - _prevBytesSent;
                _bytesRecvPerSec = BytesReceived - _prevBytesRecv;
                
                _prevSent = MessagesSent;
                _prevRecv = MessagesReceived;
                _prevBytesSent = BytesSent;
                _prevBytesRecv = BytesReceived;
                
                _frameCount = 0;
                _timer = 0;
            }
        }
        
        private void OnGUI()
        {
            if (!showStats) return;
            
            int w = 260;
            int h = 130;
            Rect rect = new Rect(10, 10, w, h);
            
            GUI.Box(rect, "");
            
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 14;
            style.normal.textColor = _fps >= 50 ? Color.green : _fps >= 30 ? Color.yellow : Color.red;
            
            GUI.Label(new Rect(15, 12, w, 20), $"FPS: {_fps:F0}", style);
            
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(15, 32, w, 20), $"Sent: {_msgSentPerSec} msg/s ({FormatBytes(_bytesSentPerSec)}/s)", style);
            GUI.Label(new Rect(15, 52, w, 20), $"Recv: {_msgRecvPerSec} msg/s ({FormatBytes(_bytesRecvPerSec)}/s)", style);
            GUI.Label(new Rect(15, 72, w, 20), $"Entities: {NetworkManager.Instance?.EntityCount ?? 0}", style);
            
            string connStatus = NetworkManager.Instance?.IsConnected == true ? "Connected" : "Disconnected";
            style.normal.textColor = NetworkManager.Instance?.IsConnected == true ? Color.green : Color.red;
            GUI.Label(new Rect(15, 92, w, 20), $"Status: {connStatus}", style);
            
            style.fontSize = 10;
            style.normal.textColor = Color.gray;
            GUI.Label(new Rect(15, 112, w, 20), "F3 to toggle", style);
        }
        
        private string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }
        
        public static void Reset()
        {
            MessagesSent = 0;
            MessagesReceived = 0;
            BytesSent = 0;
            BytesReceived = 0;
        }
    }
}
