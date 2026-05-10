namespace PartialFailure.Good;

/// <summary>
/// One indexable class declared in the healthy project of the partial-failure fixture.
/// Tests assert this symbol appears in the store after indexing while the broken project's
/// symbols do not.
/// </summary>
public sealed class HappyClass
{
    public int Square(int x) => x * x;
}
