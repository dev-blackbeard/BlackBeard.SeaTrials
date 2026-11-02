using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Provenance;

/// <summary>
/// Stamps a request: the plan (the whole file except "generated") in canonical form, kept as a string, with its
/// SHA-256. tools/stamp.py does the same on the writing side; a shared test vector pins that the bytes are identical.
/// </summary>
public static class Stamper
{
    public const string Tool = "seatrials 1";

    /// <summary>The plan: the request as JSON, without the stamp.</summary>
    public static string PlanJson(RequestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.ToJson(includeGenerated: false);
    }

    /// <summary>The canonical form of the plan: what the stamp keeps as "baseline".</summary>
    public static string Baseline(RequestFile file)
    {
        using var document = JsonDocument.Parse(PlanJson(file));
        return CanonicalJson.Serialize(document.RootElement);
    }

    /// <summary>The SHA-256 of the text as UTF-8, in lower-case hexadecimal.</summary>
    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(CanonicalJson.ToUtf8(text))).ToLowerInvariant();

    /// <summary>The time as the stamp writes it: UTC, to the second, with a Z.</summary>
    public static string FormatTime(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static Generated MakeStamp(RequestFile file, string by, DateTimeOffset at, string tool = Tool)
    {
        if (string.IsNullOrWhiteSpace(by))
        {
            throw new ArgumentException("Say who wrote the request.", nameof(by));
        }

        var baseline = Baseline(file);
        return new Generated(by, FormatTime(at), tool, Sha256Hex(baseline), baseline);
    }

    /// <summary>The request with a fresh stamp. An earlier stamp is replaced; the plan is not touched.</summary>
    public static RequestFile Stamp(RequestFile file, string by, DateTimeOffset at, string tool = Tool) =>
        file with { Generated = MakeStamp(file, by, at, tool) };
}
