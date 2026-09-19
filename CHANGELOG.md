# Changelog

Notable changes per release. The section for a version is lifted straight into its GitHub release
notes by `.github/workflows/release.yml`, so what you write here is what people read on the
release page — write it for someone deciding whether to download, not for someone reading a diff.

Add the new section **before** tagging: the workflow reads the changelog at the tagged commit.

## [Unreleased]

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

[Unreleased]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Harlock123/CIRQAvalonia/releases/tag/v0.1.0
