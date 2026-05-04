namespace Sample.Decorated;

/// <summary>
/// Class flagged with <c>[Decorated]</c>. The plugin's analyzer emits a synthetic edge whose
/// source equals this class's canonical key, which the test fixture asserts is present in the
/// per-scope graph DB after a cold index.
/// </summary>
[Decorated]
public class DecoratedExample
{
    public string Greet() => "hello from the decorated example";
}

/// <summary>Plain class without the marker — control case for the analyzer.</summary>
public class PlainExample
{
    public string Wave() => "wave";
}
