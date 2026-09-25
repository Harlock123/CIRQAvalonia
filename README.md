# CirqAvalonia

[![Release](https://github.com/Harlock123/CIRQAvalonia/actions/workflows/release.yml/badge.svg)](https://github.com/Harlock123/CIRQAvalonia/actions/workflows/release.yml)

An electronic circuit analyzer and mixed-signal simulator: a SPICE-style Modified Nodal Analysis
solver for the analog side, an event-driven scheduler for the digital side, and an Avalonia
schematic editor with a live oscilloscope on top.

![The CirqAvalonia editor: component palette on the left, schematic canvas in the centre, properties panel on the right, and the oscilloscope showing the input square wave and the capacitor charging against it](docs/images/01-overview.png)

**[Read the User Guide](docs/USER_GUIDE.md)** for how to drive the editor — also attached to every
[release](../../releases) as a printable PDF. What follows is how it works underneath.

```
CirqAvalonia.slnx
├── src/
│   ├── Cirq.Core/         Domain model: components, terminals, nets, MNA stamping contract
│   ├── Cirq.Engine/       LU solver, Newton-Raphson, transient loop, digital event scheduler
│   ├── Cirq.Components/   Component library (RCL, transistors, diodes, thyristors, 741, 555, 74xx/4000, displays, boards)
│   └── Cirq.UI/           Avalonia editor, schematic canvas, inspector, ScottPlot scope
└── tests/
    ├── Cirq.Engine.Tests/      Solver accuracy against closed-form solutions
    ├── Cirq.Components.Tests/  Behavioural tests for every analog and logic device
    └── Cirq.UI.Tests/          View models, controllers, and every window opened for real
```

`Cirq.Core` holds the stamping contract (`MnaSystem`, `SimulationState`) so components can
describe themselves to the solver without depending on it. The dependency only ever runs
Core ← Engine ← Components ← UI, which keeps the whole engine headless-testable.

## Running

```bash
dotnet run --project src/Cirq.UI     # the editor
dotnet test                          # 3428 tests
./scripts/build-guide.sh             # the user guide as a PDF
```

Targets .NET 10. The UI uses Avalonia 12 and ScottPlot 5.1; those two versions are pinned
together in `Directory.Packages.props` because they must agree on SkiaSharp (see the note there).

Prebuilt binaries for every supported platform are on the
[releases page](../../releases) — self-contained, so there is no runtime to install first.
Download, extract, run. The user guide is there too, as a PDF.

`build-guide.sh` renders `docs/USER_GUIDE.md` into `dist/`: a small tool turns the Markdown into
one HTML file with a title page, a two-column contents and a stylesheet written for paper, and a
headless browser prints it. The Markdown stays the single source, so the PDF cannot drift from the
guide people read on GitHub — and the browser does the hard part, which is pagination, widow and
orphan control, shaping the Greek and the box-drawing characters, and keeping a table row off a
page boundary. `PAPER=Letter` for US paper instead of A4.

**On macOS the build is not signed or notarized**, so Gatekeeper blocks the first launch. You get
"cannot be opened" or "is damaged", which reads like a bad download rather than a policy decision.
Either route below clears it, permanently — you only do this once per download.

**Without a terminal:**

1. Double-click `CirqAvalonia`. macOS refuses, offering only **Done** or **Move to Bin**. Choose
   Done — the refusal is the step that arms the next one.
2. Open **System Settings → Privacy & Security** and scroll to **Security**. An **Open Anyway**
   button is now there, naming the app.
3. Click it, authenticate, and launch the app again.

If there is no button, launch the app once more first: it only appears after a blocked attempt, and
it lapses after about an hour.

**Or from a terminal:**

```bash
xattr -d com.apple.quarantine CirqAvalonia
./CirqAvalonia
```

Right-clicking in Finder and choosing **Open** used to work as well, and still does through
macOS 14. Apple removed that bypass in macOS 15 (Sequoia), which is why the Settings route is the
one written out above.

This is required on Apple Silicon, not merely advisory. The only real fix is signing and
notarization, which needs a paid Apple developer account.

**Signing is the intention, just not yet.** Leaving it unsigned is a decision rather than an
oversight. Notarization is per build, not per project: every archive of every release has to go to
Apple, come back, and be stapled individually. So the cost is not one afternoon of setup — it is a
step welded onto every release, and releases here currently land within hours of each other. Apple
is not quick at its end either. Until the pace settles down, the one-time click above is the better
trade for everyone. The plan has not changed — a Developer ID certificate, hardened runtime and a
notarized `.app` bundle — and when it happens this whole section goes away.

On Linux, mark it executable if your extractor dropped the bit: `chmod +x CirqAvalonia`.

## Building releases

```bash
./scripts/publish-all.sh              # all seven platforms, version 0.1.0
./scripts/publish-all.sh 1.2.0        # explicit version
./scripts/publish-all.sh 1.2.0 win-x64 linux-x64
```

Produces a self-contained single-file executable per platform in `dist/`, archived with the docs
and a `SHA256SUMS`. All seven cross-build from any one machine, because the runtime packs come from
NuGet.

`./scripts/verify-artifacts.sh` then checks that each archive really holds a binary for the
platform its name claims — a cross-build that targets the wrong architecture still exits 0 from
`dotnet publish`, so the only way to know is to look at the binary.

Releases are cut by [GitHub Actions](.github/workflows/release.yml), which runs the test suite,
builds all seven targets in parallel, verifies each one, and publishes them to a release:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Or run the workflow by hand from the Actions tab with a version. The workflow calls the same
scripts, so CI and a developer's machine cannot drift apart. A version with a pre-release suffix
(`0.2.0-beta.1`) is published as a prerelease, and re-running for an existing version replaces its
assets rather than failing.

Release notes come from [`CHANGELOG.md`](CHANGELOG.md): the section matching the version being
built is lifted into the release page above the download table, so **add the entry before
tagging** — the workflow reads the changelog at the tagged commit. A version with no entry warns
and publishes with the download table alone, rather than throwing away a build that passed over
missing prose.

```bash
./scripts/changelog-section.sh 0.2.0     # exactly what the release will carry
```

| Target | Platform |
| --- | --- |
| `win-x64` / `win-x86` / `win-arm64` | Windows, 64-bit / 32-bit / ARM |
| `osx-x64` / `osx-arm64` | macOS, Intel / Apple Silicon |
| `linux-x64` / `linux-arm64` | Linux, 64-bit / ARM |

Trimming is deliberately **off**. The properties inspector and the `.cirq` serializer both discover
component parameters by reflection, so a trimmer would strip properties nothing appears to
reference and break both at runtime rather than at build time — a silent failure is not worth the
saved megabytes. Single-file compression is on instead, which roughly halves the download.

ReadyToRun is also off, because it needs a cross-compiler matching the target architecture and that
would tie the script to whatever machine it runs on.

The macOS builds are neither signed nor notarized, so Gatekeeper quarantines them on download; each
macOS archive carries a `MACOS-FIRST-RUN.txt` with the one command that clears it.

## The engine

**Analog.** Each time point is stamped into a dense MNA system and solved by LU decomposition with
partial pivoting. Reactive elements use companion models discretised with the trapezoidal rule,
falling back to Backward Euler on the first step after a discontinuity to damp the transition.
Inductors and transformers are solved in branch-current form, so mutual inductance is just an
off-diagonal coefficient and a DC inductor is simply a short.

**Non-linear.** Diodes, transistors, op-amps and regulators are iterated with damped Newton-Raphson.
Bipolars use the Ebers-Moll transport model with an Early-effect output conductance; MOSFETs use the
level-1 square law with drain/source reversal and an intrinsic body diode. The diode
runs SPICE's `pnjlim` limiter and carries a real internal ohmic node, so the exponential is
linearised about the true junction voltage rather than the terminal voltage. The operating point
falls back to Gmin stepping when a circuit will not converge directly.

![The inverting amplifier example: an LM741 on plus and minus 15 volt rails with a 10k input resistor and 100k feedback resistor, the scope showing a small input sine and the inverted output ten times larger](docs/images/09-opamp.png)

**Digital.** Logic devices are event-driven. Outputs are stamped as Thevenin sources at the logic
family's VOH/VOL, inputs are classified against VIH/VIL, and transitions are queued at
`t + tpd` in a timestamp-ordered priority queue. The transient loop truncates its step so a time
point lands exactly on the next queued transition or waveform edge, then resolves zero-delay
propagation as delta cycles before accepting the step. That is what lets a 555 timer's comparators,
latch and discharge transistor all interact with the RC network around them in one solve.

![The digit counter example: a 7490 decade counter driving a 7447 decoder into a seven-segment display, with the QA and QD outputs plotted on a stacked scope](docs/images/05-digital.png)

**Analyses.** A good many, and they answer genuinely different questions — the guide's
[index of them](docs/USER_GUIDE.md#which-analysis-answers-which-question) is the map. The four the
rest are built on: the **operating point** settles
the circuit as though it had been powered for ever. **Transient** walks it through time, which is
what the scope shows. **Frequency response** linearises about the bias point and sweeps a small
signal across a range, which measures what the circuit *does* to each frequency. **DC sweep**
steps a parameter and re-solves the operating point at each value, which draws the curves parts
are specified by — a diode's exponential, a transistor's output characteristic, a panel's maximum
power point. Stepping a second parameter as well turns the last of those into a curve tracer.

To those, three measurements of the traces themselves: the scope's **automatic readouts** (peak to
peak, mean, RMS, frequency, duty cycle, 10–90 % rise time) with a pair of draggable time cursors;
an **FFT** of whatever has been recorded; and **protocol decoding**, which reads the traces as
I²C, SPI, UART, 1-Wire or CAN rather than as edges. The response and the FFT are easy to confuse
and should not be: one is a measurement of the circuit, the other a measurement of a signal.

The whole circuit sits at an **ambient temperature**, saved with the file and sweepable like any
other parameter. Every semiconductor junction reads it, so it moves forward drops, transistor gains
and leakage together — a silicon diode falls about 2 mV/°C, which is the figure every
temperature-compensated circuit is built around. Junctions, MOSFETs and op-amps
carry it — a power MOSFET's on-resistance climbs about 80 % from 25 °C to 125 °C, which is what
every derating curve is about. So do regulators, whose output falls about a millivolt a degree as
the die warms, from the room and from its own dissipation together. The **TL431**'s bandgap is
*bowed* rather than sloped: both ends of the range sit below the middle, which is the whole reason
the part costs more than a zener and why its datasheet quotes a deviation band instead of a
coefficient.

Part of a circuit can be drawn as a **block**: select it, press Ctrl+G, and it becomes one symbol
with a pin wherever a wire crossed the boundary. The hierarchy is flattened before anything is
solved, and the parts are moved inside rather than copied, so a block changes nothing — not the
answer, not a probe attached to something inside it, and not what you get back on ungrouping.
**Printing** (`Ctrl+P`) lays the schematic, the traces and the parts list onto real sheets — a page
each, fitted inside the margins, with a header naming the circuit and the date. It prints in ink
whatever theme you are using, since a dark theme's light strokes would come out blank on white
paper. Avalonia has no print API, so the document is produced as a page-sized PDF and handed to the
system's own print path; the dialog says whether that will reach a printer or a viewer.

Export is five picture formats plus two text ones: a **SPICE netlist**, so a circuit drawn here
can go to ngspice or LTspice for an analysis this engine does not do, and **CSV** of the recorded
traces for a spreadsheet. The netlist names the parts it could not carry rather than omitting them
silently. Models can be **imported from SPICE cards** — paste a `.model` line off a datasheet and the part
is available wherever a built-in one is, kept between runs. **Circuits travel**: a saved file
carries the cards for the imported models it uses, so it opens complete on a machine that never
had them — only what the circuit uses, only what was imported, and never overwriting a model of
that name you already have, which is reported instead. A block can be **saved to a library**
and placed again, here or in another circuit — as a copy
rather than a reference, which the docs say plainly rather than implying a link that is not there.
**Notes, headings and boxes** can be put on the drawing too, and exports carry them.

The palette has a **Find a part** box (`Ctrl+F`): 188 components in 16 groups is more than anybody
browses, and it matches on the name, on what the part does — `shift register` finds the 74164 — and
on the group. Enter arms the first match, so a part can be found and placed without the mouse.

**Worst-case analysis** sits beside the tolerance one and answers the other question: not what a
few hundred random builds did, but what the circuit can *ever* do. The corner where every part is
at its extreme the same way is one combination out of 2ⁿ, which random sampling never finds — so
this finds it directly, in a solve per part rather than 2ⁿ, and gives the recipe for each extreme.
The DC sweep also runs **backwards**: say what reading you want and it finds the value that gives
it, along with the nearest E24 part you can actually buy and what that one achieves.

**SPICE subcircuits import too**, not only `.model` cards. A `.model` describes one device; anything
more interesting than a transistor — an op-amp, a regulator, a reference — is published as a
`.subckt`, a pin list and a little netlist. One comes in as a **block**, which is already a pin
list and a little netlist, already flattens before the solve and already travels inside a saved
file. The pin order is the interface, and an element there is no part for stops the import and is
named rather than quietly left out of a block that would then lie about what it is. **VCVS and
VCCS** are parts in their own right now, which is what makes the macromodels work.

**Stability analysis** answers the question a gain plot cannot: will it oscillate. Put a **Loop
Probe** in the feedback path — a piece of wire everywhere else, so it changes no other answer — and
`Ctrl+F7` gives the loop gain, the **phase margin**, the gain margin and the crossover, with the
verdict in words. The break is small-signal only, so the operating point stays the one the working
circuit has.

**Noise analysis** measures the floor under everything: output noise density in volts per root
hertz, the total in RMS over a band, and — the half that changes what you do — a ranking of every
generator in the circuit by what it contributed. Resistors bring Johnson noise, junctions bring
shot noise, MOSFET channels bring their own, and an op-amp brings its input-referred voltage and
current noise — which in a circuit built around one are usually the whole answer. **Flicker** noise
rises towards DC on the active devices, with the corner as an editable property, because a
bipolar's few hundred hertz against a MOSFET's hundred kilohertz is the reason a low-frequency
front end is built out of bipolars.

**Impedance** measures what the circuit looks like from one point rather than between two: one amp
of small-signal current in at a probe, and the volts that appear are the ohms. Every other source is
silenced for the sweep, which is what makes it the Thévenin impedance rather than a number with the
circuit's own signal mixed in. The phase says which kind of resonance you are at — a decoupling
capacitor's **series resonance**, above which it is not a capacitor any more but the loop of wire it
is soldered into, is the most useful number on the plot.

**Poles and zeros** give the circuit's own natural frequencies. Stability says a loop has eight
degrees of phase margin; this says why — a conjugate pair at 145 kHz with a Q of seven. The
small-signal system is `G·x + C·dx/dt = 0`, so the poles are the roots of `det(G + sC)`, found as
the eigenvalues of `(G + σC)⁻¹C` with a general real eigensolver written for it. Zeros come from the
**bordered system matrix** rather than the textbook shortcut of "the natural frequencies with the
output shorted", because the shortcut silently loses zeros at the origin and every AC-coupled stage
has one. A transmission line is refused rather than approximated: a delay has infinitely many poles.

**Self-heating** closes the loop every temperature model left open. Give a MOSFET, a bipolar or a
diode a thermal resistance in degrees per watt and its die stops sitting at ambient: on-resistance
climbs, a bipolar draws more as it warms, a rectifier's drop falls. The update is an outer loop
rather than a perturbation inside Newton, so the power it works from came from a settled circuit.
Runaway stops at the junction temperature the part is rated for and says so, rather than the solver
failing with a message about time steps.

**Requirements** are the other half of a simulator. Every analysis here produces numbers; a
requirement is a name, one of those numbers and a limit, held against what the circuit did and
saved with the file. The answer is three-way — met, not met, or nothing to measure — because the
third is not the second. Thirteen quantities, including the ones a datasheet is actually written
in: **overshoot**, **settling time** to two percent, fall time, pulse width and slew rate. The first
two are refused for anything that is not a step, because the arithmetic would happily produce an
overshoot for a sine and it would mean nothing.

**Timing between two traces** — propagation delay, skew, setup and hold — is the other half of a
mixed-signal simulator's measurements. Everything else here is about a single waveform, and the
number every logic datasheet leads with is not: a delay is the gap between one signal moving and
another moving because of it. Skew is matched nearest-edge with a half-cycle guard, because pairing
purely by nearest lets an edge whose partner falls past the end of the capture pair with the
previous cycle's, and two traces twelve nanoseconds apart come back as nearly a whole period.

**Parameters** let the circuit name a number, so that several parts are set from one place: type `=`
and the name into any of a part's numbers — `=Rf`, `=Rf / 10`, `=1 / (2 * pi * R * fc)`. A design is
a handful of choices and a great many consequences of them, and typed as literals the consequences
stop being consequences the first time a choice changes. The expression language is the one the
scope already had for trace arithmetic, reused rather than reimplemented; underneath, a bound
setting is still a plain number on the part, so the solver and 188 component types know nothing
about it.

**A baseline** is the rest of the answer. Requirements assert what somebody thought to assert; a
baseline records the whole of what the circuit produced, so that an edit which breaks something
three chapters away shows up as a change rather than as something noticed six weeks later. The
shape is compared as well as the measurements — a sine and a triangle of the same peak to peak have
the same minimum, maximum and frequency — and each measurement is judged against a scale that means
something rather than against itself, because the mean of a symmetric waveform is numerically zero
and two runs of the same circuit differ by several percent of it.

**Ratings** gather every limit in the circuit into one list. A part fails three ways — too hot, too
many volts across it, too many amps through it — and each is checked against what it is sold for,
alongside the die temperature and whatever the part itself has to say. Thirty-two component types
already reported their own violations and those reached a hover card and a red ring about a dozen
symbols bothered to draw, so a part could be complaining and drawing nothing at all.

**Peaks for the limits and averages for the heat**, in one walk. Watts are thermal, so what matters
is the mean over long enough for the part to warm up — which is why a MOSFET survives a pulse that
would destroy it held on. Volts are not: a dielectric breaks down at the instant the peak arrives,
and averaging is exactly the wrong thing to do to it. A capacitor is not counted as dissipating at
all — it stores and returns its energy rather than spending it, and nothing in the topology can tell
it from a resistor, so the parts that dissipate say so.

**Live values** write each net's voltage and each part's current onto the schematic, and onto the
hover card whether or not the annotation is on. Nothing ambiguous is shown: a package with ten pins
has no single current through it, ground is zero everywhere by definition, and anything below a
microvolt is written as a plain zero rather than dressed up as `450pV`.

**Current flow** puts moving dots on the wires, at a rate set by what each is carrying and in the
direction it is going. Logarithmic, because a microamp of base current beside an amp of collector
current would otherwise be indistinguishable from stopped. Where a junction divides the current the
wires stay dark rather than being guessed at.

**Arbitrary stimulus.** A waveform source plays back a list of points — SPICE's PWL source — and
reads a scope CSV or a WAV straight into one; a pattern generator drives four logic lines from a
written sequence, a row per line and a character per step. Every corner and every step boundary is a
solver breakpoint, which is what stops the transient loop walking over a two microsecond glitch
nothing declared.

The scope can **keep a reference**: take a copy of what is on screen, change the circuit, and see
both curves on the same axes — because what you are looking for after an edit is the difference
between them. It also takes **arithmetic on the traces** — `Out / In`, `db(Out / In)`,
`{DC out} - {AC in}`, `I ^ 2 * 220` — and adds the result as its own trace.

A copy of the circuit is **autosaved** every minute while there are unsaved changes, and offered
back on the next start.

**The sheet can be edited without a mouse.** Arrows move the selection by a grid square and by one
unit with Shift, Tab steps between parts — bringing one into view only when it is not already there
— and Enter drops the armed part in the middle of the view, so with `Ctrl+F` to search the palette a
part can be found and placed without a pointer.

**Click a wire or a pin and the whole net lights up** — every wire on it, a dot on every pin, and
the status bar naming what is on it. It comes from the netlist the solver uses rather than the
lines as drawn, so net labels are resolved and blocks opened out: a typo'd label shows up at once,
because the other end stays dark.

**Stepping a parameter across a transient** runs the whole circuit once per value and overlays the
results — three capacitor values and the ringing that goes with each, four gate resistors and four
switching edges. A DC sweep says where a circuit settles; this says how it gets there, which is the
question people actually have. Every pass starts from the same conditions, so the curves differ
only by the value.

The spectrum window also measures **distortion**: THD, THD+N, and the harmonics in dB relative to
the carrier. An amplifier at one percent looks exactly like a sine on the scope, so the figure is
the only way to know — and *which* harmonics is the diagnosis: odd means the distortion is
symmetric, like both rails clipping; even means it is lopsided, like a half-wave rectifier.

The scope also plots **one trace against another** instead of against time, which draws an I-V
curve, a transfer characteristic or a hysteresis loop while the circuit runs.

Probes measure voltage, current, logic level, the **difference** between two points, or the
**power** those two things multiply to. And **tolerance analysis** rebuilds the circuit a few
hundred times with its parts drawn from their tolerance bands, which is the only analysis here
that asks whether a design works with the parts you can buy rather than the ones in the drawing.

## Initial conditions

The t=0 state has two modes, and both actually solve the network:

- **Bias point** (default) settles the circuit as though it had been powered for ever: capacitors
  are open circuits, inductors are shorts.
- **Use initial conditions** pins each capacitor to its initial voltage and each inductor to its
  initial current (zero unless stated), so a source applied at t=0 is a genuine step.

Because the network is solved either way, an unsolvable topology — a voltage-source loop, a missing
ground — is reported when the circuit is compiled rather than silently producing zeros.

## Verification

Tests measure the solver against closed-form answers rather than recorded output — a second-order
step overshoots by `exp(−πζ/√(1−ζ²))` and a first-order one settles in `τ·ln(49)`, so the expected
figure is arithmetic and not a recording of what this code happened to produce.

The interface is **driven rather than reasoned about**: every window is opened on a headless
Avalonia platform, clicked with a real pointer and typed at with real keys. That matters more than
it sounds, because a raised `Click` event does not invoke a button's command — a test that raises
one passes against a button that would never have fired.

| Area | What is checked |
| --- | --- |
| Transient accuracy | RC step response vs `V₀(1 − e^(−t/RC))`; trapezoidal error quarters when the step halves |
| Reactive elements | RL time constant, series RLC ringing frequency within 5% of `1/(2π√LC)` |
| Diode | Datasheet forward drop, ~100 mV/decade slope, zener breakdown, KCL consistency, convergence from 1 V to 100 V |
| LM741 | Closed-loop gain, virtual ground, rail saturation, open-loop gain, slew-rate limiting, −3 dB corner at GBW/gain |
| NE555 | Astable frequency vs `1.44/((R1+2R2)C)` across four R/C combinations, duty cycle, supply independence |
| 74xx | Truth tables end-to-end through the analog domain, 7474/7476 edge triggering and async clear, 7490 BCD sequence, 74138 one-hot decode, 74151 addressing, shift register load and clock inhibit |
| LM339 | Per-channel comparison, wired-AND when outputs are tied, release when unpowered |
| Regulators | Line and load regulation, dropout, current limiting, LM317 divider programming |
| BJT | Beta in forward active, saturation, cutoff, exponential Vbe, PNP mirroring, KCL at all three terminals |
| MOSFET | Square-law saturation current, triode switching, zero gate current, reverse conduction, body diode |
| JFET | Full I_DSS at zero gate drive, square law across the range, pinch-off, vanishing gate current, P-channel mirroring |
| Thyristors | SCR blocking and firing, staying on after the gate drive goes away, dropping out when the anode current is interrupted, reverse blocking; triac firing on both half cycles and turning itself off at every zero crossing; diac breakover in either polarity |
| Noise | The RMS coming out being the RMS asked for and centred on zero, the same seed giving the same noise and a different one giving different, a sample held for its whole interval rather than redrawn every time point, and the amplitude scaling as asked |
| Hysteresis | A clean ramp crossing a comparator's threshold once, the same ramp with noise on it chattering, and a feedback resistor putting it back to one — which is what the LM311, LM393 and 74HC14 are for |
| Output swing | A rail-to-rail part reaching within a whisker of both supplies, an LM358 reaching the bottom one but not the top, an LM741 reaching neither, all three agreeing halfway up, and a 741 unable to put out one volt on a single five volt supply. Plus a matrix of every headroom pair against every supply arrangement, since a symmetric part on a split supply hides the cases that matter |
| Live controls | Building the panel changing nothing and raising no change events, working a control moving its component and reporting it, the panel following a part worked from the canvas without reporting that as an edit, a range becoming a slider and a six-decade one becoming a logarithmic slider, a whole-number property being written a whole number, and the panel following parts placed and removed |
| H-bridge | Driving forward and reverse with the current reversing too, both inputs the same braking with nothing across the motor, disabling coasting rather than braking, the interlock stopping shoot-through and its absence making a short across the supply, unwired inputs reading low, and the body diodes clamping an inductive kick |
| AC analysis | An RC filter turning over at 1/(2πRC) and falling twenty decibels a decade past it with ninety degrees of lag, a high-pass leading instead, an LC tank peaking at 1/(2π√LC), an op-amp rolling off at its gain-bandwidth product over the noise gain, a quarter-wave stub looking like a short, a ferrite bead peaking where its datasheet says, a supply being a short circuit to a small signal and a source given an AC magnitude driving the sweep, and a resistive divider being flat across nine decades |
| PLL | The VCO range falling out of R1 and C1, the loop pulling the oscillator onto signals across its range and following them as they move, reporting lock on pin 1, a phase-frequency detector capturing from anywhere in range where an exclusive-OR only captures what is already close, a signal outside the VCO's range never being caught, and inhibit stopping the oscillator |
| Analog multiplier | The product over ten volts across the quadrants, the Z input summing, squaring when the inputs are tied, clipping at the rail and saying so, and an envelope that follows the modulation |
| Current sensor | The current through the shunt and the voltage on the load, the power as their product, a rail above the common-mode range being flagged rather than reported, a larger shunt reading better and wasting more, the registers in the datasheet's own units, over-range stopping rather than wrapping, and the reading coming back over the bus |
| RS-485 | A bit crossing as a difference rather than a voltage against ground, a common-mode offset on the pair changing nothing until it leaves the receiver's window, releasing the driver letting go of the bus entirely, an idle bus without biasing being flagged rather than trusted, failsafe and bias resistors each fixing it, and a terminator loading the driver far more than the receivers do |
| Varactor | The capacitance following the junction law across the range, a hyperabrupt profile buying ten to one where an abrupt one gives two, a tuned tank whose resonance rises with the bias and lands where 1/(2π√LC) says with the capacitance that bias gives, and losing reverse bias being reported rather than quietly wrong |
| Common-mode choke | Two modes seeing completely different inductances, the leakage being whatever the coupling missed, kilohms to common-mode current and a fraction of an ohm to the signal at the same frequency, the signal passing while the noise does not, and being wire either way at DC |
| Transmission line | A matched source seeing half its voltage until the far end answers, an open end doubling the wave and a short inverting it, a matched load absorbing it completely, a stiff driver into an unterminated line giving a staircase, and a series resistor at the source stopping the ringing — every figure from the textbook reflection coefficient rather than from a previous run |
| 1-Wire | A presence pulse answering a reset, a temperature arriving over a single wire as pulse widths, 85 °C coming back when the conversion was skipped or cut short, a function command without a ROM command being ignored, resolution trading precision for conversion time, the Dallas CRC against a known scratchpad, parasitic power browning out on an ordinary pull-up and working on a strong one, and a hundred metres of cable returning rubbish where two metres does not |
| I2C DAC | Code over full scale times the supply, full scale following the supply because there is no reference, a load pulling the output down and an op-amp follower restoring it, both write forms and their different bit alignments, only the EEPROM write surviving a power cycle, and the ADC beside it reading back what the DAC put out |
| Ferrite bead | A wire at DC, the datasheet impedance being true only at the datasheet frequency, falling away above its band, reactance dominating below the band and resistance inside it — and the consequence: it rings exactly like an inductor against a large capacitor and damps only where the ringing is in its band |
| Gate driver | Switching a gate orders of magnitude faster than a logic pin, moving the gate charge in charge over current, taking the gate to its own supply rather than the logic rail, a logic pin unable to reach the gate voltage at all, sinking harder than it sources, and refusing to drive below its lockout voltage |
| Reed switch | A magnet of either pole closing it with no supply at all, holding between the two thresholds, a burst of edges on closing that a Hall switch does not produce, and opening cleanly because there is nothing to rebound off |
| PIR sensor | Ignoring everything while it warms up, triggering on movement and holding long after it stops, retriggerable restarting the hold where single-shot gives up, and driving both ways rather than needing a pull-up |
| Li-ion charger | Constant current well below full, the current tapering as the cell approaches its float voltage and stopping at it, no headroom meaning no charge, the dissipation being exactly what the input does not put in the cell, and termination latching rather than cycling |
| I2C ADC | A voltage on a node read back over two wires as the same number, the gain setting pinning at full scale rather than complaining and pushing further past it changing nothing, a wider range fitting what the narrow one clipped, reading faster than it converts returning the previous answer, differential measuring the difference, and the configuration register written and read back |
| Hall sensor | A field strong enough pulling the output down, the pull-up having it otherwise, hysteresis holding the same field either way depending on which way it was approached, the wrong pole ignored by a unipolar part and answered by an omnipolar one, and a missing pull-up reported rather than silently useless |
| Phototransistor | Current following the light and doubling when it doubles, too large a load saturating it in ordinary light, and a dark current rather than nothing |
| UART | A character crossing the wire and a whole string in order, the far end echoing back, a two-percent clock error tolerated and a doubled or halved one reading definite wrong bytes — more than were sent when counting fast, fewer when counting slow — with framing errors where the stop bit missed; the same wrong bytes every run; TX wired to TX moving nothing; a receive line held low being a break rather than a byte; parity agreed and disagreed |
| Level shifter | Each side resting at its own rail, the low side pulled down taking the high side with it and the high side pulled down taking the low side back through the body diode, channels independent, and each missing or reversed rail reported |
| I2C | A byte written over two wires and found in the device, multi-byte writes and read-back, an unanswered address going unacknowledged, two devices sharing one bus, a port expander's byte reaching eight pins, and a bus with no pull-ups doing nothing at all |
| SPI | A byte clocked into a 74595 and onto its outputs, most significant bit first, the latch holding the outputs still while the bits walk through, and output enable releasing rather than driving |
| Character LCD | Latching on E falling and not before, the command table, four-bit nibble pairing, an unpaired nibble doing nothing, and line two being an address rather than a continuation |
| Solar cell | Open-circuit voltage per cell, short-circuit current, light setting the current while barely moving the voltage — against `n·kT/q·ln(2)` — the knee, and a maximum power point that is neither extreme |
| Switching regulator | Stepping 12 V down to 5 V, the divider setting the output at three ratios, the same chip rewired stepping 5 V up to 12 V, the timing capacitor setting the frequency, a lighter load using less of the time, and the current limit tripping when the sense resistor is too large |
| Thermocouple | Microvolts per degree for each alloy pair, measuring a difference rather than a temperature, and an INA126 turning two millivolts into something readable |
| Load cell | Rated output in millivolts per volt, proportional to load and to excitation both, and saying nothing at all when the bridge is not excited |
| Rotary encoder | Two staggered pulses per detent, which contact moves first carrying the direction, and contacts that bounce on every edge unless told not to |
| Charge pump | A negative rail from a positive one, following the input because nothing regulates, and sagging when the pump capacitor is too small for the load |
| Centre-tapped transformer | The two halves swinging opposite ways about the tap, a full-wave output rippling at twice the line frequency, and one diode drop in the path rather than a bridge's two |
| Inductor saturation | Ideal while the saturation current is left at zero, inductance halved at the stated current, and the current running away past the knee |
| DS1307 | BCD encoding both ways, the time read back off the bus from the datasheet's registers, the clock advancing on its own between two reads, and the clock-halt bit stopping it |
| HC-SR04 | 58 µs of echo per centimetre at three distances, silence until it is triggered, a trigger under ten microseconds ignored, and a 38 ms pulse rather than none when nothing is in range |
| Crystal | Resonating within 0.1% of its rated frequency from the motional L and C alone, a motional inductance in henries, and a far larger response on resonance than beside it |
| Microphone | Biasing part way down the rail, sound swinging the output and silence not, and being starved by too large a bias resistor |
| Servo | Pulse width setting the angle at both ends of travel and the centre, taking time to travel there, going limp when the pulses stop, and reporting a pulse that drives it into its end stop |
| Stepper | Turning with the coil order and back with it reversed, two adjacent coils holding it half way between, the first energisation aligning rather than stepping, and losing steps when driven faster than it can follow |
| Current probes | Ohm's law through a resistor, equal and opposite at the two ends, a capacitor's charging current against `C dv/dt`, a transistor's three terminals summing to zero, a switch carrying nothing when open |
| Battery | Sag against the internal resistance, a coin cell sagging where a lead-acid does not, coulomb counting against the rating, a discharge curve that is flat and then is not |
| Surge parts | A TVS invisible below its standoff and clamping either polarity above it; a varistor's power law, and wearing out on the energy it absorbs |
| ULN2003 | Sinking the load current, the Darlington's saturation voltage, a released channel passing nothing, and a load wired to ground doing nothing at all |
| Amplifiers | LM386 idling at half supply, its stated gain, clipping at the rails; INA126 rejecting four volts of common mode; the speaker's average power |
| 40xx series | Gate truth tables on the CMOS pinout, and that it is *not* the 74xx one; 4093 hysteresis and its relaxation-oscillator period against the closed form; 4013 toggling and its active-high set and reset; 4040 stage-by-stage division and falling-edge clocking; 4017 one-hot walk, reset and carry; 4511 glyph table, active-high drive, lamp test and blanking priority; 4066 and 4051 analog pass-through, switch resistance and off-state leakage; 4060 oscillating from its own Rt and Ct at the datasheet's 1/(2.3·Rt·Ct), and the frequency tracking a changed capacitor |
| Comparator | Open-collector pull-down and release, driving a higher rail through a pull-up, edge timing |
| Displays | 7447 glyph table, open-collector drive, lamp test and blanking priority, counter walking 0-9 on the display |
| About | Every library reporting a version that matches the assembly actually loaded, an unstamped build saying so rather than claiming 1.0.0, the palette counts being counted, and the copied text carrying everything a bug report needs |
| Parts list | Identical parts collapsing to one line with a count, the value separating otherwise identical parts, grounds left out, R10 sorting after R2, and the table reaching the file rather than merely being given room |
| Export | Every format writes a file worth opening; a PNG at the size of the circuit and a raster scale that only multiplies pixels; a hand-written BMP header that agrees with its own pixel count; an SVG made of shapes rather than an embedded image; a PDF that starts and ends as one, with two pages when the traces get their own; framing that follows the circuit rather than the view; and a scope that clears the canvas being unable to wipe the schematic above it |
| Files | Every component type round-trips with its parameters, pins, wires, waypoints and probes; a reloaded circuit solves to the same answer; damaged, unknown and newer-format files are handled without losing the open circuit |
| Settings | Theme round-trips across a restart, corrupt and newer-format preference files fall back to defaults, opening the dialog writes nothing, choosing a theme saves immediately |
| UI | Background transport, probe decimation, reflection-built inspector, dirty tracking, every example compiles, runs, saves and reopens, and both grouped lists opening one group at a time — starting closed, the open one closing as the next opens, bulk toggles exempt, and a search opening everything it matched and restoring the open group when cleared |
| Copy and paste | A duplicate carrying every parameter including the model on an op-amp and the input count on a gate, its own designator and identity, a clipboard that survives the original being edited or deleted, repeated pastes cascading rather than stacking, an undoable paste, and every one of the 183 palette parts surviving the round trip |
| Hover cards | A part described where it sits — designator, type, value, settings in operable-first order, a long list cut with a count, an oversized value shortened, the violations behind a red ring carried as words, and every one of the 183 palette parts describable without throwing |
| Convergence | A failure naming the net that was still moving and what is attached to it, the parts that say they have not settled listed separately from that — and a part that did not complain never blamed, because a branch current is a current inside a part rather than a place and is left unnamed rather than called a row number |
| Arrange | Aligning left, right, top and bottom by the parts' edges and the two centre lines by their middles, a centre being the average so a selection does not drift, distributing leaving the two outermost exactly where they were with equal gaps between the rest, parts already in line left alone so nothing lands in the undo history for a move that did not happen, and everything rounded back onto the grid when snapping is on |
| Find on sheet | An exact designator beating one that merely starts with the term, values and kinds and net names after that, case never mattering, a named net being one row however many parts are on it, blocks searched inside, and nothing matching nothing |
| Compare | Two copies of one circuit differing in nothing, a changed value read as one line, parts matched by identity so a renamed and moved part is two changes rather than a removal and an addition, a wire being the pair of pins it joins so redrawing it through a corner or the other way round is not a change, rounding through a save not counting as an edit, probes and requirements and conditions compared, and a summary that counts moves without leading with them |
| Explain | A divider's tap in volts and a loaded one refused, a low-pass told from a high-pass with the corner, a tuned circuit peaking or dipping, decoupling, pull-ups, an LED's current, an op-amp's gain inverting and non-inverting and as a follower, emitter followers and common-emitter stages, a flyback diode that is not an LED, a 555's frequency and duty — and silence on anything it does not recognise, proved across all 87 shipped examples |
| Design report | A whole HTML document with nothing fetched from anywhere, the verdict before everything else, the schematic as selectable SVG with no stray declaration, requirements coloured by met and not met and not measurable, one row per part and value, the conditions always stated, everything a person typed escaped, and a report without a drawing still a report |
| Windows | A second editor with its own circuit, simulation, scope and history; a clipboard shared between them because carrying a block across is what they are for; a recovery snapshot each so two cannot overwrite each other's work; and every abandoned snapshot offered back in a window of its own |
| Colour codes | The value as it is written on the part, drawn beside the numbers — four bands at five percent and five at one, because you cannot promise one percent on a value you only spelled to two figures; gold and silver multipliers that divide, so a 4R7 is yellow-violet-gold; the wider gap that says which end to read from; a ceramic's three digits counted in picofarads and an inductor's bands in microhenries; the tolerance band rounded to the tighter promise rather than the looser one; a value the code cannot spell saying so and saying what it does spell; every marking also read out in words; and an electrolytic given none, because its value is printed on the can in plain microfarads |
| Memory and SPI | A memory whose contents are typed in and read back with the circuit's own writes showing, a dump that round-trips and skips runs of zeros, a counter walking its addresses and every stored byte reaching the bus, a reset restoring the preset, a transparent latch that follows while open and holds what it saw, and an SPI converter whose code the master decodes exactly where the datasheet says |
| Temperature (MOSFET and op-amp) | A power MOSFET's on-resistance climbing towards double between room temperature and a hot heatsink, its threshold falling about two millivolts a degree, an op-amp's offset drifting with the CMOS part drifting several times less than the bipolar one, every parameter exactly nominal at 27 °C, that drift reaching the output multiplied by the noise gain, a regulator's output falling about a millivolt a degree with the die taking its heat from the room and its own dissipation together, an adjustable supply obeying the divider law against the reference its junction is actually at, and the TL431's bandgap bowing rather than sloping — both ends of the range below the middle, flat to first order at the trim point, inside the datasheet's deviation band, and dead flat again with the curvature switched off |
| Temperature | A silicon diode's forward drop falling about two millivolts a degree and a Schottky's falling less steeply, the same coefficient at both ends of the range rather than only near room temperature, LEDs falling too, saturation current roughly doubling every ten degrees, a bipolar's base-emitter voltage falling while its gain climbs by half again across the range, every parameter exactly nominal at the 27 °C the models are quoted at, a temperature sweep coming out a straight line and putting the circuit back afterwards, and a circuit carrying its own temperature through a save |
| Printing | Every paper size at its real dimensions and landscape swapping the edges, the content area being the sheet less its margins and header, content scaled down to fit but never up, proportions kept, placed content always landing on the paper whatever its shape, a sheet per thing printed and every sheet the same size, an empty circuit still producing a page a printer would accept, the page being the paper's size where the ordinary PDF export is the circuit's, and printing drawing in dark ink on a light page whatever the application is wearing — with the application's own colours restored afterwards |
| Netlist export | Every passive and source becoming an element line, a designator not getting its prefix twice and a multi-letter one stripped properly, ground as node 0, a named net keeping its name, a current source's nodes swapped for SPICE's convention, a generator becoming a source with its waveform and the shapes SPICE lacks written as a PULSE with the right timings, one model card however many parts use it, a part entitled to its own name keeping it, every element name in a deck unique, a part with no equivalent named in the deck rather than dropped, a block's contents appearing as ordinary parts, values written unambiguously rather than with a suffix that means milli to SPICE and mega to everyone else — and the model cards it writes being ones the importer reads back to the models they came from |
| CSV export | A header naming each column with its unit, every recorded point becoming a row including the last one that floating-point put a hair past the window, a hidden trace left out, two traces on different time axes both filled in by interpolation, a probe blank outside its own recorded span rather than invented, a long run decimated evenly across the whole run rather than truncated, numbers written with a dot whatever the machine's culture, and a label containing a comma quoted |
| SPICE import | SPICE numbers with their suffixes, including M meaning milli where MEG means mega; cards wrapped over continuation lines, comments dropped, several in one block; a diode, a bipolar and a MOSFET read into their models with a P-channel's negative threshold brought into the channel's own convention; a device there is no model for named rather than ignored; parameters with nowhere to go named rather than dropped; an imported model joining the library, replacing by name, and surviving a restart; and a diode imported from the 1N4148's own parameters solving to the same drop as the built-in one |
| Noise examples | The Inverting Amplifier's noise being mostly the op-amp's own with the amplifier leading, scaling every resistor up a hundredfold turning it from a voltage-noise problem into a current-noise one, the RC Low-Pass refusing to get quieter across a hundredfold change of resistor — and the window opening on the output probe rather than an input a source holds at no noise at all |
| Reference traces | Nothing kept until one is taken, a kept trace copying what is on screen and not following the probe afterwards, every visible probe being kept and an empty one not, several kept with the summary counting them, clearing throwing them away, and a kept trace surviving a change to the circuit at the amplitude it was taken at |
| Computed traces | A ratio of two traces being a gain, the box clearing on success, the expression being the label when none is given, a bad expression refused with a reason rather than added, typing checked as it goes, one removed again, a trace whose input has gone coming back empty rather than throwing, and several getting colours no probe is using |
| Expressions | Every operator doing what it says, precedence and brackets, powers right associative, exponential numbers, the functions, braces around a label with spaces, case-insensitive names, a gain as a ratio and in decibels, a dissipation as I²R, dividing by zero leaving a gap rather than infinity, traces sampled at different times interpolated onto the union of their times — and every way an expression can be wrong saying why, with an unknown name listing what there is |
| Autosave | Nothing pending before anything is written, what is written coming back with its original path, an unsaved circuit named as one, discarding, a damaged snapshot being nothing rather than a crash, an interrupted write not destroying the last good one, an unmodified circuit not being snapshotted and a modified one being, saving discarding it, accepting recovery bringing the circuit back still marked modified, declining also throwing it away, and a recovered circuit remembering its path |
| Stability | The DC loop gain being the open-loop gain times the feedback fraction, the crossover being the gain-bandwidth product times it, a single-pole loop having ninety degrees of phase margin and no gain margin because it never inverts, more feedback meaning more loop gain and a higher crossover, a capacitive load eating the margin and enough of one leaving it ringing with the verdict saying so, a probe in the loop changing no DC answer and having no volts across it, the injection being put back afterwards, a sweep that never reaches crossover saying so — and the Loop Stability example losing its margin when the load is switched in |
| Impedance | A resistor being its own value at every frequency and a capacitor being 1/ωC with the reactance reading back as the farads it started from, a divider seen from its tap being its two resistors in parallel, a series RC crossing at exactly R√2 and minus forty-five degrees at its own corner, a parallel tuned circuit peaking at 1/2π√(LC) with only its damping resistor left there, a decoupling capacitor dipping to its ESR at its series resonance and reading back as an inductor above it, a follower's output being the bare output resistance divided by one plus the loop gain at DC and the bare figure again past the gain-bandwidth product — climbing by four orders of magnitude in between — and a probe with both ends on one net being refused rather than divided by |
| Poles and zeros | An RC having exactly one pole at minus one over RC with the time constant and the corner both reading back from it, a series RLC giving a conjugate pair at 1/2π√(LC) with a Q of (1/R)√(L/C) and a decay of R/2L, two independent sections giving two poles with the slower named as dominant, a high-pass having a zero at the origin where the textbook shortcut loses one and a low-pass having none, a purely resistive circuit having no modes at all, a transmission line being refused because a delay has infinitely many poles — declared by the part and caught by sampling when a part lies about it — and a follower's pole pair ringing where its own loop gain crosses over with the Q its phase margin predicts |
| Eigenvalues | A triangular matrix giving its diagonal, a companion matrix giving its polynomial's roots, a real matrix's complex eigenvalues coming out conjugate, a rotation sitting on the unit circle, the whole spectrum summing to the trace and multiplying to the determinant, and six significant figures surviving a similarity that spans eighteen decades |
| Self-heating | A part sitting at ambient until it is given a thermal resistance, the die then settling exactly where its own dissipation puts it, a hotter MOSFET dropping more and burning more for it, a worse heatsink always meaning a hotter die, the rise sitting on top of the ambient rather than instead of it, a diode's drop falling at the coefficient its own forward voltage implies rather than the textbook two millivolts, a bipolar drawing more current as it warms, the die lagging its dissipation through a transient and arriving in the end, and a runaway stopping at the rated junction temperature with the part saying so rather than the solver failing |
| Requirements | At most and at least failing on the side they are meant to, within failing on both, a missing trace and an unmeasurable quantity coming back unknown rather than failed, margin reading as a fraction of the limit and going negative by how far it is exceeded, disabled requirements left out rather than passed, a summary keeping failures apart from gaps, and every requirement surviving a save — including one measuring something this version has never heard of, which is kept and reported rather than silently measuring the wrong thing |
| Opening windows | Every window the menu opens, actually opened: laying out, closing from its own button and on Escape, and nothing in it drawn in a colour that cannot be read against its own background — plus the two dialogs built in code following the theme when it changes underneath them, which is the bug that shipped in 0.33.3 and which reverting that fix makes fail by name |
| Ratings | A dissipation past its rating, a capacitor past its volts, an LED past its amps and a part's own complaint all reaching one list; peaks reported for the limits and the mean for the heat out of a single run; a part with no rating left out rather than listed with dashes; and the worst coming first, where worst means nearest a limit rather than largest |
| Timing | A delay measuring back as the delay that was put in, an inverting stage timing the same as a non-inverting one, noise not inventing edges, skew staying small however the capture is cut — over four shifts chosen to break each of the two simpler pairing rules — setup and hold being the two halves of the data window, and a real gate's delay measured off its traces without asking the device |
| Parameters | A value resolving to itself, one parameter written in terms of another in whatever order, pi always available, a cycle reported rather than iterated and a typo reading differently from one, a broken binding leaving the part's value alone, and both the expression and the number it works out to surviving a save |
| Keyboard | Arrows moving the selection by a square and by one unit with Shift, a whole selection moving together, Tab stepping and wrapping in both directions, stepping scrolling only when it has to, Enter placing the armed part and placing nothing when none is armed, and annotations skipped — pressed as real keys at a real canvas in a real window |
| Current flow | Nothing moving below a nanoamp, the speed logarithmic so a microamp is visibly moving and a milliamp visibly faster, saturating at the ceiling, the sign being the direction and nothing else, dots evenly spaced and still on their own wire after a day of running, a series loop carrying the same current all the way round with the signs describing one circulation, the return through ground known by what must follow from a two-terminal part, reversing the supply reversing every dot, and a junction's wires left out rather than guessed at |
| Op-amp and flicker noise | An amplifier's voltage noise coming out times the noise gain rather than the signal gain, an inverting stage of gain −1 being twice as noisy as a follower, the measurement reading low at the closed-loop corner because the noise gain has started following the open-loop gain down, flicker being √2 the floor at the corner and rising as 1/√f below it, an amplifier noisier at one hertz than at a hundred kilohertz, small resistors making it a voltage-noise problem and large ones a current-noise problem, a JFET input beating a bipolar one against a megohm, a MOSFET's flicker corner being three orders above a bipolar's, and switching a corner off making the curve flat |
| Noise | A resistor's density being exactly √(4kTR) and flat with frequency, two in parallel making the parallel value's noise with half the power each, an RC settling at √(kT/C) whatever the resistor is across three decades of it, a divider's shares going as 1/R so the smaller resistor dominates, contributors adding in quadrature to the total, cooling helping by the square root of the ratio, a diode's shot noise coming out at √(2qI)·r — which is 1/√2 of a resistor of the same slope — shot noise following the current rather than the temperature, a bipolar reporting both its generators with the base's leading on a current drive and the collector's on a stiff one, and probing ground or a circuit of ideal parts being refused rather than answered with a zero |
| Net highlighting | A wire lighting up both its ends and everything joined to them, two points joined only by having the same net label coming out as one net while two different names do not, a block's pin reaching what is inside it without lighting up wiring that is not on the sheet, the ground net knowing it is ground, a part on a net twice being named once, the summary naming what is on the net and trimming a long one — and a terminal that is not in the circuit having no net rather than a guessed one |
| Stepping a transient | One run per value with each keeping the time constant its own value gives it, stepping either the resistor or the capacitor, every run starting from the same conditions so no pass contaminates the next, the parameter and the sampling settings going back where they were, the circuit left at its own bias point rather than the last pass's final state, a long run still keeping its beginning, a stated sample interval being used, every probe recorded and the runs not sharing their samples, a parameter that is not a writable number refused — and an RLC ringing past its supply at two ohms and not at two hundred, with the overshoot reported |
| Distortion | A pure sine measuring clean and its amplitude coming out right whichever window was used, one harmonic at a tenth being ten percent, harmonics adding in quadrature so 3 % and 4 % is 5 %, each harmonic coming back at the amplitude it was built with, a tenth reading as −20 dBc, a square wave's odd harmonics falling as 1/n with the fundamental at 4/π, symmetric clipping being odd-harmonic and lopsided clipping even, THD and THD+N agreeing when only harmonics are present and diverging when something that is not a harmonic is, a tone deliberately placed between two bins still measuring to a fraction of a percent and never reading high, running out of band being reported rather than ignored, a fundamental too low to resolve being refused, a stated fundamental beating a guessed one when a harmonic is bigger — and, on real circuits, an op-amp clean inside its rails and odd-harmonic beyond them, and a half-wave rectified sine matching its own Fourier series with the second harmonic at 4/3π, the fourth at 4/15π and nothing odd |
| Worst case | Both corners landing where the arithmetic says, the recipe naming which way each part went, the spread and worst error matching it, random sampling always reporting the narrower spread, a solve per part rather than two to the number of them, a part the answer does not depend on being left alone, everything put back afterwards, and nothing toleranced or no probe being refused |
| Solving for a value | The value the arithmetic says on either resistor, the nearest E24 part and what it achieves, converging in a couple of dozen solves, the parameter going back, a target out of reach reporting what the range can do — and the preferred series rounding in the logarithm, with every E24 value its own nearest at every decade |
| Parts list export | A header and one line per part and value, three of a value grouped onto one line, the designator list quoted so the columns survive, needing no traces, and an empty schematic saying there is nothing to list |
| Subcircuit import | Pins coming back in the header's order, values read with their suffixes, continuations and comments, a model defined inside belonging to the subcircuit, a block coming out with its parts and pins — and solving: a divider giving half its input, and a macromodel of a transconductance, a resistor and a buffer giving a gain of a hundred. A transistor built from its own local model. And refusing rather than approximating: a switch, a nested subcircuit, a parameterised header, a missing .ends and a pin joined to nothing all named |
| The guide's plots | Nine analysis plots drawn from the analyses themselves rather than screenshotted, each asserting what it illustrates: an RC turning over at 1/2πRC, a transistor's curves fanning upwards with base current, a clipped sine being odd-harmonic, a loaded follower's phase margin, an amplifier noisier at one hertz than at a hundred kilohertz, four step responses with four time constants, a diode's last tenth of a volt multiplying its current several times, five percent resistors moving a divider by more than one percent, and a bandgap peaking above both ends of its range |
| The documentation itself | Every contents entry pointing at a heading that exists and every cross-reference resolving, the contents numbered without gaps and in the order the sections appear, no two sections sharing an anchor, every keyboard shortcut in the menu appearing in both the guide and Help > Keyboard Shortcuts, every window the menu opens being mentioned in the guide, the parts table's per-category counts and total matching the catalogue exactly, every picture referenced being on disk and every picture on disk being referenced, and every one of them describing what it shows |
| Palette search | Everything showing before anything is typed and the groups starting closed, a chip's number finding it, what a part does finding it where the name never would, a group name finding everything in it, case not mattering, a search opening every group it matched and hiding the ones it emptied, a search that matches nothing saying so rather than showing everything, clearing putting back the group that was open before, a group opened during a search not being mistaken for the search's doing, Enter arming the first match and arming nothing when there is none, asking for the box opening the palette — and every one of the 188 parts being findable by its own name |
| Circuits travelling | A circuit opening complete after the machine has forgotten the model entirely, the same file falling back and saying so without the card, a circuit of built-in parts carrying no models at all, only the models actually used being carried, a model inside a block travelling too, a model already present winning with the disagreement reported and an identical one passing silently, a built-in being unredefinable by a file, a card written back out reading as the same card and as the same text, every kind of device travelling, a card whose model has been removed no longer being carried, and keeping a file's models putting them in the library for good |
| Sensitivity | Two equal parts sharing the blame equally with the shares adding to one, the wider band ranked first, a part the output barely depends on ranked last however loose it is, elasticity being a property of the circuit rather than of the tolerance, each probe ranked separately with parts it does not depend on contributing nothing, and every part put back afterwards |
| Analysis examples | Each one asserted against the thing it exists to show and against showing it as shipped: a hysteresis example opening already in XY and drawing a loop whose two branches overlap in input voltage, a bridge reading nothing nominally and hundreds of millivolts once tolerance is allowed for, a shunt read differentially where against ground it is lost in the rail, and a diode thermometer sweeping out a straight line at two millivolts a degree |
| Undo | A grouping undone giving the parts back rather than losing them and the circuit solving to what it did before, one step per gesture however many changes it makes, nested gestures still costing one, a gesture that changes nothing recording nothing, a gesture inside a restore recording nothing at all, a drag across the canvas costing one step, and an ungrouping and a placed block each undoing whole |
| Block library | A block saved and rebuilt with its contents, surviving a restart, two instances that share no object, identity or designator and both solve correctly, editing one leaving the other alone, saving over a name replacing it, nesting surviving the round trip, and a damaged library file being an empty one rather than a crash |
| XY mode | The first visible trace taken as the horizontal axis unless another is chosen, a hidden trace being neither, y = 2x coming out as exactly that, two sines a quarter cycle apart drawing a unit circle and the same two in phase collapsing to a line, and traces of different lengths paired by time rather than by index |
| Blocks | Grouping part of a circuit changing what it solves to by nothing at all, and ungrouping changing it back; a pin appearing wherever a wire crossed the boundary and two wires onto one inner terminal sharing one pin; wires wholly inside moving inside; a non-linear part inside a block still solving as itself; a transient running through one unchanged; a block inside a block flattening the same way; every part appearing exactly once in the flattened list; a probe on a part that goes inside a block still reading it; and all of it surviving a save, nesting included |
| Annotations | Notes and boxes that change no answer to the last bit, stay out of the bill of materials, wrap on words with the height following, keep explicit line breaks, size themselves where a part is sized by its pins, fall inside the bounds an export frames to, and raise nothing in the rule check |
| Protocol decoding | Synthetic transactions decoded back to the bytes they were built from, and then the shipped examples decoded to what they actually contain: the EEPROM example's whole conversation including the restart between the write and the read and the master's deliberate NACK on the last byte, the SPI converter's answer arriving underneath the question with the ten-bit code landing where the knob is, the serial link's text, and a DS18B20's SKIP ROM and WRITE SCRATCHPAD with its three configuration bytes. Plus the refusals: a capture too coarse to decode reported rather than turned into shifted bytes, a CAN trace carrying no frame reported rather than parsed into one, a wrong bit rate caught by bit stuffing's own invariant, and the clock edge that sets up an I²C stop condition not counted as a ninth data bit |
| Derived probes | A differential probe reading between its two points and reversing sign when they swap, a shunt high in a 24 V rail readable differentially where a plain probe sees only the rail, power as the voltage across times the current through with I²R checked both ways, a supply delivering exactly what the resistors between them dissipate, and a reference that survives a save and reload |
| Tolerance analysis | A divider's spread staying inside what the tolerance arithmetic allows and reaching its corners, a wider band giving a wider spread, the same seed giving the same run and a different seed not, every varied part put back at its marked value, the worst departure reported as a fraction of nominal, a histogram bucketing every reading exactly once, and a bridge of four 5 % resistors reading hundreds of millivolts where nominally it reads nothing at all |
| DC sweep | A diode swept out to its own exponential and the slope between two decades of current landing on the textbook hundred millivolts, a transistor's output characteristic stepped into the fan off the datasheet with the same beta on every curve, those curves tilting upward by a few percent because a real output resistance is finite, a divider sweeping out the straight line it is, every swept property put back and the bias point restored afterwards, and a point the solver cannot reach leaving a gap rather than throwing the curve away |
| Trace measurements | A sine giving back its own amplitude, mean and RMS and a square wave's RMS being its amplitude, the frequency of three tones across three decades, fewer than two cycles refusing to be a frequency rather than guessing at one, noise on the edges not multiplying the measured frequency, duty cycle at a quarter, a half and three quarters, the rise time of an exponential landing on ln(9) time constants, ringing after an edge not being counted as part of the rise, and a window measuring what is in it rather than the rest of the history |
| Cursors | Placed across the window when first turned on, the gap between them and its reciprocal, what each trace did between them, a value between two samples interpolated rather than snapped, and nothing outside the samples pretending to be a reading |
| Net labels | Two labels with one name being one net, different names staying apart, case and surrounding space ignored, a blank one joining nothing, three labels making one net rather than two, a net taking its name from the label on it, ground still winning on a net that is also labelled, a label stamping nothing so it cannot change an answer, and a divider split across labels solving to the same midpoint as one wired together |
| Rule check | A good circuit producing nothing and an empty canvas not being an error, a missing ground reported in words rather than as a singular matrix, a source shorted across itself, a supply pin on the ground net, a package with an unwired supply pin, a floating input as an error where a floating passive lead is only a warning, two driven outputs sharing a net, a net label nobody else uses, a blank one, two names on one net, and errors listed ahead of warnings |
| Spectrum | The transform of a constant landing entirely in the first bin, a length that is not a power of two refused rather than quietly wrong, a sine reading its own amplitude under all three windows, the DC term in bin zero at its own value, the peak found past the DC lobe rather than in its skirt, a square wave's odd harmonics at a third, a fifth and a seventh with no even ones at all, a modulated carrier's sidebands at half the modulation depth and nothing at twice the spacing, windowing keeping an awkward tone from smearing across everything, unevenly spaced samples still giving the right frequency and amplitude, and a transform no larger than the samples support |
| Power and interface | A PPTC carrying its hold current for ever and tripping on a fault to a trickle rather than to nothing, and cooling back far slower than it tripped; a solid-state relay commanded at a mains peak refusing to fire until the next zero crossing and then conducting properly; an LM324 running four independent followers off one supply pair with the fourth saturating exactly where its family's headroom says; an A4988 holding 0.8 A out of a supply that would otherwise force 4.3 A, reversing the winding rather than switching it off, and commanding intermediate currents when microstepping is selected; and a MAX232 making ±8.5 V from a single 5 V rail, inverting TTL onto the line and back, deciding with half a volt of hysteresis, and sagging its pump when the line load is heavier than the standard allows |
| New devices | Tri-state outputs genuinely releasing a bus where a pull-down alone then decides it, a CAN bus going dominant whenever any node sends a zero and the recessive node knowing it lost, an IGBT conducting with a voltage offset where a MOSFET holds a resistance and still passing current after its gate has gone, a photodiode linear across decades with a thousandth of a phototransistor's current and saturating when its load resistor is too big, and a synchronous counter whose carry is gated by the enable that chains it |
| Box selection | A part caught only when wholly inside the box and not when clipped, captions excluded from the test, a group copying with the wires between its members and without the ones leaving it, the copy keeping its shape, two pastes giving two separately wired groups, and a copied divider solving to the same midpoint voltage as the original |
| Example browser | Every example filed under exactly one group and no group left empty or enormous, the flattened list carrying the group each came from, opening with something already described, searching narrowing to what matches across name, description and group, a search opening whatever it matched, an empty result saying so, selecting one row unmarking the rest, and opening and cancelling each closing with and without a choice |
| Audio files | A WAV surviving a round trip to sixteen-bit accuracy, loud samples clipped rather than wrapped, 8/16/24/32-bit and stereo files read, a speaker's recording landing on a fixed grid at the frequency and level fed to it whatever the solver did, a microphone playing a clip instead of its tone, an unreadable file reported rather than thrown, and a clip going in one end of an LM386 and coming out the other carrying the gain |
| Shipped examples | Each one run for real and asserted against the thing it exists to show — a 4066 passing its selected source to three millivolts and departing by most of a volt the moment a second channel closes, a servo at 1.0, 1.5 and 2.0 ms sitting at its three datasheet angles, a shunt reference holding its threshold as the rail falls where a divider would not, a Hall switch counting one pass per pass, a thermocouple reading short by exactly its cold junction, a crystal passing a fourteen-kilohertz band where the LC tuned to the same megahertz passes the whole sweep, a coin cell losing half a volt at fifty milliamps where an 18650 loses three millivolts, an I2C bus crossing a 3.3 V boundary and the bytes coming back from a 5 V memory, a choke passing its signal whole and a hundredth of the common-mode noise riding with it, a thermostat switching off hotter than it switches back on, a stepper's windings clamped a diode drop above their rail, three followers on one supply each stopping exactly at its own datasheet headroom, a strain-gauge bridge whose five millivolts survive two and a half volts of common mode, an LM386 gaining exactly ten times more with its gain capacitor, a relay changeover whose two lamps are never lit together, a panel whose peak power lands four fifths of the way to open circuit, a lithium charger going constant-current then constant-voltage then terminating in that order, a surge clamped in two stages with the fuse opening only for the fault it is there for, an encoder closing A first one way and B first the other, a window detector high only between its two limits, an SCR staying lit with the gate released and dropping out only when its anode is interrupted, a 4060 whose period follows the capacitor on the canvas, a JFET stage self-biasing to a negative gate-source voltage and losing two thirds of its gain when the source bypass goes, a flyback diode holding the drain a diode drop above the rail where without it the solver reports a number with seven digits in it, an HD44780 taking two lines of text over four bits and reaching the second line by addressing it rather than running off the first, an RS-485 link carrying data down fifty metres and ringing once its terminator is switched out, a varactor-tuned tank whose resonance follows its knob, an adjustable 7805 supply tracking its potentiometer across five settings and rejecting the ripple on the rail below it, the PLL locking onto its signal, the modulated carrier having an envelope, the current sensor following a load switched in while it runs, the reflections example doubling at the far end and going quiet when the terminator is switched in, the bead cutting the noise on the rail, the driven gate reaching twelve volts where the one off the logic pin cannot, the DS18B20 returning the temperature it was set to with a valid CRC, the DAC coming back round through the ADC, one magnet counted several times, the PIR holding its lamp on after the movement stops, a microstepping driver holding its current limit in both directions while the shaft turns exactly twelve steps of a 200-step motor, a solid-state relay waiting out the rest of a half cycle before it picks up, a quad op-amp chain biasing to half its rail and then buffering, doubling and inverting with the fourth stage mirroring the third at every instant, a PPTC collapsing a short to milliamps and recovering when it is cleared, two MAX232s carrying text in both directions over a cable swinging either side of ground, and a curve tracer whose four curves are ordered, separated and evenly spaced because its base is driven by a current rather than a voltage |

## Development boards

Raspberry Pi and Arduino boards sit on the schematic as ordinary logic devices, either sourcing
signals or reading them. Electrically a board is a pin map plus a per-pin mode, and the existing
machinery covers all of it: `LogicState.HighImpedance` — built for the 7447's open-collector
outputs — is exactly a GPIO configured as an input, which is what lets a pin change direction
without changing the size of the matrix, so retyping the setup never recompiles the netlist.

A board's pins are configured with one line, because a forty-pin header as forty inspector rows
would be unreadable and most circuits use three pins:

```
GPIO17=high; GPIO18=clock@1kHz; GPIO22=in-pullup; GPIO12=pwm@500Hz:25%
GPIO23=seq@1kHz:1101_0010; GPIO24=once@1kHz:001
```

`seq` loops a bit pattern and `once` plays it through and holds the last step, which is what makes
a one-shot usable as a reset pulse rather than something that yanks the line back down every few
milliseconds. A `z` step releases the pin for that step, so an open-drain line or a shared bus
handed over mid-pattern both fall out of the same mechanism.

Boards are then checked against their datasheet limits as the simulation runs — a Pi GPIO above
3.3V (it is not 5V tolerant), a pin over its current rating, the total GPIO budget — and a
violation is reported in the status bar. These are deliberately kept separate from solver errors:
the circuit solves perfectly well, it is the hardware that would not survive it.

The [User Guide](docs/USER_GUIDE.md#development-boards) has the full pin-mode reference.

## Saving circuits

Circuits are saved as `.cirq` files — plain, indented JSON, so a schematic is readable and diffs
sensibly in version control.

Components are persisted by type name plus the parameters
`ComponentReflection.EditableProperties` reports — the *same* rule the properties inspector uses to
decide what you can edit. That is deliberate: if a value can be edited it is saved, and if it is
saved it can be edited. A new component type is therefore saveable the moment it exists, with no
registry to forget to update.

Wires and probes refer to terminals as (component id, pin id) rather than by object identity, so
they reattach to exactly the right pins on reload. Device models are stored by name and resolved
against their library. A gate's input count is carried separately, because it shapes the pins and
so cannot simply be assigned after construction.

Loading is deliberately forgiving: an unknown component type, a wire whose endpoint has gone, or a
model this build no longer ships are each reported as warnings while the rest of the circuit still
opens. Only a malformed file, or one written by a newer format version, is refused outright. Saves
go to a temporary file and are moved into place, so an interrupted write cannot destroy the
original.

## Theme and settings

`File > Settings` opens a preferences dialog offering **System**, **Light**, **Dark** and
**High contrast**. System comes first and follows the desktop's light/dark setting, tracking it if
it changes while the application is running; the others pin a palette. When System is selected the
dialog says what the desktop is currently reporting, or admits that it cannot tell — on a bare
window manager there may be no portal to ask, and silently falling back to dark would otherwise
look like the setting had been ignored.

![The settings dialog, showing the theme selector set to System and a note reporting what the desktop currently uses](docs/images/06-settings.png)

The same circuit in the light and high contrast palettes:

![The RC low-pass circuit drawn in the light theme](docs/images/07-theme-light.png)

![The RC low-pass circuit drawn in the high contrast theme, pure black with white symbols and wires](docs/images/08-theme-high-contrast.png)

Preferences are stored as JSON under the user's config directory (`~/.config/CirqAvalonia/` on
Linux) and survive a restart. Changes apply and save immediately, so there is no OK/Cancel to get
wrong. A damaged or unreadable file falls back to defaults rather than preventing startup.

Colours come from one semantic palette in `Views/Themes.axaml` — `PanelBackground`,
`SymbolStroke`, `WireStroke` and so on — defined once per theme variant. The canvas and the scope
are drawn in code rather than declared in XAML, so they resolve those same keys at runtime through
`ThemeManager` instead of carrying a second palette that would silently drift. Adding a theme means
adding one more `ResourceDictionary` and one more entry to the selector.

High contrast is a *custom* `ThemeVariant` rather than one of Avalonia's two built-ins, so its
dictionary is keyed with `x:Static` — the XAML type converter only understands `Light` and `Dark`
and throws on anything else. It inherits from Dark, which means the Fluent control chrome the
palette does not name still resolves instead of coming back empty. Tests assert that all three
variants define exactly the same key set, and that every canvas element clears a minimum contrast
ratio against the canvas it is drawn on — a missing key would otherwise render magenta at runtime
rather than failing a build.

## Using the editor

Commands live in the menu bar — **File, Edit, Tools, View, Simulate, Help** — rather than on a
toolbar. Three wrapped rows of buttons cost more vertical space than the canvas could spare in a
narrow window; one menu row gives about 140 px back. The status bar carries the readouts the
toolbar used to show: active tool, simulation speed, elapsed time and solver rate.

`Help > Keyboard Shortcuts` lists everything below, and the
[User Guide](docs/USER_GUIDE.md) walks through building a circuit from an empty canvas.

Six things are about working on a drawing rather than solving one. **Find on Sheet**
(`Ctrl+Shift+F`) goes to a part by designator, value, kind or net name — `Ctrl+F` searches the
palette for a part to *place*, and past twenty parts "where is R17" is the more frequent question.
**Edit > Arrange** lines up or spaces out a selection, snapped back onto the grid and in one undo
step however many parts moved. **What Is This Circuit?** (`Ctrl+F1`) reads the drawing back in
words — a divider and where its tap sits, an RC and its corner, an op-amp stage and its gain — and
stays quiet about anything it does not recognise exactly, because a confident wrong description is
the one thing here that could teach somebody something false. **Compare with a Saved Circuit** says
what changed in values rather than in braces, matching parts by identity so one renamed *and* moved
is still the same part. **Design Report** writes one self-contained HTML page with the schematic as
SVG, the requirements and their verdicts, and the parts. And **New Window** (`Ctrl+Shift+N`) opens
a second editor sharing the clipboard, for comparing two designs or carrying a block between them.

When the solver cannot find an answer it now says **where**: the net that was still moving, what is
attached to it, and any part that said it had not settled — all of which name somewhere on the
drawing to go and look, which "try a smaller time step" does not.

The palette holds 188 components in 16 collapsible categories, with a **Find a part** box
(`Ctrl+F`) over them:

![The component palette: a Find a part box at the top, "186 parts in 16 groups" under it, and the sixteen categories with their counts — passive 11, switches 4, sources 14, semiconductors 13, transistors 11, LEDs and displays 9, power 10, analog ICs 13, logic gates 7, 74xx series 21, 40xx series 15, buses 17, digital I/O 8, sensors and actuators 18, switching and isolation 11, dev boards 4 — every group closed, so all sixteen headings fit on screen at once](docs/images/02-palette.png)

Selecting a component fills the properties panel. It is built by reflection, so a new component
type gets a complete editor without any UI code:

![The properties panel showing a selected function generator: Designator, X, Y and Rotation under Placement, then AC magnitude, AC phase, peak-to-peak amplitude, DC offset, duty cycle, edge time and frequency under Parameters, with output resistance and waveform shape below the fold](docs/images/03-inspector.png)

Probes attach to terminals and stream into the scope, which redraws on its own timer so the render
rate is decoupled from the solver. A probe reads **voltage, current or logic level** — pick which
from the trace list — and each trace carries its own unit, so a current reads `26.4 mA` rather than
being squeezed onto a volts axis:

![The 555 astable example running, with the capacitor and output waveforms on the oscilloscope](docs/images/04-scope.png)

The scope answers what a circuit does over *time*. **Simulate > Frequency Response** answers the
other question — what it does to a sine at each frequency — by linearising the whole circuit about
its bias point and solving it once per frequency over a complex matrix. Magnitude and phase against
a logarithmic frequency axis, with the −3 dB corner worked out for you. A capacitor at one
frequency *is* an admittance of jωC, so there is no integration error anywhere in the answer, which
makes it exact in a way that measuring the same thing by stepping a generator is not. It is also
strictly small-signal: it says nothing about clipping, slew limiting or distortion, and the
oscilloscope remains the instrument for those.

For audio there is a third answer, which is to listen to it. A **Speaker** takes a `Recording
Path` and writes the voltage across it to a WAV as the simulation runs, resampled onto a fixed
grid; **Simulate > Play Speaker Recording** hands that file to the desktop. A **Microphone** takes
a `Source Path` and plays a WAV instead of its built-in tone, so real programme material goes
through the circuit rather than one frequency for ever. Clipping is a small flat spot on a trace
and an unmistakable noise, and which of those conveys how bad it is depends on who is looking.

It is files rather than the sound card on purpose: a variable-step transient solver cannot produce
forty-four thousand samples a second on time for ever, and a file makes the same run reproducible
and assertable. The WAV reader and writer are a couple of hundred lines here rather than a
dependency shipped for six runtime identifiers.

Seventy-four worked circuits ship with it, grouped and searchable in **File > Examples...** rather
than piled into a submenu — Fundamentals, Analog, Power Supplies, Switching & Motors, Digital
Logic, Timers & Oscillators, Buses & Interfaces, Sensors, Signal Integrity & RF, Audio, Displays
and Development Boards. Each one is run by the test suite and asserted against the thing it exists to show, so a
broken example fails a build rather than a user.

A real circuit's controls are on its front panel, not scattered across its schematic. The
**CONTROLS** panel gathers every switch, potentiometer, light level, temperature, magnet and target
distance in the circuit into one place, and they work **while the simulation runs** — move a wiper
and the next time point is solved with the new value:

![The editor running the motor reversing example, with a CONTROLS panel at the bottom right listing SW1, SW2 and SW3 as toggle switches](docs/images/14-controls.png)

Switches become toggles and ranges become sliders showing the value and its unit. Ranges spanning
several decades are laid out logarithmically, because a linear slider from a dark room to direct
sun puts everything a circuit responds to in the first half-millimetre of travel. What appears is
decided by the components rather than the interface: a property marked `[Operable]` becomes a
control, so a part added later gets one without any of this being touched. The panel and the canvas
stay in step — double-clicking a part on the schematic still works it, and the panel follows.

`Ctrl+E` writes the schematic and the traces out as **PNG, JPEG, BMP, SVG or PDF** — the schematic,
the scope, or both on one sheet, optionally with a parts list. SVG and PDF are true vector rather
than a bitmap in a wrapper, because the export is drawn by the same renderer that draws the editor
against a second backend; captions come out as real text, not outlines:

![An exported sheet: the full-wave rectifier schematic on top with its probe flags, and below it the oscilloscope showing the two antiphase secondary halves against the flat rectified rail](docs/images/11-export-sheet.png)

The frame follows the circuit rather than the window, so whatever the view is scrolled to you get
the whole thing at its natural size. The editing aids — dot grid, terminal dots, hover highlighting
— stay behind. `docs/USER_GUIDE.md` has [the rest](docs/USER_GUIDE.md#exporting).

| | |
| --- | --- |
| Place | Click a palette entry, then click the canvas |
| Wire | `W`, click a terminal, click empty space for corners, click a terminal to finish |
| Probe | `P`, then click a terminal |
| Select | `V` — drag to move, `R` to rotate, `Delete` to remove |
| Undo | `Ctrl`+`Z`, redo with `Ctrl`+`Y` or `Ctrl`+`Shift`+`Z`. The Edit menu names what it will undo. Covers placing, moving, rotating, deleting, wiring, probing and parameter edits; a drag is one step, not one per pixel |
| View | `Ctrl`+`+` / `Ctrl`+`-` zoom in and out, wheel zooms at the cursor, middle-drag or space-drag pans, `F` fits the whole circuit — captions and all |
| Scope | The vertical range follows the traces by default, so changing a source amplitude keeps the waveform on screen. The `Auto` checkbox turns that off, and using the vertical `+`/`-` buttons turns it off for you |
| File | `Ctrl+N` new, `Ctrl+O` open, `Ctrl+S` save, `Ctrl+Shift+S` save as, and `File > Settings` for the theme. The title bar shows the document and an asterisk while there are unsaved edits |
| Export | `Ctrl+E` writes the schematic and the traces out as PNG, JPEG, BMP, SVG or PDF, optionally with a grouped parts list. SVG and PDF are true vector, drawn by the same code that draws the screen. The frame follows the circuit rather than the view |
| About | `Help > About` reports the version and the libraries it is built on, read from the loaded assemblies rather than a hand-kept list, with a button that copies the lot for a bug report |
| Transport | `F5` run/pause, `F6` single step, `F8` reset |
| Controls | Every switch, potentiometer, light level, temperature and magnet — and every supply voltage, generator frequency and amplitude — is gathered into a **CONTROLS** panel under the properties panel, and can be worked while the simulation runs. Sweep a frequency with the scope running and watch a filter turn over. A part declares a control by marking the property `[Operable]`, so a new component gets one without the UI being edited |
| Interactive | Switches, buttons, logic toggles, LDRs and thermistors are operated by double-clicking them. They are ringed with a small dot so you can tell which parts those are; **View > Mark Interactive Parts** turns the rings off |
| Panels | `F9` collapses the palette to the left, `F10` the properties panel to the right — or click the chevron in either panel's header. A collapsed panel leaves a labelled rail at the edge; click it to bring the panel back. Palette groups also collapse individually (`All` toggles every group) |

The toolbar wraps onto extra rows rather than overflowing, and `MinWidth` is deliberately small, so
the window stays usable at whatever size a tiling window manager hands it.

Component values accept engineering notation: `10k`, `2.2M`, `100R`, `4k7`, `100nF`, `1kHz`. The
odd-looking one is **RKM code** (BS 1852, now IEC 60062), where the multiplier letter stands where
the decimal point would have been — `4k7` is 4.7 kΩ and `4R7` is 4.7 Ω — because a decimal point is
the easiest mark on a drawing to lose and `47` against `4.7` is a factor of ten. Note that a
**SPICE card reads the same text differently**: `4k7` is 4 000 there, because SPICE takes the first
letter after the digits as the scale and discards the rest. Both are right where they are, and the
[guide](docs/USER_GUIDE.md#4k7-is-not-a-typo) has the detail.

Switches, push buttons and logic toggles are operated by **double-clicking them on the canvas**
rather than through the inspector.

The properties inspector is built by reflecting over each component's public settable properties,
so a new component type gets a complete editor without any UI code.
