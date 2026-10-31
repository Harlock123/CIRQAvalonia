# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` explains the engine and the analyses in
depth, and `docs/USER_GUIDE.md` covers driving the editor; this file covers how the code is put together and the
rules that keep it working.

## What this is

CirqAvalonia is an electronic circuit simulator in C# on .NET 10. It has a SPICE-style Modified Nodal Analysis
solver for analog parts and an event-driven scheduler for digital ones. On top sits an Avalonia 12 schematic
editor with a ScottPlot oscilloscope, and there is a `cirq` command line that runs the same engine with no window.
The editor is also an MCP server, so AI agents can build and run circuits in it, and it has an AI Chat panel (F12)
where Claude does so through that server. Releases are self-contained builds for Windows, macOS and Linux.

## Commands

```bash
dotnet build CirqAvalonia.slnx                 # no global.json: any .NET 10 SDK
dotnet test CirqAvalonia.slnx                  # all six test projects, headless
dotnet run --project src/Cirq.UI               # the editor
dotnet run --project src/Cirq.Cli -- --help    # check, baseline, compare, run, netlist, info, version
dotnet run --project src/Cirq.UI -- --mcp      # the MCP server on stdio, headless (--attach: the open editor)
./scripts/build-guide.sh [version]             # docs/USER_GUIDE.md to dist/CirqAvalonia-User-Guide.pdf (needs Chrome or Chromium)
./scripts/publish-all.sh 1.2.0 linux-x64       # self-contained archives in dist/; omit the RIDs for all seven
./scripts/verify-artifacts.sh                  # checks each archive in dist/ holds the right binary
```

## Layout

- `src/Cirq.Core/`: the domain model, with no solver in it.
  - `Topology/` has `Circuit`, `CircuitComponent`, terminals, nets and the netlist builder.
  - `Simulation/` has the stamping contract (`MnaSystem`, `SimulationState`, `SimulationSettings`).
  - `Digital/`, `Probing/` (probes, scope triggers, trace measurements) and `Units/` (SI prefixes, colour codes).
- `src/Cirq.Engine/`: the maths.
  - `Simulation/CircuitSimulator.cs` is the mixed-signal engine: Newton-Raphson per time point, then the
    digital devices, with steps cut to land on queued transitions.
  - The other files there are the analyses (AC, DC sweep, noise, Monte Carlo, pole-zero, stability and more).
  - `Numerics/` (LU solvers, FFT), `Digital/DigitalScheduler.cs` and `Decoding/` (I2C, SPI, UART, CAN, 1-Wire).
- `src/Cirq.Components/`: the part library, one folder per family (`Passive`, `Nonlinear`, `Digital`, `Ics`,
  `Buses`, `Boards` and more).
  - `Serialization/` reads and writes `.cirq` files (indented JSON).
  - `Spice/` imports and exports SPICE models and netlists.
- `src/Cirq.UI/`: the Avalonia editor.
  - `ViewModels/` (CommunityToolkit.Mvvm). `ComponentCatalog.cs` is the palette, and `Examples.cs` holds the
    built-in example circuits.
  - `Views/` has one window per analysis. `Rendering/` draws the schematic, and `Services/` holds settings,
    autosave, export, printing and the `SimulationController`.
- `src/Cirq.Mcp/`: the MCP server agents drive the editor through (ModelContextProtocol.Core). No Avalonia.
  - `CircuitTools.cs` and `SimulationTools.cs` are the tools, as `[McpServerTool]` methods; `CircuitServer.cs` has the
    instructions every agent is told and the argument checks; `Recipes.cs` the saved recipes (MCP prompts).
  - Handlers touch the circuit only through `ICircuitHost.RunAsync` and an `ICircuitSession` (`Hosting.cs`):
    `HeadlessHost` has a circuit of its own, `LiveHost` the open window's, on the UI thread. The palette, the
    examples and the schematic picture come from the host's `ICircuitLibrary`, which the editor supplies.
  - `LiveListener` (a per-user named pipe) and `LiveBridge` (`--mcp --attach`) let outside agents into the editor.
- `src/Cirq.Chat/`: the AI Chat's conversation (`ChatSession`), its Claude client (the Anthropic SDK), the API key
  store and the bridge to the MCP server's tools. No UI.
- `src/Cirq.UI/Agents/`: the editor's side: `EditorLibrary` (the palette, examples and print-coloured PNG),
  `EditorSession` (a window's document, undo and transport as an agent sees them) and `LiveAgents` (the pipe door
  and every window's chat host). The panel is `Views/ChatPanel.cs`.
- `src/Cirq.Cli/`: the `cirq` tool. `CommandLine.Run` takes its writers as arguments, so tests drive it without
  starting a process.
- `tools/Cirq.Docs/`: turns the Markdown guide into print-ready HTML (Markdig). It is never shipped.
- `tests/`: xUnit v2, one project per source project. `Cirq.UI.Tests/Support/HeadlessApp.cs` runs the real `App`
  on Avalonia's headless platform.
- `scripts/`: the build, publish, verify and changelog scripts that CI also calls.

## Rules that keep it working

- **Dependencies run one way.** Core, then Engine, then Components, then Mcp, then Chat, then UI; Cli sits on
  Components. Core holds the stamping contract so parts can describe themselves without knowing the solver. Keep
  simulation logic out of `Cirq.UI`, where it can't be tested without a window.
- **Package versions live in `Directory.Packages.props`.** Central package management is on, so a
  `PackageReference` has no `Version`. Avalonia and ScottPlot move together because both must agree on SkiaSharp
  (see the note in that file).
- **Tests stay on xUnit v2.** UI tests use the base `Avalonia.Headless` package driven by hand. They do not use
  `Avalonia.Headless.XUnit`, which pulls in xUnit v3. Window tests share one session through a collection fixture.
- **Reflection does the plumbing.** The properties panel and the save format both come from
  `ComponentReflection.EditableProperties`, so a new part is editable and saveable with no registry. Mark a
  property `[Operable]` to put it in the CONTROLS panel.
- **Repeatable randomness.** Monte Carlo draws from a seeded `Xorshift` (`MonteCarloRequest.Seed`), not
  `System.Random`, so a seed always gives the same run.
- **Settings and saves.** The app keeps `settings.json`, autosave recovery files, the block library and user
  models under the `CirqAvalonia` folder in `SpecialFolder.ApplicationData`. Tests that touch files use a temp
  folder.
- **Tests redraw the guide's pictures.** Some `Cirq.UI.Tests` (`GuidePlotTests`, `SymbolFigureTests` and others)
  write PNGs into `docs/images/` through `DocPlot`. Expect those files to change after a test run, and commit them
  only when the change is intended.
- **Warnings.** `TreatWarningsAsErrors` is off, but nullable reference types are on in every project.
- **Agent tools.** Every change an agent makes goes through `CircuitTools.Edit`: one undo step, rolled back whole
  if any part of it fails. Runs solve a serialised copy of the circuit on a worker thread, never the editor's own
  simulator, then write a transient's samples back into the probes. A new tool needs a `[Description]` on it and
  every parameter (snake_case names), and a place in `EndToEndTests.ToolNames`. Recipes are run call by call by
  `EndToEndTests`, so a recipe that stops working fails the build.
- **No real Anthropic API in tests.** The chat tests answer from `FakeClaude` (an `HttpMessageHandler` replaying
  scripted streams) with a made-up key; window tests point the settings and the key file at a temp folder. The key
  is only ever shown masked.

## Making a change

- Add tests next to the code you touch: solver accuracy in `Cirq.Engine.Tests`, part behaviour in
  `Cirq.Components.Tests`, view models and windows in `Cirq.UI.Tests`, MCP tools in `Cirq.Mcp.Tests` and the
  chat in `Cirq.Chat.Tests`.
- A new part goes in the matching `Cirq.Components` folder and into `ComponentCatalog.cs` so it appears in the
  palette. Add an example circuit to `Examples.cs` where it helps.
- User-facing behaviour belongs in `docs/USER_GUIDE.md` as well as `README.md`.
- Add a section to `CHANGELOG.md` before tagging a release: the workflow copies it into the release notes.

## CI and releases

`.github/workflows/release.yml` is the only workflow. It runs on a `v*` tag or by hand with a version; ordinary
pushes run nothing. It tests the solution, then builds the PDF guide. After that it runs `scripts/publish-all.sh`
for win-x64/x86/arm64, osx-x64/arm64 and linux-x64/arm64, and verifies each archive. Finally it publishes a GitHub
release with `SHA256SUMS`, using the matching `CHANGELOG.md` section as its notes. A version with a suffix
(`1.2.3-beta.1`) is marked as a pre-release.
