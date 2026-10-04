using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a ViewSheet.
///
/// Params:
///   - sheetNumber: string, optional (Revit auto-assigns if omitted). Applied exactly, or the
///                  command fails (name_collision 409 for a number already in use) and no sheet
///                  is created.
///   - sheetName:   string, optional. Same contract (invalid_chars 400).
///   - titleBlockName: string, optional (defaults to first loaded title block family type)
/// </summary>
public sealed class CreateSheetCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "create_sheet";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var tbName = P.StrOrNull(p, "titleBlockName");
        var tbSymbol = ResolveTitleBlock(doc, tbName);

        var sheet = ViewSheet.Create(doc, tbSymbol?.Id ?? ElementId.InvalidElementId);

        var number = P.StrOrNull(p, "sheetNumber");
        // A swallowed failure here was the worst of the lot: a number already in use left the sheet
        // under whatever number Revit assigned, and the caller was told it had succeeded.
        if (!string.IsNullOrWhiteSpace(number))
            NameRules.ApplySheetNumber(sheet, number!);

        // Not swallowed before, but a refusal surfaced as Revit's raw exception (500).
        var name = P.StrOrNull(p, "sheetName");
        if (!string.IsNullOrWhiteSpace(name))
            NameRules.ApplyViewName(sheet, name!, "sheetName");

        return new JsonObject
        {
            ["affected"] = Affected.Created(sheet.Id.Value),
            ["id"] = sheet.Id.Value,
            ["sheetNumber"] = sheet.SheetNumber,
            ["name"] = sheet.Name,
        };
    }

    private static FamilySymbol? ResolveTitleBlock(Document doc, string? name)
    {
        var query = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>();

        if (!string.IsNullOrWhiteSpace(name))
            return query.FirstOrDefault(s => s.Name == name || s.FamilyName == name);

        return query.FirstOrDefault();
    }
    /// <summary>The sheet must exist with the number and name the command reported.</summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result) =>
        ReadBack.Created(ctx.RequireDoc(), result,
            e => ReadBack.Text("sheetNumber", result["sheetNumber"]?.GetValue<string>(), ((ViewSheet)e).SheetNumber),
            e => ReadBack.Text("name", result["name"]?.GetValue<string>(), e.Name));
}
