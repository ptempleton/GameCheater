using System.Globalization;
using System.Xml.Linq;

namespace GameCheater.Core.Tables;

/// <summary>
/// Parses a Cheat Engine .CT file (XML) into a <see cref="CtTable"/> and classifies each
/// entry so the app knows who runs it: our own runtime (plain value/pointer entries) or
/// the Cheat Engine backend (Lua / Auto Assembler scripts).
///
/// The parser is deliberately tolerant — real tables in the wild are messy — and does NOT
/// try to execute anything. It extracts structure and makes the routing decision.
///
/// NOTE on offset ordering: Cheat Engine stores &lt;Offset&gt; elements in a .CT in the
/// REVERSE of the order they're applied when resolving the pointer. We reverse them here
/// so <see cref="CtEntry.Offsets"/> is in base→…→final order (what our PointerChain wants).
/// This matches community tooling but should be spot-checked against a couple of real
/// tables before relying on pointer entries in production.
/// </summary>
public static class CtParser
{
    public static CtTable ParseFile(string path) => Parse(File.ReadAllText(path));

    public static CtTable Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root
            ?? throw new FormatException("Not a valid .CT: no root element.");
        if (root.Name.LocalName != "CheatTable")
            throw new FormatException($"Not a Cheat Engine table (root is <{root.Name.LocalName}>).");

        int version = root.Attribute("CheatEngineTableVersion") is { } a
            && int.TryParse(a.Value, out int vv) ? vv : 0;
        var table = new CtTable { Version = version };

        var entriesRoot = root.Element("CheatEntries");
        if (entriesRoot is not null)
        {
            foreach (var el in entriesRoot.Elements("CheatEntry"))
                table.Entries.Add(ParseEntry(el));
        }

        foreach (var e in table.Entries)
            Classify(e);

        return table;
    }

    private static CtEntry ParseEntry(XElement el)
    {
        var offsetsEl = el.Element("Offsets");
        var offsets = new List<int>();
        if (offsetsEl is not null)
        {
            foreach (var o in offsetsEl.Elements("Offset"))
                if (TryParseHex(o.Value, out int v))
                    offsets.Add(v);
            offsets.Reverse(); // CE stores offsets reversed vs. resolution order (see class note)
        }

        var asmScript = el.Element("AssemblerScript")?.Value;
        var hasLua = el.Element("LuaScript") is { } lua && !string.IsNullOrWhiteSpace(lua.Value);

        var entry = new CtEntry
        {
            Id = ParseInt(el.Element("ID")?.Value),
            Description = Unquote(el.Element("Description")?.Value ?? ""),
            ValueType = MapType(el.Element("VariableType")?.Value),
            Address = el.Element("Address")?.Value?.Trim(),
            Offsets = offsets,
            AssemblerScript = string.IsNullOrWhiteSpace(asmScript) ? null : asmScript,
            HasLuaScript = hasLua,
        };

        // Children live in a nested <CheatEntries> under this entry.
        var childRoot = el.Element("CheatEntries");
        if (childRoot is not null)
        {
            foreach (var child in childRoot.Elements("CheatEntry"))
                entry.Children.Add(ParseEntry(child));
        }

        return entry;
    }

    /// <summary>Decide who runs each entry, recursively.</summary>
    private static void Classify(CtEntry e)
    {
        foreach (var c in e.Children)
            Classify(c);

        if (e.HasScript)
            e.Kind = CtEntryKind.Script;                    // Lua/AA → Cheat Engine backend
        else if (string.IsNullOrEmpty(e.Address) && e.Children.Count > 0)
            e.Kind = CtEntryKind.Group;                     // header / category
        else if (!string.IsNullOrEmpty(e.Address) && e.ValueType != CtValueType.Unknown)
            e.Kind = CtEntryKind.Value;                     // plain value/pointer → our runtime
        else
            e.Kind = CtEntryKind.Unsupported;
    }

    public static CtLoadReport Summarize(CtTable table)
    {
        int groups = 0, values = 0, scripts = 0, unsupported = 0;
        var scriptNames = new List<string>();

        foreach (var e in table.Flatten())
        {
            switch (e.Kind)
            {
                case CtEntryKind.Group: groups++; break;
                case CtEntryKind.Value: values++; break;
                case CtEntryKind.Script:
                    scripts++;
                    scriptNames.Add(e.Description);
                    break;
                default: unsupported++; break;
            }
        }

        return new CtLoadReport
        {
            Total = groups + values + scripts + unsupported,
            Groups = groups,
            RunnableValues = values,
            RoutedToCe = scripts,
            Unsupported = unsupported,
            ScriptEntries = scriptNames,
        };
    }

    // --- helpers ---

    private static CtValueType MapType(string? t) => t?.Trim() switch
    {
        "Byte" => CtValueType.Byte,
        "2 Bytes" => CtValueType.TwoBytes,
        "4 Bytes" => CtValueType.FourBytes,
        "8 Bytes" => CtValueType.EightBytes,
        "Float" => CtValueType.Float,
        "Double" => CtValueType.Double,
        "String" => CtValueType.String,
        "Array of byte" or "Array of Byte" => CtValueType.ByteArray,
        "Binary" => CtValueType.Binary,
        "Auto Assembler Script" => CtValueType.AutoAssembler,
        _ => CtValueType.Unknown,
    };

    /// <summary>CE wraps descriptions in double quotes, e.g. <c>"Infinite Health"</c>.</summary>
    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];
        return s;
    }

    private static bool TryParseHex(string s, out int value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static int ParseInt(string? s) =>
        int.TryParse(s, out int v) ? v : 0;
}
