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
        r.Register(new ListOpenDocumentsCommand());
        r.Register(new OpenDocumentCommand());
        r.Register(new ExitRevitCommand());
        r.Register(new ListPhaseFiltersCommand());
        r.Register(new CreatePhaseFilterCommand());
        r.Register(new SetElementPhaseCommand());
        r.Register(new SetViewPhaseCommand());
        r.Register(new RenamePhaseCommand());
        r.Register(new ListRevisionsCommand());
        r.Register(new CreateRevisionCommand());
        r.Register(new UpdateRevisionCommand());
        r.Register(new SetSheetRevisionsCommand());
        r.Register(new CreateRevisionCloudCommand());
        r.Register(new CreateDetailFamilyCommand());
        r.Register(new SetTemplateControlsCommand());
        r.Register(new SetViewScaleCommand());
        r.Register(new TidyTextLeadersCommand());
        r.Register(new ConvertLineLeadersCommand());
        r.Register(new CreateModelFamilyCommand());
        r.Register(new FitCropToSectionBoxCommand());
        r.Register(new SetViewCropCommand());
        r.Register(new NewProjectFromTemplateCommand());
        r.Register(new SetSharedCoordinatesCommand());
        r.Register(new LinkCadCommand());
        r.Register(new GetCadGeometryCommand());
        r.Register(new LinkPointCloudCommand());
        r.Register(new SamplePointCloudGridCommand());
        r.Register(new CreateToposolidCommand());
        r.Register(new CreatePropertyLineCommand());
        r.Register(new SurveyCheckCommand());
        r.Register(new ConvertCoordinatesCommand());
    }
}
