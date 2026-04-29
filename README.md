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
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/apkd/Conduit?authorFilter=apkd&label=Commits&labelColor=2C3439&color=EBFF65&logo=git)](https://github.com/apkd/Conduit/commits/master)
[![GitHub last commit](https://img.shields.io/github/last-commit/apkd/Conduit?labelColor=2C3439&color=f97&logoColor=f96&logo=tinder&label=Last%20commit)](https://github.com/apkd/Conduit/commit/HEAD~1)

A Unity MCP server that stays out of the way of your coding agent.

- Robust: survives crashes, restarts, assembly reloads, and handles multiple agents and Unity instances.
- Context-efficient: conserves the agent's context window and saves tokens. Small number of versatile tools.
- Simple setup: one Unity package, one server exe, editor config wizard. No dependencies, no pollution.

> [!CAUTION]
> This package is WIP:
> - No linux support
> - Docs are incomplete

> [!WARNING]
> **Granting an AI agent access to Unity indirectly gives them escalated access to your machine.**
> Have a resilient backup strategy, and make sure your work machine is resilient to data loss.

# Installation

Add the package to your project by Git URL:

```text
https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release
```

You can also declare it directly in `Packages/manifest.json`:

```json
"dependencies": {
  "dev.tryfinally.conduit": "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release",
```

## Automatic setup

> [!TIP]
> The Unity package includes a wizard for setting up the MCP server.
> This downloads the server executable and configures your installed code editors.

***Tools → Conduit → Setup MCP Server***

> [!CAUTION]
> *(automatic setup and tutorial/docs coming soon)*

## Manual setup

<details>
  <summary><h4>Build and editor configuration instructions</h4></summary>

You can either:

- Download the server executable from the [releases page](https://github.com/apkd/Conduit/releases/latest), or...
- Build it by running `dotnet publish`,

Now configure your editor:

<details>
  <summary>Codex</summary>

For a basic `stdio` setup, add this to `config.toml`:

##### stdio | Windows (Native)

```toml
[mcp_servers.unity]
command = "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe"
cwd = "C:\\src\\Conduit"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

##### stdio | Windows (WSL)

```toml
[mcp_servers.unity]
command = "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe"
cwd = "/mnt/c/src/Conduit"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

##### stdio | Linux

```toml
[mcp_servers.unity]
command = "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit"
cwd = "/home/you/src/Conduit"
disabled_tools = []
tool_timeout_sec = 1800
enabled = true
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```bash
codex mcp add unity --url http://127.0.0.1:5080
```

##### Approve tool calls

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
tools.play.approval_mode = "approve"
tools.refresh_asset_database.approval_mode = "approve"
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
```

</details>

<details>
  <summary>Claude Code</summary>

Claude Code adds MCP servers with `claude mcp add`.

##### stdio | Windows (Native)

```bash
claude mcp add --transport stdio unity -- C:\src\Conduit\Conduit.Server\publish\win-x64\conduit.exe
```

##### stdio | Windows (WSL)

```bash
claude mcp add --transport stdio unity -- /mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe
```

##### stdio | Linux

```bash
claude mcp add --transport stdio unity -- /home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit
```

##### http

```bash
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```bash
claude mcp add --transport http unity http://127.0.0.1:5080
```

</details>

<details>
  <summary>Cursor</summary>

Cursor uses `mcp.json` with a top-level `mcpServers` object. You can put it in `~/.cursor/mcp.json` for a global setup or `.cursor/mcp.json` for a project-local setup.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe"
    }
  }
}
```

##### stdio | Windows (WSL)

```json
{
  "mcpServers": {
    "unity": {
      "command": "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe"
    }
  }
}
```

##### stdio | Linux

```json
{
  "mcpServers": {
    "unity": {
      "command": "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit"
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

Windsurf stores MCP servers in `~/.codeium/windsurf/mcp_config.json`.

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

Cline stores MCP settings in `cline_mcp_settings.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
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
      "url": "http://127.0.0.1:5080",
      "type": "streamableHttp",
      "disabled": false
    }
  }
}
```

</details>

<details>
  <summary>Continue</summary>

Continue uses YAML, typically as standalone files under `.continue/mcpServers/`

Create `.continue/mcpServers/unity.yaml`:

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
  <summary>Gemini CLI</summary>

Gemini CLI stores MCP configuration in `~/.gemini/settings.json` for user scope or `.gemini/settings.json` for project scope.

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
      "httpUrl": "http://127.0.0.1:5080"
    }
  }
}
```

Equivalent CLI commands:

##### Local stdio

```bash
gemini mcp add unity /home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit
```

##### HTTP

```bash
gemini mcp add --transport http unity http://127.0.0.1:5080
```

</details>

<details>
  <summary>GitHub Copilot CLI</summary>

GitHub Copilot CLI stores MCP servers in `~/.copilot/mcp-config.json`.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "type": "local",
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
      "type": "local",
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
      "type": "local",
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
  <summary>VS Code / GitHub Copilot Chat</summary>

VS Code uses `mcp.json` with a top-level `servers` object. For workspace scope, put it in `.vscode/mcp.json`; for user scope, open the user MCP configuration from the Command Palette.

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

You can get to the config file from **Manage MCP Servers → View raw config**.

##### stdio | Windows (Native)

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
      "args": [],
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
      "args": [],
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
      "args": [],
      "cwd": "/home/you/src/Conduit"
    }
  }
}
```

</details>

<details>
  <summary>Zed</summary>

Zed uses `context_servers` in its settings. For project scope, put this in `.zed/settings.json`; for user scope, add it to your user settings.

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
  <summary>Roo Code</summary>

Roo Code stores global MCP configuration in `mcp_settings.json`. For project-local setup, create `.roo/mcp.json`.

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
conduit --http [--port 5080] [--url http://127.0.0.1:5080]
```

```json
{
  "mcpServers": {
    "unity": {
      "type": "streamable-http",
      "url": "http://127.0.0.1:5080",
      "disabled": false
    }
  }
}
```

</details>


<details>
  <summary>JetBrains IDEs / Junie</summary>

Junie in JetBrains IDEs and Junie CLI use the same MCP config file format. Use `~/.junie/mcp/mcp.json` for user scope or `.junie/mcp/mcp.json` in the project root for project scope.

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

#### The basics:
These core tools that cover most situations.
In particular, `execute_code` is vital, as it can run arbitrary C# code without an assembly reload.
It returns the result, logs, exceptions, and compilation errors.
Agents are very proficient at using it for interacting with Unity and debugging code.

- ***`status`***: project and MCP connection state
- ***`refresh_asset_database`***: imports modified assets, recompiles code
- ***`execute_code`***: runs ad hoc C# code
- ***`restart`***: starts or restarts Unity Editor

#### Object search, reading, and editing:
Together, these tools enable agents to find, read, and write any asset, GameObject, or component.

- ***`search`***: finds objects and assets
- ***`show`***: displays object properties
- ***`to_json`***: read object in JSON format
- ***`from_json_overwrite`***: overwrite object properties with JSON
- ***`find_missing_scripts`***: scans objects for invalid/deleted scripts
- ***`get_dependencies`***: what assets does this use?
- ***`find_references_to`***: what assets use this?
- ***`save_scenes`***: save current changes for open scenes
- ***`discard_scenes`***: discard current changes for open scenes

#### Testing:
These complete the iteration loop, allowing the agent to validate their work.
By the way: if your project doesn't have tests, *you're doing it wrong*.

- ***`run_tests_editmode`***: run Edit Mode tests
- ***`run_tests_playmode`***: run Play Mode tests
- ***`run_tests_player`***: run player tests
- ***`screenshot`***: captures the game view, scene view, or any other object

## Agent instructions

The tool descriptions themselves should be enough to get started.

If you really want to, you can include something like this in your agent instructions:

```
Use the Unity MCP tools to prototype solutions, validate code compilation and run tests.
Invoke the `restart` tool in case of instability.
Don't build the Unity solution manually; simply call `refresh_asset_database` after making any code changes.
When dealing with assets and GameObjects, `search`, `show`, `to_json`, `from_json_overwrite`, `find_missing_scripts`, `get_dependencies` and `find_references_to` are your friends.
```
