using System.Text.RegularExpressions;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Is the matched catalogue part the same kind of part as the invoice line? A copy of the DGX's
/// classify_to_part_component (Inventory_Management_Tool, donor_selection.py, 2026-11 revision): keep the two
/// in step. Used to re-check lines the DGX already flagged "check the part" with the older, stricter rules,
/// without looking them up again: "Lambda sensor; 4 wires" and "Oxygen Sensor" are the same part.
///
/// Rules: only the part name counts (text before the first ';' or ',', brackets dropped); numbers, units,
/// spec words and qualifiers (diesel, genuine) are dropped; plurals made singular; synonyms are one word.
/// MATCH when the naming words agree (allowing kit/housing/... extras); DIFFERENT when exactly one side is
/// an electrical part (brake pad vs wear sensor); otherwise UNCERTAIN, including different media
/// (oil filter vs air filter).
/// </summary>
public static class PartNameMatch
{
    public const string Match = "MATCH", Different = "DIFFERENT", Uncertain = "UNCERTAIN";

    private static readonly HashSet<string> Electrical = new() { "sensor", "warning", "contact", "switch", "indicator", "sender", "relay", "probe" };
    private static readonly HashSet<string> SystemWords = new()
    {
        "drive", "air", "coolant", "brake", "engine", "timing", "suspension", "fuel", "wheel",
        "stabiliser", "stabilizer", "control", "trailing", "cabin", "exhaust", "cooling", "ignition",
        "transmission", "clutch", "steering", "front", "rear", "left", "right", "upper", "lower", "axle",
        "compressed", "system", "ribbed", "oil", "water", "camshaft", "crankshaft", "propshaft", "disc",
    };
    private static readonly HashSet<string> Struct = new() { "the", "a", "of", "for", "and", "with", "set", "kit", "assembly", "repair", "cv", "v", "to", "in", "on", "at", "by" };
    private static readonly HashSet<string> Qualifiers = new() { "diesel", "petrol", "gasoline", "complete", "genuine", "original", "oe", "new", "heavy", "duty" };
    private static readonly HashSet<string> SpecWords = new() { "cw", "ccw", "teeth", "tooth", "wire", "pin", "pole", "kw", "mm", "cm", "ml", "nm", "volt", "amp", "ah", "pcs", "pc" };
    private static readonly HashSet<string> KeepS = new() { "bellows", "gas", "glass", "axis", "bus", "abs", "chassis" };
    private static readonly HashSet<string> Modifiers = new()
    {
        "kit", "housing", "bracket", "mounting", "holder", "cap", "seal", "gasket", "bolt", "nut", "clip", "plug",
        "cover", "flange", "tank", "motor", "unit", "module", "element", "insert", "cartridge",
    };
    private static readonly Dictionary<string, string> Media = new()
    {
        ["oil"] = "oil", ["air"] = "air", ["fuel"] = "fuel", ["cabin"] = "cabin", ["pollen"] = "cabin",
        ["coolant"] = "coolant", ["water"] = "coolant", ["hydraulic"] = "hydraulic",
    };
    private static readonly string[][] SynonymGroups =
    {
        new[] { "boot", "bellow", "gaiter" }, new[] { "pad", "lining" }, new[] { "hose", "pipe", "line" },
        new[] { "absorber", "shock", "damper" }, new[] { "spider", "tripod" }, new[] { "lambda", "oxygen", "o2" },
        new[] { "sensor", "probe", "sender", "transmitter" }, new[] { "alternator", "generator", "dynamo" },
        new[] { "turbocharger", "turbo", "charger", "supercharger" }, new[] { "mount", "mounting", "support" },
        new[] { "bush", "bushing" }, new[] { "arm", "wishbone" }, new[] { "headlight", "headlamp" }, new[] { "lamp", "light" },
    };
    private static readonly Dictionary<string, int> SynIndex = SynonymGroups
        .SelectMany((g, i) => g.Select(t => (t, i))).ToDictionary(x => x.t, x => x.i);
    private static readonly (Regex Pattern, string Replacement)[] Compounds =
    {
        (new Regex(@"\bturbo[\s-]*charger\b", RegexOptions.Compiled), "turbocharger"),
        (new Regex(@"\bsuper[\s-]*charger\b", RegexOptions.Compiled), "supercharger"),
        (new Regex(@"\bhead[\s-]*light\b", RegexOptions.Compiled), "headlight"),
        (new Regex(@"\bhead[\s-]*lamp\b", RegexOptions.Compiled), "headlamp"),
    };
    private static readonly Regex Brackets = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^a-z0-9\s/]", RegexOptions.Compiled);

    /// <summary>MATCH, DIFFERENT or UNCERTAIN (also when either name is missing).</summary>
    public static string Verdict(string? invoiceDescription, string? partName)
    {
        if (string.IsNullOrWhiteSpace(invoiceDescription) || string.IsNullOrWhiteSpace(partName)) return Uncertain;
        var incAll = Tokens(invoiceDescription).ToHashSet();
        var donAll = Tokens(partName).ToHashSet();

        var incEl = incAll.Overlaps(Electrical);
        var donEl = donAll.Overlaps(Electrical);
        if (incEl != donEl) return Different;

        var incM = incAll.Where(Media.ContainsKey).Select(t => Media[t]).ToHashSet();
        var donM = donAll.Where(Media.ContainsKey).Select(t => Media[t]).ToHashSet();
        if (incM.Count > 0 && donM.Count > 0 && !incM.Overlaps(donM)) return Uncertain;

        var incH = Heads(invoiceDescription);
        var donH = Heads(partName);
        if (incH.Count == 0 || donH.Count == 0) return Uncertain;

        var incC = incH.Select(Canon).ToHashSet();
        var donC = donH.Select(Canon).ToHashSet();
        var shared = incC.Intersect(donC).ToHashSet();
        if (shared.Count == 0) return Uncertain;

        static bool OnlyModifiers(IEnumerable<string> extra) => extra.All(c => c.StartsWith("t:") && Modifiers.Contains(c[2..]));
        return OnlyModifiers(incC.Except(shared)) && OnlyModifiers(donC.Except(shared)) ? Match : Uncertain;
    }

    private static string Canon(string t) => SynIndex.TryGetValue(t, out var g) ? "g:" + g : "t:" + t;

    private static List<string> Heads(string s)
    {
        var toks = Tokens(s).Where(t => !Struct.Contains(t)).ToList();
        var heads = toks.Where(t => !SystemWords.Contains(t)).ToList();
        return heads.Count > 0 ? heads : toks;
    }

    private static IEnumerable<string> Tokens(string s)
    {
        foreach (var raw in NonWord.Replace(PartName(s), " ").Replace('/', ' ')
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw != "o2" && raw.Any(char.IsDigit)) continue;
            var t = Singular(raw);
            if (SpecWords.Contains(t) || Qualifiers.Contains(t)) continue;
            yield return t;
        }
    }

    private static string PartName(string s)
    {
        s = Brackets.Replace(s.ToLowerInvariant(), " ");
        var cut = s.IndexOfAny(new[] { ';', ',' });
        if (cut >= 0) s = s[..cut];
        foreach (var (p, r) in Compounds) s = p.Replace(s, r);
        return s;
    }

    private static string Singular(string t)
    {
        if (KeepS.Contains(t) || t.Length <= 3 || t.EndsWith("ss")) return t;
        if (t.EndsWith("ies")) return t[..^3] + "y";
        return t.EndsWith('s') ? t[..^1] : t;
    }
}
