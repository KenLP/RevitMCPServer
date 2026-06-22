using System.Collections.Generic;

namespace RevitMCPAddin.Commands;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, IRevitCommand> _commands = new();

    public void Register(IRevitCommand handler) => _commands[handler.Name] = handler;

    public bool TryGet(string name, out IRevitCommand? handler) =>
        _commands.TryGetValue(name, out handler);

    public IEnumerable<string> Names => _commands.Keys;

    public IEnumerable<(string Name, bool IsReadOnly, string RiskLevel, string ExecutionKind)> Describe()
    {
        foreach (var kv in _commands)
            yield return (kv.Key, kv.Value.IsReadOnly, kv.Value.RiskLevel,
                          ((IRevitCommand)kv.Value).Execution.ToString());
    }

    public void RegisterDefaults()
    {
        // === Diagnostics ===
        Register(new PingCommand());
        Register(new GetVersionCommand());
        Register(new GetDocumentInfoCommand());

        // === Inspection / introspection ===
        Register(new ListElementsCommand());
        Register(new GetElementInfoCommand());
        Register(new GetElementRoomsCommand());
        Register(new FindElementsCommand());
        Register(new GetParameterCommand());
        Register(new ListLevelsCommand());
        Register(new ListWallTypesCommand());
        Register(new ListFloorTypesCommand());
        Register(new ListCategoriesCommand());
        Register(new ListFamiliesCommand());
        Register(new ListFamilyTypesCommand());
        Register(new ListSheetsCommand());
        Register(new ListRoomsCommand());
        Register(new ListSpacesCommand());
        Register(new ListMaterialsCommand());
        Register(new ListPhasesCommand());
        Register(new ListViewTemplatesCommand());
        Register(new GetViewsCommand());
        Register(new GetActiveViewCommand());
        Register(new GetSelectedElementsCommand());
        Register(new GetLinkedFilesCommand());
        Register(new GetLinkedElementsCommand());
        Register(new GetElementGeometryCommand());
        Register(new GetViewImageCommand());
        Register(new GetModelHealthCommand());
        Register(new GetWorksetsCommand());

        // === Creation — architecture ===
        Register(new CreateWallCommand());
        Register(new CreateFloorCommand());
        Register(new CreateLevelCommand());
        Register(new CreateGridCommand());
        Register(new CreateRoomCommand());
        Register(new CreateColumnCommand());
        Register(new CreateBeamCommand());
        Register(new CreateCeilingCommand());
        Register(new CreateOpeningInWallCommand());
        Register(new PlaceFamilyInstanceCommand());

        // === Creation — documentation ===
        Register(new CreateSheetCommand());
        Register(new PlaceViewOnSheetCommand());
        Register(new CreateFloorPlanViewCommand());
        Register(new CreateSectionViewCommand());
        Register(new Create3DViewCommand());
        Register(new CreateScheduleCommand());
        Register(new TagElementCommand());
        Register(new TagAllInViewCommand());
        Register(new CreateAlignedDimensionCommand());
        Register(new CreateSpotElevationCommand());
        Register(new CreateTextNoteCommand());

        // === Annotation — query ===
        Register(new GetTagsInViewCommand());

        // === Edit — parameters ===
        Register(new SetParameterCommand());
        Register(new SetParameterBatchCommand());
        Register(new RenameElementCommand());
        Register(new CopyParametersCommand());
        Register(new ChangeElementTypeCommand());
        Register(new ApplyViewTemplateCommand());
        Register(new ConfigureScheduleCommand());
        Register(new SetLevelElevationCommand());
        Register(new ExportViewPdfCommand());

        // === Edit — transform ===
        Register(new MoveElementCommand());
        Register(new RotateElementCommand());
        Register(new CopyElementCommand());
        Register(new MirrorElementCommand());
        Register(new ArrayLinearCommand());
        Register(new DeleteElementsCommand());

        // === Edit — grouping ===
        Register(new GroupElementsCommand());
        Register(new UngroupElementsCommand());

        // === View manipulation ===
        Register(new OpenViewCommand());
        Register(new SetViewDetailLevelCommand());
        Register(new HideElementsInViewCommand());
        Register(new UnhideElementsInViewCommand());
        Register(new IsolateElementsInViewCommand());
        Register(new DuplicateViewCommand());
        Register(new SetSectionBoxCommand());
        Register(new SelectElementsCommand());
        Register(new ZoomToElementsCommand());
        Register(new ApplyViewFilterCommand());
        Register(new ColorOverrideByParamCommand());

        Register(new OverrideElementGraphicsCommand());

        // === Coordination / clash ===
        Register(new CheckClearanceCommand());
    }
}
