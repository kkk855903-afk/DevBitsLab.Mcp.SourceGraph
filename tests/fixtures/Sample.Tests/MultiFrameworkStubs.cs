// Stub copies of the NUnit and MSTest discriminator attributes. The graph indexer's test-framework
// detector inspects only the simple attribute name (with the trailing "Attribute" stripped), so
// these locally-declared attributes are equivalent for the purpose of the multi-framework
// detection scenario without forcing the fixture to take on the full NUnit/MSTest runtime
// dependencies. See `TestDiscriminator.Detect` for the discrimination rules.
namespace NUnit.Framework
{
    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestCaseAttribute : System.Attribute
    {
        public TestCaseAttribute(params object[] args) { Arguments = args; }
        public object[] Arguments { get; }
    }

    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TestFixtureAttribute : System.Attribute { }
}

namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
    public sealed class TestMethodAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TestClassAttribute : System.Attribute { }
}
