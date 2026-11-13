using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The decoupled action model: a trigger says when a program starts, its steps say what it
/// does, and its execution strategy says how it ends. The three are one record the GUI and
/// the DSL share, so a program configured in the editor and one typed as text are the same
/// thing - and every default is the long-press behaviour profiles have always had.
/// </summary>
public class ComplexShortcutActionModelTests
{
    private const float Threshold = 0.5f;

    private const float Tick = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "action-model-profile",
        Mappings = mappings,
    };

    private static GamepadState Held(GamepadButton button)
        => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    /// <summary>A program on the button's own click, the position a press/release trigger reads.</summary>
    private static MappingEntry ClickProgram(
        GamepadButton button,
        ComplexShortcutTarget program,
        ButtonGesture? gesture = null)
        => new()
        {
            Source = new ButtonBinding(button) { Gesture = gesture },
            Target = program,
        };

    private static MappingEntry LongPressProgram(
        GamepadButton button,
        ComplexShortcutTarget program,
        ButtonGesture? gesture = null)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = gesture ?? new ButtonGesture { LongPressThreshold = Threshold },
            },
            Target = new NoneTarget(),
        };

    // ---- the DSL half ------------------------------------------------------------

    [Fact]
    public void A_Script_With_No_Header_Is_The_Long_Press_Program_Profiles_Always_Had()
    {
        var target = ComplexShortcutDsl.ParseTarget("hold Alt\nevery 1s tap Tab");

        Assert.Equal(ActionTrigger.LongPress, target.Trigger);
        Assert.Equal(ActionExecution.WhileHeld, target.Execution);
        Assert.Equal(ActionRepeat.Restart, target.Repeat);
    }

    [Fact]
    public void Header_Lines_Set_The_Trigger_Execution_And_Repeat()
    {
        var target = ComplexShortcutDsl.ParseTarget("trigger press\nrun once\nrepeat queue\ntap A");

        Assert.Equal(ActionTrigger.Press, target.Trigger);
        Assert.Equal(ActionExecution.Once, target.Execution);
        Assert.Equal(ActionRepeat.Queue, target.Repeat);
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.A), Assert.Single(ComplexShortcutDsl.Parse(target.Script)));
    }

    [Fact]
    public void Headers_Accept_Chinese_Words_Too()
    {
        var target = ComplexShortcutDsl.ParseTarget("触发 双击\n执行 一次\n重复 排队\ntap A");

        Assert.Equal(ActionTrigger.DoubleClick, target.Trigger);
        Assert.Equal(ActionExecution.Once, target.Execution);
        Assert.Equal(ActionRepeat.Queue, target.Repeat);
    }

    [Fact]
    public void A_Header_After_A_Step_Is_Refused()
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(
            () => ComplexShortcutDsl.Parse("tap A\ntrigger press"));

        Assert.Contains("只能写在脚本开头", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Unknown_Header_Value_Names_The_Form()
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(
            () => ComplexShortcutDsl.Parse("trigger sideways\ntap A"));

        Assert.Contains("未知触发方式", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Legacy_Script_Gains_No_Header_And_Re_Parses_The_Same()
    {
        // The headers are only written when they differ from the default, so a script
        // written before any of them existed gains none and compiles to the same steps.
        const string script = "hold Alt\nevery 1s tap Tab";
        var target = ComplexShortcutDsl.ParseTarget(script);

        Assert.Equal(
            ComplexShortcutDsl.Parse(script),
            ComplexShortcutDsl.Parse(ComplexShortcutDsl.Format(target)));
        Assert.DoesNotContain("trigger", ComplexShortcutDsl.Format(target), StringComparison.Ordinal);
        Assert.DoesNotContain("run ", ComplexShortcutDsl.Format(target), StringComparison.Ordinal);
        Assert.DoesNotContain("repeat", ComplexShortcutDsl.Format(target), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Configured_Program_Round_Trips_Through_Its_Own_Text()
    {
        var target = new ComplexShortcutTarget
        {
            Script = "tap A",
            Trigger = ActionTrigger.Press,
            Execution = ActionExecution.Toggle,
            Repeat = ActionRepeat.Ignore,
        };

        var reparsed = ComplexShortcutDsl.ParseTarget(ComplexShortcutDsl.Format(target));

        Assert.Equal(target.Trigger, reparsed.Trigger);
        Assert.Equal(target.Execution, reparsed.Execution);
        Assert.Equal(target.Repeat, reparsed.Repeat);
        Assert.Equal(
            ComplexShortcutDsl.Parse(target.Script),
            ComplexShortcutDsl.Parse(reparsed.Script));
    }

    [Fact]
    public void A_Text_Step_Takes_The_Rest_Of_The_Line()
    {
        var steps = ComplexShortcutDsl.Parse("text hello 世界");

        Assert.Equal(new ComplexShortcutTextStep("hello 世界"), Assert.Single(steps));
        Assert.Equal(["text hello 世界"], ComplexShortcutDsl.FormatSteps(steps));
    }

    [Fact]
    public void A_Text_Step_Without_Content_Is_Refused()
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse("text   "));

        Assert.Contains("text 之后缺少内容", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Click_Step_Taps_A_Mouse_Button_And_Formats_Back()
    {
        var steps = ComplexShortcutDsl.Parse("click right");

        Assert.Equal(new ComplexShortcutClickStep(MouseButton.Right), Assert.Single(steps));
        Assert.Equal(["click Right"], ComplexShortcutDsl.FormatSteps(steps));
    }

    [Fact]
    public void An_Unknown_Mouse_Button_Names_The_Choices()
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse("click sideways"));

        Assert.Contains("未知鼠标按键", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_Keys_Are_Named_In_The_Key_Vocabulary()
    {
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.VolumeUp), Assert.Single(ComplexShortcutDsl.Parse("tap volup")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.MediaNextTrack), Assert.Single(ComplexShortcutDsl.Parse("tap next")));
    }

    // ---- the engine half ---------------------------------------------------------

    [Fact]
    public void A_Press_Triggered_Program_Runs_While_The_Button_Is_Held()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "hold Alt", Trigger = ActionTrigger.Press })));

        // The press edge starts it: no gesture is configured, so there is no wait.
        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.A), Tick)));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), Tick));

        // The release ends it and lifts what it held.
        Assert.Equal(
            new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void A_Press_Triggered_Program_With_Once_Runs_To_Its_End_And_Stops_By_Itself()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "tap X", Trigger = ActionTrigger.Press, Execution = ActionExecution.Once })));

        var press = engine.Tick(Held(GamepadButton.A), Tick);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            press);

        // Finished: nothing more comes out of holding it, and the release is silent too.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), Tick));
        Assert.Empty(engine.Tick(Released, Tick));
    }

    [Fact]
    public void A_Press_Triggered_Once_Program_Keeps_Running_After_The_Release()
    {
        // "执行一次" is the point: a program that runs to its end does not care whether the
        // button is still down. Stepped at 0.1 秒 so a 0.5 秒 wait is five ticks.
        const float Step = 0.1f;

        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "wait 0.5s\ntap X",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.Once,
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), Step));
        Assert.Empty(engine.Tick(Released, Step));
        Assert.Empty(engine.Tick(Released, Step));
        Assert.Empty(engine.Tick(Released, Step));

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Released, Step));
        Assert.Empty(engine.Tick(Released, Step));
    }

    [Fact]
    public void A_Toggle_Program_Is_Stopped_By_The_Trigger_That_Started_It()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "hold Alt",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.Toggle,
            })));

        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.A), Tick)));

        // The release does nothing: a toggle is only ever ended by the next trigger.
        Assert.Empty(engine.Tick(Released, Tick));
        Assert.Empty(engine.Tick(Released, Tick));

        // The second press stops it and lifts what it held.
        Assert.Equal(
            new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.A), Tick)));

        Assert.Empty(engine.Tick(Released, Tick));
    }

    [Fact]
    public void A_Double_Click_Triggered_Program_Runs_On_The_Second_Press()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    DoubleClickAction = new ComplexShortcutTarget
                    {
                        Script = "tap X",
                        Trigger = ActionTrigger.DoubleClick,
                        Execution = ActionExecution.Once,
                    },
                },
            },
            Target = new NoneTarget(),
        }));

        // The first press only opens the double-click window.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), Tick));
        Assert.Empty(engine.Tick(Released, Tick));

        var second = engine.Tick(Held(GamepadButton.A), Tick);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            second);

        Assert.Empty(engine.Tick(Released, Tick));
    }

    [Fact]
    public void A_Release_Triggered_Program_Runs_When_The_Button_Is_Let_Go()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "tap X",
                Trigger = ActionTrigger.Release,
                Execution = ActionExecution.Once,
            })));

        // Holding it does nothing: the program waits for the release.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.A), Tick));

        var release = engine.Tick(Released, Tick);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            release);
    }

    [Fact]
    public void A_Restart_Repeat_Cuts_The_Running_Program_Short()
    {
        // A second long press restarts a run that is still waiting, so the tap it owes
        // comes 2 秒 after the second press rather than 1.5 秒 after the first.
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = Threshold,
                    LongPressAction = new ComplexShortcutTarget
                    {
                        Script = "wait 2s\ntap X",
                        Execution = ActionExecution.Once,
                    },
                },
            },
            Target = new NoneTarget(),
        }));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Released, 0.25f));

        // The second long press: the running program is stopped and started again, so its
        // wait restarts from the full 2 秒.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));

        for (var i = 0; i < 7; i++)
        {
            Assert.Empty(engine.Tick(Released, 0.25f));
        }

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Released, 0.25f));
    }

    [Fact]
    public void An_Ignore_Repeat_Leaves_The_Running_Program_Alone()
    {
        // The same shape with 忽略: the second long press does nothing, so the tap still
        // comes 1.5 秒 after the first one.
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = Threshold,
                    LongPressAction = new ComplexShortcutTarget
                    {
                        Script = "wait 2s\ntap X",
                        Execution = ActionExecution.Once,
                        Repeat = ActionRepeat.Ignore,
                    },
                },
            },
            Target = new NoneTarget(),
        }));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Released, 0.25f));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 0.25f));

        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(engine.Tick(Released, 0.25f));
        }

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Released, 0.25f));
    }

    [Fact]
    public void A_Queued_Run_Starts_When_The_Running_One_Ends()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "tap X",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.Once,
                Repeat = ActionRepeat.Queue,
            })));

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.A), Tick));

        // The program already finished, so the next press cycle simply runs it again.
        Assert.Empty(engine.Tick(Released, Tick));
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.A), Tick));
    }

    [Fact]
    public void A_Queued_Run_Waits_For_A_Run_That_Is_Still_Going()
    {
        // Stepped at 0.1 秒 so a 0.5 秒 wait is five ticks.
        const float Step = 0.1f;

        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "wait 0.5s\ntap X",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.Once,
                Repeat = ActionRepeat.Queue,
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.A), Step));
        Assert.Empty(engine.Tick(Released, Step));

        // A second press while the first run is still waiting queues one more run.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), Step));

        // The first run finishes, and the queued one starts straight after it - so its own
        // 0.5 秒 wait begins from here, not from the press.
        Assert.Empty(engine.Tick(Released, Step));
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Released, Step));

        for (var i = 0; i < 4; i++)
        {
            Assert.Empty(engine.Tick(Released, Step));
        }

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            engine.Tick(Released, Step));
        Assert.Empty(engine.Tick(Released, Step));
    }

    [Fact]
    public void A_Text_Step_Types_Through_The_Engine()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "text open folder", Trigger = ActionTrigger.Press, Execution = ActionExecution.Once })));

        Assert.Equal(new TextCommand("open folder"), Assert.Single(engine.Tick(Held(GamepadButton.A), Tick)));
        Assert.Empty(engine.Tick(Released, Tick));
    }

    [Fact]
    public void A_Click_Step_Taps_The_Mouse_Button()
    {
        var engine = new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "click right", Trigger = ActionTrigger.Press, Execution = ActionExecution.Once })));

        Assert.Equal(
            [
                new MouseDownCommand(MouseButton.Right),
                new MouseUpCommand(MouseButton.Right),
            ],
            engine.Tick(Held(GamepadButton.A), Tick));
    }

    [Fact]
    public void A_Program_Whose_Trigger_Names_Another_Position_Is_Refused()
    {
        // The program rides in the double-click action but says it runs on the long press.
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    DoubleClickAction = new ComplexShortcutTarget { Script = "tap X" },
                },
            },
            Target = new NoneTarget(),
        })));

        Assert.Contains("long-press program", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Double_Click_Triggered_Program_Needs_A_Double_Click_Window()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A)
            {
                Gesture = new ButtonGesture
                {
                    DoubleClickAction = new ComplexShortcutTarget
                    {
                        Script = "tap X",
                        Trigger = ActionTrigger.DoubleClick,
                        Execution = ActionExecution.Once,
                    },
                },
            },
            Target = new NoneTarget(),
        })));

        Assert.Contains("double-click window", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Queued_Repeat_Refuses_A_Held_Run()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "hold Alt",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.WhileHeld,
                Repeat = ActionRepeat.Queue,
            }))));

        Assert.Contains("queues a repeat", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Release_Trigger_Needs_A_Program_That_Runs_Once()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "tap X", Trigger = ActionTrigger.Release }))));

        Assert.Contains("松开", exception.Message, StringComparison.Ordinal);
    }

    // ---- the shared preview ------------------------------------------------------

    [Fact]
    public void The_Preview_Lists_The_Press_And_Release_Order()
    {
        var preview = ComplexShortcutRuntime.Preview(new ComplexShortcutTarget
        {
            Script = "hold Ctrl\nhold C\nrelease C\nrelease Ctrl\ntap X",
        });

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.LeftCtrl, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.LeftCtrl, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.X, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.X, KeyModifiers.None),
            ],
            preview);
    }

    [Fact]
    public void The_Preview_Releases_What_Is_Still_Held_At_The_End()
    {
        // The same reverse press order Stop unwinds a hold in.
        var preview = ComplexShortcutRuntime.Preview(new ComplexShortcutTarget { Script = "hold A\nhold B" });

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.A, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.B, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.B, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.A, KeyModifiers.None),
            ],
            preview);
    }

    [Fact]
    public void The_Preview_Shows_One_Firing_Per_Timer_And_Every_Other_Step()
    {
        var preview = ComplexShortcutRuntime.Preview(new ComplexShortcutTarget
        {
            Script = "wait 0.5s\nevery 1s tap Tab\nclick middle\ntext hi",
        });

        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Tab, KeyModifiers.None),
                new MouseDownCommand(MouseButton.Middle),
                new MouseUpCommand(MouseButton.Middle),
                new TextCommand("hi"),
            ],
            preview);
    }

    // ---- the profile half --------------------------------------------------------

    [Fact]
    public void The_Three_Fields_Round_Trip_And_Stay_Out_Of_An_Old_Profile()
    {
        var configured = Profile(ClickProgram(
            GamepadButton.A,
            new ComplexShortcutTarget
            {
                Script = "tap X",
                Trigger = ActionTrigger.Press,
                Execution = ActionExecution.Once,
                Repeat = ActionRepeat.Ignore,
            }));

        var json = ProfileSerializer.Serialize(configured);
        Assert.Contains("\"Trigger\": \"Press\"", json);
        Assert.Contains("\"Execution\": \"Once\"", json);
        Assert.Contains("\"Repeat\": \"Ignore\"", json);

        var reloaded = ProfileSerializer.Deserialize(json);
        Assert.Equal(Assert.Single(configured.Mappings).Target, Assert.Single(reloaded.Mappings).Target);
    }

    [Fact]
    public void A_Default_Program_Writes_No_Header_Fields_At_All()
    {
        var json = ProfileSerializer.Serialize(Profile(LongPressProgram(
            GamepadButton.A,
            new ComplexShortcutTarget { Script = "hold Alt" })));

        Assert.DoesNotContain("\"Trigger\"", json);
        Assert.DoesNotContain("\"Execution\"", json);
        Assert.DoesNotContain("\"Repeat\"", json);
    }
}
