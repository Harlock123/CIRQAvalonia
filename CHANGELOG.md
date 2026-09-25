# Changelog

Notable changes per release. The section for a version is lifted straight into its GitHub release
notes by `.github/workflows/release.yml`, so what you write here is what people read on the
release page — write it for someone deciding whether to download, not for someone reading a diff.

Add the new section **before** tagging: the workflow reads the changelog at the tagged commit.

## [Unreleased]

**A drawing can be spread over sheets.** `View > Add Sheet` puts a strip of tabs above the canvas,
and each tab is a page — a supply page, a logic page, an I/O page. Double-click the tab you are on
to rename it; `Ctrl` `PgDn` and `Ctrl` `PgUp` step between them.

Sheets change nothing about the circuit. Every page goes to the solver together, as one network, and
two pages are joined exactly the way two ends of one large page are joined: by naming a net. That is
also the difference between a sheet and a block, which is the feature people reach for and then find
does something else — a block *hides* a section behind one symbol with pins, a sheet just puts an
ordinary section of the schematic on another page.

What is on another page is genuinely not there rather than hidden: it is not drawn, cannot be
clicked, dragged, tabbed to or caught by a box selection, and changing page clears the selection, so
`Delete` cannot reach something nobody can see. A part you place or paste lands on the page in front
of you, which is also how a block is moved between pages. Export and print give you the page you are
on, because every page starts its coordinates in the same corner and one file with all of them in it
would be every page drawn on top of the first.

The first sheet you add makes two: everything already drawn becomes page one without a single part
being relabelled, so splitting a schematic you have been working on for a week costs one menu item.
Removing a page moves what was on it to the page beside it rather than deleting it — losing a page
should cost the arrangement, not the circuit — and the last page cannot be removed. A circuit saved
before any of this opens as one page with no tab strip, and its file is byte-for-byte what it was.

**Opening a circuit brought back its parts and left the rest of the document behind.** The named
numbers, the requirements and the recorded baseline are all saved and were all being dropped on the
way in — a circuit with parameters opened as one whose parts pointed at names that no longer existed.
Sheets are what made it obvious, since a document that forgets its pages arrives as every page drawn
on top of the first. Undo took the same path, so it had the same hole.

**Find on the sheet can now change what it found.** Search for `10k`, pick a setting every match
has, and set them all, bind them all to a parameter, or take the value they already share and give
it a name. The picker only offers settings every match has, so a change cannot silently skip half of
them, and anything it could not apply is named rather than counted.

The third of those is the migration path for every circuit drawn before parameters existed. Those
have their choices typed into a dozen boxes as literals, and no amount of naming a parameter
afterwards connects them to it — so four resistors that were all coincidentally 10k become four
parts that are the same number on purpose. It is refused when they do not all agree, because a name
for a number they do not share would be a name for a number that is not there.

**Every control a person can operate now has a name a screen reader can read out.** There were
none. Sixty-three of the application's hundred and ten interactive controls announced themselves as
nothing at all — every number box in every analysis window, every picker, the temperature slider,
the lists of findings. A button with a word on it gets a name from the word; a box you type a
frequency into has no word, and the label beside it is separate text with no connection anything can
follow.

This is the other half of the keyboard work in 0.35.0: making the sheet operable without a mouse is
not much use to somebody who cannot see which control they have landed on. A test walks every
window and fails on a control that would announce nothing, and on one named after its own type,
which passes the first check and tells nobody anything.

**Large circuits are eight to twelve times faster.** A circuit matrix is nearly empty — three
entries a row, under half a percent full — and the solver was eliminating it densely, which is N³
and spends almost all of its time subtracting zero. Measured on a ladder that refactors every
Newton iteration: 402 unknowns went from 9.3 ms a time point to 1.1, and 1602 from 238 ms to 22 —
four time points a second became forty-five.

Two things were needed and only one was obvious. Running the inner loop over the columns the pivot
row actually has something in, done alone, made it **twice as slow**: the unknowns arrive numbered
in the order parts were added, so a node is routinely coupled to one several hundred rows away, the
elimination fills in the whole band, and iterating indirectly over rows that have become dense
anyway is just a slower way of doing dense arithmetic. Reordering them first — Reverse
Cuthill-McKee — is what makes the sparsity survive being eliminated.

Below forty-eight unknowns, which is most circuits, the elimination is the dense one it always was,
to the last bit. The reordering is an optimisation that can decline: a matrix that is barely
non-singular, like a bus with no pull-ups, can pivot to exactly zero in one order and 1e-20 in
another, so a failure there falls back to the order the circuit was stamped in rather than being
reported.

## [0.35.0] - 2026-09-25

Every limit a part is sold against, gathered into one list. Four measurements the circuit could not
take. A number it can now give a name to, a sheet you can edit without a mouse, and a test suite
that finally opens its own windows. 188 components in 16 categories, 87 worked examples, 3428
tests.

### Added

**The sheet can be edited without a mouse.** Arrow keys move the selection by one grid square, and
by one unit with Shift — which is the only way to put something off the grid deliberately. **Tab**
steps to the next part and **Shift+Tab** to the previous, bringing one into view when it is off
screen and leaving the view alone when it is not. **Enter** drops the armed part in the middle of
the view, so with `Ctrl+F` to search the palette a part can be found and placed without a pointer.

The canvas has always answered R, W, V, P, F, Delete and Escape, and has always needed a pointer for
everything that actually changes the drawing. Notes and headings are skipped when stepping: they are
on the drawing rather than in the circuit.

**Parameters: numbers the circuit gives names to.** **Simulate > Parameters...** (`Ctrl+F9`) names a
value once, and any part is set from it by typing `=` and the name into one of its numbers — `=Rf`,
or `=Rf / 10`, or `=1 / (2 * pi * R * fc)`. Parameters may be written in terms of each other, in
whatever order, and `pi` is always there.

A design is a handful of choices and a great many consequences of those choices. Typed as literals
into a dozen boxes, the consequences stop being consequences the first time a choice changes, and
nothing in the file records they were ever related.

The `=` is the spreadsheet's convention for the spreadsheet's reason: the box already accepts `4k7`,
so an expression needs a mark that cannot be mistaken for a value. A typo and a cycle get different
sentences, because one is a name to correct and the other is a design to untangle — and a binding
that stops working leaves the part holding the value it had, since clearing it to zero would turn a
typo in one box into a circuit that still solves and is quietly wrong.

The expression language is the one the scope already had for trace arithmetic, with the vocabulary
in its error messages made to follow: "there is no trace called Rf" is a confusing thing to be told
about a parameter. Underneath, a bound setting is still a plain number on the part — an expression
is a way of *setting* a value, not a second kind of value, which is why the solver and a hundred
and eighty-eight component types know nothing about any of it. The file keeps both, so an older
build opens it and gets a working circuit with the right numbers in it.

**Timing between two traces: propagation delay, skew, setup and hold.** Every measurement here was
about a single waveform, and the most-quoted number on any logic datasheet is not — a propagation
delay is the gap between one signal moving and another moving because of it. A library shipping
twenty-one 74xx parts, fifteen 40xx parts and an event scheduler had no way to measure the figure
those parts are sold on.

A requirement using one of the four grows a **from** box for the second trace, so "this gate has to
respond inside 40 ns" is now a sentence a circuit can be held to. Probe either side of a real gate
and the delay measures back as the one the part was given.

Skew is the worst pairing across the capture, matched nearest-edge but rejecting any pairing more
than half a cycle apart. Both simpler rules were tried and both break at an end of the capture:
pairing purely by nearest lets an edge whose partner falls outside the window pair with the previous
cycle's, and two traces twelve nanoseconds apart come back as nearly a whole period.

**One list of everything outside its ratings.** **Simulate > Ratings...** (`Ctrl+F5`, and what the
Power window has become) gathers every limit in the circuit into one place, grouped by part.

All of this existed and none of it was anywhere you could see it. **Thirty-two component types
report their own violations** — a relay coil with no flyback, an electrolytic over its volts, a
cell being asked for most of its short-circuit current — and those reached exactly two places: a
red ring drawn by about a dozen of the symbols, and the hover card. A part could be complaining and
drawing nothing at all, and the only way to find it was to hover over each of a hundred and
eighty-eight in turn.

A part fails three ways and all three are now checked: watts against a power rating, volts against
a voltage rating, amps against a rated current, plus the die against its maximum. **A plain
capacitor has a voltage rating at last** — it was the one passive in the library that could be
taken to four times what it is sold for and say nothing whatever.

**Peaks for the limits, averages for the heat.** Watts are thermal, so what matters is the mean
over long enough for the part to warm up — which is why a MOSFET survives a pulse that would
destroy it held on. Volts are not: a dielectric breaks down at the instant the peak arrives, and
averaging is exactly the wrong thing to do to it. One run reports the worst voltage and current
reached and the mean dissipation, at the same time.

**The suite opens windows now.** Three thousand tests and not one of them had ever constructed one
— which is why both of the UI bugs that have ever shipped got out: a dialog with no way to close
it, and a dialog drawn in the light palette while the application around it was dark, its buttons
pale grey on pale grey. Neither is visible from a view model, and neither is visible from the XAML.

Every window is now opened for real, on a headless Avalonia platform, and clicked with a real
pointer — which matters more than it sounds, because a raised `Click` event does not invoke a
button's command, so a test that raises one passes against a button that would never have fired.
Each window has to lay out, has to close from its own button, has to close on Escape, and has to
have nothing in it drawn in a colour that cannot be read against its own background.

The theme test is the interesting one: it builds a dialog while the application says Light, turns
the application dark underneath it, and asks what colour the dialog is now. Reverting the actual
fix that shipped in 0.33.3 makes it fail, with the reason in the message.

Having a real application to ask also turned up something that had never been askable: the theme is
resolved from drawing code, drawing is not inherently a UI-thread job, and an Avalonia object
throws at any thread but its own. Asked from somewhere that cannot reach the application, it now
answers the same way it answers when there is no application at all, rather than taking the render
down.

## [0.34.0] - 2026-09-24

Five things the circuit could not tell you before: what it is doing, what it costs to run, what it
does to a waveform that is not a sine, whether it settles the way the specification says, and
whether your last edit changed anything else. 188 components in 16 categories, 87 worked examples,
3276 tests.

### Added

**A baseline: what the circuit produced then, against what it produces now.**
**Simulate > Baseline...** (`Ctrl+F8`) records the whole answer, so that an edit which breaks
something three chapters away from what you touched shows up as a change rather than as something
noticed six weeks later. Requirements assert the handful of things you thought to assert; this is
the rest.

It compares the **shape** as well as the measurements, because two waveforms can measure the same
and be entirely different — a sine and a triangle of the same peak to peak share a minimum, a
maximum and a frequency. A change has to be more than one percent to be reported: every run differs
from every other in the last few digits, and a report that is never empty is a report nobody reads.

Each measurement is judged against a scale that means something rather than against itself, and the
mean is what forces that. The mean of a symmetric waveform is numerically zero — a few times ten to
the minus seventeen — so two runs of the *same* circuit differ by several percent of it. Against the
swing, the same difference is one part in ten to the sixteenth.

The baseline is saved with the circuit, as the shape thinned to 256 points. Its measurements are
worked out again on the way in rather than stored, so a release that fixes a measurement fixes it
for baselines taken before the fix instead of holding new runs against an old bug. Taking one does
not move the file format's version: it is a new section, and an older build already ignores what it
does not recognise.

**Requirements can now be written the way a specification is.** Five new measurements — **fall
time**, **overshoot**, **settling time**, **pulse width** and **slew rate** — join the eight that
were there. "Settles within 2 % in 10 µs" is most of what a control loop is judged on, and there
was no way to say it.

Overshoot and settling time are refused for anything that is not a step, which matters more than it
looks: the arithmetic would happily produce an overshoot for a sine, and it would mean nothing. The
settling time is timed from when the trace *left* its starting value, so a capture that began a
millisecond before the edge does not report a millisecond of settling that is really waiting. An
edge faster than the sampling gets no rise time rather than an interpolated one — any number there
would be a statement about the solver's step rather than about the circuit.

**A Bode plot can carry a family of curves.** Tick **Step** in the frequency response window and the
whole sweep runs once per value of any parameter on the canvas. DC sweeps and transients could both
already do this and the response could not, which is where the question comes up most: what the
feedback resistor does to the peaking, how far the corner moves across a capacitor's tolerance
band. Each pass re-solves the bias point, because a small-signal sweep linearises about it and a
parameter worth stepping usually moves it.

**Two ways to feed a circuit something other than a sine.** A function generator offers five
shapes, and a supply brownout, a startup ramp, a load step and a measured capture off a bench scope
are none of them.

**Sources > Waveform Source** plays back a list of points — SPICE's PWL source, written the way
anybody would say it: `0 0; 1m 0; 1.01m 5; 5m 5` is a five volt pulse with ten microsecond edges.
**File > Import Waveform...** reads a CSV off a scope or a data logger, or a WAV, straight into one.
Scale, offset and a time scale reshape the table without editing it.

**Digital I/O > Pattern Generator** drives four logic lines from a written sequence — a row per
line, a character per step, which is the logic analyser's own notation read backwards. A counter
that should reset on the third pulse, a state machine that has to see a particular sequence: until
now the only way to produce either was to sit in front of the circuit flipping a toggle while it
ran.

Every corner of a waveform and every step boundary of a pattern is a **solver breakpoint**. Without
that the transient loop takes the largest step the error allows and walks straight over a two
microsecond glitch, producing a smooth, plausible, entirely wrong answer.

Imported samples are **copied into the circuit rather than referenced**: a source pointing at a file
on disk is a circuit that opens differently on another machine. Long files are resampled rather than
truncated — the whole recording at lower resolution instead of the first fraction of it at full —
and the resampling averages across each stride rather than picking one sample in six, because
picking aliases and an aliased waveform is a different waveform that looks entirely plausible.

**A power budget: what every part is dissipating, against what it is rated for.**
**Simulate > Power...** (`Ctrl+F5`) ranks every part by how close it is to a limit, and says what
the circuit costs to run. A semiconductor already worried about itself — a MOSFET reports its own
watts, models its own die and complains when it is past either. A **resistor sitting at nine tenths
of a watt in a quarter-watt package said nothing at all**, solved perfectly, and is one of the
commonest ways a circuit that works on screen becomes a smell on the bench. Resistors, thermistors
and LDRs now carry a power rating; "warm" starts at half of it, which is the margin the trade has
used for as long as there have been resistors.

It **averages across a run by default**, because the instant a switching circuit happens to be
caught at is not a summary of it: a MOSFET mid-edge is dissipating watts and the same MOSFET a
microsecond later is dissipating milliwatts. Put a battery on the sheet and it works out how long
the circuit runs, from the capacity on the cell.

A capacitor is not counted, and that is not an omission — it stores and returns its energy rather
than spending it, and counting volts times amps as heat would put an imaginary watt in the budget
of every filter in the library. Parts that dissipate say so; nothing about the topology can tell a
capacitor from a resistor.

**The circuit now says what it is doing, not just what it is made of.** Every net's voltage and
every part's current can be written straight onto the schematic — **View > Show Live Values**, or
`Ctrl+L` — and the hover card carries the same figures whether or not the annotation is on. Finding
out what a node was sitting at used to cost placing a probe; resting the pointer on a resistor used
to tell you it was a 4k7 while declining to mention it was passing 40 mA and dissipating a watt.

What is deliberately not shown is anything ambiguous. A current is reported for a two-terminal part
and for nothing else: a package with ten pins has no single current through it, and a figure picked
from among them would be a plausible answer to a question nobody asked. A part that models its own
heating reports its die temperature and its dissipation, which it knows better than multiplying two
of our numbers would. Ground is left off — it is zero everywhere by definition — and figures below
a microvolt or a picoamp are written as a plain zero rather than dressed up as `450pV`, which reads
as a measurement of something at a resolution no instrument has.

The figures go on the drawing rather than in the margin, so they sit on plates of the canvas colour
and can be read where they land. They follow into an export and onto a printout: a schematic with
its operating point marked on it is a thing you take to a bench.

**The file format's version number now says when it may move, and a test holds it to that.** Adding
something — a field, a part, a parameter, a whole new section — never bumps it: a build that has
never heard of any of those already ignores what it does not recognise and opens the rest. The
number moves only when the *meaning* of existing data changes, which is the one case where an older
build would read a file successfully and be confidently wrong.

The distinction matters because bumping it for an addition costs everything and buys nothing: every
build already in the world would refuse every new file, for a change those builds could have
ignored. Six tests take a real saved circuit, edit the JSON into something a later version might
have written, and assert it still loads cleanly — which is the only way to test against a future
that does not exist yet.

### Fixed

**A current probe on a battery read backwards.** The battery reported the current it was
*delivering* where every other part in the library reports the current going *into* it — the
convention a clamp meter uses. So a probe clamped on a cell read the right magnitude with the wrong
sign, and the current-flow dots on its wires ran the wrong way. It was the only part doing this, and
the shape of the mistake is why it lasted: each end still read equal and opposite, and "current out
of a battery" is such a natural phrase that the wrong answer looked like the right one. Only a
comparison against its neighbours catches it, and there is now a test that makes exactly that
comparison across all three kinds of source.

## [0.33.3] - 2026-09-24

The dialog that offers back unsaved work, which could not be read. 186 components in 16
categories, 87 worked examples, 3070 tests.

### Fixed

**The recovery prompt was unreadable, and its question was not the answer.** The dialog offering
back an unsaved circuit came up in the light palette while the rest of the application was dark, so
its **Discard** and **Recover** buttons were pale grey on pale grey and effectively invisible — the
only legible thing was the message, which ended "Open it?", so that is what people clicked.

It is two bugs. The dialogs built in code are constructed at startup, **before the theme variant
has settled**: asked at that moment the application answers Light, and it becomes Dark a moment
later once the windows are realised. Fetching a brush there freezes the light one into a dialog the
rest of the application then paints dark around. The colours are bound now rather than fetched, so
they follow. And the message no longer asks a question the buttons do not answer — the buttons are
the question.

The code-built dialogs also close on Escape now, like every other one, and the check that every
dialog is shown through the helper covers the whole application rather than only the main window.
The four built in code were missed by the first version of it, which is why this one was still
broken after the last release.

## [0.33.2] - 2026-09-24

Two fixes, one of them reported and one found while fixing it. 186 components in 16 categories,
87 worked examples, 3069 tests.

### Fixed

**Dialogs could not be closed from inside them.** Seventeen of the twenty-two had no close button
and no key binding, so on a desktop that draws no title bar — Hyprland, Sway, i3 — the only way out
was the window manager's own close command, which the application never mentioned. Reported against
**Conditions**, which is where somebody sets the ambient temperature and then wants to go back and
run the circuit at it.

Every dialog now closes on **Escape** and carries a visible **Close** button. Both are wired
centrally, by the helper every dialog is shown through, so a dialog added later cannot forget them —
and a test asserts that every dialog has a visible way out and that none is shown any other way. It
went unnoticed for so long because it is invisible on a desktop with title bars.

**Recovering a second window's work never happened.** A window other than the first keeps its own
autosave snapshot, and the first window is supposed to offer those back on the next start. The flag
that decided which window was the first was passed in as a constructor argument, and the one call
site that had to pass it lost the argument in an unrelated revert — leaving it false everywhere and
the recovery silently dead. A window now counts which one it is rather than being told.

## [0.33.1] - 2026-09-23

Documentation only — nothing in the application changed, so there is no need to update for it
unless you want the guide. 186 components in 16 categories, 87 worked examples, 3046 tests.

**`4k7` is now explained rather than assumed.** It is RKM code — BS 1852, now IEC 60062 — where the
multiplier letter stands where the decimal point would have been, because a decimal point is the
easiest mark on a drawing to lose and `47` against `4.7` is a factor of ten in a value somebody is
about to solder in. The guide used the spelling throughout without ever naming it, which makes it
read like a typo to anybody who has not met it.

**And the collision it has with SPICE is written down.** `4k7` is 4 700 in a value box and 4 000 in
a SPICE card, because SPICE takes the first letter after the digits as the scale and discards the
rest. Both readings are correct where they are and neither can change without breaking files that
already exist, so it is documented — and pinned by a test, so it cannot drift into being an
accident.

## [0.33.0] - 2026-09-23

Twelve additions, in two halves. Five analyses and an overlay about the part of a design a waveform
does not show; and six about working on a drawing rather than solving one. 186 components in 16
categories, 87 worked examples, 3045 tests.

**What does it look like from here?** `Ctrl+F6` measures **impedance** against frequency at any
probe — magnitude and phase. Every bench question about loading is this one: what an amplifier
presents to whatever drives it, what a regulator can hold its output against, where a decoupling
network resonates and how much it actually helps at the frequency the chip switches at. A gain plot
answers none of them, because gain is a ratio between two points and this is a property of one.

One amp of small-signal current goes in and the volts that appear are the ohms. Every other source
is silenced for the sweep, which is what makes it the Thévenin impedance rather than a number with
the circuit's own signal mixed in. The phase says which kind of resonance you are looking at, and a
decoupling capacitor's series resonance — above which it is not a capacitor any more, it is the loop
of wire it is soldered into — is marked on the plot.

**What does it ring at?** `Shift+F6` finds the circuit's **poles and zeros**. Stability says a loop
has eight degrees of phase margin; this says why: a conjugate pair at 145 kHz with a Q of seven. A
transient shows a circuit ringing; this gives the frequency and how many cycles it takes to stop
without measuring either off a trace. They are the circuit's own properties rather than properties
of whatever you drove it with.

That needed a general real eigensolver, so there is now one. Zeros come from the bordered system
matrix rather than the textbook shortcut of "the natural frequencies with the output shorted",
because the shortcut silently loses zeros at the origin and every AC-coupled stage has one. A
transmission line is refused rather than approximated: a delay has infinitely many poles.

**How hot does the part get?** MOSFETs, bipolars and diodes now **heat themselves**. Give one a
thermal resistance in degrees per watt and its die stops sitting at ambient: on-resistance climbs,
a bipolar draws more as it warms, a rectifier's drop falls. A switch specified at room temperature
is specified wrong, and this is how you find out by how much. Zero is the default and keeps the old
behaviour exactly. Thermal runaway stops at the junction temperature the part is rated for and says
so on its hover card, rather than the solver failing with a message about time steps.

**Is that what it was supposed to do?** `Ctrl+F4` is where **requirements** are written down — a
name, a trace, one measurement off it, and a limit — and checked. Every analysis here already
produces numbers; until the limit is written down somewhere, whether they are the right numbers
lives in whoever last looked. The answer is three-way: met, not met, or nothing to measure, and the
third is not the second. Requirements are saved with the circuit.

**Where is the current actually going?** **View > Show Current Flow** puts moving dots on the wires,
at a rate set by what each is carrying and in the direction it is going. A schematic shows what is
connected and a scope shows what one point is doing; neither shows that current leaves the supply,
divides at a junction, and comes back. The speed is logarithmic, because a microamp of base current
beside an amp of collector current would otherwise be indistinguishable from stopped. Where a
junction divides the current the wires are left dark rather than guessed at.

**Why will it not solve?** The message used to be *Newton-Raphson failed to converge, try a
smaller time step*, which is only useful to somebody who already knows what is wrong — and that is
the one person who does not need it. The solver knew more all along: it tracks how far every
unknown moved, so it knows which net was still swinging, and it asks every non-linear part whether
it settled, so it knows which said no. Both name somewhere on the drawing to go and look. A part
that did not complain is never blamed.

**Where is R17?** `Ctrl+Shift+F` finds a part on the sheet — by designator, value, kind or net
name. `Ctrl+F` searches the palette for a part to *place*; this searches the drawing you already
have, which past twenty parts is the more frequent question.

**Edit > Arrange** lines up or spaces out a selection: six alignments and two distributions,
snapped back onto the grid, in one undo step however many parts moved.

**What is this circuit?** `Ctrl+F1` reads the drawing back in words — a divider and where its tap
sits, an RC and its corner, an op-amp stage and its gain, a flyback diode, a 555 and its frequency
and duty. About a dozen structures, each recognised exactly, and silence on everything else: a
confident wrong description is the one thing here that could teach somebody something false.

**What did I change?** **File > Compare with a Saved Circuit** answers it in values rather than
braces. Parts are matched by identity, so one that was renamed *and* moved is still the same part;
a wire is the pair of pins it joins, so redrawing it through a different corner is not a change.

**File > Design Report** writes one self-contained HTML page — the schematic as SVG, what the
circuit is, the requirements with their verdicts, the parts, and the conditions. Every piece of it
existed already; what was missing was the thing that assembles them, which is the artefact you hand
to another person.

**File > New Window** opens a second editor with its own circuit, sharing the clipboard — for
comparing two designs, and for carrying a block from one into the other. Each window keeps its own
recovery snapshot, and the next start offers every abandoned one back in a window of its own.

## [0.32.0] - 2026-09-23

What a part looks like, not just what it is set to. Hover a resistor and its colour bands are drawn
beside its value — and an inductor's, and the code printed on a ceramic. 186 components in 16
categories, 87 worked examples, 2833 tests.

**The value as it is written on the part.** A schematic says `4k7`; the one in your hand says
yellow-violet-red, and the distance between those two facts is why people carry a card in their
wallet. The hover card closes it: the bands a resistor or an inductor carries, or the three-digit
code on a ceramic disc, drawn next to the numbers that were already there.

**The conventions are followed rather than approximated.** Four bands at five percent and five at
one, because you cannot promise one percent on a value you only spelled to two figures — which is
why a 1 % part is brown-black-black-brown-brown and not brown-black-red-brown. Gold and silver
multipliers divide, so a 4R7 is yellow-violet-gold. The wider gap before the tolerance band is
drawn too, because on a real part it is the only thing that says which end to read from. And
tolerance rounds to the tighter band, never the looser one: the marking is a promise, and rounding
it the wrong way turns it into a lie.

**The two traps it is most useful for.** A ceramic's three digits are **picofarads** however large
the part is, so `104` is 100 nF rather than 104 of anything. An inductor's bands are
**microhenries**, for the same reason — there is no way to paint 100 µH in henries at all, because
the multiplier band does not reach that far down.

**A code that cannot say the value says so.** 1234 Ω has no four-band spelling, so the card shows
what the bands *do* say and marks it, rather than quietly drawing the nearest thing and letting the
picture assert something untrue. Electrolytics get no marking at all: their value is printed on the
can in plain microfarads, and inventing a code for one would teach something the part does not
carry.

Every marking is also read out in words — *brown 1 · black 0 · orange × 1,000 · gold ± 5 %* — which
is the half that works for anybody who cannot tell the colours apart, and the half that teaches the
code rather than just showing it.

**A hover card can no longer be wider than the canvas it sits on.** Placement could move a card off
an edge but never shrink one, so on a narrow canvas a long line was clipped at the control's edge.
Nothing wraps wider than the surface now, which also fixes a long violation overflowing the same
way.

**Thirteen of the guide's pictures are new or redrawn.** The plate of colour codes is generated by
the same code that draws the card, so it cannot drift away from it. Ten screenshots had gone two
releases stale — no palette search box, part counts from a much smaller catalogue, a scope with no
cursors, and an About dialog reporting 140 components where there are 186. The prose never drifted,
because the documentation tests check text against the code; nothing can read what is inside a PNG.

## [0.31.0] - 2026-09-22

Six new analyses, and the parts to use them on. Until now this could tell you what a circuit does;
this release is mostly about telling you whether it is any *good* — will it oscillate, how much
noise does it make, how distorted is it, and will it still work built from the parts you can
actually buy. 186 components in 16 categories, 87 worked examples, 2777 tests.

**Will it oscillate.** `Ctrl+F7` measures the loop gain of a feedback circuit and reports the
**phase margin**, the gain margin and the crossover, with the verdict in words. This is the single
most common reason a circuit that is correct on paper does not work on a bench: a regulator that
rings, an amplifier that sings at two megahertz, a servo that hunts are all the same question, and
none of them can be answered by looking at gain alone.

Where to break the loop is a judgement the circuit cannot make for itself, so it is a part — a
**Loop Probe**, placed in the feedback path. Everywhere else it is a piece of wire, and it becomes
a break only while the sweep runs, and only for small signals, so the operating point stays the one
the working circuit has. The new **Loop Stability** example is a follower, which surprises people
by being the hardest configuration to keep stable rather than the easiest.

**How much noise, and from what.** `Shift+F5` gives output noise density in volts per root hertz,
the total in RMS, and — the half that changes what you do — a ranking of every generator in the
circuit by what it put on the output. "This makes 12 µV" is a number; "and 80 % of it is R3" is an
instruction, and it is almost always one part and almost always not the one people guess.

Resistors contribute Johnson noise, diodes and bipolars shot noise in every current that crosses a
junction, MOSFETs their channel noise, and op-amps their input-referred voltage and current noise —
which in a circuit built around one are usually the whole answer. Flicker noise rises towards DC on
the active devices, with the corner as an editable property, because a bipolar's few hundred hertz
against a MOSFET's hundred kilohertz is the reason a low-frequency front end is built out of
bipolars.

**How distorted.** The spectrum window now measures **THD** and **THD+N** and lists the harmonics
in dB relative to the carrier. An amplifier at one percent distortion looks exactly like a sine on
the scope — you cannot see it, you can only measure it. And *which* harmonics is the diagnosis
rather than the figure: odd means the distortion is symmetric, like both rails clipping; even means
it is lopsided, like a half-wave rectifier. The new **Crossover Distortion** example is a class-B
pair and the notch it puts in everything.

**What happens if I try other values.** `Ctrl+Shift+F7` runs the whole transient once per value of
something and lays the results on one set of axes — three capacitor values and the ringing that
goes with each, four gate resistors and four switching edges. The DC sweep says where a circuit
settles; this says how it gets there, which is the question people actually have. Every pass starts
from the same conditions, so the curves differ only by the value.

**The worst it can ever be, and the value you need.** Beside the tolerance analysis there is now
**Worst case**, which finds the corner rather than sampling towards it. The combination where every
part sits at its extreme the same way is one out of 2ⁿ, so random trials essentially never land on
it and the sampled spread is always optimistic. This finds it in a solve per part rather than 2ⁿ,
and reports the recipe as well as the number.

And the DC sweep runs **backwards**: say what reading you want and it finds the value that gives
it, along with the nearest E24 part you can actually buy and what *that* one achieves — because the
exact answer is usually not a thing anybody sells.

**Printing.** `Ctrl+P` lays the schematic, the traces and the parts list onto real sheets — a page
each, fitted inside the margins, with a header naming the circuit and the date. It prints in ink
whatever theme you are using, since a dark theme's light strokes would come out blank on white
paper. Avalonia has no printing API on any platform, so the document is produced as a page-sized
PDF and handed to the system; the dialog says whether that will reach a printer or a viewer.

**Parts you did not have to model.** SPICE `.subckt` definitions import, not just `.model` cards. A
card describes one device, which is why importing has only ever reached diodes, bipolars and
MOSFETs; anything more interesting — an op-amp, a regulator, a reference — ships as a subcircuit,
and one now comes in as a block you can place. **VCVS and VCCS** are parts in their own right too,
which is what makes the macromodels work and which are worth having anyway: a transconductance into
a capacitor *is* an op-amp's input stage.

**Circuits travel.** A saved file carries the SPICE cards for the imported models it uses, so it
opens complete on a machine that never had them. Until now it opened with the *default* part in
place of the one you chose — a different forward drop, different answers, and nothing on the
schematic to say so. Only what the circuit uses, only what was imported, and a model you already
have always wins.

**Around the editor.** The palette has a **Find a part** box (`Ctrl+F`) that matches on the name, on
what the part does, and on the group. **Click a wire or a pin** and the whole net lights up, taken
from the netlist the solver builds rather than the lines as drawn — so a typo'd net label shows up
at once. The scope can **keep a reference** to compare against after a change, and takes
**arithmetic on the traces** (`Out / In`, `db(Out / In)`, `I ^ 2 * 220`). A copy of the circuit is
**autosaved** every minute and offered back on the next start. `Ctrl+E` will write the parts list as
a **bill of materials**. And moving the pointer across a loop-gain or noise plot says what it reads
there.

**Temperature reaches the regulators.** A regulator's output falls about a millivolt a degree, with
the die at the room temperature plus whatever it is dissipating — so one idling in a hot enclosure
and one working hard on a bench land in the same place. The TL431's bandgap is **bowed rather than
sloped**: sweep it and you get an arch, both ends below the middle, which is the whole reason the
part costs more than a zener.

### Fixed

**The TL431 would not converge at some setpoints.** Ten siemens through a sigmoid a couple of
millivolts wide is a knife edge for Newton, so 2.490 V would not solve while 2.495 V solved
immediately, for no reason anybody could see. It now limits its step the way the diode limits its
junction voltage.

**Exporting could write its file and then silently do nothing else.** The palette used for printing
in ink was built from thread-affine brushes, so drawing it from another thread threw — and the
export catches that exception. The status bar just stayed on "Ready".

**Stability measured the wrong thing if anything else was driving the circuit.** A loop gain is a
ratio either side of the break, so a generator still connected added its own response to both ends.
It stayed plausible while it was wrong: a follower read 6 dB of loop gain where it has 106. Every
other small-signal source is now silenced for the length of a stability sweep.

## [0.30.0] - 2026-09-21

Export stops being only pictures. Two text formats, so a circuit drawn here can be handed to
another tool rather than only looked at. 183 components in 16 categories, 84 worked examples,
2411 tests.

**A SPICE netlist.** `Ctrl+E` and pick *SPICE netlist*, and the circuit comes out as a deck.
There are analyses this engine does not do — noise, distortion, pole-zero — and a deck ngspice or
LTspice can read is the way to them. Together with the model import added in 0.29.0 that makes
this a front end you can leave: a card off a datasheet comes in, and a circuit built round it
goes out.

Resistors, capacitors, inductors, sources, function generators, diodes and transistors all have
direct equivalents. Models are written out alongside the elements, so the deck stands on its own
rather than depending on a library the reader has not got. A net you have named keeps its name, so
the file reads as your circuit rather than as a list of numbered nodes. A block's contents appear
as the ordinary parts they are.

**And it tells you what it could not carry.** A 7400, an I²C master, an ultrasonic ranger: those
are modelled here by event-driven code rather than by a netlist, and there is no honest
translation. They appear in the deck as comments naming the part, under a line saying the circuit
is incomplete where they were. A netlist that quietly omits half a circuit is worse than one that
says so.

**CSV of the traces.** A header naming each column with its unit, then a row per sample — for a
spreadsheet to fit a curve, a script to compare two runs, or a report that wants the figures
rather than a picture of them.

It writes everything recorded rather than the window on screen, because a file is not a screen.
The probes do not share a time axis — each records when the solver accepted a time point, and one
attached halfway through a run starts halfway through — so the rows are the union of their sample
times, with each column interpolated where it has no sample of its own, and a probe that did not
exist yet left blank rather than given a reading nobody took. Long runs decimate evenly across the
whole run rather than being truncated to the first fraction of it.

Both live in the same export dialog as the image formats, where the layout, resolution and
background settings grey out — they have no use for them — and a line appears saying what the
chosen format actually does.

### Also

Two things turned up while exporting the shipped examples, which is why it was worth doing rather
than testing the writer against waveforms of its own:

- **Function generators were left out of the deck entirely**, which for most of the analog examples
  meant exporting a circuit with nothing driving it. SPICE has exactly that element: a sine becomes
  `SIN`, and the shapes it has no element for become `PULSE` — a triangle is a pulse that spends
  its whole period rising and falling, which is the same waveform rather than an approximation
  of it.
- **An LED came out named `DED1`**, because stripping a designator's prefix assumed one letter.
  Element names are now allocated rather than computed, and a part whose designator already starts
  with the right type letter reserves it first — so a source called `V1` stays `V1` instead of
  losing it to an `FG1` that happens to sort earlier.

One caution, stated plainly: the decks are verified by structure, and by reading their own model
cards back through the SPICE importer — two independent pieces of code agreeing, which is a real
check. They have **not** been run through ngspice, which is not installed on the machine this was
built on. Expect them to be right; do not expect them to be certified.

## [0.29.0] - 2026-09-21

The release where the app stopped being only a simulator and became something you can measure
with. Four new analyses, protocol decoding, hierarchy, tolerances and SPICE model import. 183
components in 16 categories, 84 worked examples, 2366 tests.

### Four ways to ask a question

**DC sweep** (`Shift+F7`) steps a parameter, re-solves the operating point at every value, and
plots the result. It is how you draw the curves parts are actually specified by — a diode's
exponential, a panel's maximum power point, an amplifier's transfer characteristic — and none of
them were visible before without building a ramp generator and squinting at a transient. Tick
**Step** for a second parameter and it becomes a curve tracer: the **Curve Tracer** example sweeps
a transistor's collector voltage while stepping its base current, and the fan off the front of
every datasheet comes out, evenly spaced, tilting upward with the Early effect.

**Spectrum** (`F3`) transforms the traces the scope has already recorded. This is not the frequency
response and the difference matters: the response measures what the circuit *does* to each
frequency, and this measures what is *in* a signal. Half of what the examples teach lives here and
was invisible — the sidebands that are what AM is, the harmonics that make a full-wave rectifier's
ripple easier to filter than a half-wave one's, the odd harmonics of a square wave.

**Tolerance analysis** (`Shift+F4`) builds the circuit a few hundred times with its parts drawn
from their tolerance bands. Every other analysis uses the value written on the schematic, and no
resistor has ever had the value written on it. Resistors, capacitors and inductors now have a
tolerance — 5 %, 20 % and 10 % by default, which is what the ordinary part is. Beside the histogram
is the other half of the question: **which part is to blame**, ranked, with each part's band next
to what the circuit does with it. A megohm across a 2 kΩ divider can have twenty times the
tolerance of everything else and still be the least of your worries.

**Temperature.** The whole circuit sits at an ambient temperature, saved with the file and
sweepable like any other parameter. A silicon diode's forward drop falls about 2 mV/°C, a power
MOSFET's on-resistance climbs about 80 % from 25 °C to 125 °C, and a bipolar's gain roughly doubles
across its range. The **Diode Thermometer** example is a diode at constant current waiting for a
temperature sweep. (Junctions, MOSFETs and op-amps carry temperature; regulator references and the
TL431 do not yet, and come out of a sweep flat because the model is silent rather than because the
part is stable — the guide says so plainly rather than letting you read it as a result.)

### Reading a bus

**Decode Bus** (`Shift+F3`) reads the recorded traces as **I²C, SPI, UART, 1-Wire or CAN** instead
of as edges. Open it on any of the bus examples and it is already set up. The EEPROM example comes
back as its whole conversation on one line — address, restart, the three bytes that spell `HI!`,
and the NACK the master sends on purpose to stop the device sending more. The SPI converter's
answer arrives *underneath* the question, which is what full duplex means and what a picture of
three wiggly lines cannot show.

It also refuses rather than guessing. A capture too coarse to decode is rejected with a reason: a
pulse that fell between two samples is not a short pulse, it is an absent one, and every bit after
it has moved. A trace carrying no frame is not parsed into one.

### Measuring

The scope gained **automatic readouts** — peak to peak, mean, RMS, frequency, period, duty cycle,
10–90 % rise time — and two **draggable cursors** with the gap between them, its reciprocal, and
what each trace did there. The periodic figures are left out rather than guessed when there is not
enough signal to support them.

**XY mode** plots one trace against another instead of against time. That draws an I-V curve or a
transfer characteristic while the circuit runs, and it draws **hysteresis as the loop it actually
is** — which a sweep cannot do, because a sweep only goes one way. The **Hysteresis Loop** example
opens with the scope already in XY.

**Probes** now measure the **difference** between two points, or the **power** those two things
multiply to. A CAN pair, a high-side shunt and a bridge sensor are all defined as a difference and
could not honestly be shown any other way; power is what a regulator is burning and what a resistor
has to be rated for. Shift-click a terminal with the probe tool to set the second point.

### Drawing

**Blocks.** Select two or more parts and press `Ctrl+G`: they become one symbol with a pin wherever
a wire crossed the boundary. The hierarchy is flattened before anything is solved, so a block is
exactly as accurate as the parts inside it — because it *is* the parts inside it, moved rather than
copied, so a probe attached to one keeps reading it. `Ctrl+Shift+G` puts them back. **Edit > Block
Library** saves a block by name so it can be placed again, in this circuit or the next.

**Net labels.** Two labels with the same name are one net, with no wire between them. Past a
certain size a drawing has signals that go everywhere, and routing each of them to every place it
is needed hides the circuit it is meant to show.

**Notes, headings and boxes** go on the drawing without being in the circuit, and exports carry
them. A schematic is a document as much as a description.

### Checking

**Check Circuit** (`F4`) looks for the mistakes that are silent, because they are about how the
parts are joined rather than about any one of them: no ground, a shorted source, a supply pin on
the ground net, an unwired supply, a floating input, two driven outputs sharing a net, a mistyped
net label. Every rule in it is one that caught something real during development. Select a finding
and the parts it is about are selected on the canvas.

### Anything with a datasheet

**Edit > Import SPICE Model** takes a `.model` card and turns it into a part. Diodes, bipolars and
MOSFETs; the parameters map across because both this simulator and SPICE come from the same
formulation. Imported models are offered wherever built-in ones are and kept between runs.
Parameters this simulator has nowhere to put are **named rather than dropped**, so nobody comes
away believing a part is modelled more closely than it is.

### New parts

Nineteen new palette entries, 164 to 183. Four of them are the net label and annotations above;
the rest are parts for things the palette could not show at all: a **74245** bus transceiver and
**74373** latch, a **74161** synchronous counter, a **CAN transceiver**, an **IGBT**, a
**photodiode**, **2K×8 SRAM and ROM** whose contents you type in and read back, an **MCP3008** SPI
converter, a **solid-state relay** that waits for the mains to cross zero, an **A4988**
microstepping driver that regulates coil current rather than applying a voltage, an **LM324** quad
op-amp, a **MAX232** that makes its own ±8.5 V from a 5 V rail, a **resettable PPTC fuse** that
heats rather than opening, and a **bipolar stepper** wiring for the driver to drive.

### Also

- **Hover a part** and it describes itself — designator, type, value and relevant settings —
  without your having to select it. Turn it off in the View menu.
- Four new **Measurement** examples, each set up so the analysis it demonstrates works immediately
  with nothing to change first.
- **Fixed: undo after grouping lost the parts.** Group two components, press `Ctrl+Z`, and they
  were gone — the history recorded grouping as three separate steps and one undo landed between
  them. Ungrouping and placing a block had the same fault.
- **Fixed: the 1-Wire example could not be decoded at its own timebase.** Its pulses are six
  microseconds and the scope was sampling too slowly to see them.
- **Fixed: an op-amp's input offset had never had any effect.** It was added to the input stage's
  differential voltage and then subtracted back out by the linearisation, cancelling exactly.
- **Fixed: importing a SPICE card named after a built-in part could delete the built-in.**

## [0.28.0] - 2026-09-20

An editing release. The schematic gains the gesture it was missing — select, copy, paste — and the
guide gains a printable form. 164 components in 16 categories, 68 worked examples, 1889 tests.

**Copy and paste.** Select a part and press `Ctrl+C`, then `Ctrl+V`: a duplicate lands a little
down and to the right, already selected and ready to drag. It is a new part with its own
designator — copy `R4` and you get `R5` — carrying every value across, including the ones easy to
forget: the model on an op-amp, the number of inputs on a gate, the waveform and duty cycle on a
generator. Eight identical current-limiting resistors is now eight keystrokes rather than eight
trips to the palette.

The copy is taken when you press `Ctrl+C` rather than when you paste, so editing or deleting the
original afterwards does not change what comes out. Repeated pastes cascade down the canvas instead
of stacking on one spot.

**Box selection.** With the Select tool, drag a box across empty canvas and everything wholly
inside it is selected — parts and the wires between them. Drag any one of them and the whole group
moves, keeping its shape. `R` rotates all of it, `Delete` removes all of it.

**And a group copies with its wiring.** Draw a box round an input stage you like, copy it, paste
it, and you have the stage again — wired the way you drew it, a working circuit rather than a pile
of parts that happen to look right. Paste twice and the two are independent, each wired inside
itself and neither joined to the other. A wire only comes across when both of its ends are in the
group; one leaving the selection went to something that is not being duplicated, so there is
nothing for the copy to attach to.

**The palette and the example browser open one group at a time.** Both start closed, and opening a
group closes whichever was open. Sixteen palette categories and twelve example groups opened
independently stack up past the height of the panel; kept to one, every heading and its count stays
in view. **All** in the palette header is the way out of it, and a search in the example browser
still opens everything it matched — a match folded away inside a closed group is the same as no
match at all.

**The user guide is now a PDF as well.** Seventy-six pages, with a title page, a two-column
contents of all seventy headings, the screenshots in place and the tables intact. It is attached to
this release alongside the binaries and checksummed with them, and `./scripts/build-guide.sh`
builds it from the same Markdown — nothing is duplicated, so the PDF cannot drift from the guide on
GitHub. `PAPER=Letter` for US paper.

### Fixed

- **`Ctrl+V` switched tools instead of pasting.** `V` on its own selects the Select tool, and the
  canvas looked at the plain letters before the modifiers.
- **Selection could have leaked into exports.** Highlighting several parts at once meant the
  renderer could no longer take the selection from a single property, so a PNG of a circuit risked
  depending on what happened to be highlighted when it was taken. Rendering now takes an explicit
  option and the exporter turns it off.
- **A timing test was flaky on shared machines.** The one covering real-time pacing compared a
  paced run against an unpaced one, which measures the scheduler as much as the pacing and failed
  on a loaded CI runner. It is now two tests, each asserting against the pacing ceiling at a speed
  factor chosen to give its own assertion a wide margin — load can only move both in the safe
  direction.

## [0.27.0] - 2026-09-20

Every component in the palette now has a worked example. That was not true of any previous
release, and it is the point of this one: 164 components in 16 categories, **68 worked examples**
in 12 groups, and 1852 tests.

Two releases ago, thirty-five of the parts had nothing demonstrating them and eleven sections of
the guide described a circuit that did not ship. Both numbers are now zero. If a part is in the
palette, something in **File > Examples...** uses it, and the guide section that explains it says
which one.

**Sixteen new examples.**

Analog and power:

- **Negative Rail** — an ICL7660 making −4.9 V out of +5, and a TL081 straddling both rails to
  follow a sine that goes below ground. The negative rail is not the point; the half of the
  waveform it buys is.
- **Battery Resistance** — three cells, the same fifty milliamp load on each, nothing else in the
  circuit. The AA gives up thirteen millivolts, the 18650 three, and the coin cell **half a volt**.
- **Crystal Q** — a 1 MHz crystal and an LC tuned to the same megahertz, each into the same load.
  The crystal passes a fourteen kilohertz band; the LC passes the whole sweep. Press `F7`.
- **Common-Mode Choke** — a signal across the pair and a five volt interferer on both wires at
  once. The signal arrives whole and about fifty millivolts of the noise does.
- **Analog Switch** — a 4066 picking one of four sources, and what happens when you close two.
- **Night Light** — an LDR against a TL431 rather than another divider, because a divider's
  threshold moves with the supply and a reference's does not.

Sensors and actuators:

- **Thermostat** — an NTC, a comparator, and the feedback resistor that is the difference between
  a thermostat and a relay that chatters.
- **Thermocouple** — forty-one microvolts a degree into an INA126, and the cold junction problem
  made visible: warm the cold end and the reading falls with the hot end untouched.
- **Servo Sweep** — 1.0, 1.5 and 2.0 ms against the three angles on the datasheet. Change the
  frequency instead of the width and watch nothing happen.
- **Hall Counter** — a Hall switch counting one pass per pass, next to the reed switch that cannot.

Buses and displays:

- **Level Shifting** — a whole I²C bus crossing a 3.3 V to 5 V boundary, with the acknowledgements
  coming back down the channels the data went up.
- **I2C LCD** — the same HD44780 on a PCF8574 backpack, two wires instead of six.
- **Stepper Motor** — a 4017 walking four windings through a ULN2003.
- **Analog and Logic** — the ADC and DAC bridges, and why one of them has two thresholds.

### Fixed

- **The rotary encoder's Detent control did nothing electrically.** Its contacts are driven by a
  turn in progress and only double-clicking the part started one, so dragging the control on the
  CONTROLS panel changed a number and produced no quadrature at all. The two are now one action.
- **The ULN2003's COM pin was declared and never modelled**, so the part's freewheeling diodes —
  the reason it exists for relay coils and stepper windings — were not there, and an inductive load
  switched off drove its output to tens of kilovolts. *(Also in 0.26.0; the stepper example is what
  it was built for.)*
- **The Window Detector example never detected anything**, and **the Night Light came on in
  daylight**: both had a comparator's inputs the wrong way up, which gives a working circuit that
  is exactly wrong. Each now has a test that fails if it is put back.
- **The TL431 in the night light was fed through 2.2 kΩ**, which leaves it under its minimum
  operating current once the supply falls to four volts. Sized for the bottom of the supply
  instead, which is the advice its own section gives.

### Documentation

The guide gains sections on the two signal bridges — which had been in the palette with nothing
written about them — and on maximum power point, two-stage surge protection, recording audio and
what a ULN2003's COM pin is for. Sixty-eight examples, sixty-eight pointers from the prose to the
circuit.

## [0.26.0] - 2026-09-20

The examples stop being a menu and start being a library. The palette gains three parts and now
holds 164 components in 16 categories; there are 54 worked examples in 12 groups, and 1799 tests.

**File > Examples... is now a browser** (`Ctrl+Shift+E`). Forty-odd entries in a submenu is a list
to be got through rather than one to look at, and it was getting worse with every release. They are
now grouped the way the component palette is — Fundamentals, Analog, Power Supplies, Switching &
Motors, Digital Logic, Timers & Oscillators, Buses & Interfaces, Sensors, Signal Integrity & RF,
Audio, Displays, Development Boards — with a description of whichever one is selected beside the
list and a search box across the name, the description and the group. Looking for `I2C` finds the
bus examples; looking for `hysteresis` finds the comparator circuit whose name you have forgotten.

**Eleven new examples**, most of them for parts that had been in the palette with nothing
demonstrating them:

- **Rail to Rail** — the same follower three times on one 5 V supply, an LM741 against an LM358
  against an MCP6002, so how much of a supply each part can actually use is one picture instead of
  a table. The single most common surprise in single-supply analog work.
- **Character LCD** — an Arduino driving an HD44780 in four-bit mode, playing the whole
  initialisation dance as a pin sequence. The part every library hides, visible byte by byte.
- **Load Cell** — a strain-gauge bridge into an INA126: ten millivolts riding on two and a half
  volts of common mode, which is the measurement instrumentation amplifiers exist for.
- **Audio Amplifier** — a microphone through a volume control into an LM386 into a speaker.
- **Relay Driver** — logic to a coil through an optocoupler and a ULN2003, with both sides of the
  changeover contact lit.
- **Solar Panel** — the one example where the thing under test is the *load*: turn the knob and
  find the maximum power point, which lands four fifths of the way to open circuit.
- **Lithium Charge Cycle** — constant current to 4.2 V, then constant voltage tapering to
  termination, the whole cycle in about a second.
- **Surge Protection** — a varistor, a series resistor and a TVS bringing a two-hundred-volt spike
  down in two stages, because neither clamp could do it alone.
- **Rotary Encoder** — quadrature, where the direction is in which output moves first and neither
  one alone says anything.
- **Varactor Tuning** and **RS-485 Link** for the new parts below.

**Three new components.** A **varactor** — a diode used for its capacitance, which is how every
radio built since the sixties is tuned. A **common-mode choke**, which passes a signal and blocks
the noise riding on both of its wires at once. And an **RS-485 transceiver**, with a terminator you
can switch out mid-run to watch fifty metres of cable start ringing.

**Speakers and microphones can now work in audio files.** A **Speaker** takes a `Recording Path`
and writes the voltage across it to a WAV as the simulation runs; **Simulate > Play Speaker
Recording** hands that file to whatever your desktop plays audio with. A **Microphone** takes a
`Source Path` and plays a WAV instead of its built-in tone, so real programme material goes through
the circuit rather than one frequency for ever. A waveform and a sound are different evidence about
the same circuit, and for an audio stage the second is what it is actually judged by: clipping is a
small flat spot on a trace and an unmistakable noise.

It is files rather than the sound card deliberately. A variable-step transient solver takes the
steps the circuit needs, not the steps a clock wants, so it cannot hand a device forty-four thousand
samples a second on time for ever — and through a file the same run is reproducible and can be
asserted in a test. The recording is resampled onto a fixed grid, because otherwise its pitch would
follow the solver's step size.

### Fixed

- **The ULN2003's COM pin did nothing.** It was declared and never modelled, which meant the part's
  freewheeling diodes — the entire reason that pin exists, and the reason the part is used for
  relay coils and stepper windings — were not there. An inductive load switched off drove its
  output to tens of kilovolts. The diodes are now modelled, so tying COM to the load's supply
  clamps a diode drop above it and leaving it off has the consequence it has on a breadboard.
- **The rotary encoder's Detent control changed a number and nothing else.** Its contacts are
  driven by a turn in progress and only double-clicking the part ever started one, so dragging the
  control on the CONTROLS panel produced no quadrature at all. The two are now the same action.
- **The Window Detector example never detected anything.** Both LM339 channels had their signal and
  reference the wrong way round, so the two open-collector outputs were never released together and
  the "In range" probe was a flat line at every input voltage. An open collector says "out of
  range" by pulling and says "in range" only by letting go.
- **The load cell had no operable property**, so the weight could not be varied while it ran.

### Documentation

Ten older examples had never been named anywhere in the guide, so nothing led a reader from the
prose to the circuit demonstrating it — a JFET amplifier was one click away and unmentioned. Every
one of the 54 examples is now referenced from the section it belongs to, and the guide gains
sections on recording audio, on maximum power point, on two-stage surge protection and on what the
COM pin is for.

## [0.25.1] - 2026-09-20

One new example. The palette is unchanged at 161 components in 16 categories; there are now 43
worked examples and 1680 tests.

**File > Examples > Adjustable Supply** is a whole linear bench supply end to end — transformer,
bridge rectifier, reservoir, regulator and load — with a probe on each of the three voltages, so
the AC going in, the rectified rail with its ripple, and the clean regulated output can all be
watched turning into one another.

The interesting part is that the regulator is a **7805**, which is a fixed five volt part, and the
supply is adjustable. All a 78xx does is hold its output five volts above its own GND pin. Ground
that pin and you get five volts, which is how everybody uses one. Lift it — a resistor from the
output to it, and a potentiometer from there to ground — and the output rises with it. Turn **RV1**
in the CONTROLS panel while the simulation runs and the output moves from 5 V to about 18 V, while
the two traces behind it stay exactly where they were.

The arrangement's real fault is in the model rather than hidden by it. A 78xx returns its whole
quiescent current — several milliamps — through that same GND pin, so it flows through the
potentiometer and adds a few volts of its own to the answer, moving with load and temperature as it
goes. It is a fine way to get an adjustable rail and a poor way to get an accurate one, and it is
exactly why the LM317 exists: the same circuit round one, whose adjust pin takes fifty microamps
instead, gives the clean relationship its datasheet prints.

The guide gains a section on all of that, and the example is tested at five settings of the knob
against the arithmetic, plus the two things that actually go wrong when one is built — the rail
clearing dropout at the ripple troughs rather than on average, and the regulator rejecting a volt
and a half of ripple down to under twenty millivolts.

## [0.25.0] - 2026-09-20

A second way of looking at a circuit. The palette holds 161 components in 16 categories, 42 worked
examples, and 1671 tests measure them against closed-form answers.

**The simulator can now sweep frequency.** Until this release it could find a bias point and step
through time, and that was all. **Simulate > Frequency Response** (F7) answers the other question a
circuit gets asked: what does it do to a sine at *each frequency*, from one end of a span to the
other. Magnitude and phase against a logarithmic axis — a Bode plot, which is what every
datasheet's response curve is drawn on — with the −3 dB corner worked out underneath.

Put a probe where you want the answer and open the window. The traces are your probes, so nothing
else needs setting up, and it sweeps its own solve from the bias point, so it can be opened while a
transient is running without disturbing it.

A great many things that are tedious to establish by stepping a generator are one picture here:

- **Where a filter turns over.** The RC example reads 159 Hz off the status line.
- **How much gain an amplifier has left.** A gain of ten from a 1 MHz op-amp is flat to about
  90 kHz — the product over the *noise* gain, so 1 MHz over eleven rather than over ten, which is
  the sort of distinction a plot settles in a second.
- **What a ferrite bead really does**, which is the impedance curve its datasheet prints, and the
  quickest way to see that "600 Ω" is true at exactly one frequency.
- **Resonances nobody put there.** An unterminated stub of transmission line looks like a dead
  short at its quarter-wave frequency. That is obvious in a sweep and very nearly impossible to
  find in the time domain.

The answer is exact in a way a measured one is not: at one frequency a capacitor *is* an admittance
of jωC, so there is no integration error and no time step anywhere in the calculation. It is also
strictly **small-signal** — everything non-linear is replaced by its slope at the bias point — so
it says nothing at all about clipping, slew limiting or distortion. For questions about size rather
than frequency, the oscilloscope is still the instrument.

**A CD4046 phase-locked loop.** A VCO, two phase comparators, and nothing else: everything
interesting about a PLL happens outside the package, in the loop filter you have to add. It is the
part that makes **lock range** and **capture range** two genuinely different numbers — how far the
input may drift once locked, against how close it has to be before the loop can grab it from cold.
A PLL that holds a signal perfectly and refuses to lock onto the same signal from a standing start
is not faulty, and this shows why. The two comparators differ in exactly that way: the
phase-frequency detector pulls in from anywhere in range, while the exclusive-OR only captures what
is already close.

**An AD633 analog multiplier.** W = (X1−X2)(Y1−Y2)/10 + Z, and the ten volts is the first surprise:
two inputs at 5 V give 2.5 V, not 25. One line, read differently, is amplitude modulation (a
carrier multiplied by a tone, which is what AM *is* rather than something done to a carrier),
mixing, squaring, true RMS, a voltage-controlled amplifier, and a phase detector.

**An INA219 current sensor.** A shunt in the **high side** of a rail, so the load's ground stays
where it belongs and every other measurement in the circuit still means what it says. And with it
the limit that makes a current-sense amplifier different from any other: a **common-mode range**
that is not the same thing as its supply. This one runs from 3.3 V and watches a rail up to 26 V —
that is the point of it — but past that it still returns a number over the bus, and that number is
not a measurement. It says so, because nothing else would.

**Fixed:** the oscilloscope resolved its plot colours against the application's theme variant
rather than its own. The two disagree until the desktop reports its preference, and the scope only
escaped it because it repaints twenty times a second and corrects itself on the way past.

**Three new examples** — a PLL locking onto a signal, a carrier with an envelope on it, and a
current sensor following a load switched in while it runs — each asserted in the test suite against
the thing it is there to show.

## [0.24.0] - 2026-09-20

Eight new parts, one of which needed the solver to remember more than one step. The palette holds
158 components in 16 categories, 39 worked examples, and 1610 tests measure them against
closed-form answers.

**Transmission lines.** Every connection in this program was instantaneous until now — change a
voltage at one end of a wire and it is that voltage at the other end in the same instant — and that
is the assumption that fails first as things get faster. A line carries a *wave* instead: about
five nanoseconds a metre, and for that long it looks to whatever is driving it like a plain 50 Ω
resistor, whatever is connected at the far end, because nothing about the far end has reached the
driver yet.

Then the wave arrives, and what it finds decides how much comes back. An open end sends all of it
back the same way up, so the far end briefly sits at *twice* the incident voltage. A short sends it
back inverted. A matched load absorbs it and that is the end of the story. Then the reflection
travels home, meets the source impedance, and some of it turns round again — which is the staircase
on the scope that looks like a broken driver and is nothing of the kind.

**File > Examples > Reflections** puts the terminator on a switch, so you can close it from the
CONTROLS panel while it runs and watch the ringing disappear. It explains the overshoot on a fast
logic edge into a long trace, why a series resistor at the *driver* quietens a line that a resistor
at the far end could not, and why a 10 cm track is a wire at 1 MHz and a component at 500 MHz.

**A 1-Wire bus, and a DS18B20 on it.** The odd bus: no clock, and on most parts no second wire
either, so a bit cannot be a level sampled on an edge — it is a **pulse width**. Six microseconds
down is a one, sixty is a zero, half a millisecond is a reset.

All three of the DS18B20's famous traps are modelled rather than described. **Eighty-five degrees
means "no reading"** — the scratchpad powers up holding exactly 85.0 °C and keeps it until a
conversion has actually finished, which is the most reported fault with this part and is not a
fault. **A conversion takes most of a second** at twelve bits, and nine bits is ninety milliseconds
at a sixteenth of the precision. And **parasitic power browns out**: converting needs a milliamp
and a half that no ordinary pull-up can pass, so the line sags and the reading stays at 85 with
nothing anywhere saying why. The part draws the current, so the sag and the failure are real.

**An MCP4725 DAC**, which closes the loop the ADS1115 opened. The library could digitise a voltage
but not produce one, so the only way out of the digital world was a pin at the rail or at ground.
Its code is a live control, and the example runs it through a follower into a load the DAC could
not have driven — because it cannot drive anything, which is the first thing that disappoints
people about it.

**A ferrite bead**, and the half of it nobody is told. It is not an inductor: at low frequencies it
is a piece of wire, in its band it is a resistor that turns noise into heat, and above that its own
stray capacitance shorts it out. So the number on the datasheet is the impedance at *one
frequency*, and the same bead is under thirty ohms at a megahertz. It follows that a bead only
damps ringing **inside its band** — put one in front of a hundred nanofarads and the pair resonate
where the bead is still an inductor, so it rings exactly as an ordinary inductor would and "just
fit a bead" has achieved nothing.

**A MOSFET gate driver**, for the arithmetic: a gate is tens of nanofarads, 50 nF to 10 V in 50 ns
is ten amps, and a logic pin has twenty milliamps. Slow switching is where the heat comes from — a
MOSFET is cheap on and cheap off and expensive in between. It also takes the gate to **its own
supply** rather than the logic rail, which a faster pin could not fix. The example is the
comparison in one schematic: one clock, two identical MOSFETs, one gate off the pin and one through
the driver.

**A reed switch**, which answers the same question as the Hall sensor and has almost nothing else
in common with it. It is a contact rather than a semiconductor: no supply at all, either pole, and
it will switch mains. What it has against it is that it **bounces** — so one magnet passing clocks
a counter five times, which is what debouncing is for and what the Hall switch beside it does not
do.

**A PIR motion sensor**, whose whole character is what it does after the movement stops. It holds
its output high for seconds to minutes, so it is not reporting what is happening now but that
something happened recently. And the retrigger jumper is the difference between a light that stays
on while you are there and one that goes out while you are standing under it.

**Seven new examples**, one per part, each asserted in the test suite against the thing it exists
to show rather than merely against running.

## [0.23.0] - 2026-09-20

A lit LED that looks lit. The palette holds 150 components in 16 categories, and 1499 tests measure
them against closed-form answers.

**You can now tell at a glance which LEDs are on.** An LED that was conducting barely changed its
symbol: the triangle — the biggest mark in it, and the one your eye goes to — stayed the same dark
shape whether it was passing twenty milliamps or nothing at all, and the only difference was a pale
wash behind it and two arrows a few pixels long. On a board with half a dozen indicators you had to
go looking for which ones were on, which rather defeats the point of an indicator.

Now the body itself takes the light, its outline deepens to match, the emission arrows turn the
colour of the light when it is on and stay neutral when it is off, and the glow behind it is a soft
falloff rather than a flat disc. Off is a plain dark diode; on is unmistakable, at the size symbols
are actually drawn rather than only when zoomed in.

**The same for the seven-segment display**, where a half-lit digit used to wash out to pink instead
of reading as a dimmer red. Lit elements now go solid immediately and carry how hard they are driven
in the colour, which is how a real display looks; they also bleed a little past their own edges, so
an element that is on is never merely a slightly different grey from one that is off.

Three rules sit under all of that, and each of them fixed something visible:

- **How hard a part is driven is not how strongly it should be drawn.** An LED at a tenth of its
  rated current is plainly on to anyone looking at the breadboard, but a tenth of the way from the
  unlit colour to the lit one is a smudge. Anything conducting is now drawn at least half way to
  full, with the rest of the range separating dim from bright.
- **Dimness belongs in the colour, not in transparency.** A half-transparent red on a white sheet
  becomes pink; a dim LED should look like a darker red.
- **An emitted colour too close to the canvas cannot be seen at all.** A white LED on a light sheet
  used to draw as an empty outline — which reads as *off*, the exact opposite of what it was trying
  to show. Emitted colours are now pushed away from the background until they stand out, and left
  alone when they already do.

**Fixed:** the unlit colour of a seven-segment element was written into the renderer as the dark
theme's value, so on a light canvas the off segments were drawn in a colour meant to sit on a dark
one. It comes from the theme now, which has had a light variant waiting unused.

One thing to know: a white LED on the dark theme is still the least striking of the six, because an
unlit symbol is already drawn in a light colour there. Its halo tells you it is on, but it will not
jump out the way a red or green one does.

## [0.22.0] - 2026-09-20

Op-amps that stop where the real ones do. The palette holds 150 components in 16 categories, and
1493 tests measure them against closed-form answers.

**An LM358 now reaches the bottom rail.** The real part is lopsided — its output pulls down to
within twenty millivolts of the negative supply but stops a volt and a half below the positive one
— and that asymmetry is the whole reason it exists. It is why an LM358 works on a single battery
where an LM741 does not. Until now the model clamped symmetrically about the middle of the
supplies and split the difference, so the guide had to tell you not to trust it near either rail
in particular.

Build the same follower three times on a single 5 V supply and ask each one for 50 mV. The MCP6002
and the LM358 both give you 50 mV; the LM741 gives you 1.5 V and looks broken. Ask the same three
for 4.9 V and the LM358 is no better than the LM741 — both stop around 3.5 V, and only the
rail-to-rail part gets there. Which rail a part is good at is worth checking before you pick one,
and now the simulator will tell you.

**Coming out of saturation no longer takes milliseconds.** Drive an op-amp hard against a rail and
leave it there, and its internal gain node used to drift off into the kilovolts, because nothing
in the model bounded it. The DC answer still looked right — the output was at the rail either way
— but the next time the input changed its mind, that node had to travel all the way back at the
datasheet slew rate. A 741 that should recover in fifty microseconds took thousands. Comparators,
peak detectors, anything that spends time against a rail and then has to come off it, were all
slower than the part they were modelling.

**And the slew rate is now the datasheet figure.** A 741 driven by a fast step sustains 0.50 V/µs,
where before it managed 0.23 — the old output stage compressed the ramp on its way through.

Under all three: the output buffer no longer clamps itself. The rails clamp the **gain node**,
which is where a real amplifier does it, and the buffer that follows is a plain linear follower.
That leaves the solver something to steer with when the output is hard against a rail, which is
what made the asymmetric case converge at last.

Verified over a **245-case matrix** — every headroom pair against every supply arrangement,
split and single, asked for voltages inside the window and well outside it. A symmetric part on a
split supply hides every one of these faults, which is how they survived this long.

## [0.21.0] - 2026-09-19

Noise, and the parts that exist because of it. The palette holds 150 components in 16 categories,
and 1247 tests measure them against closed-form answers.

**A noise source**, and with it the reason half the analog parts in the palette exist. Every
circuit here has been perfectly clean until now, which is the one way in which none of them
resembled a real one.

Feed a slow ramp through a comparator and the output changes once. Put a dozen millivolts of noise
on the same ramp and it changes a dozen times — every wobble back across the threshold is another
edge, and anything counting them counts all of them. Add a feedback resistor and it goes back to
once. That is what the LM311, the LM393 and the 74HC14 are *for*, and there was previously no way
to show it. **File > Examples > Noise and Hysteresis** is that circuit; a test asserts all three
outcomes rather than the docs merely claiming them.

The noise is **band-limited** — drawn at a fixed rate and held, not a fresh number every time
point, or its character would depend on the solver's step size — and **repeatable** from a seed,
because a result you cannot reproduce is not much use for working out what went wrong.

**MCP6002**, a rail-to-rail op-amp, for the comparison. Build the same follower on a single 5 V
supply with each part and ask for 4.9 V: an LM741 manages 3.5, an LM358 4.3, and the MCP6002 4.9.
Ask for 1 V and the LM741 cannot do it at all — its output stops a volt and a half above the
negative rail, which is why single-supply parts exist. Halfway up they all agree, so the difference
really is the rails.

**Bench instruments now have knobs.** A supply's voltage and a generator's frequency, amplitude,
offset and duty are controls in the CONTROLS panel alongside the switches — so you can sweep a
frequency with the scope running and watch a filter turn over, rather than stopping, typing a
number and starting again.

## [0.20.0] - 2026-09-19

A circuit you can operate while it runs, and a motor you can run both ways. The palette holds 148
components in 16 categories, and 1229 tests measure them against closed-form answers.

**A front panel for the circuit.** Every switch, potentiometer, light level, temperature, magnet
and target distance in a circuit is now gathered into a **CONTROLS** panel under the properties
panel — and they can be worked while the simulation runs. Move a wiper and the next time point is
solved with the new value; nothing has to be stopped or restarted.

Until now, operating a running circuit meant finding the part on the canvas and double-clicking it.
That is fine for one switch and hopeless for a board with eight, and it could only ever toggle
between two values rather than set one. A potentiometer had no way to be turned at all while the
circuit ran.

Switches become toggles and ranges become sliders, showing the value and its unit. Ranges spanning
several decades — light, most obviously — are laid out logarithmically, because a linear slider
from a dark room to direct sun puts everything a circuit responds to in the first half-millimetre
of travel. The panel and the canvas stay in step: double-clicking a part still works it, and the
panel follows.

What appears is decided by the components rather than by the interface: a property marked
`[Operable]` becomes a control, so a part added later gets one by saying so on the property and
nothing in the UI changes.

**H-bridge.** There was a DC motor in the palette and no way to run it backwards, which is most of
what anyone does with one. Four switches in an H, two inputs and an enable, and all four input
combinations behave as they should — including the two people get wrong. **Both inputs the same is
a brake**, not an off: the motor is shorted to itself and its own back-EMF stops it. **Disabling is
a coast**, where it just spins down. Deciding which of those you want is usually the whole design.

**Shoot-through** is the failure it exists to let you make safely. Real drivers decode the inputs
so the top and bottom of one leg cannot be on together; turn that interlock off — as a bridge built
from four loose MOSFETs effectively is — and both inputs high becomes a dead short across the
supply through two switches, doing nothing whatsoever to the motor, reported with the watts it is
burning. The four **body diodes** are modelled too, because a motor is an inductance and when the
switches open its current has to go somewhere.

**TP4056 lithium charger**, which completes a chain the library could nearly build: generate,
store, regulate — and now charge. A cell cannot simply be connected to a supply, so this is the
procedure instead. Constant current until the cell reaches 4.2 V, then constant voltage while the
current tails away, then stop — and stay stopped, rather than restarting the moment the voltage
sags.

It is a **linear** charger, so everything between the input and the cell is thrown away inside it:
a full amp from five volts into a cell at three and a half is one and a half watts in a part the
size of a grain of rice, and that is reported rather than left to be found by smell. It also
cannot lift a voltage, only drop one, so a supply that sags below the cell quietly stops charging.

**File > Examples > Motor Reversing** has three switches you can flip while it runs.

## [0.19.0] - 2026-09-19

Three ways of getting the physical world into a circuit, and one of getting the circuit back out
of it. The palette holds 146 components in 16 categories, and 1189 tests measure them against
closed-form answers.

**An ADC on the I²C bus.** Everything else on that bus dealt in bytes that were already digital —
memory, pins, the time. The **ADS1115** reaches back into the circuit it is sitting in and reports
what the voltage on a node really is, which is the point of putting a converter on a bus at all.

Its three ways of lying to you are modelled, because they are the ones that cost an afternoon.
The **gain setting** decides what counts as full scale: set to ±2.048 V and fed three volts it does
not complain, it returns the largest number it has and goes on returning it — push to five and the
reading does not move. **Conversion takes time**: at eight samples a second, reading more often
than every 125 ms hands back the previous answer, because polling harder does not sample faster.
And it measures a **difference**, so single-ended quietly includes whatever sits between its ground
and yours.

**Hall sensor**, the A3144 sort. Open collector, so with no pull-up it does not work at all rather
than working badly — reported, not left for you to find. And real **hysteresis**: on at about
20 mT, off again at 10, and in between it remembers what it was doing, so 15 mT reads differently
depending on which way the magnet arrived. That is what makes a wheel magnet give one pulse instead
of a burst. Unipolar by default, so turning the magnet round gets you nothing, as it does on a
bench.

**Phototransistor**, which is the LDR's opposite and worth understanding before choosing either.
An LDR is a resistance that falls as it is lit; this is a current source commanded by light. It is
also a junction rather than a bulk photoconductor, so it responds in microseconds where an LDR
takes tens of milliseconds — which is why no remote control has ever contained one. Being a current
source is what makes it awkward: too small a load and the output barely moves, too large and it
saturates in room light and stops following the light at all.

**File > Examples > Light Meter** puts the chain together: light into the phototransistor, current
into a resistor, volts into the ADC, a number over two wires.

## [0.18.0] - 2026-09-19

The last of the serial buses, and the part that lets two voltage domains share one. The palette
holds 143 components in 16 categories, and 1157 tests measure them against closed-form answers.

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

[Unreleased]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.33.3...HEAD
[0.33.3]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.33.2...v0.33.3
[0.33.2]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.33.1...v0.33.2
[0.33.1]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.33.0...v0.33.1
[0.33.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.32.0...v0.33.0
[0.32.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.31.0...v0.32.0
[0.31.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.30.0...v0.31.0
[0.30.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.29.0...v0.30.0
[0.29.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.28.0...v0.29.0
[0.28.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.27.0...v0.28.0
[0.27.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.26.0...v0.27.0
[0.26.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.25.1...v0.26.0
[0.25.1]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.25.0...v0.25.1
[0.25.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.24.0...v0.25.0
[0.24.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.23.0...v0.24.0
[0.23.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.22.0...v0.23.0
[0.22.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.21.0...v0.22.0
[0.21.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.20.0...v0.21.0
[0.20.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.19.0...v0.20.0
[0.19.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.18.0...v0.19.0
[0.18.0]: https://github.com/Harlock123/CIRQAvalonia/compare/v0.17.0...v0.18.0
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
