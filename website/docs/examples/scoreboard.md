# Example: Multiplayer Scoreboard

A shared scoreboard using NetworkVariables. Shows how to sync game state without physics.

## Components

```
Scoreboard
├── NetworkObject (entityType: "scoreboard", pushable: false, grabbable: false)
├── NetworkVariables
├── NetworkEvents                                ← "add_score", "reset"
└── Scoreboard.cs
```

## Script

```csharp
public class Scoreboard : MonoBehaviour
{
    private NetworkVariables _vars;
    private NetworkEvents _events;
    public TextMesh[] scoreTexts; // One per team/player slot

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _events = GetComponent<NetworkEvents>();

        // Define score slots
        _vars.DefineVariable("score_red", VarType.Int, SyncMode.Immediate, persist: true);
        _vars.DefineVariable("score_blue", VarType.Int, SyncMode.Immediate, persist: true);
        _vars.DefineVariable("round", VarType.Int, SyncMode.Immediate, persist: true);

        _vars.OnValueChanged += (name, type) => UpdateDisplay();

        _events.On("reset", () =>
        {
            _vars.SetInt("score_red", 0);
            _vars.SetInt("score_blue", 0);
            _vars.SetInt("round", 0);
        });
    }

    public void AddScore(string team, int points)
    {
        string key = $"score_{team}";
        int current = _vars.GetInt(key);
        _vars.SetInt(key, current + points);
    }

    public void NextRound()
    {
        int round = _vars.GetInt("round") + 1;
        _vars.SetInt("round", round);
    }

    public void Reset()
    {
        _events.Send("reset");
    }

    private void UpdateDisplay()
    {
        scoreTexts[0].text = $"Red: {_vars.GetInt("score_red")}";
        scoreTexts[1].text = $"Blue: {_vars.GetInt("score_blue")}";
        scoreTexts[2].text = $"Round {_vars.GetInt("round")}";
    }
}
```

## Late Joiners

Because all variables use `persist: true`, late joiners see the current scores immediately via snapshot. No catch-up logic needed.
