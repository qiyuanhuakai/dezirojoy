using System.Text;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>What kind of standard gamepad output a capture step fills in.</summary>
public enum Sdl3MappingStepRole
{
    /// <summary>
    /// A button output (face keys, d-pad, start/back/guide, shoulders, stick clicks). A hat or
    /// an axis can stand in for one, which is how axis d-pads and digital triggers get mapped.
    /// </summary>
    Button,

    /// <summary>
    /// A stick axis output. The raw axis is mapped whole, and the direction the user is asked
    /// to move decides whether the mapping needs SDL's inversion suffix.
    /// </summary>
    Stick,

    /// <summary>
    /// A trigger output. SDL reads a trigger from a half axis, so the mapping uses the
    /// <c>+aN</c>/<c>-aN</c> prefix - a whole-axis token would be "pressed" at rest.
    /// </summary>
    Trigger,
}

/// <summary>
/// One thing the capture flow asks the user to activate, in order. The step set is the roadmap's
/// minimum coverage - face keys, d-pad, start/back/guide, shoulders, stick clicks, both sticks
/// and both triggers - plus the optional extras a richer pad may have. A step the device cannot
/// provide is skipped, and skipping is recorded and reported rather than silently dropped.
/// </summary>
/// <param name="Key">The SDL mapping key this step fills, e.g. "leftx".</param>
/// <param name="Label">What the user is asked to do, in Chinese.</param>
/// <param name="Role">Button, stick or trigger output - this decides the token's sign form.</param>
/// <param name="Required">
/// True for the minimum coverage. A skipped required step is reported at the end of the flow;
/// an optional one is only listed.
/// </param>
/// <param name="AcceptsButton">Whether a raw button may be bound to this step.</param>
/// <param name="AcceptsAxis">Whether a raw axis may be bound to this step.</param>
/// <param name="AcceptsHat">Whether a raw hat direction may be bound to this step.</param>
public sealed record Sdl3MappingStep(
    string Key,
    string Label,
    Sdl3MappingStepRole Role,
    bool Required,
    bool AcceptsButton,
    bool AcceptsAxis,
    bool AcceptsHat)
{
    /// <summary>
    /// For stick steps: the sign a correctly-oriented stick produces in the direction the user
    /// is asked to move. Left and up are negative on a normally-oriented pad, so a raw value of
    /// that sign needs no inversion in the mapping; the opposite sign gets SDL's <c>~</c>.
    /// Zero for non-stick steps.
    /// </summary>
    public int ExpectedAxisSign { get; init; }

    /// <summary>The steps the flow walks through, in the order the user is asked.</summary>
    public static IReadOnlyList<Sdl3MappingStep> CreateDefault() =>
    [
        // The face keys are SDL's own mapping keys - a/b/x/y, not the enum's
        // South/East/West/North. SDL's parser matches its table exactly and drops an element it
        // does not recognize without an error, so "south:b0" would produce a mapping that loads,
        // applies and has no face buttons.
        new("a", "面键 下（A / Cross）", Sdl3MappingStepRole.Button, true, true, true, true),
        new("b", "面键 右（B / Circle）", Sdl3MappingStepRole.Button, true, true, true, true),
        new("x", "面键 左（X / Square）", Sdl3MappingStepRole.Button, true, true, true, true),
        new("y", "面键 上（Y / Triangle）", Sdl3MappingStepRole.Button, true, true, true, true),
        new("dpup", "十字键 上", Sdl3MappingStepRole.Button, true, true, true, true),
        new("dpdown", "十字键 下", Sdl3MappingStepRole.Button, true, true, true, true),
        new("dpleft", "十字键 左", Sdl3MappingStepRole.Button, true, true, true, true),
        new("dpright", "十字键 右", Sdl3MappingStepRole.Button, true, true, true, true),
        new("start", "Start / Options", Sdl3MappingStepRole.Button, true, true, false, true),
        new("back", "Back / Select", Sdl3MappingStepRole.Button, true, true, false, true),
        new("guide", "Guide / Home / PS 键", Sdl3MappingStepRole.Button, true, true, false, false),
        new("leftshoulder", "左肩键（LB / L1）", Sdl3MappingStepRole.Button, true, true, false, true),
        new("rightshoulder", "右肩键（RB / R1）", Sdl3MappingStepRole.Button, true, true, false, true),
        new("leftstick", "左摇杆按下（L3）", Sdl3MappingStepRole.Button, true, true, false, true),
        new("rightstick", "右摇杆按下（R3）", Sdl3MappingStepRole.Button, true, true, false, true),
        new("leftx", "左摇杆 左右（推到最左）", Sdl3MappingStepRole.Stick, true, true, true, true)
        {
            ExpectedAxisSign = -1,
        },
        new("lefty", "左摇杆 上下（推到最上）", Sdl3MappingStepRole.Stick, true, true, true, true)
        {
            ExpectedAxisSign = -1,
        },
        new("rightx", "右摇杆 左右（推到最左）", Sdl3MappingStepRole.Stick, true, true, true, true)
        {
            ExpectedAxisSign = -1,
        },
        new("righty", "右摇杆 上下（推到最上）", Sdl3MappingStepRole.Stick, true, true, true, true)
        {
            ExpectedAxisSign = -1,
        },
        new("lefttrigger", "左扳机（LT / L2）", Sdl3MappingStepRole.Trigger, true, true, true, true),
        new("righttrigger", "右扳机（RT / R2）", Sdl3MappingStepRole.Trigger, true, true, true, true),
        new("misc1", "截屏 / 分享键（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
        new("paddle1", "背键 1（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
        new("paddle2", "背键 2（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
        new("paddle3", "背键 3（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
        new("paddle4", "背键 4（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
        new("touchpad", "触摸板按下（如有）", Sdl3MappingStepRole.Button, false, true, false, false),
    ];
}

/// <summary>Shared numeric conventions for the capture flow.</summary>
public static class Sdl3MappingDefaults
{
    /// <summary>
    /// How far an axis must travel from its baseline before it counts as "the user moved this",
    /// in SDL's signed 16-bit range. About a quarter of full travel: high enough that stick
    /// drift and a resting trigger do not register, low enough that a deliberate push reaches it.
    /// </summary>
    public const int AxisDeadzone = 8000;

    /// <summary>SDL hat bitmask values, as the mapping's <c>hN.mask</c> tokens use them.</summary>
    public const byte HatUp = 1;

    public const byte HatRight = 2;

    public const byte HatDown = 4;

    public const byte HatLeft = 8;
}

/// <summary>
/// Turns two raw samples into "which control did the user just activate". Pure data in, pure
/// data out - no SDL, no clock - so the detection rules (what counts as a new press, a new hat
/// direction, a new axis travel) are testable without hardware.
/// </summary>
public static class Sdl3MappingActivation
{
    /// <summary>
    /// Every control that newly activated between <paramref name="baseline"/> and
    /// <paramref name="now"/>, in the order a step should prefer them: buttons first (the most
    /// common intent), then hats, then axes. A control that was already active at the baseline
    /// is not reported again, which is what keeps a button held across two steps from being
    /// captured twice.
    /// </summary>
    public static IReadOnlyList<Sdl3MappingControl> Detect(
        Sdl3JoystickSnapshot baseline,
        Sdl3JoystickSnapshot now,
        int axisDeadzone = Sdl3MappingDefaults.AxisDeadzone)
    {
        var found = new List<Sdl3MappingControl>();

        for (var i = 0; i < now.Buttons.Count; i++)
        {
            if (now.Button(i) && !baseline.Button(i))
            {
                found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Button, i, $"b{i}"));
            }
        }

        for (var h = 0; h < now.Hats.Count; h++)
        {
            var mask = now.Hat(h);
            if (mask != 0 && baseline.Hat(h) == 0 && IsHatMask(mask))
            {
                found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Hat, h, $"h{h}.{mask}"));
            }
        }

        for (var a = 0; a < now.Axes.Count; a++)
        {
            var value = (int)now.Axis(a);
            if (Math.Abs(value) < axisDeadzone || Math.Abs((int)baseline.Axis(a)) >= axisDeadzone)
            {
                continue;
            }

            var sign = value > 0 ? 1 : -1;
            found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Axis, a, $"a{a}") { AxisSign = sign });
        }

        return found;
    }

    /// <summary>
    /// The token for binding <paramref name="control"/> to <paramref name="step"/>. This is the
    /// one place SDL's sign conventions are applied, because they differ per role:
    /// <list type="bullet">
    /// <item>a button is always <c>bN</c>; a hat is always <c>hN.mask</c>;</item>
    /// <item>a stick axis is <c>aN</c>, or <c>aN~</c> when the axis runs opposite to the
    /// direction the user was asked to move;</item>
    /// <item>a trigger (and any button output bound to an axis) is <c>+aN</c> or <c>-aN</c>,
    /// because SDL reads a trigger from a half axis and a whole-axis token would report it as
    /// pressed at rest.</item>
    /// </list>
    /// </summary>
    public static string TokenFor(Sdl3MappingStep step, Sdl3MappingControl control)
    {
        switch (control.Kind)
        {
            case Sdl3MappingControlKind.Button:
                return control.Token;

            case Sdl3MappingControlKind.Hat:
                return control.Token;

            default:
                if (step.Role == Sdl3MappingStepRole.Stick && step.ExpectedAxisSign != 0)
                {
                    return control.AxisSign == step.ExpectedAxisSign ? control.Token : $"{control.Token}~";
                }

                return control.AxisSign > 0 ? $"+{control.Token}" : $"-{control.Token}";
        }
    }

    /// <summary>Every control active in this sample, in the same preference order as Detect.</summary>
    public static IReadOnlyList<Sdl3MappingControl> Active(
        Sdl3JoystickSnapshot now,
        int axisDeadzone = Sdl3MappingDefaults.AxisDeadzone)
    {
        var found = new List<Sdl3MappingControl>();
        for (var i = 0; i < now.Buttons.Count; i++)
        {
            if (now.Button(i))
            {
                found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Button, i, $"b{i}"));
            }
        }

        for (var h = 0; h < now.Hats.Count; h++)
        {
            var mask = now.Hat(h);
            if (IsHatMask(mask))
            {
                found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Hat, h, $"h{h}.{mask}"));
            }
        }

        for (var a = 0; a < now.Axes.Count; a++)
        {
            var value = (int)now.Axis(a);
            if (Math.Abs(value) >= axisDeadzone)
            {
                found.Add(new Sdl3MappingControl(Sdl3MappingControlKind.Axis, a, $"a{a}")
                {
                    AxisSign = value > 0 ? 1 : -1,
                });
            }
        }

        return found;
    }

    /// <summary>SDL hat masks are a bitmask of the four directions; anything else is not a hat.</summary>
    public static bool IsHatMask(byte mask) => mask is > 0 and <= 0x0F;
}

/// <summary>
/// The state machine of one mapping capture: which step is current, what has been assigned,
/// what was skipped, and the mapping string the result produces. It owns no SDL call and no
/// console - the caller feeds it snapshots and renders it - so the whole flow, including its
/// cancel and skip paths, is exercised deterministically.
/// </summary>
public sealed class Sdl3MappingCaptureSession
{
    private readonly List<Sdl3MappingStep> _steps;
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly List<Sdl3MappingStep> _skipped = [];
    private Sdl3MappingControl? _lastCapturedControl;
    private Sdl3JoystickSnapshot? _lastCapturedSnapshot;
    private bool _lastCapturedControlReturnedToNeutral;

    public Sdl3MappingCaptureSession(IReadOnlyList<Sdl3MappingStep>? steps = null)
    {
        _steps = [.. (steps ?? Sdl3MappingStep.CreateDefault())];
        Baseline = Sdl3JoystickSnapshot.Empty;
    }

    /// <summary>Every step, in the order the user is asked.</summary>
    public IReadOnlyList<Sdl3MappingStep> Steps => _steps;

    /// <summary>Index of the current step; equal to <see cref="Steps"/>.Count once complete.</summary>
    public int Index { get; private set; }

    /// <summary>The step being captured, or null once the flow is complete or cancelled.</summary>
    public Sdl3MappingStep? Current => Index < _steps.Count ? _steps[Index] : null;

    /// <summary>True once every step has been assigned or skipped.</summary>
    public bool IsComplete => Index >= _steps.Count;

    /// <summary>True once the user aborted; nothing further may be captured or saved.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// The latest sample that did not satisfy the current step. A control active in two
    /// consecutive samples is not newly activated; a release replaces the held baseline, so
    /// the next press can be captured even when the control was already held at step start.
    /// </summary>
    public Sdl3JoystickSnapshot Baseline { get; private set; }

    /// <summary>Steps the device could not provide, in the order they were skipped.</summary>
    public IReadOnlyList<Sdl3MappingStep> Skipped => _skipped;

    /// <summary>Steps that ended with a real control, in the order they were captured.</summary>
    public IReadOnlyList<Sdl3MappingStep> Captured =>
        _steps.Where(step => _tokens.ContainsKey(step.Key)).ToList();

    /// <summary>The token recorded for one step, or null when it was skipped.</summary>
    public string? TokenFor(Sdl3MappingStep step) => _tokens.TryGetValue(step.Key, out var token) ? token : null;

    /// <summary>
    /// Required steps that do not have a control yet. Mid-flow that is simply "not reached";
    /// once the flow is complete it is exactly the set the device could not provide, which is
    /// the honest gap the flow reports instead of pretending there is a full standard layout.
    /// </summary>
    public IReadOnlyList<Sdl3MappingStep> UncoveredRequired =>
        _steps.Where(step => step.Required && !_tokens.ContainsKey(step.Key)).ToList();

    /// <summary>
    /// Records the baseline for the current step. Called by the caller each time it starts
    /// polling a step, so the control that completed the previous step is not captured again
    /// before it returns to neutral. A neutral baseline is itself the proof of that return.
    /// </summary>
    public void BeginStep(Sdl3JoystickSnapshot baseline)
    {
        Baseline = baseline;
        if (_lastCapturedControl is { } previous
            && Sdl3MappingActivation.Active(baseline).All(control => control.Token != previous.Token))
        {
            _lastCapturedControlReturnedToNeutral = true;
        }
    }

    /// <summary>
    /// Looks for a newly activated control that fits the current step and, when one appears,
    /// records its token and advances. Returns true when the step was completed - the caller
    /// then moves on - and false while it should keep polling.
    /// </summary>
    public bool TryCapture(Sdl3JoystickSnapshot now, int axisDeadzone = Sdl3MappingDefaults.AxisDeadzone)
    {
        var step = Current;
        if (step is null || IsCancelled)
        {
            return false;
        }

        var candidates = Sdl3MappingActivation.Detect(Baseline, now, axisDeadzone).ToList();
        if (_lastCapturedControl is { } previous && _lastCapturedSnapshot is { } previousSnapshot)
        {
            var active = Sdl3MappingActivation.Active(now, axisDeadzone);
            if (active.All(control => control.Token != previous.Token))
            {
                _lastCapturedControlReturnedToNeutral = true;
            }

            if (!_lastCapturedControlReturnedToNeutral)
            {
                candidates.RemoveAll(control => control.Token == previous.Token);
            }

            // A control that became active after the previous capture is a valid answer even
            // when it was already active at this step's prompt. A control that was active in
            // the previous captured frame is stale input, not a new answer: on this DInput
            // device one physical stick reports the same Y travel on two axes, and accepting
            // the unselected duplicate would fill the next stick with the previous one.
            var previouslyActive = Sdl3MappingActivation.Active(previousSnapshot, axisDeadzone);
            foreach (var control in active)
            {
                if (previouslyActive.All(old => old.Token != control.Token)
                    && (_lastCapturedControlReturnedToNeutral || control.Token != previous.Token)
                    && candidates.All(candidate => candidate.Token != control.Token))
                {
                    candidates.Add(control);
                }
            }
        }

        var selected = Choose(step, candidates);
        if (selected is null)
        {
            // Keep the comparison moving while this step stays open. A fixed step-start
            // baseline would permanently reject a control that was held then: even a release
            // followed by a fresh press would still compare against the original held frame.
            Baseline = now;
            return false;
        }

        _tokens[step.Key] = Sdl3MappingActivation.TokenFor(step, selected);
        _lastCapturedControl = selected;
        _lastCapturedSnapshot = now;
        _lastCapturedControlReturnedToNeutral = false;
        Index++;
        return true;
    }

    /// <summary>
    /// Records a token for a step explicitly. Used when the flow already knows the answer - a
    /// hand-corrected token, or a test that does not want to script snapshots - and returns
    /// false when the step is unknown or already assigned.
    /// </summary>
    public bool TryAssign(Sdl3MappingStep step, string token)
    {
        if (IsCancelled || !_steps.Contains(step) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        _tokens[step.Key] = token;
        _skipped.Remove(step);
        return true;
    }

    /// <summary>
    /// Marks the current step as not provided by the device and advances. The step stays in
    /// <see cref="Skipped"/> so the end of the flow can name exactly what the mapping lacks.
    /// </summary>
    public void Skip()
    {
        var step = Current;
        if (step is null || IsCancelled)
        {
            return;
        }

        _skipped.Add(step);
        _tokens.Remove(step.Key);
        _lastCapturedControl = null;
        _lastCapturedSnapshot = null;
        _lastCapturedControlReturnedToNeutral = false;
        Index++;
    }

    /// <summary>
    /// Abandons the capture. Nothing is written: a cancelled flow must leave the user with the
    /// mappings they already had, not a half-built one.
    /// </summary>
    public void Cancel() => IsCancelled = true;

    /// <summary>
    /// The SDL mapping string for what was captured, in the order the SDL controller database
    /// uses. Steps that were skipped simply produce no element, so the mapping is a valid -
    /// if partial - description of the device rather than a lie about it.
    /// </summary>
    public string BuildMapping(string deviceGuid, string deviceName, string platform)
    {
        var order = new[]
        {
            "a", "b", "x", "y", "dpup", "dpdown", "dpleft", "dpright",
            "start", "back", "guide", "leftshoulder", "rightshoulder", "leftstick", "rightstick",
            "leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger",
            "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad",
        };

        var builder = new StringBuilder();
        builder.Append(deviceGuid);
        builder.Append(',');
        builder.Append(deviceName);
        builder.Append(",platform:");
        builder.Append(platform);

        foreach (var key in order)
        {
            if (_tokens.TryGetValue(key, out var token))
            {
                builder.Append(',');
                builder.Append(key);
                builder.Append(':');
                builder.Append(token);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The progress line the interactive flow prints: how far along it is and what the mapping
    /// is still missing.
    /// </summary>
    public string DescribeProgress()
    {
        if (IsCancelled)
        {
            return "已取消。";
        }

        if (IsComplete)
        {
            var missing = UncoveredRequired;
            return missing.Count == 0
                ? "全部必需项目已捕获。"
                : $"已捕获 {Captured.Count} 项；以下必需项目未捕获：{string.Join("、", missing.Select(step => step.Label))}。";
        }

        var current = Current!;
        var total = _steps.Count(step => step.Required);
        var done = _steps.Count(step => step.Required && _tokens.ContainsKey(step.Key));
        return done >= total
            ? $"[可选] {current.Label}"
            : $"[{done + 1}/{total}] {current.Label}";
    }

    /// <summary>
    /// Picks the control a step should take from the candidates, preferring the kind that best
    /// expresses the step: a button step takes a button over a hat over an axis, while a stick
    /// or trigger takes an axis first so a thumb that brushes a face key while pushing does not
    /// bind the stick to that key.
    /// </summary>
    private static Sdl3MappingControl? Choose(Sdl3MappingStep step, IReadOnlyList<Sdl3MappingControl> candidates)
    {
        Sdl3MappingControlKind[] order = step.Role == Sdl3MappingStepRole.Button
            ? [Sdl3MappingControlKind.Button, Sdl3MappingControlKind.Hat, Sdl3MappingControlKind.Axis]
            : [Sdl3MappingControlKind.Axis, Sdl3MappingControlKind.Button, Sdl3MappingControlKind.Hat];

        foreach (var kind in order)
        {
            var match = candidates.FirstOrDefault(candidate => candidate.Kind == kind);
            if (match is not null && Accepts(step, match.Kind))
            {
                return match;
            }
        }

        return null;
    }

    private static bool Accepts(Sdl3MappingStep step, Sdl3MappingControlKind kind) => kind switch
    {
        Sdl3MappingControlKind.Button => step.AcceptsButton,
        Sdl3MappingControlKind.Axis => step.AcceptsAxis,
        _ => step.AcceptsHat,
    };
}
