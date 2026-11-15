using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>All mappings configured on one physical button or stick, across every layer.</summary>
public sealed class MappingCardViewModel
{
    public MappingCardViewModel(InputBinding source, IReadOnlyList<MappingRowViewModel> mappings)
    {
        Source = source;
        Mappings = mappings;
    }

    public InputBinding Source { get; }

    public IReadOnlyList<MappingRowViewModel> Mappings { get; }

    public string SourceText => Source switch
    {
        ButtonBinding button => MappingDescriptions.Button(button.Button, button.Code),
        StickBinding stick => MappingDescriptions.Stick(stick.Stick),
        _ => MappingDescriptions.Source(Source),
    };
}
