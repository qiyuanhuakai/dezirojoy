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

    private static MappingTarget RoundTrip(MappingTarget target)
        => ProfileSerializer.Deserialize(ProfileSerializer.Serialize(ProfileWith(target))).Mappings[0].Target;

    [Theory]
    [InlineData(SpecialKeyKind.Precision)]
    [InlineData(SpecialKeyKind.Leader)]
    public void Round_Trip_Special_Key(SpecialKeyKind kind)
    {
        var target = new SpecialKeyTarget(kind);
        Assert.Equal(target, RoundTrip(target));
    }

    [Fact]
    public void A_Layer_Leader_Round_Trips_With_Its_Action()
    {
        var target = new SpecialKeyTarget(
            SpecialKeyKind.Leader,
            SwitchMode: LeaderSwitchMode.Hold,
            Action: LeaderAction.Layer);

        var back = Assert.IsType<SpecialKeyTarget>(RoundTrip(target));

        Assert.Equal(SpecialKeyKind.Leader, back.Kind);
        Assert.Equal(LeaderAction.Layer, back.Action);
        Assert.Equal(LeaderSwitchMode.Hold, back.SwitchMode);
    }

    [Fact]
    public void A_Preset_Leader_Writes_No_Action_Field()
    {
        // The action is omitted while it is the default, so a build that predates
        // LeaderAction still reads the profile it is handed.
        var json = ProfileSerializer.Serialize(ProfileWith(new SpecialKeyTarget(SpecialKeyKind.Leader)));

        Assert.DoesNotContain("Action", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LayerId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Deprecated_Layer_Key_Still_Deserializes()
    {
        // The kind the merged leader replaced is kept, so a profile written before the merge
        // still loads; the LayerId it carried is simply not part of the model any more.
        const string json = """
        {
          "Name": "repro",
          "Mappings": [
            { "Source": { "$type": "button", "Button": "RightBumper" },
              "Target": { "$type": "specialKey", "Kind": "Layer", "SwitchMode": "Hold", "LayerId": 1 } }
          ]
        }
        """;

        var target = Assert.IsType<SpecialKeyTarget>(ProfileSerializer.Deserialize(json).Mappings[0].Target);

        Assert.Equal(SpecialKeyKind.Layer, target.Kind);
        Assert.Equal(LeaderSwitchMode.Hold, target.SwitchMode);
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
