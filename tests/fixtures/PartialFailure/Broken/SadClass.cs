namespace PartialFailure.Broken;

// This class lives in a project that fails to compile (unresolvable PackageReference).
// Tests assert no symbol from this file ends up in the indexed store.
public sealed class SadClass
{
    public int Cube(int x) => x * x * x;
}
