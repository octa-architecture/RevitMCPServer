using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Create a new <see cref="Level"/>.
///
/// Parameters:
///   - elevation: number, required.  In user units (default meters).
///   - name:      string, optional.  Applied exactly, or the command fails (invalid_chars 400 /
///                name_collision 409 for a name another level has) and no level is created.
///   - units:     "meters"|"feet"
/// </summary>
public sealed class CreateLevelCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "create_level";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);
        var toFeet = units == "feet" ? 1.0 : P.MetersToFeet;

        var elevation = P.Dbl(p, "elevation") * toFeet;
        var level = Level.Create(doc, elevation);

        // Previously a taken name kept Revit's auto name ("Level 3") with a free-text
        // renameWarning — still ok:true, so a caller that ignored the warning built on a level
        // it could not find again by name. Now the name lands or the whole call rolls back.
        var name = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(name))
            NameRules.ApplyLevelName(level, name!);

        return new JsonObject
        {
            ["affected"] = Affected.Created(level.Id.Value),
            ["id"] = level.Id.Value,
            ["elevationFeet"] = elevation,
            ["name"] = level.Name,
        };
    }
    /// <summary>The level must exist at the requested elevation, under the requested name if one was given.</summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var wantName = P.StrOrNull(ctx.Parameters, "name");
        var wantFt = result["elevationFeet"]!.GetValue<double>();
        return ReadBack.Created(ctx.RequireDoc(), result,
            e => ReadBack.Number("elevation", wantFt, ((Level)e).Elevation, 1e-5, "ft"),
            e => string.IsNullOrWhiteSpace(wantName) ? null : ReadBack.Text("name", wantName, e.Name));
    }
}
