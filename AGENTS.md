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
  `Buses`, `Boards`, `Processors` and more).
  - `Digital/ParallelMemory.cs` is every byte-wide ROM and RAM, built from a pin table (`MemoryChips.cs`);
    `MemoryImage.cs` reads binary, Intel HEX and S-record images, and `Lane` splits one for a 68000's
    pair of byte-wide chips.
  - `Processors/` is the 6502 family: `Mos6502Core` (the instruction set as bus cycles, no circuit),
    `Opcodes6502` (the opcode table the core and the disassembler share), `Processor6502` (the 40-pin
    parts, `Cpu65C02`, `Cpu6502`, and `Cpu6510`/`Cpu8500` in `Cpu6510.cs`, clocked from their PHI2 or
    Φ0 pin), the 6522, 6551 and 6526 (`Cia6526`), the SID (`SidCore`, the voices with no circuit, and
    `SidChip`, the 6581 and 8580 parts), and the bus decoder.
  - The 68000 is there too: `M68000Core` (three partial files: the bus, the addressing, the
    instructions), `Cpu68000` (the DIP-64 part, its S0-S7 states on CLK), `M68000Disassembler`,
    `Cpu68000BusDecoder`, and the peripherals `Duart68681`, `Pit68230` and `Mfp68901`, the two UARTs
    sharing `SerialLine`.
  - And the 6809: `M6809Core` (two partial files: the bus and interrupts, the addressing and
    instructions), `Opcodes6809` (the three opcode pages and the disassembler), `Cpu6809.cs`
    (`Processor6809`, with `Cpu6809` clocked from EXTAL and `Cpu6809E` from E and Q pins),
    `Cpu6809BusDecoder`, and the 6800-family peripherals `Pia6821.cs` (`PeripheralInterfaceAdapter`,
    the `Pia6821` and `Pia6520` parts) and `Acia6850`, which also uses `SerialLine`.
  - And the Z80: `Z80Core` (two partial files: the T-states, interrupts and bus request; the
    instructions), `OpcodesZ80` (the disassembler, decoding by the opcode's bit fields, and the
    patterns the test-side assembler is built from), `CpuZ80` (the DIP-40 part, a T-state per CLK
    cycle), `CpuZ80BusDecoder`, and the Z80 family in `Z80Peripheral` (the bus with no /WR and the
    interrupt daisy chain they share), `Z80Pio`, `Z80Ctc` and `Z80Sio.cs` (`Z80SerialController`,
    the `Z80Sio` and `Z80Dart` parts, on `SerialLine`).
  - And the ATmega328P: `AvrCore` (the instruction set and peripherals, no circuit; its whole state in
    `Registers`) and `Atmega328p` (the DIP-28 part, a breakpoint source that runs ahead of the circuit by a window and
    stops at each output change). Test with `AtmegaTests` and the test-side `AssemblerAvr`; the blink example's
    firmware is a byte array in `Examples.Avr.cs`, checked against the guide's source by a UI test.
  - And the 8085: `I8085Core` (two partial files: the T-states, interrupts, HOLD and reset; the
    instructions), `Opcodes8085` (the disassembler and the patterns the test-side assembler is
    built from), `Cpu8085` (the DIP-40 part, X1 at twice CLK OUT), `Cpu8085BusDecoder`, and Intel's
    peripherals: `Ram8155.cs` (`RamIoTimer8155`, the `Ram8155` and `Ram8156` parts, on the
    multiplexed bus with their own latch), and on `IntelPeripheral` (the demultiplexed bus they
    share) `Ppi8255`, `Usart8251` (on `SerialLine`), `Pit8253.cs` (`IntervalTimer`, the `Pit8253`
    and `Pit8254` parts) and `Pic8259`.
  - `Protoboard/` is the protoboard view's model, with no UI: `Breadboard` (the 830-hole geometry, strips and
    hole names such as `e12` and `T+5`), `BoardPackages` (how each part is built: a DIP by its pin numbers or a
    named-pin table, a TO-92 or TO-220 in its datasheet's leg order, two leads, a header module, or bench
    equipment), `ProtoboardLayout` (the automatic layout from the netlist, honouring `Circuit.Board`'s hand-made
    places first, and the check that rebuilds the connections from the board alone and holds them against the
    netlist), `WireRouter` (each jumper's and lead's path round the part bodies: simple shapes first, then A*
    over a half-pitch grid, pulled tight) and `ProtoboardEdits` (moves by hand, each freezing the rest of the
    board). `SolderedLayout` lays out stripboard, perfboard and a PCB (`Circuit.Board.Kind`): hand-made places first, parts
    placed so each pin lands on copper already carrying its net where it can, the cuts worked out from where the pins
    are, wire links or back wiring for the rest, and the same check. On a PCB `PcbRouter` routes the traces (A* on a
    half-pitch grid, one owner per cell, short jumper hops, a pad-to-pad jumper as the last resort; ground's net index
    is -1, so owners are `Index + 2`) and `PcbGerber` writes the Gerber and Excellon files. `BuildGuide` turns any layout into steps; `FaultFinding`
    chooses, hints at and judges the faults hidden in `Circuit.Faults` (never saved), which only
    `Circuit.BuildSimulationNetlist` (the simulator's) applies; everything else uses `BuildNetlist`, the circuit as
    drawn. The meter reads through `SimulationController.VoltageAt` and `CurrentInto`, which never wait on a running slice;
    its resistance range is `Ohmmeter`, which solves a powered-off copy. `FaultLessons` (UI) are the lessons, their
    best scores in `AppSettings.FaultScores`. The view is
    `Controls/ProtoboardCanvas.cs`, drawn by `Rendering/ProtoboardRenderer.cs` through `ISymbolCanvas`, so the
    export is the same drawing.
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
  store and the bridge to the MCP server's tools. No UI. `ChatUsage.cs` is what it uses: `TokenUsage` from the
  stream's usage, `ChatPricing` (list prices as of a date, overridable from `AppSettings.ChatPrices`), the
  `UsageLedger` behind the totals (`chat-usage.json`, one per app: `App.ChatUsage`) and the `UsageEntry` line
  after each turn. Tests that look at the whole conversation use `Shown()`, which leaves those lines out.
- `src/Cirq.UI/Agents/`: the editor's side: `EditorLibrary` (the palette, examples and print-coloured PNG),
  `EditorSession` (a window's document, undo and transport as an agent sees them) and `LiveAgents` (the pipe door
  and every window's chat host). The panel is `Views/ChatPanel.cs`.
- `src/Cirq.Cli/`: the `cirq` tool. `CommandLine.Run` takes its writers as arguments, so tests drive it without
  starting a process.
- `tools/Cirq.Docs/`: turns the Markdown guide into print-ready HTML (Markdig). It is never shipped.
- `tools/Cirq.Shots/`: draws the guide's palette picture (`docs/images/02-palette.png`, every group closed), the 6502
  bus decode (`48-bus-decode-6502.png`) and its AI Chat screenshots (`docs/images/35-*.png`, `36-*.png`): the real editor
  in a headless window rendered by Skia, with Claude's side replayed from a script and the tool calls run for real.
  `dotnet run --project tools/Cirq.Shots -- docs/images`. Never shipped.
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
- **Tests redraw the guide's pictures.** Some `Cirq.UI.Tests` (`GuidePlotTests`, `SymbolFigureTests`,
  `ToolboxFigureTests` and others) write PNGs into `docs/images/` through `DocPlot`. Expect those files to change
  after a test run, and commit them only when the change is intended.
- **Warnings.** `TreatWarningsAsErrors` is off, but nullable reference types are on in every project.
- **Operating a part.** Double-clicking a part calls `IInteractiveComponent.Interact`. A part with several things
  to operate (the keypad's keys) implements `IPointInteractive` instead, and the canvas hands it the point in its
  own unrotated drawing space (`CircuitCanvas.ToLocal`). Text drawn on a part turned half round is turned back
  upright by `SymbolRenderer`, so a part facing left is no reason to avoid rotating it.
- **Waveforms for a NeoPixel.** Nothing in the library runs code, so a WS2812 strip is driven by a pattern
  generator at 2.5 MHz; `NeoPixelStrip.PatternFor` writes that pattern from a list of frames.
- **Agent tools.** Every change an agent makes goes through `CircuitTools.Edit`: one undo step, rolled back whole
  if any part of it fails. Runs solve a serialised copy of the circuit on a worker thread, never the editor's own
  simulator, then write a transient's samples back into the probes. A new tool needs a `[Description]` on it and
  every parameter (snake_case names), and a place in `EndToEndTests.ToolNames`. Recipes are run call by call by
  `EndToEndTests`, so a recipe that stops working fails the build.
- **Memory images travel in the file.** A memory part's `Contents` is its preset in hex (`@addr:` and
  bytes, zero runs skipped), and `ParallelMemory.Load(MemoryImage)` writes a loaded binary or HEX file
  into it, so a circuit opens complete. A reset puts the preset back; `Contents` reads what is there now.
- **The 6502 core is a sequence of bus cycles.** Each instruction in `Mos6502Core` is an iterator that
  yields once per cycle after setting `Address`, `IsRead` and `DataOut`; the caller does the cycle and
  calls `Complete(data)`. Dummy reads and writes are real cycles and must stay exact. Test the core with
  `Cpu6502CoreTests` (results, per-mode cycles, interrupts, a self-checking program built by the test-side
  `Assembler6502`), and locally against Tom Harte's SingleStepTests (`65x02`: `6502/v1`, `wdc65c02/v1`)
  and Klaus Dormann's functional tests, downloaded outside the repository — neither is committed, and
  no test may need the network. Example ROMs are byte arrays in `Examples.cs`; a UI test assembles the
  source the guide prints and checks the bytes match, so change both together.
- **The 68000 core is a sequence of bus cycles too.** `M68000Core` yields once per bus cycle and once
  per run of internal clocks (`M68000BusAction.Idle` with `IdleClocks`), in the chip's order, with
  the two-word prefetch queue (IRD, IRC) modelled; an odd word access throws inside the iterator and
  `Advance` turns it into the address-error exception. Tests set `PauseBetweenInstructions`, which
  yields a `Boundary` between instructions, so a test sees one instruction's effects before any of
  the next runs; `Cpu68000` leaves it off. Test with `Cpu68000CoreTests` (the test-side
  `Assembler68000` builds the programs), `Cpu68000CircuitTests` (a board with ROM and RAM pairs and a
  74164 DTACK generator, which the peripheral tests reuse), and locally against Tom Harte's
  SingleStepTests (`680x0`: `68000/v1`), downloaded outside the repository and never committed. Where
  the suite and Motorola's manual disagree (ADDQ.L/SUBQ.L to An timing, ASR's carry past the width)
  the core follows the manual; the guide lists the counts. The 68000 examples' ROMs are byte arrays
  in `Examples.M68000.cs`, checked against the guide's source by a UI test.
- **The 6809 core is a sequence of bus cycles as well.** `M6809Core` yields once per E cycle, dead
  cycles ($FFFF reads) and don't-care reads included; `FirstCycle` marks where an instruction (or an
  interrupt sequence) starts, and `Step` runs to the next. The 6809E's LIC and AVMA need the next
  cycle before this one's data: `PeekNext` replays the instruction so far on a copy of the core
  (`TracksNextCycle` keeps what it needs, on for the 6809E only), so a change to the core's cycles
  carries into those pins by itself. Test with `Cpu6809CoreTests` (the test-side `Assembler6809`,
  which reads back everything the disassembler prints), `Cpu6809CircuitTests` (a board with a 2764,
  a 62256, a 74138 and a 7400, which the PIA and ACIA tests reuse; a 7474 makes a 6809E's E and Q),
  and locally against the SingleStepTests-format suite `neetandev/m6809` (`v1`, MAME as its
  oracle), downloaded outside the repository and never committed. The 6809 examples' EPROM images
  are byte arrays in `Examples.M6809.cs`, checked against the guide's source by a UI test.
- **The Z80 core is a sequence of T-states.** `Z80Core` yields once per T-state, having set
  `Kind` (the machine cycle), `State` (T1, T2, Tw, T3, T4, or Ti inside the processor), `Address`
  and `DataOut`; the caller completes it with the data bus's byte when `SamplesData` says the
  T-state takes one, and sets `WaitAsserted` in a T-state with `SamplesWait` to add wait states.
  Internal T-states hold the last address, as the suite records it. `FirstCycle` marks where an
  instruction or interrupt response starts and `Step` runs to the next; `Load` starts one with Q
  and the EI and LD A,I/R state as the instruction before left them. `CpuZ80` turns each T-state
  into its pins on CLK's two edges. Test with `CpuZ80CoreTests` (the test-side `AssemblerZ80`, its
  table built from `OpcodesZ80.Pattern`), `CpuZ80CircuitTests` (a board with a 27C256, a 62256 and
  two 74138s, which the peripheral tests reuse through `Computer.Peripheral`), and locally against
  Tom Harte's SingleStepTests (`z80`: `v1`), downloaded outside the repository and never committed:
  the suite draws /MREQ and /RD for one T-state, so a harness maps each T-state to its convention.
  The Z80 examples' EPROM images are byte arrays in `Examples.Z80.cs`, checked against the guide's
  source by a UI test.
- **The Z80 family shares its daisy chain.** `Z80Peripheral.EvaluateBus` does the bus (a write's
  byte and a read's side effects as /IORQ or /CE goes high), the acknowledge (decided once, as
  /IORQ falls during /M1; the vector put on the bus and the source put under service), the opcode
  watch for ED 4D, /INT and IEO. A chip supplies its sources' `Pending`, `VectorFor` and
  `Acknowledged` (the PIO and CTC forget a request once acknowledged; the SIO keeps its cause until
  it is dealt with). `Z80DaisyChainTests` runs a CTC, a SIO and a PIO in one chain.
- **The 8085 core is a sequence of T-states.** `I8085Core` yields once per T-state, having set
  `Kind` (the machine cycle, as IO/M, S1 and S0 and the strobe say), `State` (T1-T6, Tw, or a
  HALT, hold or reset state), `Address` and `DataOut`; the caller completes it with the bus's byte
  when `SamplesData` says so, sets `ReadyLow` in a T-state with `SamplesReady`, and sets the
  interrupt, HOLD and SID inputs before completing: interrupts are decided from the sample of the
  next-to-last T-state, HOLD from T2 (or T4 of a six-state fetch). RST 7.5 and TRAP latch their
  edges in `SenseEdges`, which `Cpu8085` also calls whenever the pins change. `Cpu8085` turns
  X1's rising edges into CLK OUT and starts each T-state as CLK OUT falls. The V and K flags follow
  the control lines reverse-engineered from the die, not Tundra's CA80C85B sheet; change them only
  with a source. Test with `Cpu8085CoreTests` (the test-side `Assembler8085`, its table built from
  `Opcodes8085.Pattern`; every opcode's T-states against Intel's table), `Cpu8085CircuitTests` (a
  board with a 74373, a 27C256, a 62256 and two 74138s, which the peripheral tests reuse), and
  locally against Ian Bartholomew's 8085 exerciser (`8085EX1`, with a real D8085AH-1's CRCs; it
  masks V and K out), 8080PRE and TST8080 with a CP/M stand-in, downloaded outside the repository and
  never committed. The 8085 examples' EPROM images are byte arrays in `Examples.I8085.cs`, checked
  against the guide's source by a UI test.
- **Intel's peripherals share their bus.** `IntelPeripheral.EvaluateBus` does /CS with /RD and
  /WR: a write's byte taken as /WR rises, a read driven while /RD is low with its side effects as
  it rises (`ReadStarted` for what must happen as it begins), and `Drive` for bytes put on the bus
  without /CS (the 8259A's /INTA answers). The 8155 and 8156 latch AD0-AD7, IO/M and CE
  themselves as ALE falls. RESET, where a chip has it (the 8155, 8156, 8255A and 8251A), is
  active high, from the 8085's RESET OUT.
- **6800-family peripherals act on E.** The 6821 and 6850 latch a write and do a read's side
  effects as E falls, and drive a read while E is high and they are selected, so on a 68000 they
  want /VPA from the decode and /VMA on a chip select. The 6821's port A pull-ups are stamped
  resistors (`PeripheralInterfaceAdapter.PullUpResistance`); the 6850 times its bits from the
  measured period of its TxCLK and RxCLK pins.
- **One 6502 core, several packages.** `Processor6502` builds its pins from a `ProcessorPackage`
  (65C02, 6502, 6510); the core is shared, never copied. The 6510's port lives in `Processor6510`
  through the base's hooks (`ReadInternal`, `WriteInternal`, `EndCycle`, `ResetPort`): a $0000/$0001
  access still runs its bus cycle, so the bus log, the decoder and the RAM all see it.
- **The CIA counts on PHI2; its TOD on its own pin.** `Cia6526` does every register's side effects
  as PHI2 falls, like the 6522. The time-of-day clock counts rising edges on TOD, five or six to a
  tenth, so a test feeds TOD a fast clock rather than 50 Hz. The ports' pull-ups are stamped
  resistors (`Cia6526.PullUpResistance`).
- **The SID's audio is stamped, its voices are not.** `SidCore` runs a cycle per PHI2 fall and
  leaves two mixes, round the filter and into it. `SidChip.StampMatrix` turns them into AUDIO OUT, a
  source at half VDD (stamped as half of VDD's node voltage, so the bias is right from the first
  solve), and into the filter: two ideal integrators whose capacitors are the real parts on the CAP
  pins, so the cutoff is gm/C with gm scaled to the datasheet capacitor. Only the right-hand side
  moves with the voices; the matrix moves when the cutoff, resonance, mode or volume is written.
  Keep it linear — a diode or anything non-linear on the same board doubles the cost of every time
  point, which is why the SID player has no LED.
- **A few changed entries need no refactorisation.** When the matrix differs from the last one
  factored in up to 24 entries — a capacitor's companion as the step length changes —
  `CircuitSimulator.SolveCorrected` solves it by the Woodbury identity from the old factorisation,
  refines it and checks the residual, factoring only if that fails. Below 48 unknowns it always
  factors, as before.
- **Logic families are bases with pin tables.** A 74/4000 part is usually a few lines on a shared base in
  `Digital/` — `MultiGateIc` (with an open-collector option), `DualJkFlipFlopIc`, `ClockedRegisterIc`,
  `SynchronousCounterIc`, `CmosRippleCounterIc`, `DecodedCounterIc`, `ParallelInSerialOutIc`,
  `UniversalShiftRegisterIc`, `OctalBufferIc`, `TtlSevenSegmentDecoderIc`, `AnalogMultiplexerIc` and more:
  add a part by giving its pinout and options, not by copying a class. Open-collector and tri-state outputs
  are `HighImpedance` on `Bidirectional` pins, which the rule check lets share a net. Seven-segment
  patterns live in `SevenSegmentGlyphs`, with and without tails.
- **One-shots time from the real R and C.** `MonostableTimer` (in `Monostables.cs`) discharges the
  capacitor through a stamped resistor, then ends the pulse where an RC charge crosses after K·R·C, so
  the part's `TimingConstant` is the datasheet's K and the resistor and capacitor are components on the
  canvas, as on the 555 and 4060. Judge it across the capacitor, not from the timing pin.
- **Constructor arguments are not saved.** Only public settable properties round-trip, so a part built
  with `new PatternGenerator(3)` reopens with the default four lines; examples must use what a file can
  rebuild (`EveryExampleSurvivesASaveAndReopen` catches it).
- **Runs hand back their end state.** A part implementing `IRunState` (processors, memories, the LCD,
  the CIA and the SID) has the state its copy ended in written back after an agent's `run_transient`.
- **Event stepping.** `SimulationController.SteppableByEvents` turns adaptive stepping on for a circuit with no
  integrating parts and no continuously moving source (the `Continuous` set): the steps then land on events. A new
  source whose output moves between events belongs in that set, and a part that changes outputs on its own (a
  processor) must be an `IBreakpointSource` that always names its next time, or event stepping skips it.
- **Logic speed.** The simulator reuses an LU factorisation while the stamped matrix is unchanged, so a
  part must stamp through `MnaSystem.Add` (never write `Matrix` directly) or the reuse check misses it.
- **The board is drawn from the netlist.** The protoboard is never a second description of the circuit: it is
  laid out from `Circuit.BuildNetlist()` every time, and only hand-made places (`Circuit.Board`, saved as the
  file's `Protoboard` record) are kept. A new part gets a package from `BoardPackages` by itself; add a table
  entry only when its usual package has a pinout the generic rules would get wrong. Every example must lay out
  with `ProtoboardLayout.Matches` true (`Every_example_builds_on_the_board_and_the_board_matches_its_schematic`).
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
