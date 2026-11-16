using DeziroJoy.Platform.Windows.Gaming;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The ms-gamebar vocabulary: the command the registration writes and the argument the
/// registered invocation parses. Both are the injection surface of the workaround - a
/// command line the shell builds from a URI must not be able to add arguments, and an
/// invocation that borrows the flag must not be able to drag another flag along.
/// </summary>
public sealed class GameBarProtocolTests
{
    private static readonly string[] RegisteredLink = { GameBarProtocol.NoopArgument, "ms-gamebar://Default" };
    private static readonly string[] SmuggledFlag = { GameBarProtocol.NoopArgument, "--daemon" };
    private static readonly string[] ForeignProtocol = { GameBarProtocol.NoopArgument, "https://example.com/" };
    private static readonly string[] LinkWithOwnText = { "--other", GameBarProtocol.NoopArgument, "ms-gamebar://a --daemon" };
    private static readonly string[] MixedCaseLink = { "--NoOp-GameBar", "MS-GAMEBAR://Default" };
    private static readonly string[] SplitLink = { GameBarProtocol.NoopArgument, "ms-gamebar://a", "--daemon" };
    private static readonly string[] MissingLink = { GameBarProtocol.NoopArgument };
    private static readonly string[] NotOurs = { "--daemon" };

    [Fact]
    public void BuildCommand_Quotes_The_Executable_And_The_Substituted_Link()
    {
        var command = GameBarProtocol.BuildCommand(@"C:\Program Files\DeziroJoy\DeziroJoy.exe");

        Assert.Equal(
            "\"C:\\Program Files\\DeziroJoy\\DeziroJoy.exe\" --noop-gamebar \"%1\"",
            command);
    }

    [Fact]
    public void BuildCommand_Refuses_An_Executable_Path_Containing_A_Quote()
    {
        Assert.Throws<ArgumentException>(
            () => GameBarProtocol.BuildCommand("C:\\evil\" --daemon \"DeziroJoy.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCommand_Refuses_An_Empty_Executable(string executable)
    {
        Assert.Throws<ArgumentException>(() => GameBarProtocol.BuildCommand(executable));
    }

    [Fact]
    public void The_Registered_Link_Is_Swallowed_Silently()
    {
        var handled = GameBarProtocol.TryRunNoop(RegisteredLink, out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void The_Flag_Is_Matched_Case_Insensitively()
    {
        var handled = GameBarProtocol.TryRunNoop(MixedCaseLink, out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Arguments_That_Are_Not_Ours_Are_Left_Alone()
    {
        var handled = GameBarProtocol.TryRunNoop(NotOurs, out var exitCode);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void A_Missing_Link_Is_Refused_Rather_Than_Treated_As_Handled()
    {
        var handled = GameBarProtocol.TryRunNoop(MissingLink, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void A_Flag_Smuggled_In_As_The_Link_Is_Refused()
    {
        // What an unquoted %1 substitution could have produced: the flag again, or another
        // flag of this app, standing where the URI belongs.
        var handled = GameBarProtocol.TryRunNoop(SmuggledFlag, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void A_Link_Of_Another_Protocol_Is_Refused()
    {
        var handled = GameBarProtocol.TryRunNoop(ForeignProtocol, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Extra_Arguments_After_The_Link_Are_Refused()
    {
        // The quoted "%1" keeps a URI one argument; anything arriving split means the
        // command line was built without the quotes, so it is not trusted.
        var handled = GameBarProtocol.TryRunNoop(SplitLink, out var exitCode);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void A_Link_Carrying_Its_Own_Text_Stays_One_Swallowed_Argument()
    {
        // With the quotes in place the shell cannot split a URI, so its payload is inert.
        var handled = GameBarProtocol.TryRunNoop(LinkWithOwnText, out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
    }
}
