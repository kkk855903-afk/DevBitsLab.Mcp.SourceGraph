using Sample.Domain;
using Xunit;

namespace Sample.Tests;

/// <summary>
/// xUnit fixture exercising <see cref="Calculator"/>. The graph indexer should detect the
/// <c>[Fact]</c> attribute, set <c>test_framework = "xunit"</c> on the symbol row, and emit a
/// <c>Tests</c> edge from <c>AddTwoNumbers_returnsTheirSum</c> to <c>Calculator.Add</c> — the
/// first non-trivial production-code call inside the body.
/// </summary>
public sealed class CalculatorTests
{
    [Fact]
    public void AddTwoNumbers_returnsTheirSum()
    {
        var c = new Calculator();
        var sum = c.Add(2, 3);
        Assert.Equal(5, sum);
    }

    [Theory]
    [InlineData(2, 3, 5)]
    [InlineData(0, 0, 0)]
    public void AddManyNumbers_isCommutative(int a, int b, int expected)
    {
        var c = new Calculator();
        Assert.Equal(expected, c.Add(a, b));
        Assert.Equal(expected, c.Add(b, a));
    }

    [Fact]
    public void CallsInsideLambda_areAttributedToTheContainingMethod()
    {
        var c = new Calculator();
        System.Func<System.Func<int>, int> dispatch = callback => callback();
        System.Func<int> calculate = () => dispatch(() => c.Add(20, 22));

        Assert.Equal(42, calculate());
    }

    [Fact]
    public void CandidateCallInsideNestedAsyncLambda_isAttributedToContainingMethod()
    {
        System.Action invoke = () => Dispatch(
            async () => await CandidateOnlyTargetAsync());

        invoke();
    }

    private static void Dispatch(
        System.Func<System.Threading.Tasks.Task> callback) =>
        _ = callback();

    private static async Task CandidateOnlyTargetAsync() =>
        await System.Threading.Tasks.Task.CompletedTask;
}
