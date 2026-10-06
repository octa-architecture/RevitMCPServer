namespace RevitMCPAddin.Commands.Octa;

/// <summary>Registers every OCTA command. One call from CommandRegistry keeps upstream edits to a single line.</summary>
public static class OctaCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new ListFillPatternsCommand());
        r.Register(new ListFilledRegionTypesCommand());
        r.Register(new CreateFilledRegionTypeCommand());
        r.Register(new ListDetailComponentsCommand());
        r.Register(new PlaceDetailComponentCommand());
        r.Register(new ListTextNoteTypesCommand());
        r.Register(new CreateAnnotatedNoteCommand());
        r.Register(new SetTextLeadersCommand());
        r.Register(new SetCategoryOverridesCommand());
        r.Register(new SetElementGraphicsCommand());
        r.Register(new FlagElementsCommand());
        r.Register(new CreateViewTemplateFromViewCommand());
        r.Register(new GetViewGraphicsCommand());
        r.Register(new AddInternalNoteCommand());
        r.Register(new ListInternalNotesCommand());
        r.Register(new SetInternalNotesVisibilityCommand());
    }
}
