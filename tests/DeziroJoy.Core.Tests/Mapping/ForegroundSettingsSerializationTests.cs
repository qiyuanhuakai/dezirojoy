using System.Text.Json;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// The foreground table on disk. The rule file is new in this build, so there is no older shape
/// to stay compatible with - what matters is the opposite direction: a table written now must
/// read back exactly, and a damaged one must cost the rules and nothing else.
/// </summary>
public sealed class ForegroundSettingsSerializationTests
{
    [Fact]
    public void A_Table_Round_Trips_Through_Json()
    {
        var settings = new ForegroundSettings
        {
            Enabled = true,
            DefaultSlot = 2,
            Rules =
            [
                new ForegroundRule
                {
                    ProcessName = "Chrome.exe",
                    Priority = 6,
                    Action = ForegroundAction.Profile,
                    Slot = 3,
                    FullscreenOnly = true,
                },
                new ForegroundRule
                {
                    ProcessName = "*",
                    Priority = 1,
                    Action = ForegroundAction.NativeGamepad,
                    Enabled = false,
                },
            ],
        };

        var restored = ForegroundSettingsSerializer.Deserialize(
            ForegroundSettingsSerializer.Serialize(settings));

        Assert.Equal(settings.DefaultSlot, restored.DefaultSlot);
        Assert.True(restored.Enabled);
        Assert.Equal(2, restored.Rules.Count);

        var first = restored.Rules[0];
        Assert.Equal("Chrome.exe", first.ProcessName);
        Assert.Equal(6, first.Priority);
        Assert.Equal(ForegroundAction.Profile, first.Action);
        Assert.Equal(3, first.Slot);
        Assert.True(first.FullscreenOnly);

        var second = restored.Rules[1];
        Assert.Equal(ForegroundAction.NativeGamepad, second.Action);
        Assert.False(second.Enabled);
    }

    [Fact]
    public void Enums_Are_Written_As_Names_So_The_File_Stays_Hand_Editable()
    {
        var json = ForegroundSettingsSerializer.Serialize(new ForegroundSettings
        {
            Rules = [new ForegroundRule { ProcessName = "game", Action = ForegroundAction.NativeGamepad }],
        });

        Assert.Contains("\"NativeGamepad\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Damaged_File_Loads_As_The_Defaults_And_Says_Why()
    {
        var (settings, problems) = ForegroundSettingsSerializer.Load("{ not json");

        Assert.Single(problems);
        Assert.Empty(settings.Rules);
        Assert.Equal(ForegroundRule.DefaultSlot, settings.DefaultSlot);
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void An_Empty_Object_Loads_As_The_Defaults()
    {
        var (settings, problems) = ForegroundSettingsSerializer.Load("{}");

        Assert.Empty(problems);
        Assert.Empty(settings.Rules);
        Assert.Equal(ForegroundRule.DefaultSlot, settings.DefaultSlot);
    }

    [Fact]
    public void A_Rule_Without_A_Process_Name_Is_Refused_Rather_Than_Guessed_At()
    {
        var json = """
            {
              "Rules": [ { "Priority": 3 } ]
            }
            """;

        Assert.Throws<JsonException>(() => ForegroundSettingsSerializer.Deserialize(json));
    }
}
