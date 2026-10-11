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
using UnityEngine;
using SomniumSpace.Worlds.bently.networking; // Use your creator namespace in a Somnium world.

public class Scoreboard : MonoBehaviour
{
    private NetworkVariables _vars;
    private NetworkEvents _events;
    private NetworkObject _netObj;
    public TextMesh[] scoreTexts; // One per team/player slot

    void Start()
    {
        _vars = GetComponent<NetworkVariables>();
        _events = GetComponent<NetworkEvents>();
        _netObj = GetComponent<NetworkObject>();

        // Define score slots
        _vars.DefineVariable("score_red", NetworkVariables.VarType.Int, NetworkVariables.SyncMode.Immediate, persist: true);
        _vars.DefineVariable("score_blue", NetworkVariables.VarType.Int, NetworkVariables.SyncMode.Immediate, persist: true);
        _vars.DefineVariable("round", NetworkVariables.VarType.Int, NetworkVariables.SyncMode.Immediate, persist: true);

        _vars.OnValueChanged += (name, type) => UpdateDisplay();

        _events.On("reset", () =>
        {
            if (!_netObj.HasAuthority()) return;
            _vars.SetInt("score_red", 0);
            _vars.SetInt("score_blue", 0);
            _vars.SetInt("round", 0);
        });
        _events.OnRaw("add_score", args =>
        {
            if (!_netObj.HasAuthority() || args.Length < 2 ||
                args[0] is not string team || args[1] is not int points ||
                (team != "red" && team != "blue")) return;
            string key = $"score_{team}";
            _vars.SetInt(key, _vars.GetInt(key) + points);
        });
        _events.On("next_round", () =>
        {
            if (_netObj.HasAuthority())
                _vars.SetInt("round", _vars.GetInt("round") + 1);
        });
    }

    public void AddScore(string team, int points)
    {
        _events.SendToAuthority("add_score", team, points);
    }

    public void NextRound()
    {
        _events.SendToAuthority("next_round");
    }

    public void ResetScores()
    {
        _events.SendToAuthority("reset");
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

`persist: true` makes the relay replay each score to late joiners while the room exists. The elected room writer changes unclaimed scoreboard values. Route score requests to that writer through `NetworkEvents`, and validate scoring rules on a trusted server before using this for competitive play.
