using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>What triggers a mapping: a button or an analog stick.</summary>
/// <remarks>
/// The polymorphism is carried by <see cref="InputBindingConverter"/> rather than by
/// <see cref="JsonDerivedTypeAttribute"/>: a converter registered for the base type is the
/// only shape that can also answer for a discriminator no type in this build claims, and the
/// attributes and a converter cannot coexist on one type. The converter writes the same
/// <c>$type</c> discriminator the attributes did, so profiles are unchanged either way.
/// </remarks>
[JsonConverter(typeof(InputBindingConverter))]
public abstract record InputBinding;

/// <summary>
/// A source this build does not know, kept so the mapping that carries it still loads - the
/// same contract <see cref="UnknownTarget"/> keeps on the target side. A profile written by a
/// newer build names a <c>$type</c> discriminator no type here claims, and throwing over it
/// cost the user the whole profile: the mapping row came up empty, so the mapping behind the
/// unrecognized source could neither be shown nor deleted. The discriminator and the source's
/// own JSON are kept exactly as they were written, so the mapping displays, deletes and
/// re-saves without losing what the other build put there.
/// </summary>
public sealed record UnknownBinding(string Discriminator, string RawJson) : InputBinding
{
    /// <summary>
    /// How the mapping row names it. Every description surface falls back to the source's own
    /// <c>ToString</c> for a type it has no case for, so an unknown source reads as an
    /// unsupported input rather than as the debug form the record would otherwise print.
    /// </summary>
    public override string ToString()
        => string.IsNullOrEmpty(Discriminator) ? "不支持的输入" : $"不支持的输入（{Discriminator}）";
}

/// <summary>
/// A button press. <paramref name="Code"/> carries the raw platform code and is only
/// meaningful when <paramref name="Button"/> is <see cref="GamepadButton.Extra"/>; it is
/// omitted from JSON while it holds its default so ordinary profiles stay unchanged.
/// </summary>
public sealed record ButtonBinding(
    GamepadButton Button,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Code = 0) : InputBinding
{
    /// <summary>
    /// Direction this button implies for a continuous target (mouse move / wheel). A button is
    /// binary, so it maps to one cardinal direction at full deflection rather than to an axis.
    /// Null for key / combo / mouse-button targets, where a direction is meaningless.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public InputDirection? Direction { get; init; }

    /// <summary>
    /// Optional gesture configuration (双击 / 长按). Null - the default - means no gesture is
    /// configured: the press edge fires the target immediately, adding no latency.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ButtonGesture? Gesture { get; init; }
}

/// <summary>
/// An analog stick. <see cref="Direction"/> turns the stick into a digital source for
/// key / combo / mouse-button targets; it stays null for mouse-move / wheel targets, which
/// consume both axes and need no direction.
/// </summary>
public sealed record StickBinding(GamepadStick Stick) : InputBinding
{
    /// <summary>
    /// Direction this stick acts as a switch for. Null (the default) keeps the stick
    /// omnidirectional, which is what mouse-move / wheel targets consume.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public InputDirection? Direction { get; init; }
}

/// <summary>
/// Polymorphic JSON for <see cref="InputBinding"/>, the source-side twin of
/// <see cref="MappingTargetConverter"/>. Every declared source still writes its discriminator
/// and reads back through the same path it always did; the one addition is that a
/// discriminator nothing claims no longer fails the whole profile - it becomes an
/// <see cref="UnknownBinding"/> holding the body verbatim.
/// </summary>
public sealed class InputBindingConverter : JsonConverter<InputBinding>
{
    private const string DiscriminatorProperty = "$type";

    /// <summary>Discriminator to declared type, built from the converter's own table.</summary>
    private static readonly Dictionary<string, Type> KnownSources = new(StringComparer.Ordinal)
    {
        ["button"] = typeof(ButtonBinding),
        ["stick"] = typeof(StickBinding),
    };

    public override InputBinding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var body = document.RootElement;

        if (body.ValueKind != JsonValueKind.Object)
        {
            // Not a source at all - a null, a number, a string. That is damage, not a type
            // this build does not know, so it is refused rather than preserved.
            throw new JsonException($"A mapping source must be a JSON object, got {body.ValueKind}.");
        }

        if (!body.TryGetProperty(DiscriminatorProperty, out var discriminator)
            || discriminator.ValueKind != JsonValueKind.String
            || !KnownSources.TryGetValue(discriminator.GetString()!, out var known))
        {
            // Unknown, or missing altogether: the body is preserved rather than rejected, so
            // a mapping this build cannot read still shows up and can still be deleted.
            return new UnknownBinding(
                discriminator.ValueKind == JsonValueKind.String ? discriminator.GetString()! : string.Empty,
                body.GetRawText());
        }

        // A declared source deserializes exactly as it did before the converter existed. A
        // payload that does not fit its own record - a missing required field, a value that is
        // not one of its enum names - is still a hard failure: the profile is damaged, not
        // ahead of this build.
        return (InputBinding?)JsonSerializer.Deserialize(body, known, options)
            ?? throw new JsonException($"Source \"{discriminator.GetString()}\" deserialized to null.");
    }

    public override void Write(Utf8JsonWriter writer, InputBinding value, JsonSerializerOptions options)
    {
        if (value is UnknownBinding unknown)
        {
            // Written back byte for byte, so a save never rewrites - and never loses - the
            // body a build this one cannot read put there.
            writer.WriteRawValue(unknown.RawJson, skipInputValidation: true);
            return;
        }

        // The runtime type is passed as the static type, which is also what keeps this
        // converter out of the call: it only ever answers for the base type, so there is no
        // recursion.
        var body = JsonSerializer.SerializeToElement(value, value.GetType(), options);

        writer.WriteStartObject();
        writer.WriteString(DiscriminatorProperty, DiscriminatorFor(value.GetType()));
        foreach (var property in body.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static string DiscriminatorFor(Type source)
        => KnownSources.FirstOrDefault(pair => pair.Value == source).Key
            ?? throw new InvalidOperationException(
                $"{source.Name} is not a declared {nameof(InputBinding)}: add it to the converter's table.");
}
