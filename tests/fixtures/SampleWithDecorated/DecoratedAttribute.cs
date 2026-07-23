namespace Sample.Decorated;

/// <summary>
/// Marker attribute the SamplePlugin's <c>DecoratedClassAnalyzer</c> reacts to. Carrying it on a
/// class causes the analyzer to emit a synthetic edge during the post-index pass.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DecoratedAttribute : Attribute { }
