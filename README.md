```
                    ▄████████  ▄██████▄  ███▄▄▄▄   ████████▄  ███    █▄   ▄█      ███    
                   ███    ███ ███    ███ ███▀▀▀██▄ ███   ▀███ ███    ███ ███  ▀█████████▄
                   ███    █▀  ███    ███ ███   ███ ███    ███ ███    ███ ███▌    ▀███▀▀██
                   ███        ███    ███ ███   ███ ███    ███ ███    ███ ███▌     ███   ▀
                   ███        ███    ███ ███   ███ ███    ███ ███    ███ ███▌     ███    
                   ███    █▄  ███    ███ ███   ███ ███    ███ ███    ███ ███      ███    
                   ███    ███ ███    ███ ███   ███ ███   ▄███ ███    ███ ███      ███    
                   ████████▀   ▀██████▀   ▀█   █▀  ████████▀  ████████▀  █▀      ▄████▀  

                                 A simple and fast MCP server for Unity.
```

[![Latest version number](https://img.shields.io/github/package-json/v/apkd/Conduit?filename=Conduit.Unity%2Fpackage.json&labelColor=2C3439&label=Version&logo=unity)](https://github.com/apkd/Conduit/releases/tag/latest)
[![MIT License](https://img.shields.io/github/license/apkd/Conduit?style=flat&label=License&logo=listmonk&labelColor=2C3439&color=fff)](https://github.com/apkd/Conduit/blob/master/LICENSE)
[![Test status badge](https://github.com/apkd/Conduit/actions/workflows/build-test-release.yml/badge.svg?branch=master&event=push)](https://github.com/apkd/Conduit/actions/workflows/build-test-release.yml)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/apkd/Conduit?label=Commits&labelColor=2C3439&color=EBFF65&logo=git)](https://github.com/apkd/Conduit/commits/master)
[![GitHub last commit](https://img.shields.io/github/last-commit/apkd/Conduit?labelColor=2C3439&color=f97&logoColor=f96&logo=tinder&label=Last%20commit)](https://github.com/apkd/Conduit/commit/HEAD~1)

A Unity MCP server that stays out of the way of your coding agent.

- Robust: survives crashes, restarts, assembly reloads, and handles multiple agents and Unity instances.
- Context-efficient: conserves the agent's context window and saves tokens. Small number of versatile tools.
- Simple setup: one Unity package, one server exe, automated setup wizard. No dependencies, no pollution.
- Supports Linux and Windows, both in the editor and in development builds.

> [!WARNING]
> **Granting an AI agent access to Unity indirectly gives them escalated access to your machine.**
> Agents may be able to perform actions outside the regular sandbox through Unity.
> Have a backup strategy, and make sure your work machine is resilient to data loss.

# Installation

## 1. Install the Unity package

- Window ⟶ Package Manager ⟶ `+` ⟶ *Install package from git URL*
- Paste this URL (the `release` branch points at the latest commit that passed all tests)

```text
https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release
```

- Or you can also declare it directly in `Packages/manifest.json` instead:

```json
"dependencies": {
  "dev.tryfinally.conduit": "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release",
```

## 2a. Just use the setup wizard

> [!TIP]
> The Unity package includes a wizard for setting up the MCP server.
> This lets you download the server executable and configure your installed code editors in just a few clicks.

You can find the setup wizard in Unity here:

***Edit → Preferences... → Conduit***

## 2b. Install the MCP server

> [!TIP]
> If you used the setup wizard, you can skip this step.

First, you need to grab the MCP server executable, and put it in your project (for a project-local installation) or some stable location on your computer (for a user-level setup).

<details>
  <summary><b>Windows</b></summary>

Download [`conduit-win-x64.exe`](https://github.com/apkd/Conduit/releases/latest/download/conduit-win-x64.exe) and move it to the location where you want to keep it.
For example, this PowerShell command installs it as `conduit.exe` in your user profile:

```powershell
New-Item -ItemType Directory -Force "$HOME\Conduit"
Invoke-WebRequest "https://github.com/apkd/Conduit/releases/latest/download/conduit-win-x64.exe" -OutFile "$HOME\Conduit\conduit.exe"
Unblock-File "$HOME\Conduit\conduit.exe"
```

</details>

<details>
  <summary><b>Linux</b></summary>

Download [`conduit-linux-x64`](https://github.com/apkd/Conduit/releases/latest/download/conduit-linux-x64), place it in the location where you want to keep it, and make it executable.
For example, these commands install it as `conduit` in `~/.local/bin`:

```bash
mkdir -p "$HOME/.local/bin"
curl -fL "https://github.com/apkd/Conduit/releases/latest/download/conduit-linux-x64" -o "$HOME/.local/bin/conduit"
chmod +x "$HOME/.local/bin/conduit"
```

</details>

<details>
  <summary><b>NixOS</b></summary>

When upgrading these packages, update `version` and `hash` together.
Setting `hash = pkgs.lib.fakeHash;` makes Nix print the current hash during the next build.

##### stdio

Use the static musl release for stdio. It runs directly on NixOS.

```nix
# conduit-stdio.nix
{ pkgs }:

pkgs.stdenvNoCC.mkDerivation rec {
  pname = "conduit";
  version = "0.3.74";

  src = pkgs.fetchurl {
    url = "https://github.com/apkd/Conduit/releases/download/release/conduit-linux-musl-x64";
    hash = "sha256-XBqpjtEXiitdRN7EPCmW1fg2nB63EdwjG2eEAv3L/7Q=";
  };

  dontUnpack = true;

  installPhase = ''
    runHook preInstall
    install -Dm755 "$src" "$out/bin/conduit"
    runHook postInstall
  '';
}
```

Add Conduit and its Unity launch tools to the system profile:

```nix
# configuration.nix
{ lib, pkgs, ... }:

let
  conduit = import ./conduit-stdio.nix { inherit pkgs; };
in
{
  environment.systemPackages = with pkgs; [
    bash
    conduit
    unityhub
    util-linux
  ];

  nixpkgs.config.allowUnfreePredicate = package:
    lib.getName package == "unityhub";
}
```

Configure your editor to use the `conduit` executable (see the editor configuration sections below for more details).

```toml
[mcp_servers.unity]
command = "conduit"
```

##### http

The HTTP server loads OpenSSL when it creates an MCP session.
Streamable HTTP mode needs a patch because the static executable cannot dynamically load OpenSSL on NixOS.
Patch the glibc artifact's ELF interpreter and add OpenSSL to its runtime search path:

```nix
# conduit-http.nix
{ pkgs }:

pkgs.stdenvNoCC.mkDerivation rec {
  pname = "conduit";
  version = "0.3.74";

  src = pkgs.fetchurl {
    url = "https://github.com/apkd/Conduit/releases/download/release/conduit-linux-x64";
    hash = "sha256-mRlqgG2f+XZ1XJzwxi39hm8v3WG9CQ1yMbkogqsoatk=";
  };

  dontUnpack = true;

  nativeBuildInputs = [ pkgs.autoPatchelfHook ];
  buildInputs = [ pkgs.stdenv.cc.cc.lib ];
  runtimeDependencies = [ pkgs.openssl.out ];

  installPhase = ''
    runHook preInstall
    install -Dm755 "$src" "$out/bin/conduit"
    runHook postInstall
  '';
}
```

Run one server in the graphical user session:

```nix
# configuration.nix
{ lib, pkgs, ... }:

let
  conduit = import ./conduit-http.nix { inherit pkgs; };
  conduitUser = "bob";
in
{
  environment.systemPackages = [ conduit ];

  nixpkgs.config.allowUnfreePredicate = package:
    lib.getName package == "unityhub";

  systemd.user.services.conduit = {
    description = "Conduit Unity MCP server";
    wantedBy = [ "graphical-session.target" ];
    after = [ "graphical-session.target" ];
    partOf = [ "graphical-session.target" ];
    unitConfig.ConditionUser = conduitUser;
    path = with pkgs; [
      bash
      util-linux
      unityhub
    ];
    serviceConfig = {
      ExecStart = "${conduit}/bin/conduit --http --url http://127.0.0.1:5080";
      Restart = "on-failure";
      RestartSec = "1s";
    };
  };
}
```

Set `conduitUser` to the account that runs Unity and the MCP client.
Conduit reads the `unityhub` wrapper to find `unityhub-fhs-env`. Its detached launch path calls `bash` and `setsid` from `util-linux`.
After applying the configuration, start the service with `systemctl --user start conduit`; subsequent graphical sessions start it automatically.

Configure your editor to use the HTTP server (see the editor configuration sections below for more details).

```toml
[mcp_servers.unity]
url = "http://127.0.0.1:5080"
```

</details>

<details>
  <summary><b>Manual build instructions</b></summary>

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for your operating system.
Then clone this repository and publish the server:

```bash
dotnet publish Conduit.Server/Conduit.csproj -c Release
```

The executable is written to `Conduit.Server/publish/win-x64/conduit.exe` on Windows or `Conduit.Server/publish/linux-x64/conduit` on Linux.

</details>

## 3. Configure your code editor

> [!TIP]
> If you used the setup wizard, you can skip this step.

Replace the executable paths in the examples below with the path where you installed the MCP server.

<details>
  <summary><h4>Select your editor...</h4></summary>

<details>
  <summary>Codex</summary>

Configure the MCP server in either location:

- **Unity project:** `.codex/config.toml`. Codex loads this file after the project is trusted.
- **User account:** `%USERPROFILE%\.codex\config.toml` on Windows, or `~/.codex/config.toml` on Linux.

##### stdio | Windows (Native)

```toml
[mcp_servers.unity]
command = "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe"
args = []
cwd = "C:\\src\\Conduit"
disabled_tools = []
tool_timeout_sec = 300
enabled = true
```

##### stdio | Windows (WSL)

```toml
[mcp_servers.unity]
command = "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe"
args = []
cwd = "/mnt/c/src/Conduit"
disabled_tools = []
tool_timeout_sec = 300
enabled = true
```

##### stdio | Linux

```toml
[mcp_servers.unity]
command = "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit"
args = []
cwd = "/home/you/src/Conduit"
disabled_tools = []
tool_timeout_sec = 300
enabled = true
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```bash
codex mcp add unity --url http://127.0.0.1:5080
```

##### approve tool calls

To avoid going insane from having to approve every tool call separately:

```toml
[mcp_servers.unity]
tools.discard_scenes.approval_mode = "approve"
tools.execute_code.approval_mode = "approve"
tools.find_missing_scripts.approval_mode = "approve"
tools.find_references_to.approval_mode = "approve"
tools.from_json_overwrite.approval_mode = "approve"
tools.get_dependencies.approval_mode = "approve"
tools.help.approval_mode = "approve"
tools.playmode.approval_mode = "approve"
tools.editmode.approval_mode = "approve"
tools.profiler_browse.approval_mode = "approve"
tools.profiler_overview.approval_mode = "approve"
tools.profiler_record.approval_mode = "approve"
tools.project_settings.approval_mode = "approve"
tools.refresh_asset_database.approval_mode = "approve"
tools.reimport_assets.approval_mode = "approve"
tools.reflect.approval_mode = "approve"
tools.restart.approval_mode = "approve"
tools.run_tests_editmode.approval_mode = "approve"
tools.run_tests_player.approval_mode = "approve"
tools.run_tests_playmode.approval_mode = "approve"
tools.save_scenes.approval_mode = "approve"
tools.screenshot.approval_mode = "approve"
tools.search.approval_mode = "approve"
tools.show.approval_mode = "approve"
tools.status.approval_mode = "approve"
tools.to_json.approval_mode = "approve"
tools.view_burst_asm.approval_mode = "approve"
```

</details>

<details>
  <summary>Claude Code</summary>

Configure the MCP server in either location:

- **Unity project:** `.mcp.json`.
- **User account:** `%USERPROFILE%\.claude.json` on Windows, or `~/.claude.json` on Linux.

The commands below create the **Unity project** configuration. Replace `--scope project` with `--scope user` to configure the **User account** instead.

##### stdio | Windows (Native)

```bash
claude mcp add --scope project --transport stdio unity -- C:\src\Conduit\Conduit.Server\publish\win-x64\conduit.exe
```

##### stdio | Windows (WSL)

```bash
claude mcp add --scope project --transport stdio unity -- /mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe
```

##### stdio | Linux

```bash
claude mcp add --scope project --transport stdio unity -- /home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```bash
claude mcp add --scope project --transport http unity http://127.0.0.1:5080
```

</details>

<details>
  <summary>Claude Desktop</summary>

Claude Desktop keeps one MCP configuration for the **User account**:

- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
- **Linux:** `~/.config/Claude/claude_desktop_config.json`, or the equivalent path under `XDG_CONFIG_HOME`

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

Claude Desktop's Custom Connectors are reached through Anthropic's cloud and cannot connect directly to a server on `localhost`.
Use the local `stdio` configuration above for Conduit.
A publicly reachable HTTPS deployment may be added through **Settings → Connectors** where Custom Connectors are available.

</details>

<details>
  <summary>Cursor</summary>

Cursor uses `mcp.json` with a top-level `mcpServers` object.
Save it in either location:

- **Unity project:** `.cursor/mcp.json`.
- **User account:** `%USERPROFILE%\.cursor\mcp.json` on Windows, or `~/.cursor/mcp.json` on Linux.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>

<details>
  <summary>Windsurf</summary>

Windsurf keeps one MCP configuration for the **User account**:

- **Windows:** `%USERPROFILE%\.codeium\mcp_config.json`
- **Linux:** `~/.codeium/mcp_config.json`

Some older Cascade builds use `%USERPROFILE%\.codeium\windsurf\mcp_config.json` on Windows or `~/.codeium/windsurf/mcp_config.json` on Linux.
Use **Windsurf Settings → Cascade → MCP Servers → View Raw Config** when both files exist.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "serverUrl": "http://127.0.0.1:5080"
    }
  }
}
```

</details>

<details>
  <summary>Cline</summary>

Cline keeps one MCP configuration for the **User account**. The location depends on which version of Cline you use.

For the VS Code extension:

- **Windows:** `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json`
- **Linux:** `~/.config/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json`, or the equivalent path under `XDG_CONFIG_HOME`

For JetBrains and CLI builds:

- **Windows:** `%USERPROFILE%\.cline\data\settings\cline_mcp_settings.json`
- **Linux:** `~/.cline/data/settings/cline_mcp_settings.json`

`CLINE_MCP_SETTINGS_PATH`, `CLINE_DATA_DIR`, and `CLINE_DIR` can relocate the JetBrains and CLI file.
Current Cline uses flat transport fields in each server entry.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": [],
      "disabled": false
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": [],
      "disabled": false
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": [],
      "disabled": false
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "type": "streamableHttp",
      "url": "http://127.0.0.1:5080",
      "disabled": false
    }
  }
}
```

</details>

<details>
  <summary>Kilo Code</summary>

Kilo Code's extension and CLI share the unified `kilo.json`/`kilo.jsonc` format.
Configure the MCP server in either location:

- **Unity project:** `kilo.json`, `kilo.jsonc`, `.kilo/kilo.json`, or `.kilo/kilo.jsonc`.
- **User account:** `%USERPROFILE%\.config\kilo\kilo.json` on Windows, or `~/.config/kilo/kilo.json` on Linux. The `.jsonc` filename also works.

On Linux, `XDG_CONFIG_HOME` can move the **User account** file.

##### stdio | Windows (Native)

```json
{
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe"],
      "enabled": true
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe"],
      "enabled": true
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit"],
      "enabled": true
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcp": {
    "unity": {
      "type": "remote",
      "url": "http://127.0.0.1:5080",
      "enabled": true
    }
  }
}
```

</details>

<details>
  <summary>Continue</summary>

Continue's IDE extensions load MCP files from either location:

- **Unity project:** `.continue/mcpServers/`.
- **User account:** `%USERPROFILE%\.continue\mcpServers\` on Windows, or `~/.continue/mcpServers/` on Linux.

Continue accepts native schema-v1 YAML and Claude-compatible JSON files in either folder.
The `cn` CLI does not currently auto-discover files from either directory.

Create `unity.yaml` in either directory:

##### stdio | Windows (Native)

```yaml
name: Unity MCP
version: 0.0.1
schema: v1
mcpServers:
  - name: unity
    type: stdio
    command: C:\src\Conduit\Conduit.Server\publish\win-x64\conduit.exe
    cwd: C:\src\Conduit
```

##### stdio | Windows (WSL)

```yaml
name: Unity MCP
version: 0.0.1
schema: v1
mcpServers:
  - name: unity
    type: stdio
    command: /mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe
    cwd: /mnt/c/src/Conduit
```

##### stdio | Linux

```yaml
name: Unity MCP
version: 0.0.1
schema: v1
mcpServers:
  - name: unity
    type: stdio
    command: /home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit
    cwd: /home/you/src/Conduit
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```yaml
name: Unity MCP
version: 0.0.1
schema: v1
mcpServers:
  - name: unity
    type: streamable-http
    url: http://127.0.0.1:5080
```

</details>

<details>
  <summary>OpenCode</summary>

Configure the MCP server in either location:

- **Unity project:** `opencode.json`, `opencode.jsonc`, `.opencode/opencode.json`, or `.opencode/opencode.jsonc`.
- **User account:** `%USERPROFILE%\.config\opencode\opencode.json` on Windows, or `~/.config/opencode/opencode.json` on Linux. The `.jsonc` filename also works.

On Linux, `XDG_CONFIG_HOME` can move the **User account** file.
Local MCP commands are arrays whose first element is the executable path.

##### stdio | Windows (Native)

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe"],
      "enabled": true
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe"],
      "enabled": true
    }
  }
}
```

##### stdio | Linux

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "local",
      "command": ["/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit"],
      "enabled": true
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "remote",
      "url": "http://127.0.0.1:5080",
      "enabled": true
    }
  }
}
```

</details>

<details>
  <summary>Gemini CLI</summary>

Configure the MCP server in either location:

- **Unity project:** `.gemini/settings.json`.
- **User account:** `%USERPROFILE%\.gemini\settings.json` on Windows, or `~/.gemini/settings.json` on Linux.

Setting `GEMINI_CLI_HOME` moves the **User account** file to a `.gemini` folder inside that directory.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "cwd": "C:\\src\\Conduit"
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "cwd": "/mnt/c/src/Conduit"
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "cwd": "/home/you/src/Conduit"
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "type": "http",
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>

<details>
  <summary>GitHub Copilot CLI</summary>

Configure the MCP server in either location:

- **Unity project:** `.github/mcp.json` or `.mcp.json`.
- **User account:** `%USERPROFILE%\.copilot\mcp-config.json` on Windows, or `~/.copilot/mcp-config.json` on Linux.

Setting `COPILOT_HOME` moves the **User account** file to that directory.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": [],
      "env": {},
      "tools": ["*"]
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "type": "http",
      "url": "http://127.0.0.1:5080",
      "headers": {},
      "tools": ["*"]
    }
  }
}
```

Interactive alternative:

```text
/mcp add
```

</details>

<details>
  <summary>Visual Studio / GitHub Copilot</summary>

Visual Studio uses `mcp.json` with a top-level `servers` object.
Configure the MCP server in either location:

- **Unity project:** `.mcp.json`.
- **User account:** `%USERPROFILE%\.mcp.json`.

Visual Studio also recognizes `.vs/mcp.json`, `.vscode/mcp.json`, and `.cursor/mcp.json` inside the **Unity project** folder.

##### stdio | Windows (Native)

```json
{
  "servers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "servers": {
    "unity": {
      "type": "http",
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>

<details>
  <summary>VS Code / GitHub Copilot Chat</summary>

VS Code uses `mcp.json` with a top-level `servers` object.
Configure the MCP server in either location:

- **Unity project:** `.vscode/mcp.json`.
- **User account on Windows:** `%APPDATA%\Code\User\mcp.json`.
- **User account on Linux:** `~/.config/Code/User/mcp.json`, or the equivalent path under `XDG_CONFIG_HOME`.

Named profiles use an opaque profile directory, so use **MCP: Open User Configuration** from the Command Palette when a named profile is active.

##### stdio | Windows (Native)

```json
{
  "servers": {
    "unity": {
      "type": "stdio",
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "servers": {
    "unity": {
      "type": "stdio",
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "servers": {
    "unity": {
      "type": "stdio",
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "servers": {
    "unity": {
      "type": "http",
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>



<details>
  <summary>Antigravity</summary>

In the Antigravity IDE, open the Agent side panel and choose **… → MCP Servers → Manage MCP Servers → View raw config**. This opens the user-account configuration. Click **Refresh** in the MCP manager after saving changes.

In Antigravity CLI, use `/mcp` to manage and reload MCP servers.

Antigravity lets you configure the MCP server in either location:

- **Unity project:** `.agents/mcp_config.json` in the folder opened as the workspace.
- **User account:** `%USERPROFILE%\.gemini\config\mcp_config.json` on Windows, or `~/.gemini/config/mcp_config.json` on Linux.

Paths under `%USERPROFILE%\.gemini\antigravity\` or `%USERPROFILE%\.gemini\antigravity-cli\` on Windows, and `~/.gemini/antigravity/` or `~/.gemini/antigravity-cli/` on Linux, are legacy.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": [],
      "cwd": "C:\\src\\Conduit",
      "disabled": false
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": [],
      "cwd": "/mnt/c/src/Conduit",
      "disabled": false
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": [],
      "cwd": "/home/you/src/Conduit",
      "disabled": false
    }
  }
}
```

##### http

```bash
conduit --http --port 5080 --url http://127.0.0.1:5080
```

```json
{
  "mcpServers": {
    "unity": {
      "serverUrl": "http://127.0.0.1:5080",
      "disabled": false
    }
  }
}
```

</details>

<details>
  <summary>Zed</summary>

Zed uses `context_servers` in its settings.
Configure the MCP server in either location:

- **Unity project:** `.zed/settings.json`.
- **User account on Windows:** `%APPDATA%\Zed\settings.json`.
- **User account on Linux:** `~/.config/zed/settings.json`, or the equivalent path under `XDG_CONFIG_HOME`.

##### stdio | Windows (Native)

```json
{
  "context_servers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "context_servers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "context_servers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "context_servers": {
    "unity": {
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>


<details>
  <summary>JetBrains IDEs / Junie</summary>

Junie in JetBrains IDEs and Junie CLI use the same MCP config file format.
Configure the MCP server in either location:

- **Unity project:** `.junie/mcp/mcp.json`.
- **User account:** `%USERPROFILE%\.junie\mcp\mcp.json` on Windows, or `~/.junie/mcp/mcp.json` on Linux.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
      "args": []
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
      "args": []
    }
  }
}
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "url": "http://127.0.0.1:5080"
    }
  }
}
```

</details>

</details>

## Available tools

#### Prototyping basics:
These core tools that cover most situations.
In particular, `execute_code` is vital, as it can run arbitrary C# code without an assembly reload.
It returns the result, logs, exceptions, and compilation errors.
Agents are very proficient at using it for interacting with Unity and debugging code.

- ***`status`***: project and MCP connection state
- ***`playmode`***: enters Play Mode
- ***`editmode`***: enters Edit Mode
- ***`refresh_asset_database`***: imports modified assets, recompiles code
- ***`reimport_assets`***: forces matching assets to reimport
- ***`execute_code`***: runs ad hoc C# code
- ***`detour`***: replaces managed C# method implementations at runtime
- ***`reflect`***: searches loaded C# types and members
- ***`restart`***: starts or restarts Unity Editor

#### Object search, reading, and editing:
Together, these tools enable agents to find, read, and write any asset, GameObject, or component.

- ***`help`***: additional usage instructions for the agent
- ***`search`***: finds objects and assets
- ***`show`***: displays the object in a readable format
- ***`to_json`***: read object in JSON format
- ***`from_json_overwrite`***: overwrite object properties with JSON
- ***`find_missing_scripts`***: scans objects for invalid/deleted scripts
- ***`get_dependencies`***: what assets does this use?
- ***`find_references_to`***: what assets use this?
- ***`save_scenes`***: save current changes for open scenes
- ***`discard_scenes`***: discard current changes for open scenes
- ***`project_settings`***: searches, reads and modifies project settings

#### Testing:
These complete the iteration loop, allowing the agent to validate their work.
By the way: if your project doesn't have tests, *you're doing it wrong*.

- ***`run_tests_editmode`***: run Edit Mode tests
- ***`run_tests_playmode`***: run Play Mode tests
- ***`run_tests_player`***: run player tests
- ***`screenshot`***: captures the game view, scene view, or any other object

#### Profiling:
These inspect runtime performance and Burst output.

- ***`profiler_record`***: capture, save, load, or list profiler data
- ***`profiler_overview`***: summarizes hot frames and samples
- ***`profiler_browse`***: browses profiler sample hierarchy
- ***`view_burst_asm`***: Burst assembly and optimization stats

## Agent instructions

The tool descriptions themselves should be enough to get started. Your coding agent should be able to use Unity out-of-the-box.

If you want to add some additional instructions in your `AGENTS.md` file, you can start with this:

```
Use the Unity MCP tools to prototype solutions, validate code compilation and run tests.
Invoke the `restart` tool in case of instability.
Don't build the Unity solution manually; simply call `refresh_asset_database` after making any code changes.
When dealing with assets and GameObjects, `search`, `show`, `to_json`, `from_json_overwrite`, `find_missing_scripts`, `get_dependencies`, `find_references_to` and `reimport_assets` are your friends.
When working with code, you can use `reflect` to browse types and members, and `view_burst_asm` to validate Burst-compiled code.
Use `help` once to get instructions about the common query format used in `search`, `show`, `Search<T>` calls in `execute_code`, etc.
```
