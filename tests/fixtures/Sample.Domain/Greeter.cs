namespace Sample.Domain;

public class Greeter : IGreeter
{
    private readonly string _prefix;
    private int _count;

    public Greeter(string prefix)
    {
        _prefix = prefix;
        _count = 0;
    }

    public string Greet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new System.ArgumentException("name required", nameof(name));
        }

        _count++;                        // read+write
        _count += 2;                     // read+write (compound assignment)
        var current = _count;            // read
        Bump(ref current);               // ref => read+write at the call site
        TrySet(out _count);              // out => write
        return $"{_prefix}, {name}! ({_count})";
    }

    public int Count => _count;          // read

    private static void Bump(ref int n) => n++;

    private static void TrySet(out int n) => n = 1;

    [System.Obsolete("legacy")]
    [Legacy("use Greet instead", Severity = 2)]
    public string LegacyGreet(string name) => Greet(name);
}
