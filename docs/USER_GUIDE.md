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

![The component palette showing all sixteen categories with their counts — passive, switches, sources, semiconductors, transistors, LEDs and displays, power, analog ICs, logic gates, 74xx series, 40xx series, buses, digital I/O, sensors and actuators, switching and isolation, and dev boards — with the Passive group open](images/02-palette.png)

Click a palette entry, then click the canvas. The part lands where you click, snapped to the grid.

The palette holds **140 components in 16 categories**:

| Category | Count | Contents |
| --- | --- | --- |
| Passive | 8 | Resistor, capacitor, electrolytic capacitor, inductor, transformer, **centre-tapped transformer**, potentiometer, **crystal** — see [below](#crystals) |
| Switches | 4 | SPST, SPDT, push button, **8-way DIP switch** |
| Sources | 6 | Ground, DC voltage, DC current, function generator, battery, **solar cell** — see [below](#batteries-and-panels) |
| Semiconductors | 11 | 1N4148, 1N4001, Schottky, zeners, bridge rectifier, SCR, triac, diac, **TVS and varistor** — see [below](#surge-protection) |
| Transistors | 10 | NPN and PNP bipolars, N- and P-channel MOSFETs, **three JFETs** — see [below](#jfets) |
| LEDs & Displays | 9 | Six LED colours, seven-segment displays, **HD44780 character LCD** — see [below](#the-character-lcd) |
| Power | 9 | Fixed and adjustable regulators, TL431 shunt reference, ICL7660 charge pump, **MC34063 switching controller** — see [below](#switching-instead-of-dropping) |
| Analog ICs | 10 | LM741, NE555, LM311, LM339, **LM386** audio amp and **INA126** instrumentation amp |
| Logic Gates | 7 | AND, OR, NAND, NOR, XOR, XNOR, NOT |
| 74xx Series | 18 | Counters, decoders, flip-flops, shift registers (including the **74595**), multiplexers, Schmitt inverter |
| 40xx Series | 14 | CMOS gates, counters, flip-flops, analog switches — see [below](#the-40xx-series) |
| Buses | 5 | I2C master, EEPROM, port expander and **DS1307 clock**, SPI master — see [below](#i2c-and-spi) |
| Digital I/O | 6 | Logic toggle, clock, indicators, rotary encoder, **oscillator module** |
| Sensors & Actuators | 13 | DC motor, LDR, thermistors, buzzers, speaker, microphone, servo, stepper, thermocouple, load cell, **HC-SR04 ranger** — see [below](#sensors-and-actuators) |
| Switching & Isolation | 6 | Relay, fuses, optocouplers, **ULN2003** — see [below](#switching-and-isolation) |
| Dev Boards | 4 | Raspberry Pi, Arduino Uno / Nano / Mega — see [below](#development-boards) |

Click a category header to open or close it. **All** in the palette header toggles every group at
once — useful when you are hunting for a part and do not remember which group it is in.

Switches, push buttons and logic toggles are operated by **double-clicking them on the canvas**,
not through the properties panel. A push button is momentary; a switch latches. LDRs and
thermistors work the same way — double-clicking covers or warms them.

Anything you can operate is ringed with a small dot beside its designator, so you do not have to
remember which parts those are. Turn the rings off with **View > Mark Interactive Parts** once
they have served their purpose.

Everything you do to the schematic can be taken back with `Ctrl` `Z`, and put back with `Ctrl` `Y`
(or `Ctrl` `Shift` `Z`). The Edit menu names what it is about to reverse — **Undo Add Resistor**,
**Undo Move Capacitor** — so you are not guessing at what the last thing was.

Undo covers edits to the document: placing, moving, rotating and deleting parts, wiring, attaching
probes, and changing values in the properties panel. A drag across the canvas costs one step rather
than one per pixel travelled. Opening a file or loading an example starts a fresh history, since
there is nothing sensible to step back into.

What it does not cover is **operating** the circuit — double-clicking a switch, or pressing Run.
Those are things you do *to* a running circuit rather than edits to the drawing, and having them
fill the undo history would push your actual edits out of reach.

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

## Measuring current

A probe reads a **voltage** by default, but it can read the **current** into the pin it is attached
to instead. Pick which from the dropdown beside the trace in the scope's trace list.

That is worth knowing about, because a great deal of what a circuit is doing is only visible as
current. The holding current that drops a triac out at every zero crossing, the flyback spike a
relay coil produces, what a motor draws as it stalls, whether an LED is getting 5 mA or 50 — none
of it shows up in a voltage trace unless you put a shunt in and do the arithmetic yourself.

The sign convention is a clamp meter's: **positive is current flowing into the component through
the pin you clamped**. Probe both ends of a resistor and you get equal and opposite readings; probe
all three pins of a transistor and they sum to zero.

Each trace carries its own unit, so a current reads `26.4 mA` rather than `0.0264`. In **Tiled**
layout every trace gets its own axis, labelled with its own unit, which is the comfortable way to
look at a current and the voltage driving it together. In **Unified** layout they share one axis
and it is labelled for whatever is on it.

Changing what a probe measures clears the trace — the samples already recorded are in the wrong
units, and rescaling them would be a lie.

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

Everything above is also in **File > Examples**, along with twenty-four other circuits. Six of them
exercise the newer parts: **Lamp Dimmer** (triac and diac phase control), **SCR Latch** (a thyristor
that stays on after you let go of the button), **LED Chaser** (a 4017), **4060 Timer** (a chip
clocking itself from one resistor and one capacitor), **Staircase Generator** (a 4040 addressing a
4051) and **JFET Amplifier**.

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

### Building a power supply

Three parts cover the usual linear supply, and each models the thing that actually bites:

- **Bridge Rectifier** (Semiconductors) — four real diodes, not an idealised block, so the output
  peak sits about 1.4 V below the input peak. Both halves of the cycle conduct, giving ripple at
  twice the input frequency.
- **Electrolytic Cap** (Passive) — polarised, with real series resistance. ESR is what actually
  sets the ripple on a smoothing capacitor, so it is solved rather than assumed.
- **Regulator 7805 / 7809 / 7812 / 7905 / LM317 / LD1117** (Power) — fixed and adjustable linear
  regulators, with dropout, current limiting and thermal shutdown.

An electrolytic reports being used outside its ratings, and is ringed in red on the canvas:

- **Connected backwards.** The classic destructive mistake — a reversed electrolytic vents. It
  has to be held backwards for a millisecond before it complains, so the startup transient of a
  supply coming up around it does not raise a false alarm.
- **Over its working voltage**, which is the other number printed on the can.

**File > Examples > Linear Power Supply** builds the whole chain — a 14 V secondary through a
bridge into a 1000 µF reservoir into a 7812. The two probes are the point: the reservoir rail
sags and recharges twice per mains cycle, and the regulated rail beside it is flat. It is the one
example that sets the simulation speed, because a 50 Hz cycle at the usual 1/1000 would take fifty
seconds of wall time.

**TL431** is a programmable shunt reference — a zener whose voltage you choose. Tie its reference
pin to its cathode and it is a fixed 2.5 V shunt; feed the reference from a divider off the cathode
and it holds the cathode at 2.495 × (1 + R₁/R₂). It needs a milliamp or so through it to regulate
at all, and sizing the feed resistor so the load takes everything is the classic way to end up with
a circuit that almost works, so it reports being starved.

**74HC14** is a hex Schmitt-trigger inverter, and its two thresholds are what make it useful. An
ordinary gate has one, so an input creeping through it produces a burst of output chatter. A
Schmitt input will not call a rising input high until it clears about 0.58 of the supply, nor a
falling one low until it drops past about 0.38 — and between them it remembers. That is what cleans
up a slow edge, debounces a contact, and lets a single gate with a resistor from output back to
input and a capacitor to ground free-run as an oscillator.

**Transformer (CT)** is the other way to rectify, and for fifty years it was the usual one. Its
secondary has a tap at the middle; take that tap as your zero volt line and the two ends swing in
opposite directions, so **two diodes give you full-wave rectification** instead of a bridge's four.
The saving is not the two diodes — it is that the current only ever passes through one of them, so
you lose one forward drop instead of two. At five volts that is most of a volt, which is why the
arrangement outlived the bridge in low-voltage supplies. Rectify each half separately against the
tap instead and you have a positive and a negative rail from one winding, which is where every
±15 V op-amp supply comes from.

The secondary is modelled as what it physically is — two windings in series sharing a core, each
with a quarter of the whole inductance, all three coupled — so the tap is a real connection to the
middle rather than an ideal half-voltage point, and loading one half unevenly pulls the other about
the way it does in practice. The windings have resistance, because a winding is a long piece of
thin wire; it is also what limits the inrush when the supply is first switched on.

**File > Examples > Full-Wave Rectifier** wires one up with two 1N4001s. Both halves are on the
scope against the rectified rail, and the rail sits one diode drop below the peaks rather than two.

**Inductor saturation.** An inductor has a `SaturationCurrent`, and it is left at zero — meaning
ideal — so nothing that worked before changes. Set it and the part behaves like iron: past the
knee the core cannot take any more flux, the inductance collapses, and the current stops being
limited by anything except resistance. That is the failure mode behind a switching supply that
works on the bench and dies at full load, and behind an inductor that gets hot without the
waveform ever looking wrong. It is worth setting on any inductor carrying real current, because a
part that is ideal at ten amps is not telling you anything.

Give the regulator headroom. It needs its dropout voltage above the output *at the bottom of the
ripple*, not on average — a supply that measures fine on a meter can still be dropping out on
every trough.

---

## Sensors and actuators

**DC Motor** is modelled electrically and mechanically at once, because the two are inseparable.
The armature is a resistance and an inductance in series with a back-EMF proportional to speed;
the torque is proportional to current, and the rotor's inertia integrates the difference between
that torque and the load.

That coupling is why a motor cannot be simulated as a resistor. At rest there is no back-EMF, so
the armature draws the **stall current** — a 12 V motor with a 3 Ω armature pulls 4 A — and only
as the rotor spins up does the back-EMF rise and choke the current back to a few hundred
milliamps. The startup surge is what trips supplies and welds relay contacts, and it falls out of
the model rather than being asserted.

Load the shaft and it slows until torque balances, so the current a motor draws is set by what it
is driving rather than by the supply. Load it past what it can turn and it sits stalled with
nothing but armature resistance limiting the current — the motor is ringed in red and the fault
named, because that is how they burn out.

**LDR** is a cadmium-sulphide cell whose resistance follows a power law, roughly
`R = R₁₀·(10/E)^γ` with γ near 0.8. A decade of light is a fixed ratio of resistance — about 6.3×
at that γ — so the part covers several decades between a dark room and daylight. That span is why
an LDR is normally read with a comparator against a divider rather than measured directly.

Double-click it on the canvas to cover and uncover it, the same as operating a switch, so a
light-sensing circuit can be exercised while the simulation runs.

**Thermistor** comes in both flavours. An NTC follows the Beta equation, `R = R₂₅·exp(B·(1/T −
1/T₂₅))` with the temperatures in kelvin — the real curve, and steeply non-linear: a 10 kΩ B3950
bead reads 33 kΩ at freezing and 2.5 kΩ at 60 °C. Treating that as a straight line is the usual
reason a home-made thermometer is right at one temperature and nowhere else. A PTC is specified by
a coefficient per kelvin instead, so it is modelled that way rather than by forcing one equation to
cover both.

Double-click it to swing between cold and warm while the simulation runs, and pair it with a
comparator to act on temperature.

**Buzzer** comes in the two sorts you can buy, and the difference is the point rather than a
detail. A **passive** piezo is a capacitor: it turns a *changing* voltage into movement, so a
steady one does nothing at all. Wiring one across a pin that is simply switched high is the most
common reason a first buzzer circuit is silent — so that is reported, with the buzzer ringed in
red, rather than drawing a plausible current and leaving you to wonder. An **active** buzzer has
its own oscillator behind the element and DC is exactly what it wants.

A passive element sounds at whatever it is fed, and the frequency shown is measured from the drive
rather than assumed.

**Ultrasonic Ranger** is the HC-SR04, and the whole part is a lesson in one idea: **the distance is
in the width of a pulse, and nowhere else**. Give TRIG at least ten microseconds high, and about
450 µs later ECHO goes up and stays up while the chirp is out and back — **58 microseconds per
centimetre**, which is simply the speed of sound over a round trip. Nothing on the wire tells you
the distance; your code has to time an edge, which is why driving one from an interrupt is a
different exercise from reading a sensor over a bus.

Both of its awkward behaviours are modelled, because both catch people:

- A trigger pulse shorter than ten microseconds is **ignored entirely**, so a sloppy `digitalWrite`
  pair that happens to take eight gets you nothing at all rather than a wrong answer.
- With nothing in range it does not stay quiet — it gives a **38 millisecond pulse** and gives up.
  Code with no timeout reads that as about six metres, which is why a ranger pointed at the sky
  reports something absurd instead of hanging.

Double-click it to move the target between `DistanceCentimetres` and `AlternateDistance` while the
simulation runs, and watch the echo pulse change width. **File > Examples > Ultrasonic Ranger**
has one pinged a hundred times a second with both pins on the scope.

---

## Switching and isolation

Three parts for the boundary between a control circuit and the thing it controls. Each reports the
mistake that actually destroys it.

**Relay** — a coil and a changeover contact. The coil is an inductor, so interrupting its current
produces `v = L·di/dt`: hundreds of volts, backwards, in microseconds. That spike is what kills the
transistor driving it, and the relay says so — the coil is ringed in red and the status bar names
the fault. Put a diode across the coil, cathode to the positive end, and the warning goes away
because the spike now has somewhere to go.

Pull-in and drop-out are deliberately different currents, so a coil sitting near the threshold
holds its state instead of chattering. `COM` connects to `NC` at rest and to `NO` when energised.

**Fuse** — opens on its melting integral rather than on instantaneous current, which is why a real
fuse survives an inrush many times its rating: the heat has to accumulate. A fuse carries its rated
current for ever, so only the excess counts. Doubling the rated current on a 1 A / 0.5 A²s fuse
takes about a sixth of a second; five times the rating takes twenty milliseconds. Once blown it
stays blown.

**ULN2003** — seven Darlington drivers, and the way logic drives anything with a coil in it. A
74xx output will not run a relay or a stepper winding, and doing it with a transistor per channel
is seven transistors and fourteen resistors.

Every channel **sinks**, which is the thing to get right: the load goes between the supply and the
output pin, not between the output and ground. An output that is off is not driving low, it is
disconnected — so a load wired from an output down to ground does nothing at all, which is the
usual first mistake. Being a Darlington it does not saturate to nothing either: about a volt stays
across a conducting output, which matters when a 5 V relay is running off a 5 V rail. Tie the COM
pin to the load's supply and the internal flyback diodes have somewhere to send the inductive kick.

**Optocoupler** — an infrared LED facing a phototransistor with no conductive path between them, so
the output side can sit at a completely different potential. This is the honest answer to "how do I
switch something dangerous from a 3.3 V board".

The isolated side still needs its own ground reference. That is a property of isolation, not a
limitation of the simulator — two circuits with nothing at all between them have no solution. A
real device leaks through some hundreds of gigohms and that is what is modelled, which keeps the
matrix solvable without meaningfully coupling the halves.

---

## The 40xx series

The 40xx CMOS parts have a palette group of their own rather than sitting among the 74xx ones,
because they are a different family with different habits — and mixing the two has a specific way
of going wrong, covered at the end of this section.

**Gates.** The 4001 (quad NOR), 4011 (quad NAND), 4070 (quad XOR), 4071 (quad OR), 4081 (quad AND)
and 4069 (hex inverter) do what their 74xx counterparts do, more slowly. The 4011 is the one you
reach for by reflex, the way a 7400 is in TTL.

The pinout is the trap. A 7400 puts gate two on pins 4 and 5 driving 6, and gate three on 9 and 10
driving 8; a 4011 puts gate two on 5 and 6 driving 4, and gate three on 8 and 9 driving 10. Only
gates one and four agree. Dropping a 4011 into a board laid out for a 7400 leaves you with two
working gates and two that do nothing sensible, which is a long afternoon if you are not expecting
it — so the parts here have the pinouts they really have, and wiring one as if it were the other
is a mistake you can make on the canvas too.

The family is at least consistent with itself: where TTL moves the 7402's outputs to pins 1, 4, 10
and 13, the CMOS NOR has exactly the same pinout as the CMOS NAND.

**4093** — the 4011's function and pinout with hysteresis on every input, and the reason is that it
makes an oscillator out of almost nothing. Tie one gate's inputs together, run a resistor from its
output back to them and a capacitor from them to ground, and it runs: the capacitor charges until
it crosses the upper threshold, the output flips, and it discharges until it crosses the lower one.
Four gates gets you four oscillators, or one oscillator and three gates to debounce the switches
feeding it. The period is the two exponentials end to end and the tests predict it rather than
record it.

**4013** — a dual D flip-flop, the same job as a 7474 with one difference that catches everybody:
set and reset here are **active high**, where the 7474's preset and clear are active low. A 7474
with those pins floating sits there and clocks; a 4013 wired the same way is held wherever the
noise on those pins decides. Tie them low. Feed Q' back to D and it halves the clock, which is what
most of them end up doing.

**4017** — a decade counter that decodes for you. Where a 7490 counts in BCD and needs a decoder to
show anything, exactly one of the 4017's ten outputs is high at a time and it walks along them on
each clock. That is the part behind every LED chaser: ten LEDs, ten pins, no decoder. Carry-out is
high for the first five counts, so it divides by ten and chains straight into the next stage.

**4040** — twelve flip-flops in a chain, so Q1 is the clock halved and Q12 is the clock divided by
4096. It is what you use to get from something fast to something slow. It counts on the **falling**
edge, the other way round from the 4017 beside it, and its reset is active high, so tie that low
too. Being a ripple counter rather than a synchronous one, its stages do not change together — a
real 4040 shows brief false codes as the carry walks down the chain, which is why decoding its
outputs directly is a way to collect glitches.

**4060** — fourteen stages like the 4040, plus the thing that makes it the part behind almost
every long-delay timer ever built: an oscillator of its own. Hang a resistor and a capacitor off
three pins and the chip clocks itself, so one part takes you from nothing to a divided-down output.

The oscillator is not a number you set here. `RS` is the input of an inverter, `REXT` is that
inverter's output, and `CEXT` is the output of a second one behind it; those three pins and your
own Rt and Ct are the whole circuit. The timing node charges towards REXT through Rt, and each time
it crosses the threshold CEXT flips and shoves the node a supply's worth the other way through Ct.
So the frequency comes out of the network — change a resistor on the canvas and it changes, the way
it would on a breadboard.

Wire it the way the datasheet does — Rt from REXT, Ct from CEXT and Rs from RS, all three meeting
at one node — and it lands on the datasheet's `f = 1/(2.3·Rt·Ct)`. Rs wants to be several times Rt.
Its job is to keep the input protection diodes out of the timing, because the capacitor throws the
node a whole supply past the rail twice a cycle and something has to catch it. Those diodes are
modelled, so leaving Rs out has a consequence here rather than none.

Three things about this part regularly cost an afternoon, and all three are modelled. It counts on
the **falling** edge. Master reset is active **high** and stops the oscillator as well as clearing
the count, so a floating MR pin gets you a chip that does nothing whatsoever. And the first three
stages are not brought out, nor is the eleventh — the outputs run Q4 to Q10 and then jump to Q12,
so the divisions you can have are ÷16 up to ÷1024, then ÷4096 to ÷16384. If you were counting on
÷2048, it is not there.

You can also ignore the oscillator entirely: drive RS yourself, leave REXT and CEXT floating, and
it is a plain fourteen-stage counter.

**4511** — the 7447's counterpart. That part sinks current from a common-**anode** display; this
one sources it into a common-**cathode** one, so its outputs are active high and the two are not
interchangeable. It also has a latch the 7447 lacks: hold `LE` high and the display freezes on
whatever was decoded while the counter behind it carries on. Inputs above nine blank the display
rather than showing the odd glyphs a 7447 produces.

**4066** — four independent analog switches, and one of two parts here that are not logic at all.
The switched pins carry whatever you put on them — audio, a sensor divider, a reference — and the
control pin only decides whether the path exists. Neither switched pin is an input or an output,
which is what *bilateral* means. A closed switch is some tens of ohms rather than a short, so
feeding a low-impedance load through one loses real signal.

**4051** — the 4066 with an address decoder in front of it: three address pins pick one of eight
channels and connect it to the common pin, leaving the other seven open. The path is bilateral
here too, so the same part reads eight sensors into one ADC pin or fans one signal out to eight
places, depending only on which end you drive. Inhibit is active high and disconnects everything,
which is how you park it or gang several onto one bus. Because the decoder only ever closes one
path, there is no way to short two sources together by accident — which four separate 4066 switches
will happily let you do.

### Mixing CMOS with TTL

These are CMOS, not TTL, and that matters the moment the two meet. A 4000-series input on a 5 V
rail wants 3.5 V before it will call a level high, and a 74xx output only guarantees 3.4 V. A TTL
part driving a CMOS input directly is therefore a circuit that works on someone's bench and not in
the simulator, or the other way round, depending on the particular chips. Pull the TTL output up to
the rail with a few kilohms, or drive the CMOS part from something that swings rail to rail.

Going the other way is fine: a CMOS output swings to within 50 mV of both rails, which is more than
a TTL input asks for.

CMOS is also much slower. The gates here are modelled at around 90 ns at 5 V against 11 ns for the
TTL equivalents, which is the real difference and occasionally the reason a design that works in
one family does not in the other. A real part speeds up substantially at 15 V; that is not
modelled.

---

## Batteries and panels

Every other source in the palette is ideal: it holds its voltage into a dead short and never runs
out. A battery does neither, and the difference is most of why a circuit that behaves on the bench
supply misbehaves on cells.

**Internal resistance** is the whole story. Five are stocked — an AA, a 9 V PP3, an 18650, a CR2032
coin cell and a sealed lead-acid — and they span four hundred to one in impedance. A coin cell is
ten ohms, so asking it for the twenty milliamps an LED wants costs it two hundred millivolts and it
browns out whatever it is powering. The lead-acid cell is twenty milliohms and will weld a
screwdriver. Swap one for the other under the same load and watch the rail move.

**It runs down.** Charge is counted out as it is taken — amps for seconds, against the rated
capacity — so a stalled motor or a relay left energised visibly empties it. The terminal voltage
holds up across most of the discharge and then falls off a cliff, which is the shape a real cell
has and the reason batteries give so little warning. Turn **Discharges** off in the inspector if
you want the sag without the clock running.

**Solar cell.** A panel is a current source in parallel with the diode it is made of, and that
one fact explains everything awkward about them. Light makes **current**, not voltage: the current
is almost exactly proportional to brightness while the voltage barely moves — halving the light
costs about 150 mV out of three and a half.

The consequence is the knee. Draw less than the light is making and the voltage holds up; draw more
and it collapses, because there is no more current to be had at any voltage. A panel is not a
battery with a smaller capacity: there is a maximum power point part way down that knee, and
loading it either side of that gives you less. Double-click it to shade it.

---

## Surge protection

Two parts whose job is to survive something the rest of the circuit cannot. Both sit there doing
nothing until the voltage across them goes somewhere it should not.

**TVS diode** — a zener bred for speed and current rather than for a precise voltage. Below its
standoff voltage it does nothing at all, which is the point: it has to be invisible to the circuit
it is protecting. Past that it turns on hard. It is bidirectional here, so it clamps whichever way
the surge arrives. Picking one with a standoff below the working voltage is the classic mistake —
it then conducts continuously, gets hot, and takes out the thing it was defending — and the part
says so.

**Varistor (MOV)** — the part across the mains input of almost everything. Where a TVS has a sharp
silicon knee, an MOV is a very high-order power law, `I ∝ V^30`, which makes it soft: it starts
conducting well before its rated voltage and never quite stops. That is why an MOV is not a
regulator and why it belongs behind a fuse.

It also **wears out**, which is the thing about MOVs nobody expects. Every surge takes a little of
it permanently. The absorbed energy is counted against the rating here, and a spent one says so —
a real one at the end of its life conducts at normal working voltage and cooks.

---

## Rotary encoders

An incremental encoder does not report a position. Turning it makes two contacts open and close a
quarter of a cycle apart, and **which one changes first is the only thing that says which way it
went**. That is what quadrature means, and reading it is the whole job.

These are mechanical contacts, and that is why encoder code is harder than it looks. Every edge
bounces for a millisecond or two, and a bounce read as a transition sends the count backwards and
forwards at random. The bounce is modelled rather than assumed away, so a naive counter will
misread this part exactly as it would misread a real one — and the 4093 and 74HC14 already in the
palette are what you reach for to fix it. Set **Bounce Duration** to zero if you want the clean
signal a debounced circuit would see.

Double-click it to turn one detent; set **Reverse** to turn it the other way.

---

## Switching instead of dropping

Every other part in **Power** is linear. A 7805 bringing 12 V down to 5 V drops the other seven
volts across itself and turns them into heat — at 100 mA that is 0.7 W wasted to deliver 0.5 W,
which is why almost nothing is powered that way any more.

A switcher does not drop the difference, it **chops** it. The switch is either hard on or hard
off, so it dissipates almost nothing either way, and an inductor and a diode carry the energy
across in between. **File > Examples > Buck Converter** is the circuit, with the switch node, the
output and the inductor current all on the scope.

The **MC34063** is a controller rather than a converter. It brings an oscillator, a comparator
against an internal 1.25 V, a current limit and a switch; **the topology is your wiring**. Put the
switch between the supply and the inductor and it steps down; put it across the inductor's far end
and it steps up; turn the diode round and it inverts. The chip cannot tell which you have built,
which is the thing worth understanding and the reason it is not supplied as a block with a voltage
on the label.

Three things fall out of the model rather than being announced:

- **The divider sets the output.** The chip holds the feedback pin at 1.25 V and nothing else, so
  the output is `1.25 × (1 + R1/R2)`. Change the divider and it follows.
- **The frequency is the timing capacitor's.** The chip charges it at a fixed current and
  discharges it faster; the rate comes out of that, not out of a setting.
- **It regulates by skipping cycles**, not by narrowing pulses. When the output is high enough,
  whole cycles are left out — which is why a scope on the switch node shows bursts rather than an
  even train, and why an MC34063's ripple is worse than a modern part's.

The current limit is the other half of the design. The chip watches the drop across a sense
resistor between VCC and the switch and stops the cycle at 300 mV, which is what prevents the
inductor current running away inside a single on-time. Too small an inductor, or too slow an
oscillator, and the converter ends up regulating on its current limit rather than its comparator —
real behaviour, and the part says when it is happening.

A converter's switching instants are decided by the solve rather than being known in advance, so
they land wherever the time step puts them. For believable ripple figures, shorten the simulation
time step until the answer stops moving.

---

## Making a negative rail

Everything else in **Power** makes a positive voltage out of a larger positive one. Several of the
op-amps want a supply either side of ground, and a single-supply circuit has nowhere to get one.

The **ICL7660** does it by moving charge rather than by regulating: a capacitor is charged across
the supply, disconnected, turned round, and dumped onto the output, so the output ends up at
roughly minus the input. That is genuinely what is modelled — four switches changing over at the
oscillator rate, with your capacitor between them — which means the consequences come out on their
own.

There are two of those, and both surprise people. It **does not regulate**: the output follows the
input, so a supply that droops takes the negative rail with it. And because it moves a capacitor's
worth of charge per cycle, its output impedance is about `1/(f·C)` — a small pump capacitor or a
slow oscillator gives a rail that sags the moment anything draws from it, and no amount of
smoothing on the output fixes that.

---

## The character LCD

The HD44780 sixteen-by-two module, and the first display most people drive. It is a parallel port
with a controller behind it: put a byte on the data pins, say whether it is a **command** or a
**character** with RS, and pulse E. The controller latches on the **falling** edge of E, which is
why every driver pulses it rather than just setting the level.

Four-bit mode is what almost everyone uses, because sixteen pins is a lot. It starts in eight-bit
mode as a real one does, and a function set with the data-length bit clear moves it to four — from
then on each byte is two latches, high nibble first. The initialisation dance every library
performs therefore works here for the same reason it works on hardware.

Two things catch people, and both behave here as they do on a real module. **Line two is an
address, not a continuation**: running off the end of line one does not wrap onto it, you have to
set the cursor to 0x40. And **reading is not modelled** — tie RW low. The busy flag is what RW is
for, and virtually all driver code waits a fixed time instead of polling it.

---

## I2C and SPI

Two ways to talk to a chip over a handful of wires, with opposite trade-offs.

**I2C** is two wires for any number of devices, each with an address. It is **open drain**: nothing
on the bus ever drives a line high — every device can only pull down or let go — and a pair of
resistors to the supply makes the high level. That is what lets a dozen devices share two wires
without fighting, and it is why **a bus with no pull-ups does not work at all** rather than working
badly. Forgetting them is the classic first mistake and it fails here the way it fails on a
breadboard.

There is no processor to run a driver, so the master plays a written list of transactions:

```
w 50 00 00 48 49 21     ; write three bytes to device 0x50 at offset 0x0000
w 50 00 00              ; point it back at the start
r 50 3                  ; read the three back
```

Addresses are seven-bit and written as themselves — `50`, not `A0` — with the read/write bit added
for you, which is the opposite of the confusion most datasheets cause. **Addressing is nothing but
the acknowledge**: a device claims a transfer by pulling the line down during the ninth clock, and
talking to an address nobody answers to leaves the line high. That is the only symptom you get, and
it is what `LastTransferAcknowledged` reports.

Two devices are stocked: a **24LC256 EEPROM**, and a **PCF8574 port expander** — eight pins from
two wires, and the chip on the back of every "I2C LCD" backpack. The expander's outputs are
quasi-bidirectional: writing a one releases the pin to a weak pull-up rather than driving it high,
which is why these sink an LED nicely and barely source anything.

A **DS1307 real-time clock** sits on the bus as well, and it is the one that behaves like a sensor
rather than a memory: its registers change whether you talk to it or not, so reading it twice gives
two answers. That is what a device bus is actually for.

Its registers are **binary-coded decimal**, which is the thing that catches everyone. A seconds
register holding `0x59` means fifty-nine, not eighty-nine — each nibble is one decimal digit. It is
modelled rather than quietly converted, so the conversion your driver has to do is the conversion
you have to do here. Reading it is the usual two-step: write the register number, then read from
it, which is `w 68 00; r 68 3` for seconds, minutes and hours.

Register zero also carries the **clock halt** bit at the top, and a new part comes up with it set
and the clock stopped — which is why a first-time DS1307 famously "does not work" until something
writes to it. `ClockHalted` is that bit. `TimeScale` is not a real register: simulated time passes
in milliseconds, so the clock is wound on a thousand times by default to make it visibly move.

**SPI** is the opposite trade: no addressing, no acknowledgement, push-pull rather than open drain,
and a select pin for every device. Faster and far simpler to decode, at the cost of a pin per chip.
Its master takes a list of hex bytes, one transfer per line, and clocks them out most significant
bit first with data set while the clock is low — mode zero, which is what nearly everything expects.

**File > Examples > I2C EEPROM**, **I2C Clock** and **SPI Shift Register** are all wired up, with
the bus lines on the scope.

---

## Crystals

Every other oscillator here takes its frequency from the circuit around it. The 74HC14, the 4093
and the 4060 all charge a capacitor through a resistor and switch at a threshold, so the components
set the rate — change the resistor and the frequency moves. A crystal is the opposite: the
mechanical resonance of a slice of quartz is so sharp that the surrounding circuit can barely
budge it, which is why a watch keeps time and an RC oscillator does not.

Electrically that is a series R-L-C — the **motional arm**, standing in for the mechanical
resonance — in parallel with the plain capacitance of the holder. The inductance is enormous and
the capacitance tiny: a 32.768 kHz part here works out at about 24 henries. That is what a high Q
looks like written as components.

Two things about the model are worth knowing. The **Q is lower than a real crystal's**, because a
real one takes something like a hundred thousand cycles to start and you would be waiting seconds
of simulated time for anything to happen. The frequency is unaffected — it depends only on L and
C — so what you lose is sharpness, not accuracy. And the stocked parts **stop at 1 MHz**, because a
resonance needs many time steps per cycle to come out in the right place; a 16 MHz crystal would
need the simulation step shortened by two orders of magnitude before it meant anything.

---

## Thyristors: parts that latch

Everything else in the library follows its input. A thyristor remembers, and that is the only thing
you need to understand about the family.

**SCR** — a diode you switch on with a gate pulse and cannot switch off. It blocks both ways until
the gate is driven, then conducts forwards only. The gate has no further say: once it has fired,
removing the drive does nothing at all. It conducts until the anode current falls below its holding
current, which on DC means until something interrupts the supply. That is why an SCR makes an
excellent crowbar and a poor lamp switch.

**Triac** — two SCRs back to back in one package, so it latches in either direction, and gate drive
of either polarity fires it. It drops out the moment the current falls below the holding current,
so a triac on AC turns itself off at every zero crossing and has to be re-fired each half cycle.
That is the whole of phase control: fire it late in each half and the load sees only the tail of
the waveform. Fire it later still and the lamp dims.

**Diac** — no gate at all, which is the point. It blocks until the voltage across it reaches its
breakover, around 32 V, then conducts whichever way pushed it there. It is the thing that fires a
triac: an RC charges towards the mains, the diac breaks over and dumps the capacitor into the triac
gate, and moving the RC's time constant moves the firing angle. A lamp dimmer in three components.

Each of the three shows `conducting` or `blocking` as its value and is drawn filled while it
conducts, so the latch is visible on the canvas rather than something you infer from the scope.

If one of these will not stay on, the holding current is almost always why — the load is drawing
less than the part needs to hold itself latched. If one will not fire, check the gate resistor:
it sets the gate current, and the trigger threshold is a current, not a voltage.

---

## JFETs

A JFET is a **depletion** device, and that is what separates it from every MOSFET in the library:
it conducts with no gate drive at all. Zero volts from gate to source gives you the full `I_DSS`,
and it takes a *negative* gate on an N-channel part to pinch the channel off. Wiring one up
expecting it to start off is the usual first surprise. The symbol says so, if you know to look —
the channel is one unbroken bar, where an enhancement MOSFET's is drawn in three segments.

The drain current follows the square law, `I_D = I_DSS·(1 − V_GS/V_P)²`, so it is gentler than a
MOSFET's and the reason JFETs turn up in audio.

The gate is a reverse-biased junction and draws essentially nothing, which is the reason to reach
for one. It is also a real diode: drive the gate positive on an N-channel part and it stops being a
FET and starts being a diode. The part reports that rather than quietly conducting, because a
circuit that does it is almost never the circuit you meant to build.

Four models are stocked — 2N3819, J201 and 2N5457 N-channel, 2N5460 P-channel — and three of them
are in the palette.

---

## Audio and instrumentation

**LM386** — the chip behind almost every small speaker project. Single supply, no feedback network
to design, a few hundred milliwatts into eight ohms. The gain is twenty as it comes and two hundred
with a capacitor across two pins, which is the whole appeal: there is nothing to get wrong.

Its output idles at **half the supply** so it can swing both ways, which is why the speaker is
coupled through a capacitor rather than wired straight to it. Connect it directly and half the rail
sits across the voice coil continuously — the speaker's power reading will tell you so.

**Thermocouple** — two dissimilar metals joined, producing a few tens of microvolts per degree.
It is the canonical thing the INA126 exists to read: forty microvolts per degree means a furnace
and a warm hand differ by a couple of millivolts.

The catch is the whole subject. **A thermocouple measures a difference, not a temperature.** The
junction you care about produces an EMF and so does every other junction in the loop, including
where the wires meet the copper of your circuit — so without knowing how warm the cold end is you
do not know how hot the hot one is. That is what *cold junction compensation* means, and why the
cold junction temperature is a property here rather than an assumption. Double-click the junction
to heat it.

**Load cell** — four strain gauges in a Wheatstone bridge, which is how nearly everything that
weighs things works. Two gauges stretch under load and two compress, so the bridge goes out of
balance by a fraction of a percent. It is stamped as the four resistors it actually is, so the
things that matter behave properly: it **needs exciting** — no voltage across the bridge, no
output, however much you load it — and the output is proportional to the excitation rather than
absolute, which is why load cells are rated in millivolts per volt. Two millivolts per volt with
ten volts of excitation is twenty millivolts for the entire range of the thing.

**Microphone** — an electret capsule, which is not a passive transducer: there is a JFET inside
the can, and it works by **sinking a bias current** that sound then modulates. That is why it has a
polarity and why it does nothing until you give it a resistor to the supply — the resistor is what
turns its current into a voltage. With the LM386 and the speaker this closes the loop: sound in,
amplifier, sound out, with nothing in the chain that is not a real part. Double-click it to start
and stop the sound.

The capsule reports being **starved** — a bias resistor so large there is not enough voltage left
to run the JFET. It cannot report the opposite mistake, because a capsule wired straight to the
rail is perfectly happy; it is the circuit that has no resistance for the signal to develop across,
and the capsule cannot see that from its own two pins.

**Servo** — three wires, and an angle set by how long a pulse is. Not a voltage and not a duty
cycle: between about one and two milliseconds maps across the travel, repeated every twenty
milliseconds, and the gap between pulses carries no information. That is why changing the PWM
frequency rather than the pulse length makes a servo behave strangely. It moves at a finite speed,
so a commanded jump takes time to arrive, and it goes limp when the pulses stop rather than
snapping back. A pulse outside the range is reported, because a real servo drives against its end
stop and stalls there.

**Stepper motor** — four-phase unipolar, the sort the ULN2003 exists to drive. It has no idea where
it is. Energise the coils in order and the rotor follows one step at a time; reverse the order and
it goes the other way; go faster than it can follow and it stops dead while the field carries on
without it. Counting steps is the only position feedback there is, which is why losing them
matters, and the part says when it is.

The drive pattern is not built in, because the pattern is the interesting part. The rotor follows
the vector sum of whichever coils are actually carrying current, so wave drive, full step and half
step all fall out of what you send rather than being chosen from a list. Note that the first
energisation **aligns** the rotor rather than stepping it: a stepper's position is only ever
relative to where it was switched on.

**Speaker** — a voice coil: a few ohms of wire with some inductance, so the load an amplifier sees
gets harder with frequency rather than staying put. The impedance on the box is a nominal figure,
not a resistance, which is why an "8 ohm" speaker measures about six with a meter. It reports the
power it is taking, averaged over the coil's thermal time constant, because the rating is thermal
and it is the average that decides whether the coil survives.

**INA126** — for reading a small difference sitting on top of a large common voltage: a
thermocouple, a strain gauge, a current shunt. An op-amp difference stage can do it, but its
accuracy depends on four resistors matching and yours will not. This has them trimmed on the die,
and the common-mode rejection is what you are paying for.

The gain on the real part is set by one external resistor, `G = 5 + 80kΩ/R_G`. Here it is a number
you set on the part, with that formula available both ways. The **REF** pin is not decoration: on a
single supply the output cannot go below ground, so a difference that swings both ways needs the
reference lifted off it — tie REF to ground and half the measurement is lost at the rail.

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

**File > Examples > Raspberry Pi GPIO** wires all three pin roles at once — one pin toggling, one
playing a pattern, and one reading a button on the internal pull-up. Double-click the button on the
canvas to press it and watch the input fall.

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
| `Ctrl` `Z` / `Ctrl` `Y` | Undo / redo (`Ctrl` `Shift` `Z` redoes as well) |
| `Delete` | Delete selection |
| `Ctrl` `+` / `Ctrl` `-` | Zoom in / out (the numeric keypad's `+` and `-` work too) |
| `F` | Zoom to fit — the whole circuit, captions and all |
| Wheel | Zoom at the pointer |
| Middle-drag / space-drag | Pan |
| Double-click | Operate a switch, push button or logic toggle |
| `F5` / `F6` / `F8` | Run-pause / step / reset |
| `F9` / `F10` | Collapse the palette / the properties panel |
| View menu | **Mark Interactive Parts** rings everything you can double-click |
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
