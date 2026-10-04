using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Place a Room at a given point.
///
/// Params:
///   - location:   { x, y, z? }, required — point inside the room-bounding walls
///   - levelName:  string, optional (defaults to lowest level)
///   - name:       string, optional. Applied exactly or the command fails and no room is created.
///   - number:     string, optional. Same contract. Revit allows duplicate room names and numbers.
///   - units:      "meters"|"feet"
/// </summary>
public sealed class CreateRoomCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "create_room";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var location = P.Xyz(p, "location", units);
        var level = CreateWallCommand.ResolveLevel(doc, P.StrOrNull(p, "levelName"));

        var pt = new UV(location.X, location.Y);
        var room = doc.Create.NewRoom(level, pt);

        // A refused value used to escape as Revit's raw exception (command_failed 500).
        var nameVal = P.StrOrNull(p, "name");
        if (!string.IsNullOrWhiteSpace(nameVal))
            NameRules.ApplyRoomName(room, nameVal!);

        var numberVal = P.StrOrNull(p, "number");
        if (!string.IsNullOrWhiteSpace(numberVal))
            NameRules.ApplyRoomNumber(room, numberVal!);

        return new JsonObject
        {
            ["affected"] = Affected.Created(room.Id.Value),
            ["id"] = room.Id.Value,
            ["name"] = room.Name,
            ["number"] = room.Number,
            ["levelName"] = level.Name,
        };
    }
    /// <summary>
    /// The room must survive the commit (Revit can delete a room placed in a region it cannot keep)
    /// and hold the requested name and number.
    /// </summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var wantName = P.StrOrNull(ctx.Parameters, "name");
        var wantNumber = P.StrOrNull(ctx.Parameters, "number");
        return ReadBack.Created(ctx.RequireDoc(), result,
            e => string.IsNullOrWhiteSpace(wantName) ? null
                : ReadBack.Text("name", wantName, e.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()),
            e => string.IsNullOrWhiteSpace(wantNumber) ? null
                : ReadBack.Text("number", wantNumber, ((Autodesk.Revit.DB.Architecture.Room)e).Number));
    }
}
