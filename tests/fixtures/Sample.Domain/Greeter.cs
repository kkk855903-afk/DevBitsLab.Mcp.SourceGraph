namespace Sample.Domain;

public class Greeter : IGreeter
{
    private readonly string _prefix;

    public Greeter(string prefix)
    {
        _prefix = prefix;
    }

    public string Greet(string name) => $"{_prefix}, {name}!";

    [Obsolete("legacy")]
    [Legacy("use Greet instead", Severity = 2)]
    public string LegacyGreet(string name) => Greet(name);
}
