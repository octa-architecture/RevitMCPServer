using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// The canonical "which elements did this write touch" key: <c>data.affected =
/// { created: long[], modified: long[], deleted: long[] }</c>. Optional and additive — commands adopt
/// it one by one and keep their existing keys (<c>id</c>, <c>newIds</c>, <c>deletedIds</c>…). The
/// dispatcher's generic read-back and the audit log both read it.
/// </summary>
public static class Affected
{
    public static JsonObject Of(IEnumerable<long>? created = null, IEnumerable<long>? modified = null,
        IEnumerable<long>? deleted = null) => new()
    {
        ["created"] = ToArray(created),
        ["modified"] = ToArray(modified),
        ["deleted"] = ToArray(deleted),
    };

    public static JsonObject Created(params long[] ids) => Of(created: ids);
    public static JsonObject Created(IEnumerable<long> ids) => Of(created: ids);
    public static JsonObject Modified(params long[] ids) => Of(modified: ids);
    public static JsonObject Modified(IEnumerable<long> ids) => Of(modified: ids);
    public static JsonObject Deleted(IEnumerable<long> ids) => Of(deleted: ids);

    /// <summary>Reads the three lists back; missing or malformed entries read as empty.</summary>
    public static (long[] Created, long[] Modified, long[] Deleted) Read(JsonNode? affected)
    {
        if (affected is not JsonObject o) return (System.Array.Empty<long>(), System.Array.Empty<long>(), System.Array.Empty<long>());
        return (Ids(o["created"]), Ids(o["modified"]), Ids(o["deleted"]));
    }

    private static JsonArray ToArray(IEnumerable<long>? ids) =>
        new((ids ?? Enumerable.Empty<long>()).Distinct().Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static long[] Ids(JsonNode? node)
    {
        if (node is not JsonArray a) return System.Array.Empty<long>();
        var list = new List<long>(a.Count);
        foreach (var n in a)
            if (n is JsonValue v && v.TryGetValue<long>(out var l)) list.Add(l);
        return list.ToArray();
    }
}
