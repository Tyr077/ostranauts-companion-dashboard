# Ostranauts Companion Dashboard

A companion app for [Ostranauts](https://store.steampowered.com/app/1022980/Ostranauts/) that runs beside the game — typically on a second monitor or touchscreen — giving you a live view of crew status, ship atmosphere, and a canvas-based nav map.

The repo has two pieces:

- **`plugin/`** — `CompanionServer`, a BepInEx plugin that runs inside Ostranauts and exposes the game state over a local HTTP API (port 8085).
- **`dashboard/`** — a small Flask web app (port 8086) that polls the API and renders the UI.

## Requirements

- Ostranauts (Steam)
- [BepInEx 5.4.23.5 (x64)](https://github.com/BepInEx/BepInEx/releases) installed to the game folder
- Python 3.10+ (only for the dashboard)
- .NET SDK 8+ (only if building the plugin from source)

## Install

### 1. The plugin

Either grab a prebuilt `CompanionServer.dll` from a release, or build from source (see below). Place the DLL in the game folder:

```
<Ostranauts>/BepInEx/plugins/CompanionServer/CompanionServer.dll
```

Launch Ostranauts once — you should see `CompanionServer` entries in `BepInEx/LogOutput.log` confirming it loaded and the HTTP listener started on port 8085.

### 2. The dashboard

```bash
cd dashboard
pip install -r requirements.txt
python server.py
```

Open `http://localhost:8086`. Requires Ostranauts to be running with the plugin loaded.

## Build the plugin from source

The project targets `net35` (Unity 5.6.7 Mono compatibility). It needs to locate your Ostranauts install to reference BepInEx, Unity, and `Assembly-CSharp.dll`. You can point at it in three ways:

```bash
cd plugin

# 1. Pass on the command line
dotnet build -c Release /p:GameDir="D:\SteamLibrary\steamapps\common\Ostranauts"

# 2. Set an environment variable
setx OSTRANAUTS_DIR "D:\SteamLibrary\steamapps\common\Ostranauts"
dotnet build -c Release

# 3. Edit the default path inside CompanionServer.csproj
```

Output lands at `plugin/bin/Release/net35/CompanionServer.dll`.

## Remote access

By default the plugin binds to `localhost` only. To view the dashboard from another device on the LAN, run this once as Administrator on the game PC:

```
netsh http add urlacl url=http://+:8085/ user=Everyone
```

Then open `http://<game-pc-ip>:8086` from the remote browser. The dashboard will first try a direct connection to the plugin and fall back to a Flask pass-through if the URL ACL isn't set.

## Features

**Crew panel**
- Conditions, needs, priorities, equipment per crew member
- Dynamic card sizing based on crew count
- Tap/click a card to expand it center-screen

**Nav map**
- Top-down canvas view of ships, stations, and celestial bodies
- Orbital paths drawn from Kepler elements
- Range rings, adaptive grid, target selection
- Touch pan/zoom

## License

MIT — see [LICENSE](LICENSE).

Ostranauts is a trademark of Blue Bottle Games. This is an unofficial fan-made companion tool and is not affiliated with or endorsed by Blue Bottle Games.
