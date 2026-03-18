namespace SomniumNetwork.Server;

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
    
    public bool HasMoved(float threshold = 0.01f)
    {
        float positionDelta = MathF.Sqrt(
            MathF.Pow(Position.X - LastSentPosition.X, 2) +
            MathF.Pow(Position.Y - LastSentPosition.Y, 2) +
            MathF.Pow(Position.Z - LastSentPosition.Z, 2)
        );
        
        float rotationDelta = MathF.Abs(Rotation.Y - LastSentRotation.Y);
        
        return positionDelta > threshold || rotationDelta > 1f; // 1 degree threshold
    }
    
    public void MarkAsSent()
    {
        LastSentPosition = new Vector3 { X = Position.X, Y = Position.Y, Z = Position.Z };
        LastSentRotation = new Vector3 { X = Rotation.X, Y = Rotation.Y, Z = Rotation.Z };
    }
}

public class Vector3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
