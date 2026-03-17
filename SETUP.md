# Setup Instructions

## Testing Locally (Without Unity)

### 1. Start Server

You'll need .NET 8 SDK. If not installed:
```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-8.0

# macOS
brew install dotnet@8

# Windows
# Download from https://dotnet.microsoft.com/download/dotnet/8.0
```

Then run:
```bash
cd ~/clawd/projects/somnium-network/server/SomniumNetwork.Server
dotnet run
```

You should see:
```
[Server] Started on ws://localhost:8080/
[Server] Spawning 100 test entities...
[Server] Entities spawned. Waiting for clients...
```

---

## Unity Setup

### 1. Copy Scripts

```bash
# In your Unity project:
cp -r ~/clawd/projects/somnium-network/client/Unity \
      YourUnityProject/Assets/Scripts/SomniumNetwork
```

### 2. Create Scene

1. Create new scene or open existing
2. Create empty GameObject → name it "NetworkManager"
3. Add component: `NetworkManager`
4. Set fields:
   - Server URL: `ws://localhost:8080/`
   - Auto Connect: ✓
   - Entity Prefab: (see step 3)

### 3. Create Entity Prefab

1. Create → 3D Object → Cube
2. Add component: `NetworkEntity`
3. Add component: `NetworkTransform`
4. Drag cube to Project window → save as prefab
5. Delete cube from scene
6. Drag prefab to NetworkManager → Entity Prefab field

### 4. Run

1. Press Play
2. Console should show: `[NetworkManager] Connected!`
3. 100 cubes should spawn and move in circles

---

## Testing in Somnium

### 1. Upload World

- Build your Unity project with Somnium SDK
- Upload world to Somnium Space

### 2. Run Server on VPS

```bash
# On your VPS (DigitalOcean, AWS, etc.)
cd somnium-network/server/SomniumNetwork.Server
dotnet publish -c Release
dotnet bin/Release/net8.0/SomniumNetwork.Server.dll
```

### 3. Update Unity

- Change Server URL to your VPS IP: `ws://YOUR_VPS_IP:8080/`
- Re-upload world

### 4. Test

- Join world in Somnium
- Should see 100 entities moving

---

## Next Steps

Once this works, we can add:
- Custom entity types (NPCs, enemies, etc.)
- NetworkAnimator
- NetworkVariable<T>
- AOI system
- Distance-based updates
