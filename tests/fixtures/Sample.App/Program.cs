using Sample.Domain;

var calc = new Calculator();
var sum = calc.Add(2, 3);
var product = calc.Multiply(4, 5);

IGreeter greeter = new Greeter("Hello");
Console.WriteLine(greeter.Greet("World"));
Console.WriteLine($"sum={sum} product={product}");
