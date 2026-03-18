using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking
{
    /// <summary>
    /// Press F3 to toggle network stats overlay.
    /// Shows FPS, connection status, messages/bandwidth per second.
    /// </summary>
    public class NetworkStats : MonoBehaviour
    {
        private bool _show;
        private float _nextUpdate;
        private int _lastSent, _lastRecv, _lastBytesSent, _lastBytesRecv;
        private int _msgsSec, _recvSec, _bytesSentSec, _bytesRecvSec;
        private float _fps;
        private GUIStyle _style;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                _show = !_show;

            if (Time.time >= _nextUpdate)
            {
                _nextUpdate = Time.time + 1f;
                _fps = 1f / Time.deltaTime;
                _msgsSec = NetworkManager.MessagesSent - _lastSent;
                _recvSec = NetworkManager.MessagesReceived - _lastRecv;
                _bytesSentSec = NetworkManager.BytesSent - _lastBytesSent;
                _bytesRecvSec = NetworkManager.BytesReceived - _lastBytesRecv;
                _lastSent = NetworkManager.MessagesSent;
                _lastRecv = NetworkManager.MessagesReceived;
                _lastBytesSent = NetworkManager.BytesSent;
                _lastBytesRecv = NetworkManager.BytesReceived;
            }
        }

        private void OnGUI()
        {
            if (!_show) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 14,
                    alignment = TextAnchor.UpperLeft,
                    padding = new RectOffset(8, 8, 8, 8)
                };
                _style.normal.textColor = Color.white;
            }

            var nm = NetworkManager.Instance;
            string status = nm == null ? "No Manager" :
                !nm.IsConnected ? "Disconnected" :
                nm.IsMaster ? $"MASTER (client_{nm.ClientId})" :
                $"SPECTATOR (client_{nm.ClientId})";

            string text = $"FPS: {_fps:F0}\n" +
                          $"Status: {status}\n" +
                          $"Send: {_msgsSec} msg/s ({FormatBytes(_bytesSentSec)}/s)\n" +
                          $"Recv: {_recvSec} msg/s ({FormatBytes(_bytesRecvSec)}/s)";

            GUI.Box(new Rect(10, 10, 300, 100), text, _style);
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes > 1048576) return $"{bytes / 1048576f:F1} MB";
            if (bytes > 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }
    }
}
