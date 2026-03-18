namespace SomniumNetwork.Server;

/// <summary>
/// Represents a networked entity with position, rotation, and state tracking.
/// </summary>
public class Entity
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public Vector3 Position { get; set; } = new();
    public Vector3 Rotation { get; set; } = new();
    public Dictionary<string, object> Variables { get; set; } = new();
    
    // Track previous position for delta detection
    public Vector3 LastSentPosition { get; set; } = new();
    public Vector3 LastSentRotation { get; set; } = new();
    
    // Network sleep — stop sending updates for stationary objects
    public bool IsSleeping { get; set; } = false;
    public int SleepCounter { get; set; } = 0;
    private const int SleepThreshold = 30; // ~0.5 sec at 60Hz before sleeping
    
    // Binary ID for fast serialization (assigned on spawn)
    public Guid BinaryId { get; set; }
    
    public bool HasMoved(float threshold = 0.01f)
    {
        // Squared distance (avoids sqrt — much faster for 1000+ entities)
        float dx = Position.X - LastSentPosition.X;
        float dy = Position.Y - LastSentPosition.Y;
        float dz = Position.Z - LastSentPosition.Z;
        float posSqr = dx * dx + dy * dy + dz * dz;
        
        bool moved = posSqr > threshold * threshold;
        
        if (!moved)
        {
            // Check all rotation axes
            float rx = MathF.Abs(Rotation.X - LastSentRotation.X);
            float ry = MathF.Abs(Rotation.Y - LastSentRotation.Y);
            float rz = MathF.Abs(Rotation.Z - LastSentRotation.Z);
            moved = rx > 1f || ry > 1f || rz > 1f;
        }
        
        if (moved)
        {
            SleepCounter = 0;
            IsSleeping = false;
            return true;
        }
        else
        {
            SleepCounter++;
            if (SleepCounter >= SleepThreshold)
            {
                IsSleeping = true;
            }
            return false;
        }
    }
    
    public void MarkAsSent()
    {
        LastSentPosition = new Vector3 { X = Position.X, Y = Position.Y, Z = Position.Z };
        LastSentRotation = new Vector3 { X = Rotation.X, Y = Rotation.Y, Z = Rotation.Z };
    }
    
    public void MarkAsDirty()
    {
        LastSentPosition = new Vector3 { X = float.MaxValue, Y = float.MaxValue, Z = float.MaxValue };
        LastSentRotation = new Vector3 { X = float.MaxValue, Y = float.MaxValue, Z = float.MaxValue };
        IsSleeping = false;
        SleepCounter = 0;
    }
}
