namespace DevBitsLab.Mcp.SourceGraph.Sdk;

/// <summary>
/// Well-known kebab-case keys used in the optional <c>Metadata</c> dictionary on
/// <see cref="IndexEvent.EdgeEmitted"/>. These constants give XAML / WPF / Avalonia / web indexers
/// a shared vocabulary for the binding-, event-, and resource-edge payloads they emit so the host
/// renderer (and downstream tools that read <c>payload</c> on edges) stay consistent across
/// languages. Plugins MAY add additional kebab-case keys — the host stores the dictionary as opaque
/// JSON and does not reject unknown keys. Use
/// <see cref="DevBitsLab.Mcp.SourceGraph.Sdk.Validation.KebabCaseValidator"/> to validate
/// plugin-supplied keys.
/// </summary>
public static class PayloadKeys
{
    /// <summary>
    /// Binding path on a property-set or two-way binding edge — the dotted property chain that the
    /// binding evaluates against the data context.
    /// Example value: <c>"User.Address.Street"</c>.
    /// </summary>
    public const string Path = "path";

    /// <summary>
    /// Binding mode for a binding edge: typically <c>"one-way"</c>, <c>"two-way"</c>,
    /// <c>"one-time"</c>, or <c>"one-way-to-source"</c>.
    /// Example value: <c>"two-way"</c>.
    /// </summary>
    public const string Mode = "mode";

    /// <summary>
    /// Canonical key (or short type name when no key is available) of an <c>IValueConverter</c>
    /// referenced by a binding edge.
    /// Example value: <c>"csharp:T:MyApp.Converters.BoolToVisibilityConverter"</c>.
    /// </summary>
    public const string Converter = "converter";

    /// <summary>
    /// Literal value passed to the binding's <c>ConverterParameter</c>; rendered as the original
    /// markup string so downstream consumers can re-parse it if needed.
    /// Example value: <c>"Inverse"</c>.
    /// </summary>
    public const string ConverterParameter = "converter-parameter";

    /// <summary>
    /// Name of the routed event / command / signal that an event-handler edge wires up.
    /// Example value: <c>"Click"</c>.
    /// </summary>
    public const string Event = "event";

    /// <summary>
    /// Name of the handler method or command that an event-handler edge resolves to. Pair with
    /// the edge's destination canonical key when the handler is a method symbol.
    /// Example value: <c>"OnSubmitClicked"</c>.
    /// </summary>
    public const string Handler = "handler";

    /// <summary>
    /// Declared <c>x:DataType</c> / compiled-binding data type on a binding edge — the type the
    /// binding path resolves against at compile time.
    /// Example value: <c>"csharp:T:MyApp.ViewModels.UserViewModel"</c>.
    /// </summary>
    public const string DataType = "data-type";

    /// <summary>
    /// Style / template / trigger <c>TargetType</c> — the type a style applies to or a template
    /// instantiates.
    /// Example value: <c>"csharp:T:System.Windows.Controls.Button"</c>.
    /// </summary>
    public const string TargetType = "target-type";

    /// <summary>
    /// Resource <c>x:Key</c> on a resource-reference edge (e.g. <c>StaticResource</c> /
    /// <c>DynamicResource</c> lookup).
    /// Example value: <c>"PrimaryButtonStyle"</c>.
    /// </summary>
    public const string Key = "key";

    /// <summary>
    /// Style inheritance pointer — the <c>BasedOn</c> resource key (or canonical key) the current
    /// style derives from.
    /// Example value: <c>"BaseButtonStyle"</c>.
    /// </summary>
    public const string BasedOn = "based-on";

    /// <summary>
    /// Source-element name on a binding edge whose source is a sibling element via
    /// <c>ElementName=</c>.
    /// Example value: <c>"InputBox"</c>.
    /// </summary>
    public const string ElementName = "element-name";

    /// <summary>
    /// Encoded <c>RelativeSource</c> binding source: typically <c>"self"</c>,
    /// <c>"templated-parent"</c>, or <c>"find-ancestor:&lt;type-key&gt;"</c>.
    /// Example value: <c>"templated-parent"</c>.
    /// </summary>
    public const string RelativeSource = "relative-source";

    /// <summary>
    /// Literal fallback value rendered when a binding fails to produce a value. Stored as the
    /// original markup string.
    /// Example value: <c>"(unknown)"</c>.
    /// </summary>
    public const string FallbackValue = "fallback-value";

    /// <summary>
    /// Composite-formatting string applied to a binding's source value before display.
    /// Example value: <c>"{0:C}"</c>.
    /// </summary>
    public const string StringFormat = "string-format";

    /// <summary>
    /// When the source is updated for a two-way binding: typically <c>"property-changed"</c>,
    /// <c>"lost-focus"</c>, or <c>"explicit"</c>.
    /// Example value: <c>"property-changed"</c>.
    /// </summary>
    public const string UpdateSourceTrigger = "update-source-trigger";
}
