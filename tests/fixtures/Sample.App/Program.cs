using Sample.Domain;

var calc = new Calculator();
var sum = calc.Add(2, 3);
var product = calc.Multiply(4, 5);
// Deliberate use site for [Obsolete] LegacyAdd: guarantees CS0618 fires so the
// integrate-source-and-diagnostics fixture has a known diagnostic to assert against.
#pragma warning disable CS0618
var legacy = calc.LegacyAdd(1, 1);
#pragma warning restore CS0618

// And one more use site that does NOT pragma-disable, so the diagnostic actually
// reaches compilation.GetDiagnostics() (the indexer captures every diagnostic the
// compiler emits, but the fixture project itself disables TreatWarningsAsErrors via
// tests/fixtures/Directory.Build.props so the build still succeeds).
var legacy2 = calc.LegacyAdd(2, 2);

// Source-generator output: the Sample.Generators IIncrementalGenerator emits
// Sample.Generated.GeneratedHello with a static Greet() method.
var generatedHello = Sample.Generated.GeneratedHello.Greet();

IGreeter greeter = new Greeter("Hello");
Console.WriteLine(greeter.Greet("World"));
Console.WriteLine($"sum={sum} product={product} legacy={legacy} legacy2={legacy2} generated={generatedHello}");
