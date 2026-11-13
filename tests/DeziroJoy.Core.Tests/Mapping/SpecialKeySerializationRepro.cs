using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

public class SpecialKeySerializationRepro
{
    private static GamepadProfile ProfileWith(MappingTarget target) => new()
    {
        Name = "repro",
        Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.RightBumper), Target = target }],
    };

    [Theory]
    [InlineData(SpecialKeyKind.Precision)]
    [InlineData(SpecialKeyKind.Leader)]
    [InlineData(SpecialKeyKind.Layer)]
    public void Round_Trip_Special_Key(SpecialKeyKind kind)
    {
        var target = new SpecialKeyTarget(kind, LayerId: kind == SpecialKeyKind.Layer ? 2 : 0);
        var json = ProfileSerializer.Serialize(ProfileWith(target));
        var back = ProfileSerializer.Deserialize(json);
        Assert.Equal(target, back.Mappings[0].Target);
    }

    [Fact]
    public void Deserialize_Hand_Written_SpecialKey_Discriminator()
    {
        const string json = """
        {
          "Name": "repro",
          "Mappings": [
            { "Source": { "$type": "button", "Button": "RightBumper" },
              "Target": { "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Hold", "SlotA": 1, "SlotB": 2 } }
          ]
        }
        """;
        var back = ProfileSerializer.Deserialize(json);
        Assert.IsType<SpecialKeyTarget>(back.Mappings[0].Target);
    }
}