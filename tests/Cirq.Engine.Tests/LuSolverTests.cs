using Cirq.Engine.Numerics;

namespace Cirq.Engine.Tests;

public class LuSolverTests
{
    [Fact]
    public void SolvesA3x3SystemExactly()
    {
        double[][] a =
        [
            [2, 1, -1],
            [-3, -1, 2],
            [-2, 1, 2],
        ];
        double[] b = [8, -11, -3];

        var x = LuSolver.SolveSystem(a, b);

        Assert.Equal(2.0, x[0], 1e-10);
        Assert.Equal(3.0, x[1], 1e-10);
        Assert.Equal(-1.0, x[2], 1e-10);
    }

    [Fact]
    public void SolvesSystemRequiringPivoting()
    {
        // A zero leading pivot forces a row swap.
        double[][] a =
        [
            [0, 2, 1],
            [1, 0, 3],
            [4, 1, 0],
        ];
        double[] b = [7, 10, 9];

        var x = LuSolver.SolveSystem(a, b);

        Assert.Equal(7.0, a[0][1] * x[1] + a[0][2] * x[2], 1e-9);
        Assert.Equal(10.0, x[0] + 3 * x[2], 1e-9);
        Assert.Equal(9.0, 4 * x[0] + x[1], 1e-9);
    }

    [Fact]
    public void ReusesFactorizationAcrossRightHandSides()
    {
        double[][] a = [[4, 3], [6, 3]];
        var solver = new LuSolver(2);
        solver.Factor(a);

        var x1 = new double[2];
        var x2 = new double[2];
        solver.Solve([10, 12], x1);
        solver.Solve([7, 9], x2);

        Assert.Equal(10.0, 4 * x1[0] + 3 * x1[1], 1e-10);
        Assert.Equal(12.0, 6 * x1[0] + 3 * x1[1], 1e-10);
        Assert.Equal(7.0, 4 * x2[0] + 3 * x2[1], 1e-10);
        Assert.Equal(9.0, 6 * x2[0] + 3 * x2[1], 1e-10);
    }

    [Fact]
    public void ThrowsOnSingularMatrix()
    {
        double[][] a = [[1, 2], [2, 4]];
        Assert.Throws<SingularMatrixException>(() => LuSolver.SolveSystem(a, [1, 2]));
    }
}
