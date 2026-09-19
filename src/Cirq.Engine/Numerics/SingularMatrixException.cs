namespace Cirq.Engine.Numerics;

public sealed class SingularMatrixException : Exception
{
    public SingularMatrixException(int row)
        : base($"The MNA matrix is singular at row {row}. The circuit likely contains a floating node, " +
               "a voltage-source loop, or a missing ground reference.")
    {
        Row = row;
    }

    public int Row { get; }
}
