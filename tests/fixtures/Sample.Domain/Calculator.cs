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
}
