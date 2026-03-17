namespace SomniumNetwork.Server;

public class Entity
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public Vector3 Position { get; set; } = new();
    public Vector3 Rotation { get; set; } = new();
    public Dictionary<string, object> Variables { get; set; } = new();
}

public class Vector3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
