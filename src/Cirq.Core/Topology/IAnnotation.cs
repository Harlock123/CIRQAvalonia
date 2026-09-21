namespace Cirq.Core.Topology;

/// <summary>
/// Something on the drawing that is not part of the circuit: a note, a box round a section, a
/// heading.
/// <para>
/// A schematic is a document as much as it is a description. The circuit says what is connected
/// to what; the annotations say which part of it matters, what the awkward bit is doing, and which
/// node to put a probe on. Everything in this library is explained at length in prose that lives
/// somewhere else — being able to put a sentence next to the thing it is about is the missing
/// half of that.
/// </para>
/// <para>
/// Annotations are components with no terminals, which is what lets them be placed, selected,
/// dragged, copied, undone and saved by machinery that already exists. What the interface marks is
/// everything that should <i>not</i> treat them as parts: they carry no designator caption, they
/// stay out of the bill of materials, and they set their own size rather than having one inferred
/// from pins they do not have.
/// </para>
/// </summary>
public interface IAnnotation
{
    /// <summary>Half the width it occupies on the canvas, in world units.</summary>
    double HalfWidth { get; }

    /// <summary>Half the height it occupies.</summary>
    double HalfHeight { get; }
}
