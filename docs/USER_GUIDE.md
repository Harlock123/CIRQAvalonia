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

![The component palette showing all fifteen categories with their counts — passive, switches, sources, semiconductors, transistors, LEDs and displays, power, analog ICs, logic gates, 74xx series, 4000 series, digital I/O, sensors and actuators, switching and isolation, and dev boards — with the Passive group open](images/02-palette.png)

Click a palette entry, then click the canvas. The part lands where you click, snapped to the grid.

The palette holds **111 components in 15 categories**:

| Category | Count | Contents |
| --- | --- | --- |
| Passive | 6 | Resistor, capacitor, **electrolytic capacitor**, inductor, transformer, potentiometer |
| Switches | 3 | SPST, SPDT, push button |
| Sources | 4 | Ground, DC voltage, DC current, function generator |
| Semiconductors | 9 | 1N4148, 1N4001, Schottky, 5.1 V and 12 V zeners, bridge rectifier, **SCR, triac, diac** — see [below](#thyristors-parts-that-latch) |
| Transistors | 10 | NPN and PNP bipolars, N- and P-channel MOSFETs, **three JFETs** — see [below](#jfets) |
| LEDs & Displays | 8 | Six LED colours, common-anode and common-cathode seven-segment |
| Power | 7 | Fixed and adjustable regulators, TL431 shunt reference |
| Analog ICs | 8 | LM741, NE555, LM311, LM339 and friends |
| Logic Gates | 7 | AND, OR, NAND, NOR, XOR, XNOR, NOT |
| 74xx Series | 17 | Counters, decoders, flip-flops, shift registers, multiplexers, Schmitt inverter |
| 4000 Series | 13 | CMOS gates, counters, flip-flops, analog switches — see [below](#the-4000-series) |
| Digital I/O | 4 | Logic toggle, clock, and indicators |
| Sensors & Actuators | 6 | DC motor, LDR, thermistors, buzzers — see [below](#sensors-and-actuators) |
| Switching & Isolation | 5 | Relay, fuses, optocouplers — see [below](#switching-and-isolation) |
| Dev Boards | 4 | Raspberry Pi, Arduino Uno / Nano / Mega — see [below](#development-boards) |

Click a category header to open or close it. **All** in the palette header toggles every group at
once — useful when you are hunting for a part and do not remember which group it is in.

Switches, push buttons and logic toggles are operated by **double-clicking them on the canvas**,
not through the properties panel. A push button is momentary; a switch latches. LDRs and
thermistors work the same way — double-clicking covers or warms them.

Anything you can operate is ringed with a small dot beside its designator, so you do not have to
remember which parts those are. Turn the rings off with **View > Mark Interactive Parts** once
they have served their purpose.

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

Everything above is also in **File > Examples**, along with fifteen other circuits.

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

**Optocoupler** — an infrared LED facing a phototransistor with no conductive path between them, so
the output side can sit at a completely different potential. This is the honest answer to "how do I
switch something dangerous from a 3.3 V board".

The isolated side still needs its own ground reference. That is a property of isolation, not a
limitation of the simulator — two circuits with nothing at all between them have no solution. A
real device leaks through some hundreds of gigohms and that is what is modelled, which keeps the
matrix solvable without meaningfully coupling the halves.

---

## The 4000 series

The 4000-series CMOS parts have a palette group of their own rather than sitting among the 74xx
ones, because they are a different family with different habits — and mixing the two has a specific
way of going wrong, covered at the end of this section.

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
| `Delete` | Delete selection |
| `F` | Zoom to fit |
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
