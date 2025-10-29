namespace Content.Server._WL.MetaData.Components;

[RegisterComponent]
public sealed partial class RenameOnInteractComponent : Component
{
    [DataField]
    public bool NeedCharge { get; set; } = true;

    [DataField]
    public bool UseVerbs { get; set; } = true;
}
