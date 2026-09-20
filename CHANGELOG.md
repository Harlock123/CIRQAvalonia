# Changelog

Notable changes per release. The section for a version is lifted straight into its GitHub release
notes by `.github/workflows/release.yml`, so what you write here is what people read on the
release page — write it for someone deciding whether to download, not for someone reading a diff.

Add the new section **before** tagging: the workflow reads the changelog at the tagged commit.

## [Unreleased]

**UART**, the third serial bus and the one every dev board actually has. Two wires, one each way,
and — unlike I²C and SPI — no clock line at all.

Everything awkward about it follows from that, so it is modelled rather than asserted: the
receiver really does wait for the line to fall out of idle, start its **own** clock, and sample in
the middle of where it believes each bit to be. Which means a baud rate that does not match does
not produce silence, it produces **definite wrong characters** — `Hello` at 9600 into a receiver
counting at 19200 comes out as seven bytes where five were sent, because every bit is read as two,
and at 4800 as two bytes, because pairs are merged. Repeatable, not random. The **framing error**
is the only warning the hardware gives and it is the only warning given here.

A couple of percent of clock error is tolerated, as on a bench. TX wired to TX moves nothing and
says nothing, which is exactly how that mistake behaves. A receive line held at ground is a break
rather than a byte, and gives one framing error and then stops — a receiver needs the line back at
idle before it can frame again. Parity is there too, and disagreeing about it is its own error.

**Serial Terminal** is the end you type at, sending once or on a repeat; **Serial Device** is a
module that greets you on power-up and echoes back in capitals.

**Level shifter**, the four-channel BSS138 board, which passes signals both ways through a
transistor that only conducts one. Pull the low side down and the channel turns on; pull the high
side down and the **body diode** carries it — the diode drags the low side down far enough for the
channel to turn on and finish the job. It is built from an actual diode and a gate-controlled
channel rather than from logic, so that cascade really happens, and so the part cannot end up
reading the pin it is driving. Which means it only shifts open-drain signals, never push-pull, and
both rails have to be present and the right way round — all reported.

**File > Examples > Serial Link** has the two ends talking every five milliseconds. Change one
baud rate while it runs.

## [0.17.0] - 2026-09-19

A small one: the application can now tell you what it is. The palette holds 140 components in
16 categories, and 1125 tests measure them against closed-form answers.

**Help > About.** What version this is, what it is built on, and what it is running under —
Avalonia, ScottPlot, SkiaSharp and CommunityToolkit.Mvvm with their versions and licences, the
.NET and operating system versions, and how many components and examples are in the build.

None of it is written down. The library versions are read off the assemblies that are actually
loaded and the palette counts are counted, so the dialog cannot drift from the build the way a
hand-kept list does. A build straight from the source tree has nothing stamped on it and says
"development build" rather than claiming a version it does not have.

**Copy details** puts the whole lot on the clipboard as plain text, because the alternative is
someone transcribing four version numbers into a bug report and getting one of them wrong.

## [0.16.0] - 2026-09-19

Everything you draw can now leave the application as a file. Alongside that, four parts about what
a component does past the point its headline number applies. The palette holds 140 components in
16 categories, and 1115 tests measure them against closed-form answers.

**Export.** `File > Export...`, or `Ctrl+E`, writes the schematic and the oscilloscope traces out
as **PNG, JPEG, BMP, SVG or PDF**. Choose the schematic, the traces, or both — and when both,
either one file with the schematic above the traces, or a file each plus a combined PDF with the
schematic on page one and the scope on page two.

SVG and PDF are **true vector**, not a bitmap in a wrapper: the schematic scales to any size, opens
in Inkscape for editing, and its captions stay real text rather than becoming outlines. That works
because the renderer no longer draws onto Avalonia directly. It draws against an interface, and one
implementation puts the ink on screen while the other puts it in a file — so an export is produced
by exactly the code that draws the editor and cannot quietly fall behind it.

The frame follows the circuit rather than the window: whatever the view is scrolled to, an export
holds the whole thing at its natural size with a margin, captions and probe labels included. The
editing aids stay behind — no dot grid, no terminal dots, no hover highlighting.

**A parts list** can go in too. It is grouped the way a bill of materials is grouped — by what the
part is *and* what it is set to, so three 10 kΩ resistors are one line and a 4k7 among them is
another — with the quantity, the designators, the part and its value. Below the drawing on a single
sheet, or a page of its own in the combined PDF. Grounds are left out, being net labels rather than
things you can buy.

**Centre-tapped transformer.** The other way to rectify, and for fifty years the usual one. Take
the tap as your zero volt line and the two ends swing opposite ways, so two diodes do full-wave
rectification — and the current only ever passes through one of them, so you lose one forward drop
instead of a bridge's two. At five volts that is most of a volt. Rectify each half separately
against the tap instead and one winding gives you a positive and a negative rail, which is where
every ±15 V op-amp supply comes from.

It is modelled as what it physically is: two secondary windings in series sharing a core, each a
quarter of the whole inductance, all three mutually coupled. So the tap is a real connection to the
middle rather than an ideal half-voltage point, and loading one half unevenly pulls the other about
the way it does on the bench. The windings have resistance — a winding is a long piece of thin
wire, ideal ones are a dead short at DC, and it is what limits the inrush at switch-on.

**Inductor saturation.** An inductor now has a `SaturationCurrent`, left at zero — meaning ideal —
so nothing that already worked changes. Set it and the part behaves like iron: past the knee the
core takes no more flux, the inductance collapses, and the current stops being limited by anything
but resistance. That is the failure mode behind a switching supply that is fine on the bench and
dies at full load, and behind an inductor that gets hot while the waveform still looks right.

**DS1307 real-time clock** on the I2C bus, and unlike the EEPROM beside it this one has something
of its own to say: its registers move whether you talk to it or not, so two reads a moment apart
give two answers. Its registers are **binary-coded decimal** — `0x59` means fifty-nine — which is
modelled rather than quietly converted, because that conversion is the thing that catches everyone.
Register zero carries the clock-halt bit, and a new part comes up with it set and the clock
stopped, which is why a first-time DS1307 famously does nothing until something writes to it.

**HC-SR04 ultrasonic ranger**, which is one idea end to end: the distance is in the width of a
pulse and nowhere else — 58 µs per centimetre, out and back. Both of its awkward behaviours are
here too. A trigger shorter than ten microseconds is ignored outright, so a sloppy pulse gets you
nothing rather than a wrong answer; and with nothing in range it does not stay quiet but gives a
38 ms pulse and gives up, which is why a ranger pointed at the sky reports about six metres instead
of hanging. Double-click it to move the target while the simulation runs.

Three examples: **Full-Wave Rectifier**, **I2C Clock** and **Ultrasonic Ranger**.

## [0.15.0] - 2026-09-19

I2C and SPI, which the development boards have wanted since they were added, along with the
character LCD that most people put on the other end of them. The palette holds 137 components in
16 categories, and 1051 tests measure them against closed-form answers.

**I2C and SPI.** The dev boards have had GPIO and sequencing for a dozen releases with no way to
talk to anything. It turned out to need no new engine at all: I2C is open drain, which is a
released output and a pair of pull-up resistors, and both were already there. What was missing were
devices that speak the protocol.

The I2C master plays a written list of transactions — `w 50 00 00 A5` writes, `r 50 4` reads —
since there is no processor here to run a driver. The slave side is a real decoder: start is the
data line falling while the clock is high, bits are sampled on the rising clock, and every ninth is
an acknowledge. Addressing is nothing more than which device pulls the line down during that slot,
so talking to an address nobody answers to leaves the line high and that is the only symptom, as on
hardware. A **24LC256 EEPROM** and a **PCF8574 port expander** sit on it. A bus with no pull-ups
does nothing whatsoever, which is a test.

SPI is the opposite trade and much simpler, and its master drives a **74595** — a real gap in the
74xx list as well, since the latch is what separates it from the 74164 that was already there.

**HD44780 character LCD**, modelled as the parallel port it is rather than as a text box: a byte on
the data pins, RS saying command or character, and the controller latching on E falling. Four-bit
mode works the way it works on hardware, so the initialisation dance every library performs works
here for the same reason. Line two is an address rather than a continuation. The symbol is the
display, because a part whose point is that you can read it should not be a box with a part number.

**Solar cell**, a current source in parallel with the diode it is made of — which is why the
current follows the light and the voltage barely does, why there is a knee, and why there is a
maximum power point part way down it.

**8-way DIP switch** and a **crystal oscillator module**, the four-pin can that is actually on
boards as distinct from the bare resonator.

**New examples:** I2C EEPROM, and SPI Shift Register.

## [0.14.0] - 2026-09-19

A switching regulator, which is the biggest thing the Power group was missing — everything in it
until now turned the excess volts into heat. The palette holds 128 components in 15 categories,
and 992 tests measure them against closed-form answers.

**A switching regulator, which is the biggest thing the Power group was missing.** Every part in
it was linear: a 7805 bringing 12 V down to 5 V drops the other seven across itself and turns them
into heat. A switcher chops the input instead, and an inductor and a diode carry the energy across
between chops.

The **MC34063** is a controller, not a converter. It brings an oscillator, a comparator against an
internal 1.25 V, a current limit and a switch; the topology is your wiring. Wired one way it steps
down, another way it steps up, and the chip cannot tell the difference — there is a test for each,
using the same part. That is the reason it is not supplied as a sealed block with a voltage on the
label.

Three things come out of the model rather than being announced: the divider sets the output because
the chip holds the feedback pin at 1.25 V and nothing else; the frequency is whatever the capacitor
on CT makes it, since the chip charges that at a fixed current and discharges it faster; and it
regulates by **skipping whole cycles** rather than by narrowing pulses, which is why a scope on the
switch node shows bursts and why its ripple is worse than a modern part's.

**New example: File > Examples > Buck Converter**, 12 V down to 5 V, with the switch node, the
output and the inductor current on the scope.

## [0.13.0] - 2026-09-19

Four parts that each finish something already in the palette: a thermocouple and a load cell for
the instrumentation amplifier to read, a rotary encoder with the contact bounce that makes reading
one hard, and a charge pump for the negative rail a single supply cannot otherwise give you. The
palette holds 127 components in 15 categories, and 978 tests measure them against closed-form
answers.

**Thermocouple and load cell**, which give the INA126 something to read. It has been in the
palette with nothing natural to connect to it, and both of these are what an instrumentation
amplifier is for: a small difference sitting on a large common voltage.

The thermocouple is forty microvolts per degree for a K type, and the point of it is that **it
measures a difference, not a temperature** — the cold junction is a property here rather than an
assumption, which is what cold junction compensation is about. The load cell is stamped as the
four bridge resistors it actually is, so it needs exciting before it says anything at all, and its
output is proportional to the excitation rather than absolute. That is why load cells are rated in
millivolts per volt.

**Rotary encoder**, with the contact bounce that is the whole difficulty of reading one. Two
contacts a quarter of a cycle apart, and which moves first is the only thing carrying direction. A
naive counter will misread this exactly as it would misread a real one; the 4093 and the 74HC14 are
already here to fix that. Bounce can be turned off to see the clean signal.

**ICL7660 charge pump**, so a single supply can produce the negative rail several of the op-amps
want. It is modelled as what it is — four switches shuttling a capacitor at the oscillator rate —
rather than as a block that announces minus the input, so the two things that catch people fall out
of the model: it does not regulate, and its output impedance is about 1/(f·C), which means too
small a pump capacitor gives a rail that collapses under load.

## [0.12.0] - 2026-09-19

Four parts that each finish something already here: a crystal, an electret microphone, a hobby
servo and a four-phase stepper. The palette holds 123 components in 15 categories, and 950 tests
measure them against closed-form answers.

**Crystal.** Every other oscillator here takes its frequency from the circuit around it — the
74HC14, the 4093 and the 4060 all charge a capacitor through a resistor. A crystal is the opposite,
and it is modelled as what it electrically is: a series R-L-C standing in for the mechanical
resonance, in parallel with the holder's capacitance. A 32.768 kHz part works out at about 24
henries of motional inductance, which is what a high Q looks like written as components, and it
resonates within a tenth of a percent of where it says.

Two honest compromises, both documented on the part. The Q is lower than a real crystal's, because
a real one takes some hundred thousand cycles to start; the frequency is unaffected, since it
depends only on L and C. And the stocked parts stop at 1 MHz, because a resonance needs many time
steps per cycle to land in the right place.

**Electret microphone**, which closes a loop: with the LM386 and the speaker already here, sound
in, amplifier, sound out, with nothing in the chain that is not a real part. It is not a passive
transducer — there is a JFET in the can, and it works by sinking a bias current that sound
modulates, which is why it has a polarity and why it does nothing without a resistor to the supply.

**Servo.** Three wires and an angle set by how long a pulse is — not a voltage, not a duty cycle.
It moves at a finite speed so a commanded jump takes time to arrive, goes limp when the pulses stop
rather than snapping back, and reports a pulse that would drive it into its end stop.

**Stepper motor**, four-phase unipolar, which is what the ULN2003 was added to drive. The drive
pattern is not built in, because the pattern is the interesting part: the rotor follows the vector
sum of whichever coils are carrying current, so wave drive, full step and half step all fall out of
what you send. Drive it faster than it can follow and it stops dead while the field carries on
without it, and says so — a stepper has no feedback, so those steps are simply gone.

## [0.11.0] - 2026-09-19

Undo and redo, which `Ctrl` `Z` had been silently declining to do since the first commit. The
palette holds 119 components in 15 categories, and 921 tests measure them against closed-form
answers.

**Undo and redo.** `Ctrl` `Z` did nothing at all, because there was nothing behind it. There is
now: placing, moving, rotating and deleting parts, wiring, probing and parameter edits all come
back, with `Ctrl` `Y` or `Ctrl` `Shift` `Z` to put them back again. The Edit menu names what it is
about to reverse rather than just saying "Undo", and a drag across the canvas costs one step rather
than one per pixel travelled.

It is built on whole-document snapshots rather than a list of reversible edits. The usual command
pattern is more efficient and much easier to get subtly wrong — one mutation that forgets to record
its inverse and the history quietly stops matching the document. Restoring a snapshot is the same
code path as opening a file, which every component, parameter, wire and probe is already tested
against.

**Fixed: running a circuit marked it as modified.** Parts describe themselves as they run — a triac
says so when it fires, a battery as it discharges — and the dirty tracker counted those
notifications as edits. Pressing Run on the lamp dimmer was enough to put an asterisk in the title
bar and a "discard changes?" prompt in front of anyone who then tried to close it. A change now
only counts as an edit if it is a property the serializer would write to the file. Attaching a
probe, which is saved and was not counted, now does.

**The 4000-series palette group is now labelled "40xx Series"**, so it reads as a pair with the
"74xx Series" group above it rather than as a differently-shaped name for the same kind of thing.
The parts and their part numbers are unchanged; the palette screenshot is retaken.

**Every screenshot in the README and the guide is retaken.** Eight of the nine dated from the
first commit and showed an application with 73 components in 11 categories, before the electrolytic
capacitor, the current-probe selector, the 40xx group and a good deal else. They now show what the
application actually looks like.

## [0.10.0] - 2026-09-19

Probes can read current, not just voltage — which makes a great deal of what these circuits are
already doing visible for the first time. Seven new components alongside it, including a battery
that sags and runs down like a real one. The palette holds 119 parts in 15 categories, and 908
tests measure them against closed-form answers.
**Current probes.** A probe can read the current into the pin it is attached to, not just the
voltage on it — pick which from the dropdown beside the trace. A great deal of what a circuit is
doing is only visible as current: the holding current that drops a triac out at every zero
crossing, a relay's flyback spike, what a motor draws as it stalls, whether an LED is getting 5 mA
or 50. None of that showed up before without adding a shunt and doing the arithmetic yourself.

The convention is a clamp meter's — positive is current flowing into the component through the pin
you clamped — so the two ends of a resistor read equal and opposite and a transistor's three pins
sum to zero. Traces carry their own units now, so a current reads `26.4 mA` rather than being
squeezed onto an axis labelled volts.

**Battery.** Every other source holds its voltage into a dead short and never runs out. Five cells
are stocked — AA, 9 V PP3, 18650, CR2032 and sealed lead-acid — spanning four hundred to one in
internal resistance, which is most of why a circuit that behaves on the bench supply misbehaves on
cells. A coin cell is ten ohms, so an LED's worth of current costs it two hundred millivolts. It
also runs down: charge is counted out as it is taken, and the terminal voltage holds up across most
of the discharge and then falls off a cliff, which is why batteries give so little warning.

**TVS diode and varistor.** Two parts for surviving what the rest of the circuit cannot. The TVS is
invisible below its standoff voltage and clamps hard above it, either polarity. The varistor is a
power law rather than a knee — `I ∝ V^30` — so it is soft, starts conducting early and never quite
stops, which is why it belongs behind a fuse. It also wears out on the energy it absorbs, and says
when it has, because a spent MOV conducts at working voltage and cooks.

**ULN2003.** Seven Darlington sinks, which is how logic drives anything with a coil in it. Every
channel sinks rather than sources, so the load goes between the supply and the output pin — wire
one from an output to ground and nothing happens at all, which is modelled rather than smoothed
over. A conducting output keeps about a volt across it, as a Darlington does, which matters when a
5 V relay runs off a 5 V rail.

**LM386, speaker and INA126.** The LM386 idles at half its single supply so it can swing both ways,
which is why the speaker couples through a capacitor; wire it direct and half the rail sits across
the voice coil. The speaker is a real voice coil with inductance, so the load gets harder with
frequency, and it reports the power it is taking averaged over the coil's thermal time constant.
The INA126 lifts a small difference off a large common voltage and rejects the common mode, with
the datasheet's `G = 5 + 80kΩ/R_G` available both ways.

Not added: a PWM source. `DutyCycle` is already an editable property on both the function generator
and the clock, so it would have been a second name for a part that is already there.

**The macOS first-run instructions no longer assume a terminal.** Gatekeeper blocks these builds
because they are not signed or notarized, and the only route documented was `xattr`. There is a
Settings one: try to open the app, choose Done, then click **Open Anyway** under
**System Settings > Privacy & Security**. The README, the release notes and the
`MACOS-FIRST-RUN.txt` inside each macOS archive all say so now.

The old advice to right-click and choose Open was also out of date — that bypass still works
through macOS 14, but Apple removed it in macOS 15.

The README, the release notes and the `MACOS-FIRST-RUN.txt` in each macOS archive now all say why
the builds are unsigned, which is a decision rather than an oversight: notarization is per build, so it is a step
welded onto every release rather than one afternoon of setup, and releases here currently land
within hours of each other.

## [0.9.0] - 2026-09-19

Twenty new components — the thyristor family, JFETs, and a 4000-series CMOS group of its own —
six examples built out of them, and a zoom to fit that finally fits. The palette now holds 112
parts in 15 categories, and 865 tests measure them against closed-form answers.

**Thyristors: SCR, triac and diac.** Everything else in the library follows its input; these parts
remember. An SCR fires on a gate pulse and the gate then has no further say at all — it conducts
until the anode current falls below its holding current, which on DC means until something
interrupts the supply. A triac is two of them back to back, so it latches either way and either
gate polarity fires it, which also means it turns itself off at every zero crossing and has to be
re-fired each half cycle. That is phase control, and it is why a lamp dimmer works.

A diac has no gate at all: it blocks until about 32 V across it and then conducts whichever way
pushed it there. It is the thing that fires the triac, and with an RC to set the firing angle the
three of them are a lamp dimmer in three components.

All three show `conducting` or `blocking` and are drawn filled while they conduct, so the latch is
visible on the canvas instead of something you infer from the scope.

**JFETs: 2N3819, J201 and 2N5460.** A depletion device, which is what separates it from every
MOSFET already in the library: it conducts with no gate drive at all, and it takes a negative gate
on an N-channel part to pinch the channel off. Wiring one expecting it to start off is the usual
first surprise, and the symbol says so if you know to look — the channel is one unbroken bar where
an enhancement MOSFET's is drawn in three segments. The gate is a reverse-biased junction drawing
essentially nothing, which is the reason to use one; drive it positive and the part tells you,
because it has stopped being a FET and started being a diode.

**A 4000-series CMOS group, with fourteen parts in it.** The CMOS parts have a palette category
of their own now rather than sitting among the 74xx ones, because they are a different family with
different habits.

The gates are the 4001, 4011, 4070, 4071 and 4081 quads and the 4069 hex inverter, plus the 4093 —
the 4011 with hysteresis on every input, which is the part that makes an oscillator out of one
gate, a resistor and a capacitor. Its period is the two exponentials end to end, and the tests
predict it rather than record it.

The pinout is the trap these parts set, so they have the one they really have: a 7400 puts gate two
on pins 4 and 5 driving 6, and a 4011 puts it on 5 and 6 driving 4. Only gates one and four agree,
so dropping one into a board laid out for the other leaves two working gates and two that do
nothing sensible. The family is at least consistent with itself — unlike TTL, where the 7402 moves
its outputs, the CMOS NOR shares the CMOS NAND's pinout exactly.

The 4060 is fourteen stages with an oscillator of its own, and it is built rather than declared:
RS is an inverter's input, REXT its output and CEXT the output of a second one behind it, so
hanging Rt and Ct off those three pins is the whole oscillator and the frequency falls out of the
network. Wired the way the datasheet wires it, it lands on the datasheet's 1/(2.3·Rt·Ct), which is
what the test checks — and doubling the capacitor halves it, because nothing anywhere sets a
frequency. The input protection diodes are modelled too, since the capacitor throws the timing node
a whole supply past the rail twice a cycle and Rs exists to keep them out of the timing.

The 4013 is a dual D flip-flop with **active-high** set and reset, where a 7474's preset and clear
are active low; wiring one like the other leaves it held wherever the noise on those pins decides.
The 4040 is twelve flip-flops in a chain, so Q12 is the clock divided by 4096, and it counts on the
falling edge where the 4017 beside it counts on the rising. The 4060 counts on the falling edge
too, its reset stops the oscillator as well as clearing the count, and its first three stages and
its eleventh never reach a pin — so the divisions on offer are ÷16 to ÷1024 and then ÷4096 to
÷16384, with no ÷2048. The 4051 is the 4066 with an address
decoder in front: three pins pick one of eight channels, and because only one path is ever closed
there is no way to short two sources together the way four loose 4066 switches will let you.

Joining them are the 4017, 4511 and 4066 from before: the decade counter that decodes for you, the
7447's counterpart for common-cathode displays, and the quad analog switch.

These are CMOS, not TTL. A 4000-series input on a 5 V rail wants 3.5 V before it reads high and a
74xx output only guarantees 3.4 V, so driving one directly from the other is a circuit that works
in one place and not the other. They are also an order of magnitude slower — about 90 ns a gate at
5 V against 11 ns for the TTL equivalents.

**Zoom to fit now fits.** It measured the extents from the component centres and added a fixed
margin, which was ample for a resistor and nowhere near enough for a 40-pin board — a Mega's symbol
is some eight hundred units tall, so fitting to its centre left most of the part off screen, in the
one case where fitting matters most. It now measures what is actually drawn, captions included, and
takes wire waypoints into account so a circuit routed around an obstacle is not clipped either.

**Zoom in and out**, on the View menu and on `Ctrl` `+` / `Ctrl` `-`. The numeric keypad's `+` and
`-` work as well, since typing an actual `+` means holding shift on most layouts. The wheel still
zooms about the pointer; the menu and keyboard zoom about the middle of the view.

**Six new examples**, for the parts added over the last few releases: **Lamp Dimmer** (triac and
diac phase control, firing once per half cycle and turning itself off at every zero crossing),
**SCR Latch** (press the button and it stays on — only interrupting the anode puts it out), **LED
Chaser** (a 4017 walking ten LEDs), **4060 Timer** (a chip clocking itself from one resistor and one
capacitor), **Staircase Generator** (a 4040 addressing a 4051 across a resistor ladder) and **JFET
Amplifier**.

**Fixed: the JFET's two regions did not meet.** Channel-length modulation was applied to the
saturation branch only, so the drain current stepped about three percent as the device crossed
`vds = overdrive`. A discontinuity is the one thing Newton cannot walk down, and the JFET amplifier
found it: with the source bypass capacitor still charging the stage sits in triode, and the solver
hunted either side of the boundary until it gave up. Current, transconductance and output
conductance now all meet at the boundary, as they already did in the MOSFET model.

## [0.8.0] - 2026-09-19

**TL431 programmable shunt reference.** A zener whose voltage you choose. Tie its reference pin to
its cathode and it is a fixed 2.5 V shunt; feed the reference from a divider off the cathode and it
holds the cathode at 2.495 × (1 + R₁/R₂). That is why almost every switching supply has one — it
is the adjustable part of the feedback loop, and usually the thing on the other side of the
optocoupler.

It needs a milliamp or so through it to regulate at all, and it reports being starved. Sizing the
feed resistor so the load takes everything is the classic way to end up with a circuit that almost
works.

**74HC14 hex Schmitt-trigger inverter.** The package is the 7404's; the difference is entirely in
how it reads an input. An ordinary gate has one threshold, so an input creeping slowly through it
produces a burst of output chatter as noise carries the level back and forth. A Schmitt input has
two: it will not call a rising input high until it clears the upper, nor a falling one low until it
drops past the lower, and between them it remembers what it last decided.

That gap is what makes this the part you reach for to clean up a slow edge, debounce a contact, or
— with a resistor from output back to input and a capacitor to ground — build an oscillator out of
a single gate. The thresholds are held as fractions of the supply, so they follow the rail down if
you run it at 3.3 V.

**729 tests** pass, measured against closed-form answers rather than recorded output.

## [0.7.0] - 2026-09-19

**Parts you can operate are now marked as such.** Switches, push buttons, logic toggles, LDRs and
thermistors all respond to a double-click on the canvas, but nothing on the canvas said so — the
only place it was written down was the palette description you had already scrolled past, and the
status-bar hint needs you to hover the very thing you did not know was interactive.

Each now carries a small ringed dot beside its designator. It is on by default, because discovery
is the point; **View > Mark Interactive Parts** turns it off once it has served its purpose.

**709 tests** pass, measured against closed-form answers rather than recorded output.

## [0.6.0] - 2026-09-19

**Thermistors, NTC and PTC.** The NTC follows the Beta equation a datasheet actually gives,
`R = R₂₅·exp(B·(1/T − 1/T₂₅))` with the temperatures in kelvin. That curve is steeply non-linear —
a 10 kΩ B3950 bead reads 33 kΩ at freezing and 2.5 kΩ at 60 °C — and treating it as a straight
line is the usual reason a home-made thermometer is right at one temperature and nowhere else.

A PTC is specified by a coefficient per kelvin rather than a Beta, so it is modelled that way
instead of forcing one equation to cover both. Double-click either to swing it between cold and
warm while the simulation runs, and pair it with a comparator to act on temperature.

**Piezo and active buzzers**, and the difference between them is the point rather than a detail.

A **passive** piezo is a capacitor: it turns a *changing* voltage into movement, so a steady one
does nothing at all. Wiring one across a pin that is simply switched high is the most common reason
a first buzzer circuit is silent, so that is reported and the part ringed in red rather than
drawing a plausible current and leaving you to wonder. It sounds at whatever it is fed, and the
frequency shown is measured from the drive rather than assumed.

An **active** buzzer has its own oscillator behind the element, so DC is exactly what it wants.

**705 tests** pass, measured against closed-form answers rather than recorded output.

## [0.5.0] - 2026-09-19

**New category: Sensors & Actuators.**

**DC Motor** is modelled electrically and mechanically at once, because the two cannot be
separated. The armature is a resistance and an inductance in series with a back-EMF proportional
to speed; the torque is proportional to current, and the rotor's inertia integrates the difference
between that torque and the load.

That coupling is why a motor cannot be simulated as a resistor. At rest there is no back-EMF, so
the armature draws the stall current — 4 A for a 12 V motor with a 3 Ω armature — and only as the
rotor spins up does the back-EMF rise and choke the current back to a few hundred milliamps. That
startup surge is what trips supplies and welds relay contacts, and it falls out of the model
rather than being asserted. Load the shaft and it slows until torque balances, so the current a
motor draws is set by what it is driving rather than by the supply.

Load it past what it can turn and it sits stalled with nothing but armature resistance limiting
the current. The motor is ringed in red and the fault named, because that is how they burn out. A
momentary stall at switch-on does not count — every motor has one.

**LDR** is a cadmium-sulphide cell whose resistance follows a power law, so a decade of light is a
fixed ratio of resistance and the part spans several decades between a dark room and daylight:
about 900 Ω under bright indoor lighting against 400 kΩ covered. That span is why an LDR is
normally read with a comparator against a divider rather than measured directly.

Double-click it on the canvas to cover and uncover it, the same as operating a switch, so a
light-sensing circuit can be exercised while the simulation runs.

**686 tests** pass, measured against closed-form answers rather than recorded output.

## [0.4.0] - 2026-09-19

**Build a power supply.** The bridge rectifier and the electrolytic capacitor join the regulators
that were already there, and each models the thing that actually bites.

The bridge is four real diodes rather than an idealised block, so both halves of the cycle pass
through two of them and the output peak lands about 1.4 V below the input peak, rippling at twice
the input frequency. Those losses are the reason a supply needs headroom, and an ideal bridge would
hide exactly the detail worth seeing.

The electrolytic is polarised and carries real series resistance — ESR is what sets the ripple on a
smoothing capacitor, so it is solved rather than assumed. It reports being connected backwards or
run over its working voltage, and is ringed in red on the canvas.

**File > Examples > Linear Power Supply** puts the whole chain together: a mains secondary through
a bridge into a 1000 µF reservoir into a 7812. The two probes are the point — the reservoir rail
sags and recharges twice per cycle while the regulated rail beside it is flat.

**New category: Switching & Isolation** — the boundary between a control circuit and the thing it
controls. Each part reports the mistake that destroys it.

- The **relay** is a coil and a changeover contact. Switching the coil off with no flyback diode
  produces hundreds of volts backwards — the spike that kills the transistor driving it. The coil
  is ringed in red and the fault named. Pull-in and drop-out are deliberately different currents,
  so a coil sitting near the threshold holds its state instead of chattering.
- The **fuse** opens on its melting integral rather than on instantaneous current, which is why a
  real fuse survives an inrush many times its rating. It carries its rated current indefinitely;
  only the excess accumulates. Twice the rating on a 1 A part takes about a sixth of a second, five
  times takes twenty milliseconds.
- The **optocoupler** has no conductive path between its halves, so the output side can sit at a
  completely different potential — the honest answer to switching something dangerous from a 3.3 V
  board. The isolated side still needs its own ground reference, which is a property of isolation
  rather than a limitation.

**Fixed: a 7805 sat at half a volt instead of five.** Junction temperature was computed from
instantaneous power, so the few microseconds of inrush that charge an output capacitor — tens of
watts, briefly — read as a die at hundreds of degrees and latched thermal shutdown. The junction
now has thermal mass, so protection responds to sustained power the way a real part does. A dead
short still shuts the regulator down, within a few milliseconds rather than instantly.

**Fixed: a regulator starved of input drove its output negative.** The pass element is a follower —
it sources current but cannot sink it — so a positive part sits at zero however little headroom it
has. The old behaviour left the output capacitor of a supply looking reverse-biased for as long as
the reservoir took to charge.

**663 tests** pass, measured against closed-form answers rather than recorded output.

## [0.3.0] - 2026-09-19

**New example: File > Examples > Raspberry Pi GPIO.** A Pi driving two LEDs and reading a button
on its internal pull-up — all three things a GPIO pin can do in one circuit. One pin toggles, one
plays a bit pattern, one reads. Double-click the button on the canvas to press it and watch the
input fall.

The button goes to ground rather than to 3.3V because the pin uses the internal pull-up; wiring it
the other way needs an external pull-down, and is the usual reason a first attempt reads garbage.
330R on 3.3V gives about 4.5 mA per LED, comfortably inside the Pi's 16 mA per-pin rating, so the
board's own rule checks stay quiet.

**Digital waveforms are no longer clipped.** Decade Counter, Digit Counter, Ring Oscillator and
Running Light now show their traces tiled rather than stacked. Stacked mode centres each lane on
its offset, which pushed the top of a 0-3.4V logic swing outside its lane — the Ring Oscillator
was losing most of its waveform, and the Digit Counter the top of QA. Tiled gives every trace its
own auto-ranged axes.

Stacked mode itself is unchanged: it assumes traces sit around zero, which suits AC-coupled or
bipolar signals rather than logic.

**596 tests** pass, measured against closed-form answers rather than recorded output.

## [0.2.0] - 2026-09-19

**Raspberry Pi and Arduino boards.** A Pi or Arduino can now sit on the schematic as either the
source of signals or the sink for them, with its whole header available to wire:

| Board | Logic | Pins |
| --- | --- | --- |
| Raspberry Pi (40-pin) | 3.3V, **not 5V tolerant** | 26 GPIO, 8 GND, 2×5V, 2×3.3V |
| Arduino Uno R3 | 5V | 14 digital (6 PWM), 6 analog |
| Arduino Nano | 5V | 14 digital (6 PWM), 8 analog |
| Arduino Mega 2560 | 5V | 54 digital (15 PWM), 16 analog |

One Pi component covers the Pi 2, 3, 4, 5 and Zero — they share the same header, laid out as it is
on the hardware with odd numbers down one side and even down the other.

**Pins are configured with one line**, because a 40-pin header as 40 inspector rows would be
unreadable and most circuits use three pins:

```
GPIO17=high; GPIO18=clock@1kHz; GPIO22=in-pullup; GPIO12=pwm@500Hz:25%
GPIO23=seq@1kHz:1101_0010; GPIO24=once@1kHz:001
```

`seq` loops a bit pattern; `once` plays it through and holds the last step, which is what makes a
one-shot usable as a reset pulse rather than something that yanks the line back down every few
milliseconds. A `z` step releases the pin, so open-drain lines and a bus handed over mid-pattern
fall out of the same mechanism.

**Boards tell you when you are about to break them.** They are checked against their datasheet
limits as the simulation runs, and a violation appears in red in the status bar:

- A Raspberry Pi GPIO above 3.3V — the Pi is not 5V tolerant, and this is the single most common
  way people destroy one. An Arduino in the identical circuit says nothing, because a 5V board is
  fine there.
- A pin over its current rating (16 mA on a Pi, 20 mA on an Arduino) — where an LED with no series
  resistor lands.
- The total GPIO budget (50 mA / 200 mA), which several pins can exceed while each stays inside
  its own rating.

These are deliberately separate from solver errors: the circuit solves perfectly well, it is the
hardware that would not survive it.

**Also in this release**

- Designator and value captions now clear the symbol body. They were drawn at a fixed offset from
  centre, which already overlapped a 16-pin DIP and buried the caption in a board's pin rows.
- Release builds are produced by GitHub Actions, which runs the full test suite, builds all seven
  platforms in parallel and checks each binary's architecture before publishing.
- Fixed a `stackalloc` inside the gate-evaluation loop (CA2014). Harmless at current sizes, but
  the shape that overflows once a loop runs long enough.

**591 tests** pass, measured against closed-form answers rather than recorded output.

## [0.1.0] - 2026-09-19

First public build of CirqAvalonia — an electronic circuit analyzer and mixed-signal simulator.

- **Analog** — each time point is stamped into a dense MNA system and solved by LU decomposition
  with partial pivoting. Reactive elements use trapezoidal companion models, falling back to
  Backward Euler on the first step after a discontinuity.
- **Non-linear** — diodes, transistors, op-amps and regulators iterate with damped
  Newton-Raphson, SPICE `pnjlim` junction limiting, and Gmin stepping when a bias point will not
  settle directly.
- **Digital** — logic devices are event-driven, with transitions queued at `t + tpd`. The
  transient loop truncates its step so a time point lands exactly on the next edge, which is what
  lets a 555's comparators interact with the RC network around them in one solve.
- **Editor** — 73 components in 11 categories, orthogonal wire routing, a reflection-built
  properties inspector, probes streaming to a live ScottPlot oscilloscope, and 14 worked examples.

**501 tests** measure the solver against closed-form answers rather than recorded output.

[Unreleased]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.17.0...HEAD
[0.17.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.16.0...v0.17.0
[0.16.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.15.0...v0.16.0
[0.15.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.14.0...v0.15.0
[0.14.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.12.0...v0.13.0
[0.12.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.10.0...v0.11.0
[0.10.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.8.0...v0.9.0
[0.8.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Harlock123/CIRQAvalonia/releases/tag/v0.1.0
