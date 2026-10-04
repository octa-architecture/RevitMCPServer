using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Shared building blocks for <see cref="IVerifiableCommand.Verify"/>: existence of affected ids,
/// stored parameter values, names and positions. Every check compares against what the caller
/// asked for (or what the command observed inside its transaction), re-read after the commit.
/// </summary>
public static class ReadBack
{
    /// <summary>Length tolerance in feet (~0.3 mm) for positions and lengths.</summary>
    public const double LengthTolFt = 1e-3;

    /// <summary>Angle tolerance in radians (~0.006°).</summary>
    public const double AngleTolRad = 1e-4;

    private const int MaxListed = 5;

    // ── existence ──────────────────────────────────────────────────────────

    /// <summary>
    /// The generic check, pure so it can be unit-tested: created/modified ids must exist, deleted
    /// ids must not. No <c>affected</c> key → <see cref="VerifyResult.NotSupported"/>.
    /// </summary>
    public static VerifyResult CheckAffected(JsonObject result, Func<long, bool> exists)
    {
        if (result["affected"] is not JsonObject a) return VerifyResult.Unsupported();
        var (created, modified, deleted) = Commands.Affected.Read(a);

        var lostCreated = created.Where(id => !exists(id)).ToList();
        var lostModified = modified.Where(id => !exists(id)).ToList();
        var survived = deleted.Where(exists).ToList();

        var problems = new List<string>();
        if (lostCreated.Count > 0) problems.Add($"created but missing after commit: {List(lostCreated)}");
        if (lostModified.Count > 0) problems.Add($"modified but missing after commit: {List(lostModified)}");
        if (survived.Count > 0) problems.Add($"deleted but still present: {List(survived)}");

        var total = created.Length + modified.Length + deleted.Length;
        return problems.Count == 0
            ? VerifyResult.Pass($"{total} affected element(s) checked")
            : VerifyResult.Fail(string.Join("; ", problems));
    }

    public static bool Exists(Document doc, long id) => doc.GetElement(new ElementId(id)) is not null;

    /// <summary>Existence of <c>result.affected</c> against the live document.</summary>
    public static VerifyResult AffectedInModel(Document doc, JsonObject result) =>
        CheckAffected(result, id => Exists(doc, id));

    /// <summary>
    /// Existence first; if that passes, run <paramref name="checks"/> against each created element
    /// and fail on the first mismatch. A check returns null when it is satisfied.
    /// </summary>
    public static VerifyResult Created(Document doc, JsonObject result, params Func<Element, string?>[] checks)
    {
        var exists = AffectedInModel(doc, result);
        if (exists.Status != VerifyResult.Passed || checks.Length == 0) return exists;

        var mismatches = new List<string>();
        foreach (var id in Commands.Affected.Read(result["affected"]).Created)
        {
            var el = doc.GetElement(new ElementId(id));
            if (el is null) continue;
            foreach (var check in checks)
                if (check(el) is string m) mismatches.Add($"{id}: {m}");
        }
        return FromMismatches(mismatches, exists.Detail);
    }

    // ── comparisons ────────────────────────────────────────────────────────

    public static string? Text(string what, string? expected, string? stored) =>
        string.Equals(expected ?? "", stored ?? "", StringComparison.Ordinal)
            ? null : $"{what}: expected '{expected}', stored '{stored}'";

    public static string? Number(string what, double expected, double stored, double tol, string unit) =>
        Math.Abs(expected - stored) <= tol
            ? null
            : $"{what}: expected {expected.ToString("0.######", CultureInfo.InvariantCulture)} {unit}, " +
              $"stored {stored.ToString("0.######", CultureInfo.InvariantCulture)} {unit}";

    public static VerifyResult FromMismatches(IReadOnlyCollection<string> mismatches, string? passDetail = null)
    {
        if (mismatches.Count == 0) return VerifyResult.Pass(passDetail);
        var shown = string.Join("; ", mismatches.Take(MaxListed));
        return VerifyResult.Fail(mismatches.Count > MaxListed ? $"{shown}; … (+{mismatches.Count - MaxListed})" : shown);
    }

    private static string List(IReadOnlyCollection<long> ids) =>
        string.Join(", ", ids.Take(MaxListed)) + (ids.Count > MaxListed ? $", … (+{ids.Count - MaxListed})" : "");

    // ── parameters ─────────────────────────────────────────────────────────

    /// <summary>
    /// What a parameter should hold after a write of <paramref name="value"/> in
    /// <paramref name="units"/> — the same coercion <c>set_parameter</c> applies (string, int or
    /// bool→0/1, unit-converted double, element id).
    /// </summary>
    public static object ExpectedFromJson(Parameter param, JsonNode value, string units, string label)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                return P.StrFrom(value, label);
            case StorageType.Integer:
                return value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                    ? (P.BoolFrom(value, label) ? 1 : 0)
                    : P.IntFrom(value, label);
            case StorageType.Double:
                return SetParameterCommand.ConvertToInternal(param, P.DblFrom(value, label), units, out _);
            case StorageType.ElementId:
                return value is JsonObject o && o["id"] is JsonNode idn
                    ? P.LongFrom(idn, label + ".id") : P.LongFrom(value, label);
            default:
                throw new RevitCommandException("invalid_parameter", $"Unsupported StorageType '{param.StorageType}'.");
        }
    }

    /// <summary>Null when the stored value equals <paramref name="expected"/>, else a description.</summary>
    public static string? ParameterMismatch(Parameter param, object expected)
    {
        var name = param.Definition?.Name ?? "parameter";
        switch (param.StorageType)
        {
            case StorageType.String:
                return Text(name, expected as string, param.AsString());
            case StorageType.Integer:
                var wantI = Convert.ToInt32(expected, CultureInfo.InvariantCulture);
                return param.AsInteger() == wantI ? null : $"{name}: expected {wantI}, stored {param.AsInteger()}";
            case StorageType.Double:
                var wantD = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                var got = param.AsDouble();
                // Relative + absolute tolerance on internal units, then report in display form.
                return Math.Abs(got - wantD) <= 1e-6 + 1e-9 * Math.Abs(wantD)
                    ? null : $"{name}: expected {wantD.ToString("R", CultureInfo.InvariantCulture)} (internal), " +
                             $"stored {got.ToString("R", CultureInfo.InvariantCulture)} ({SafeValueString(param)})";
            case StorageType.ElementId:
                var wantId = Convert.ToInt64(expected, CultureInfo.InvariantCulture);
                return param.AsElementId().Value == wantId ? null : $"{name}: expected id {wantId}, stored id {param.AsElementId().Value}";
            default:
                return $"{name}: unsupported storage type {param.StorageType}";
        }
    }

    /// <summary>Raw stored value, comparable across two parameters of the same storage type.</summary>
    public static object? Raw(Parameter param) => param.StorageType switch
    {
        StorageType.String => param.AsString() ?? "",
        StorageType.Integer => param.AsInteger(),
        StorageType.Double => param.AsDouble(),
        StorageType.ElementId => param.AsElementId().Value,
        _ => null,
    };

    public static string? SafeValueString(Parameter p)
    {
        try { return p.AsValueString() ?? p.AsString(); } catch { return null; }
    }

    // ── position ───────────────────────────────────────────────────────────

    /// <summary>Bounding-box centre in model coordinates (feet), or null when there is no box.</summary>
    public static XYZ? Center(Element e)
    {
        var bb = e.get_BoundingBox(null);
        return bb is null ? null : (bb.Min + bb.Max) / 2;
    }

    /// <summary>
    /// A point that moves exactly with the element under move / rotate / mirror: the location point,
    /// the location curve's midpoint, else the bounding-box centre. Feet, model coordinates.
    /// </summary>
    public static XYZ? Anchor(Element e)
    {
        switch (e.Location)
        {
            case LocationPoint lp:
                try { return lp.Point; } catch { break; }
            case LocationCurve lc:
                try { return lc.Curve.Evaluate(0.5, true); } catch { break; }
        }
        return Center(e);
    }

    /// <summary>Rotates <paramref name="p"/> about the vertical axis through <paramref name="c"/>.</summary>
    public static XYZ RotateAboutZ(XYZ p, XYZ c, double angleRad)
    {
        double dx = p.X - c.X, dy = p.Y - c.Y, cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
        return new XYZ(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos, p.Z);
    }

    /// <summary>Reflects <paramref name="p"/> across the plane through <paramref name="o"/> with unit normal <paramref name="n"/>.</summary>
    public static XYZ Reflect(XYZ p, XYZ o, XYZ n) => p - 2 * (p - o).DotProduct(n) * n;

    public static string? Point(string what, XYZ expected, XYZ? stored, double tol = LengthTolFt) =>
        stored is null ? $"{what}: element has no location to compare"
        : expected.DistanceTo(stored) <= tol ? null
        : $"{what}: expected ({F(expected.X)}, {F(expected.Y)}, {F(expected.Z)}) ft, " +
          $"stored ({F(stored.X)}, {F(stored.Y)}, {F(stored.Z)}) ft — off by {F(expected.DistanceTo(stored))} ft";

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Plan orientation in radians: <c>LocationPoint.Rotation</c> for point-based elements, the
    /// curve's XY direction angle for curve-based ones, null otherwise.
    /// </summary>
    public static double? PlanAngle(Element e)
    {
        switch (e.Location)
        {
            case LocationPoint lp:
                try { return lp.Rotation; } catch { return null; }
            case LocationCurve lc:
                var d = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
                return Math.Abs(d.X) < 1e-12 && Math.Abs(d.Y) < 1e-12 ? null : Math.Atan2(d.Y, d.X);
            default:
                return null;
        }
    }

    /// <summary>Smallest signed difference a−b wrapped into (−π, π].</summary>
    public static double AngleDelta(double a, double b)
    {
        var d = (a - b) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d <= -Math.PI) d += 2 * Math.PI;
        return d;
    }

    public static JsonObject Xyz(XYZ v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    public static XYZ? Xyz(JsonNode? node) =>
        node is JsonObject o && o["x"] is JsonNode x && o["y"] is JsonNode y && o["z"] is JsonNode z
            ? new XYZ(x.GetValue<double>(), y.GetValue<double>(), z.GetValue<double>())
            : null;
}
