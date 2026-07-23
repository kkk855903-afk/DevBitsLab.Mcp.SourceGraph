using Microsoft.VisualStudio.TestTools.UnitTesting;
using NUnit.Framework;
using Sample.Domain;

namespace Sample.Tests;

/// <summary>
/// Mirrors an NUnit fixture: <c>[TestFixture]</c> on the class, <c>[Test]</c>/<c>[TestCase]</c>
/// on the methods. The indexer should set <c>test_framework = "nunit"</c> on every test method
/// here and emit a <c>Tests</c> edge to the production call.
/// </summary>
[TestFixture]
public sealed class GreeterNUnitTests
{
    [NUnit.Framework.Test]
    public void Greet_PrependsPrefix()
    {
        var g = new Greeter("Hello");
        var result = g.Greet("World");
        // Only purpose: an indexable production call so the Tests edge has a target.
        _ = result.Length;
    }

    [TestCase(1, 2, 3)]
    public void Add_isAssociative(int a, int b, int expected)
    {
        var c = new Calculator();
        _ = c.Add(a, b);
    }
}

/// <summary>
/// Mirrors an MSTest fixture: <c>[TestClass]</c> on the class, <c>[TestMethod]</c> on the
/// methods. The indexer should set <c>test_framework = "mstest"</c>.
/// </summary>
[TestClass]
public sealed class GreeterMsTests
{
    [TestMethod]
    public void MakeGreeter_returnsSomething()
    {
        var c = new Calculator();
        _ = c.MakeGreeter("Hi");
    }
}
