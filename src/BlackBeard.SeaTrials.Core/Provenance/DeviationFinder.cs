using System.Text.Json;

namespace BlackBeard.SeaTrials.Core.Provenance;

/// <summary>
/// Compares the plan as it stands with the baseline, both as JSON, and lists the differences. Revisions, steps and
/// tasks are matched by their label or name, so a step moved or removed is not a pile of changed fields; every other
/// list is compared by position, with a reordering reported as one change when the same items are in another order.
/// </summary>
public static class DeviationFinder
{
    public static IReadOnlyList<Deviation> Diff(JsonElement baseline, JsonElement current, DateTimeOffset at)
    {
        var found = new List<Deviation>();
        DiffValue("", baseline, current, null, at, found);
        return found;
    }

    private static string? IdentityKey(string propertyName) => propertyName switch
    {
        "revisions" => "label",
        "steps" => "name",
        "tasks" => "name",
        _ => null,
    };

    private static void DiffValue(
        string path, JsonElement before, JsonElement after, string? identityKey, DateTimeOffset at, List<Deviation> found)
    {
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            DiffObject(path, before, after, at, found);
        }
        else if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array)
        {
            DiffArray(path, before, after, identityKey, at, found);
        }
        else
        {
            var was = Render(before);
            var now = Render(after);
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                found.Add(new Deviation(path, DeviationKind.Changed, was, now, at));
            }
        }
    }

    private static void DiffObject(string path, JsonElement before, JsonElement after, DateTimeOffset at, List<Deviation> found)
    {
        var (beforeNames, beforeMap) = Members(before);
        var (afterNames, afterMap) = Members(after);
        foreach (var name in afterNames)
        {
            var member = Join(path, name);
            if (beforeMap.TryGetValue(name, out var old))
            {
                DiffValue(member, old, afterMap[name], IdentityKey(name), at, found);
            }
            else
            {
                found.Add(new Deviation(member, DeviationKind.Added, null, Render(afterMap[name]), at));
            }
        }

        foreach (var name in beforeNames)
        {
            if (!afterMap.ContainsKey(name))
            {
                found.Add(new Deviation(Join(path, name), DeviationKind.Removed, Render(beforeMap[name]), null, at));
            }
        }
    }

    private static void DiffArray(
        string path, JsonElement before, JsonElement after, string? identityKey, DateTimeOffset at, List<Deviation> found)
    {
        var beforeItems = before.EnumerateArray().ToList();
        var afterItems = after.EnumerateArray().ToList();
        if (identityKey is not null
            && TryIdentities(beforeItems, identityKey, out var beforeIds)
            && TryIdentities(afterItems, identityKey, out var afterIds))
        {
            DiffByIdentity(path, beforeItems, beforeIds, afterItems, afterIds, at, found);
        }
        else
        {
            DiffByPosition(path, beforeItems, afterItems, at, found);
        }
    }

    private static void DiffByIdentity(
        string path,
        List<JsonElement> beforeItems,
        List<string> beforeIds,
        List<JsonElement> afterItems,
        List<string> afterIds,
        DateTimeOffset at,
        List<Deviation> found)
    {
        var beforeSet = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < beforeIds.Count; i++)
        {
            beforeSet[beforeIds[i]] = beforeItems[i];
        }

        var afterSet = new HashSet<string>(afterIds, StringComparer.Ordinal);
        for (var i = 0; i < afterIds.Count; i++)
        {
            var item = path + "[" + afterIds[i] + "]";
            if (beforeSet.TryGetValue(afterIds[i], out var old))
            {
                DiffValue(item, old, afterItems[i], null, at, found);
            }
            else
            {
                found.Add(new Deviation(item, DeviationKind.Added, null, Render(afterItems[i]), at));
            }
        }

        for (var i = 0; i < beforeIds.Count; i++)
        {
            if (!afterSet.Contains(beforeIds[i]))
            {
                found.Add(new Deviation(path + "[" + beforeIds[i] + "]", DeviationKind.Removed, Render(beforeItems[i]), null, at));
            }
        }

        var commonBefore = beforeIds.Where(id => afterSet.Contains(id)).ToList();
        var commonAfter = afterIds.Where(id => beforeSet.ContainsKey(id)).ToList();
        if (!commonBefore.SequenceEqual(commonAfter, StringComparer.Ordinal))
        {
            found.Add(new Deviation(path, DeviationKind.Reordered, RenderNames(commonBefore), RenderNames(commonAfter), at));
        }
    }

    private static void DiffByPosition(
        string path, List<JsonElement> beforeItems, List<JsonElement> afterItems, DateTimeOffset at, List<Deviation> found)
    {
        var beforeText = beforeItems.Select(Render).ToList();
        var afterText = afterItems.Select(Render).ToList();
        if (beforeText.Count == afterText.Count
            && !beforeText.SequenceEqual(afterText, StringComparer.Ordinal)
            && beforeText.OrderBy(text => text, StringComparer.Ordinal).SequenceEqual(
                afterText.OrderBy(text => text, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            found.Add(new Deviation(
                path, DeviationKind.Reordered, "[" + string.Join(",", beforeText) + "]", "[" + string.Join(",", afterText) + "]", at));
            return;
        }

        var common = Math.Min(beforeItems.Count, afterItems.Count);
        for (var i = 0; i < common; i++)
        {
            DiffValue(Position(path, i), beforeItems[i], afterItems[i], null, at, found);
        }

        for (var i = common; i < afterItems.Count; i++)
        {
            found.Add(new Deviation(Position(path, i), DeviationKind.Added, null, afterText[i], at));
        }

        for (var i = common; i < beforeItems.Count; i++)
        {
            found.Add(new Deviation(Position(path, i), DeviationKind.Removed, beforeText[i], null, at));
        }
    }

    // The identities of a list, when every item is an object with a distinct string under the key.
    private static bool TryIdentities(List<JsonElement> items, string key, out List<string> ids)
    {
        ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty(key, out var identity)
                || identity.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = identity.GetString() ?? "";
            if (!seen.Add(text))
            {
                return false;
            }

            ids.Add(text);
        }

        return true;
    }

    private static (List<string> Names, Dictionary<string, JsonElement> Map) Members(JsonElement element)
    {
        var names = new List<string>();
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (map.TryAdd(property.Name, property.Value))
            {
                names.Add(property.Name);
            }
        }

        return (names, map);
    }

    // The canonical form when there is one; a baseline is untrusted, so a value that has none is shown as written.
    private static string Render(JsonElement element)
    {
        try
        {
            return CanonicalJson.Serialize(element);
        }
        catch (CanonicalJsonException)
        {
            return element.GetRawText();
        }
    }

    private static string RenderNames(List<string> names) =>
        "[" + string.Join(",", names.Select(CanonicalJson.Quote)) + "]";

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static string Position(string path, int index) =>
        path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
}
