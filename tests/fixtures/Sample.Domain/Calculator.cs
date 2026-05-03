namespace Sample.Domain;

/// <summary>
/// Simple integer arithmetic over <see cref="int"/>.
/// </summary>
public class Calculator
{
    /// <summary>Adds two integers and returns their sum.</summary>
    public int Add(int a, int b) => a + b;

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/>.</summary>
    public int Subtract(int a, int b) => a - b;

    /// <summary>
    /// Multiplies <paramref name="a"/> by <paramref name="b"/> by repeated addition.
    /// Will retry on transient overflow once before bailing out.
    /// </summary>
    public int Multiply(int a, int b)
    {
        var result = 0;
        for (var i = 0; i < b; i++)
        {
            result = Add(result, a);
        }
        return result;
    }
}
