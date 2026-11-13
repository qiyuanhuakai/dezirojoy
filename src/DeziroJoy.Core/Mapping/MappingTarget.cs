using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>What a mapping does when triggered.</summary>
[JsonDerivedType(typeof(KeyPressTarget), "key")]
[JsonDerivedType(typeof(KeyComboTarget), "combo")]
[JsonDerivedType(typeof(MouseButtonTarget), "mouseButton")]
[JsonDerivedType(typeof(MouseMoveTarget), "mouseMove")]
[JsonDerivedType(typeof(WheelScrollTarget), "wheel")]
[JsonDerivedType(typeof(SpecialKeyTarget), "specialKey")]
[JsonDerivedType(typeof(TextTarget), "text")]
[JsonDerivedType(typeof(ComplexShortcutTarget), "complexShortcut")]
[JsonDerivedType(typeof(NoneTarget), "none")]
public abstract record MappingTarget;

/// <summary>Hold-to-key mapping: key goes down on press, up on release.</summary>
public sealed record KeyPressTarget(GamepadKey Key) : MappingTarget;

/// <summary>Tap combo on press edge (e.g. Ctrl+C): modifiers down, key tap, modifiers up.</summary>
public sealed record KeyComboTarget(KeyModifiers Modifiers, GamepadKey Key) : MappingTarget;

/// <summary>Mouse button mapping: down on press, up on release.</summary>
public sealed record MouseButtonTarget(MouseButton Button) : MappingTarget;

/// <summary>Stick to mouse movement with modern sensitivity controls.</summary>
public sealed record MouseMoveTarget(MouseStickSettings Settings) : MappingTarget;

/// <summary>Stick to mouse wheel scrolling.</summary>
public sealed record WheelScrollTarget(ScrollStickSettings Settings) : MappingTarget;

/// <summary>A gesture action that deliberately does nothing: the gesture resolves, nothing is emitted.</summary>
public sealed record NoneTarget : MappingTarget;

/// <summary>
/// Types a short Unicode string as one tap, the shape a search box, a file rename or a
/// short URL needs: the whole string is emitted on the press edge, so a hold repeats
/// nothing and a release emits nothing. It is deliberately a tap and not a hold - there
/// is no meaningful way to keep text "down" - and it is deliberately bounded: see
/// <see cref="MaxLength"/>.
/// </summary>
/// <remarks>
/// Each platform layer sends the text through its own path rather than as a sequence of
/// key events: Windows uses KEYEVENTF_UNICODE, which types characters the local layout
/// cannot name, and Linux routes through the same virtual keyboard the keys use. Full
/// IME composition - focus hand-off, candidate windows, commit - is a follow-up; this
/// covers the plain-text case the scenarios above actually need.
/// </remarks>
public sealed record TextTarget(string Text) : MappingTarget
{
    /// <summary>
    /// The longest string one text target may carry. A text target is a short-string
    /// affordance, not a clipboard macro: the bound keeps a hand-edited profile from
    /// turning one press into an unbounded injection, and it is far above any search
    /// query or short URL the feature exists for.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// How the mapping row names it, rather than the record's own dump: every description
    /// surface falls back to the target's <c>ToString</c> for a type it has no case for,
    /// and the raw record would read as debug output there.
    /// </summary>
    public override string ToString()
        => string.IsNullOrEmpty(Text) ? "输入文本" : $"输入文本「{Text}」";
}

/// <summary>
/// A target this build does not know, kept so the mapping that carries it still loads. A
/// profile written by a newer build - or read by an older one - names a <c>$type</c>
/// discriminator no type here claims, and throwing over it cost the user the whole profile:
/// the table came up empty, so the mapping behind the unrecognized target could neither be
/// shown nor deleted. This record keeps the discriminator and the target's own JSON exactly
/// as they were written, so the mapping displays, deletes and re-saves without losing what
/// the other build put there; the engine has nothing to emit for it and skips it.
/// </summary>
public sealed record UnknownTarget(string Discriminator, string RawJson) : MappingTarget
{
    /// <summary>
    /// How the mapping row names it. Every description surface falls back to the target's own
    /// <c>ToString</c> for a type it has no case for, so an unknown target reads as an
    /// unsupported mapping rather than as the debug form the record would otherwise print.
    /// </summary>
    public override string ToString()
        => string.IsNullOrEmpty(Discriminator) ? "不支持的映射" : $"不支持的映射（{Discriminator}）";
}

/// <summary>
/// Polymorphic JSON for <see cref="MappingTarget"/>. The type list is the base record's own
/// <see cref="JsonDerivedTypeAttribute"/> set, so every declared target still writes its
/// discriminator and reads back through the same path it always did; the one addition is
/// that a discriminator nothing claims no longer fails the whole profile - it becomes an
/// <see cref="UnknownTarget"/> holding the body verbatim.
/// </summary>
public sealed class MappingTargetConverter : JsonConverter<MappingTarget>
{
    private const string DiscriminatorProperty = "$type";

    /// <summary>Discriminator to declared type, built from the attributes rather than a second hand-written table.</summary>
    private static readonly Dictionary<string, Type> KnownTargets = BuildKnownTargets();

    /// <summary>Declared type to discriminator, the same table read the other way round.</summary>
    private static readonly Dictionary<Type, string> Discriminators = BuildDiscriminators();

    public override MappingTarget? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var body = document.RootElement;

        if (body.ValueKind != JsonValueKind.Object)
        {
            // Not a target at all - a null, a number, a string. That is damage, not a type
            // this build does not know, so it is refused rather than preserved.
            throw new JsonException($"A mapping target must be a JSON object, got {body.ValueKind}.");
        }

        if (!body.TryGetProperty(DiscriminatorProperty, out var discriminator)
            || discriminator.ValueKind != JsonValueKind.String
            || !KnownTargets.TryGetValue(discriminator.GetString()!, out var known))
        {
            // Unknown, or missing altogether: the body is preserved rather than rejected, so
            // a mapping this build cannot read still shows up and can still be deleted.
            return new UnknownTarget(
                discriminator.ValueKind == JsonValueKind.String ? discriminator.GetString()! : string.Empty,
                body.GetRawText());
        }

        // A declared target deserializes exactly as it did before the converter existed. A
        // payload that does not fit its own record - a missing required field, a value that
        // is not one of its enum names - is still a hard failure: the profile is damaged, not
        // ahead of this build.
        return (MappingTarget?)JsonSerializer.Deserialize(body, known, options)
            ?? throw new JsonException($"Target \"{discriminator.GetString()}\" deserialized to null.");
    }

    public override void Write(Utf8JsonWriter writer, MappingTarget value, JsonSerializerOptions options)
    {
        if (value is UnknownTarget unknown)
        {
            // Written back byte for byte, so a save never rewrites - and never loses - the
            // body a build this one cannot read put there.
            writer.WriteRawValue(unknown.RawJson, skipInputValidation: true);
            return;
        }

        // The attributes are the source of the discriminator, so they are read from the type
        // itself and put in front of the properties the concrete type serializes. Passing the
        // runtime type as the static type is also what keeps this converter out of the call:
        // it only ever answers for the base type, so there is no recursion.
        var body = JsonSerializer.SerializeToElement(value, value.GetType(), options);

        writer.WriteStartObject();
        writer.WriteString(DiscriminatorProperty, DiscriminatorFor(value.GetType()));
        foreach (var property in body.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static string DiscriminatorFor(Type target)
        => Discriminators.TryGetValue(target, out var discriminator)
            ? discriminator
            : throw new InvalidOperationException(
                $"{target.Name} is missing its JsonDerivedType attribute on {nameof(MappingTarget)}.");

    private static Dictionary<string, Type> BuildKnownTargets()
    {
        var known = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var attribute in typeof(MappingTarget).GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            if (attribute.DerivedType is { } derived
                && attribute.TypeDiscriminator is string discriminator
                && !known.ContainsKey(discriminator))
            {
                known.Add(discriminator, derived);
            }
        }

        return known;
    }

    private static Dictionary<Type, string> BuildDiscriminators()
    {
        var discriminators = new Dictionary<Type, string>();
        foreach (var attribute in typeof(MappingTarget).GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            if (attribute.DerivedType is { } derived && attribute.TypeDiscriminator is string discriminator)
            {
                discriminators[derived] = discriminator;
            }
        }

        return discriminators;
    }
}
