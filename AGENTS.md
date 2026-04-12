Compact project overview. Keep this file up to date.

Project layout:
- `Conduit.Server`: MCP server implementation.
- `Conduit.Tests`: server-side tests.
- `Conduit.Unity`: standalone UPM package root.
- `../ConduitPlayground`: the Unity project for testing the UPM package.
- `run-unity-mcp-e2e.cs`: standalone Unity CLI runner for the MCP EditMode end-to-end test suite.

Tests:
- After editing the Unity project, you should use Unity MCP tools to recompile scripts: mcp__unity__refresh_asset_database
- Run the relevant test suite after making changes to the server or Unity project.
- Server tests: `dotnet test --project Conduit.Tests/Conduit.Tests.csproj -v minimal`
- Unity tests: mcp__unity__run_tests_editmode, mcp__unity__run_tests_playmode (excludes the E2E tests)
- Standalone E2E tests: `dotnet run --file run-unity-mcp-e2e.cs --`

C# conventions:
- Do not write obvious comments; prefer self-documenting code.
- Prefer nested local functions when a helper is used only once.
- Prefer expression-bodied members for one-line methods.
- Drop braces from single-statement `if` and `while` blocks.
- Split long method chains across multiple lines.
- Make classes `static` when they have no instance members.
- Make classes `sealed` when inheritance is not required.
- Do not make classes/members `public` unless necessary.
- Prefer `foreach` over manual indexed loops for readability.
- Do not explicitly write `private` in member declarations.
- Prefer stateless static helpers over object state where practical.
- Use modern .NET 10 features. Compact pattern matching is preferred over procedural code.
- Simplicity is paramount. Avoid complicating the code. Always think carefully until you find the simplest, most elegant solution.

Guidance for contributors:
- Read existing related package code before editing; follow established code style and architecture patterns.
- Focus on writing performance-oriented code that achieves highest possible runtime performance.
- Assume no backwards compatibility is needed.
