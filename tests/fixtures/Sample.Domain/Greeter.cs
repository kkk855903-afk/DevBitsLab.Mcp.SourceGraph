namespace Sample.Domain;

public class Greeter : IGreeter
{
    private readonly string _prefix;

    public Greeter(string prefix)
    {
        _prefix = prefix;
    }

    public string Greet(string name) => $"{_prefix}, {name}!";
}
