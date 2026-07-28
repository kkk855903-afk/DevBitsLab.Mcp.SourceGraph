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

    /// <summary>Demonstrates Instantiates + UsesType edges to an indexed type.</summary>
    public IGreeter MakeGreeter(string prefix) => new Greeter(prefix);

    /// <summary>Demonstrates Throws to an indexed exception type.</summary>
    public int Divide(int a, int b)
    {
        if (b == 0) throw new DivisionByZero();
        return a / b;
    }

    /// <summary>
    /// Marked obsolete to guarantee a CS0618 warning fires at every call site.
    /// Used by the integrate-source-and-diagnostics fixture to verify diagnostics indexing.
    /// </summary>
    [System.Obsolete("Use Add(a, b) directly")]
    public int LegacyAdd(int a, int b) => Add(a, b);

    public T Execute<T>(System.Func<T> operation) => operation();

    public int ExecuteInferred()
    {
        System.Func<int> operation = () => 42;
        return Execute(operation);
    }
}

public sealed class DivisionByZero : System.Exception
{
    public DivisionByZero() : base("cannot divide by zero") { }
}
