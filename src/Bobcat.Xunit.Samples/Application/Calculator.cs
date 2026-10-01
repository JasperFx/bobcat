namespace Bobcat.Xunit.Samples.Application;

/// <summary>
/// The system under test, copied verbatim from Storyteller 5's own documentation samples
/// (<c>src/Samples/Application/Calculator.cs</c>) so that the specifications recreated here are
/// describing exactly the same behaviour Storyteller's were.
/// </summary>
public class Calculator
{
    public double Value { get; set; }

    public void MultiplyBy(double value) => Value *= value;

    public void DivideBy(double value) => Value /= value;

    public void Add(double value) => Value += value;

    public void Subtract(double value) => Value -= value;
}
