# Windrose Dedicated Server

WindowsGSH module for Windrose dedicated servers. Windrose requires the server to start once before setting are displayed in the config correctly.

## Support

If this module helps you host your servers, you can support development here:

- [Ko-fi](https://ko-fi.com/shenniko)
- [PayPal](https://paypal.me/shenniko)

## Module Layout

```text
WindowsGSH.Windrose/
  README.md
  LICENSE.md
  ServerDescription.json
  WorldDescription.json
  Windrose.mod/
    module.json
    WindroseModule.cs
    author.png
```

Import `Windrose.mod` directly, or import the repository root and let WindowsGSH discover the nested module folder.

## Current Status

- Installs through SteamCMD app `4129620` with anonymous login.
- Starts `R5/Binaries/Win64/WindroseServer-Win64-Shipping.exe`.
- Writes `R5/ServerDescription.json` before start.
- Reads generated `WorldDescription.json` files from `R5/Saved/SaveProfiles/Default`.
- Matches world files by `WorldIslandId` / `islandId` when available.
- Applies world setting changes by running `R5WorldDescriptionUpdater.exe` after writing `WorldDescription.json`.
- Supports P2P mode by process detection and direct connection mode by TCP port check.
- Supports optional WindrosePlus detection and file-spool RCON/status integration when installed manually.
- Backs up `R5/ServerDescription.json`, `R5/Saved/SaveProfiles`, and `R5/Saved/Config`.
- Declares WindowsGSH module API `1.0`.
- Supports existing-server import for native Windrose folders and WindowsGSM-style folders.

## Quick Start

1. Import the module in WindowsGSH Module Management.
2. Create a new Windrose server.
3. Set the server name, invite code, password options, player limit, region, and network settings.
4. Install the server through WindowsGSH.
5. Start the server once so Windrose can generate world data.
6. Stop the server, refresh/read settings, then adjust world settings if needed.
7. Start the server again.

## Important Settings

- `server.name`: server name written to `ServerName`.
- `server.inviteCode`: invite code. Windrose requires at least 6 alphanumeric characters if set manually.
- `server.passwordProtected`: writes `IsPasswordProtected`.
- `server.password`: server password.
- `server.maxPlayers`: maximum simultaneous players.
- `server.region`: Windrose connection service region. `Auto` writes an empty region.
- `server.worldIslandId`: selected world id. Must match the `islandId` in a generated `WorldDescription.json`.
- `network.useDirectConnection`: switches between P2P and direct connection mode.
- `network.directConnectionPort`: direct connection port. Windrose requires both TCP and UDP availability when direct connection is enabled.
- `network.queryPort`: launch query port argument.
- `network.proxyAddress`: bind/proxy address used for the server network interface.
- `world.*`: world settings written only after Windrose has generated a matching `WorldDescription.json`.
- `server.additionalArguments`: optional extra launch arguments.

## WorldDescription Handling

Windrose creates world files after first launch. The documented path is:

```text
R5/Saved/SaveProfiles/Default/RocksDB_v2/<game version>/Worlds/<world id>/WorldDescription.json
```

Some builds may use a nearby RocksDB folder name, so the module searches under `R5/Saved/SaveProfiles/Default` instead of hard-coding the version or database folder. When `server.worldIslandId` is set, WindowsGSH prefers the `WorldDescription.json` whose `WorldDescription.islandId` matches it. If no match exists yet, world settings are skipped until the game generates the file.

After writing world settings, the module runs:

```text
R5WorldDescriptionUpdater.exe <path-to-world>/WorldDescription.json
```

This follows the official Windrose guide and lets the game process the changed world settings.

## WindowsGSM Import

The module can adopt WindowsGSM-style server folders that contain:

```text
serverfiles/R5/Binaries/Win64/WindroseServer-Win64-Shipping.exe
```

During import, WindowsGSH can read existing values from:

```text
R5/ServerDescription.json
R5/Saved/SaveProfiles/Default/**/WorldDescription.json
```

## Existing Server Import

In WindowsGSH, use **Import Existing** and choose this module. Select either a Windrose install folder or a WindowsGSM server folder that contains `serverfiles`. WindowsGSH will detect the server executable, preview values from `ServerDescription.json` and matching world data when present, and then let you copy or adopt the existing install.

## References

- Dedicated server guide: https://playwindrose.com/dedicated-server-guide/
- Steam app: https://store.steampowered.com/app/4129620

## Trust Note

C# modules run code on the user's machine. WindowsGSH does not create, own, review, sign, or guarantee third-party modules.
