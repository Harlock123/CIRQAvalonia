# CirqAvalonia User Guide

CirqAvalonia is a circuit editor and mixed-signal simulator. You draw a schematic, attach probes
where you want to look, and press run — the analog network is solved with Modified Nodal Analysis
while logic devices run on an event scheduler, in the same time loop.

This guide walks through the editor. If you want to know how the solver works instead, that is in
the [README](../README.md), and what changed between releases is in the
[changelog](../CHANGELOG.md).

**Contents**

1. [Running it](#running-it)
2. [The window](#the-window)
3. [Placing components](#placing-components)
4. [Wiring](#wiring)
5. [Setting values](#setting-values)
6. [Probes and the oscilloscope](#probes-and-the-oscilloscope)
7. [Running a simulation](#running-a-simulation)
8. [Worked example: an RC low-pass](#worked-example-an-rc-low-pass)
9. [Digital and mixed-signal circuits](#digital-and-mixed-signal-circuits)
10. [Development boards](#development-boards)
11. [Saving and loading](#saving-and-loading)
12. [Appearance](#appearance)
13. [Keyboard reference](#keyboard-reference)
14. [When a circuit will not simulate](#when-a-circuit-will-not-simulate)

---

## Running it

```bash
dotnet run --project src/Cirq.UI
```

Requires the .NET 10 SDK. Nothing else — there is no install step and no configuration file to
write. Preferences are created on first use.

---

## The window

![The CirqAvalonia editor: menu bar, component palette on the left, schematic canvas in the centre, properties panel on the right, and the oscilloscope drawer across the bottom](images/01-overview.png)

Five regions, and they stay put:

| Region | What it is for |
| --- | --- |
| **Menu bar** | Every command. File, Edit, Tools, View, Simulate, Help |
| **Components** (left) | The parts palette, grouped into collapsible categories |
| **Canvas** (centre) | The schematic. Pan, zoom, place, wire, probe |
| **Properties** (right) | Parameters of whatever is selected |
| **Scope** (bottom) | Timebase and vertical controls, the plot, and the trace list |
| **Status bar** | Active tool, node and unknown counts, speed, and elapsed simulation time |

Both side panels collapse to give the canvas more room — `F9` for the palette, `F10` for
properties, or click the chevron in either panel's header. A collapsed panel leaves a labelled rail
at the window edge; click the rail to bring it back.

---

## Placing components

![The palette with every category expanded, showing passive parts, sources, semiconductors, transistors, LEDs and displays, power, analog ICs, logic gates and the 74xx series](images/02-palette.png)

Click a palette entry, then click the canvas. The part lands where you click, snapped to the grid.

The palette holds **77 components in 12 categories**:

| Category | Count | Contents |
| --- | --- | --- |
| Passive | 5 | Resistor, capacitor, inductor, transformer, potentiometer |
| Switches | 3 | SPST, SPDT, push button |
| Sources | 4 | Ground, DC voltage, DC current, function generator |
| Semiconductors | 5 | 1N4148, 1N4001, Schottky, 5.1 V and 12 V zeners |
| Transistors | 7 | NPN and PNP bipolars, N- and P-channel MOSFETs |
| LEDs & Displays | 8 | Six LED colours, common-anode and common-cathode seven-segment |
| Power | 6 | Fixed and adjustable regulators |
| Analog ICs | 8 | LM741, NE555, LM311, LM339 and friends |
| Logic Gates | 7 | AND, OR, NAND, NOR, XOR, XNOR, NOT |
| 74xx Series | 16 | Counters, decoders, flip-flops, shift registers, multiplexers |
| Digital I/O | 4 | Logic toggle, clock, and indicators |
| Dev Boards | 4 | Raspberry Pi, Arduino Uno / Nano / Mega — see [below](#development-boards) |

Click a category header to open or close it. **All** in the palette header toggles every group at
once — useful when you are hunting for a part and do not remember which group it is in.

Switches, push buttons and logic toggles are operated by **double-clicking them on the canvas**,
not through the properties panel. A push button is momentary; a switch latches.

---

## Wiring

Press `W` (or **Tools > Wire**), then:

1. Click a terminal to start.
2. Click empty canvas to drop a corner — wires route orthogonally, so corners are how you steer.
3. Click another terminal to finish.

Terminals highlight as the pointer approaches, so you can see what you are about to connect to
before you commit. A wire must end on a terminal; clicking empty space only adds a corner.

Press `V` to go back to the select tool.

---

## Setting values

![The properties panel showing a selected function generator with its placement, amplitude, offset, duty cycle, edge time, frequency, output resistance and waveform shape](images/03-inspector.png)

Select a component and the right-hand panel fills with its parameters. Selected parts are outlined
on the canvas, so there is never a question of what you are editing.

Values accept **engineering notation**, the way you would say them out loud:

| You type | You get |
| --- | --- |
| `10k` | 10 000 |
| `2.2M` | 2 200 000 |
| `100R` | 100 |
| `4k7` | 4 700 |
| `100nF` | 100 × 10⁻⁹ |
| `1kHz` | 1 000 |

The panel is generated by reflecting over each component's public settable properties, which is why
every part has a complete editor and why the same set of values is what gets saved to a file. If
you can edit it, it is saved; if it is saved, you can edit it.

---

## Probes and the oscilloscope

Press `P` (or **Tools > Probe**) and click a terminal. A coloured pennant marks the node and a
matching trace appears in the scope.

![The 555 astable example running, with the capacitor and output waveforms on the scope](images/04-scope.png)

The scope controls, left to right:

| Control | What it does |
| --- | --- |
| **Timebase −/+** | Seconds per division. The readout shows the current setting |
| **Vertical −/+** | Volts per division |
| **Auto** | Keeps the vertical range fitted to the traces. Using the vertical `−`/`+` buttons turns it off for you |
| **Layout** | *Unified* overlays all traces, *Stacked* offsets them into lanes, *Tiled* gives each its own axes |
| **AC couple** | Subtracts each trace's DC average before display |
| **Auto-scroll** | Follows the newest data instead of holding at t = 0 |
| **Clear traces** | Empties the buffers without resetting the circuit |

Each trace in the list has a checkbox to hide it, a live readout of its present value, and an `×`
to remove the probe entirely.

**Auto** is on by default and is the setting most people want: change a source's amplitude and the
waveform stays on screen instead of running off the top.

---

## Running a simulation

Transport lives on the **Simulate** menu and on three keys:

| Key | Action |
| --- | --- |
| `F5` | Run / pause |
| `F6` | Single step |
| `F8` | Reset back to t = 0 |

**Simulate > Speed** sets how fast simulated time advances against wall-clock time:

- Real time (1×)
- 1/10, 1/100, 1/1000, 1/10000 speed
- Maximum throughput — run as fast as the solver can, ignoring the clock

Slow it down to watch a counter tick; use maximum throughput to get a long waveform quickly. The
status bar shows the elapsed simulated time and the solver's rate while it runs.

The simulation starts from initial conditions: capacitors begin at their stated initial voltage and
inductors at their initial current, both zero unless you say otherwise. A source applied at t = 0 is
therefore a genuine step, not a circuit that has already settled.

---

## Worked example: an RC low-pass

Building the first screenshot from an empty canvas, to put the above together:

1. **File > New**.
2. From **Sources**, place a **Function Generator** on the left of the canvas.
3. From **Passive**, place a **Resistor** to its right, and a **Capacitor** to the right of that.
4. From **Sources**, place two **Ground** symbols below the generator and the capacitor.
5. Press `W` and wire: generator output → resistor left; resistor right → capacitor top; generator
   bottom → its ground; capacitor bottom → its ground.
6. Select the resistor and set **Resistance** to `10k`. Select the capacitor and set **Capacitance**
   to `100nF`.
7. Select the generator; set **Shape** to `Square`, **Frequency** to `500Hz`, **Amplitude Peak To
   Peak** to `4`.
8. Press `P` and click the junction between generator and resistor, then the junction between
   resistor and capacitor.
9. Press `F5`.

You should see a square wave and the exponential charge and discharge of the capacitor chasing it —
the RC time constant here is 10 kΩ × 100 nF = 1 ms, against a half-period of 1 ms, so the capacitor
gets roughly two-thirds of the way each time.

Everything above is also in **File > Examples**, along with thirteen other circuits.

---

## Digital and mixed-signal circuits

![The digit counter example: a 7490 decade counter feeding a 7447 decoder driving a seven-segment display, with QA and QD on the scope](images/05-digital.png)

Logic parts are event-driven rather than solved at every time point. Outputs are stamped into the
analog network as Thevenin sources at the logic family's VOH/VOL, inputs are classified against
VIH/VIL, and transitions are queued at `t + tpd`.

What that means in practice is that logic and analog genuinely interact. You can hang a resistor
off a gate output and it will load it; a 555's internal comparators respond to the RC network you
built around them; a 7447 releases its unlit segments rather than driving them high, because it is
an open-collector sink driver and the model says so.

The transient loop truncates its own step so a time point lands exactly on the next queued
transition, so edges are not smeared across a step.

Useful examples to start from: **NAND Latch**, **Decade Counter**, **Ring Oscillator**,
**Digit Counter**, **Running Light**.

---

## Development boards

A Raspberry Pi or Arduino can sit on the schematic as either the **source** of signals or the
**sink** for them, with its whole header available to wire.

| Board | Logic | Pins |
| --- | --- | --- |
| Raspberry Pi (40-pin) | 3.3V, **not 5V tolerant** | 26 GPIO, 8 GND, 2×5V, 2×3.3V |
| Arduino Uno R3 | 5V | 14 digital (6 PWM), 6 analog |
| Arduino Nano | 5V | 14 digital (6 PWM), 8 analog |
| Arduino Mega 2560 | 5V | 54 digital (15 PWM), 16 analog |

One Pi component covers the Pi 2, 3, 4, 5 and Zero — they all share the same header. Pins are laid
out as they are on the hardware, odd numbers down one side and even down the other, so counting
pins on screen matches counting them on the board.

### Setting pins up

Select a board and fill in its **Pins** field. One line describes the whole setup:

```
GPIO17=high; GPIO18=clock@1kHz; GPIO22=in-pullup; GPIO12=pwm@500Hz:25%
```

| Mode | Meaning |
| --- | --- |
| `in` | Read the pin. This is what an unlisted pin does, so a board powers up with everything reading |
| `in-pullup` / `in-pulldown` | Read, with the internal pull engaged |
| `high` / `low` (or `1` / `0`) | Drive the pin at the board's logic voltage |
| `clock@1kHz` | A square wave — this is what makes a board a signal *source* |
| `pwm@500Hz:25%` | A square wave at a stated duty cycle |
| `seq@1kHz:1101_0010` | Steps through a bit pattern and repeats it |
| `once@1kHz:001` | Steps through the pattern once, then holds its last step |

### Sequences

`seq` and `once` play a pattern one step at a time, at the rate you give them. A pattern is made of
`1`, `0` and `z`, and `_` or spaces group it for readability — `1101_0010` is far easier to check
by eye than `11010010`:

```
GPIO17=seq@1kHz:1101_0010      loops forever, one step per millisecond
GPIO27=once@1kHz:001           low, low, high — then holds high
GPIO22=seq@500Hz:1z0z          releases the pin on every other step
```

The difference between the two matters:

- **`seq`** wraps round to the start, so it is a repeating waveform — a data pattern, a bit
  sequence feeding a shift register, an address burst you want to watch cycle on the scope.
- **`once`** holds its final step instead of wrapping, which is what makes it usable as a
  power-on **reset pulse** or an initialisation burst. A looping pattern would yank the line back
  down every few milliseconds; a one-shot lets go and stays let go.

A `z` step releases the pin for that step, exactly as though it were an input, so whatever else is
on the node decides the level. That is how you model an open-drain line, or hand a shared bus over
to another device mid-pattern.

Patterns are capped at 256 steps, which is far more than a schematic needs.

Frequencies accept the same engineering notation as everything else (`500Hz`, `1kHz`, `2.5kHz`).
The **Status** readout underneath says how the line was understood, and names anything it could not
parse — a typo in one entry costs that pin, not the whole setup.

Power pins are real sources: a board feeds 5V and 3.3V to the circuit around it, which you can turn
off with **Supplies Power** if the board is being powered externally. Wire at least one **GND** pin
into your circuit — every other pin on the board is measured against it.

### The board tells you when you are about to break it

Boards are checked against their datasheet limits as the simulation runs, and a violation appears
in red in the status bar:

- A **Raspberry Pi GPIO above 3.3V**. The Pi is not 5V tolerant, and tying a GPIO to 5V logic is
  the single most common way to destroy one. An Arduino in the identical circuit says nothing,
  because a 5V board is fine there.
- A **pin over its current rating** — 16 mA on a Pi, 20 mA on an Arduino. An LED with no series
  resistor lands here.
- The **total GPIO budget** — 50 mA on a Pi, 200 mA on an Arduino — which several pins can exceed
  while each one stays inside its own rating.

These are not solver failures. The circuit solves perfectly well; it is the hardware that would
not survive it, which is exactly the thing a simulator is for finding out.

### A note on logic levels

Boards use real thresholds, so real interfacing problems show up. A 74xx TTL output tops out at
3.4V and a 5V CMOS input wants 3.5V to call it high — so an Arduino reads a bare TTL gate as
undefined rather than as a one. That is not a modelling artefact; it is the reason level shifters
exist.

---

## Saving and loading

`Ctrl+S` saves, `Ctrl+Shift+S` saves under a new name, `Ctrl+O` opens, `Ctrl+N` starts fresh. The
title bar shows the file name and an asterisk while there are unsaved edits.

Circuits are stored as `.cirq` files — indented JSON, so they read sensibly and diff sensibly in
version control. Wires and probes reference terminals as (component id, pin id), so they reattach
to exactly the right pins when reopened.

Loading is deliberately forgiving. An unknown component type, a wire whose endpoint has gone, or a
device model this build no longer ships are each reported as warnings while the rest of the circuit
still opens. Only a malformed file, or one written by a newer format version, is refused outright.

Saves are written to a temporary file and moved into place, so an interrupted write cannot destroy
the file you already had.

---

## Appearance

**File > Settings** opens preferences.

![The settings dialog showing the theme selector set to System, with a note reporting what the desktop currently uses](images/06-settings.png)

Four themes:

| Theme | Behaviour |
| --- | --- |
| **System** | Follows the desktop's light/dark setting, and keeps following it if you change it while the app is running |
| **Light** | Always the light palette |
| **Dark** | Always the dark palette |
| **High contrast** | Black grounds, white text and symbols, yellow selection, cyan values |

On **System** the dialog also tells you what the desktop is currently reporting — or admits that it
cannot tell. On a bare window manager there may be no portal to ask, and silently falling back to
dark would otherwise look like the setting had been ignored.

Light and high contrast, same circuit:

![The same RC low-pass circuit in the light theme](images/07-theme-light.png)

![The same RC low-pass circuit in the high contrast theme, with pure black grounds and white symbols](images/08-theme-high-contrast.png)

Changes apply and save immediately — there is no OK/Cancel to get wrong. Preferences live in
`~/.config/CirqAvalonia/settings.json` on Linux and the equivalent elsewhere. A damaged file falls
back to defaults rather than stopping the application from starting.

---

## Keyboard reference

Also available in the app at **Help > Keyboard Shortcuts**.

| | |
| --- | --- |
| `V` | Select tool — drag to move |
| `W` | Wire tool |
| `P` | Probe tool |
| `R` | Rotate selection |
| `Delete` | Delete selection |
| `F` | Zoom to fit |
| Wheel | Zoom at the pointer |
| Middle-drag / space-drag | Pan |
| Double-click | Operate a switch, push button or logic toggle |
| `F5` / `F6` / `F8` | Run-pause / step / reset |
| `F9` / `F10` | Collapse the palette / the properties panel |
| `Ctrl+N` / `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | New / open / save / save as |

---

## When a circuit will not simulate

The circuit is compiled before it runs, so structural problems are reported up front rather than
quietly producing zeros.

| Message | Usually means |
| --- | --- |
| Missing ground | No node is fixed at 0 V. Place a **Ground** from **Sources** and wire it in |
| Voltage source loop | Two sources in parallel, or a source shorted by a wire |
| Singular matrix | A node connects to nothing else, or a section is isolated from ground |
| Failed to converge | A non-linear device is being asked for something extreme |

Convergence failures are worth a word. The solver already damps Newton-Raphson, limits junction
voltages with SPICE's `pnjlim`, and falls back to Gmin stepping when a bias point will not settle
directly. If it still fails, the circuit itself is usually the problem — an LED with no series
resistor, a transistor with its base driven straight from a stiff voltage source, a supply shorted
to ground. Add the resistor that would exist in reality and it will generally solve.

If a waveform looks wrong rather than absent, check the timebase before suspecting the solver: at a
slow timebase a fast signal is aliased into nonsense. The status bar shows how far simulated time
has actually advanced.
