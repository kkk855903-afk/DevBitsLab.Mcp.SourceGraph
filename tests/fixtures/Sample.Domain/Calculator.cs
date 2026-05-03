namespace Sample.Domain;

public class Calculator
{
    public int Add(int a, int b) => a + b;

    public int Subtract(int a, int b) => a - b;

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
}

public sealed class DivisionByZero : System.Exception
{
    public DivisionByZero() : base("cannot divide by zero") { }
}
