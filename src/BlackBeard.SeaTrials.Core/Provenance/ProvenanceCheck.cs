using System.Text.Json;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Provenance;

public enum ProvenanceStatus
{
    /// <summary>There is no stamp: it cannot be told whether the file was changed. Neutral, not an error.</summary>
    NotStamped,

    /// <summary>The baseline does not match its own hash, or is not a plan: it cannot vouch for what was requested.</summary>
    BaselineTampered,

    /// <summary>The plan differs from the baseline.</summary>
    Deviations,

    /// <summary>The plan equals the baseline.</summary>
    AsRequested,
}

public sealed record ProvenanceReport(ProvenanceStatus Status, string Message, IReadOnlyList<Deviation> Deviations);

/// <summary>Compares a request with the baseline in its stamp, on opening and again at every run.</summary>
public static class ProvenanceCheck
{
    public const string NotStampedMessage = "Not stamped: it cannot be told whether this file was changed.";

    public const string BaselineTamperedMessage =
        "The baseline in this file has itself been changed; it cannot vouch for what was requested.";

    public const string AsRequestedMessage = "As requested.";

    public static ProvenanceReport Evaluate(RequestFile file, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Generated is not { } stamp)
        {
            return new ProvenanceReport(ProvenanceStatus.NotStamped, NotStampedMessage, Array.Empty<Deviation>());
        }

        if (!BaselineMatchesItsHash(stamp))
        {
            return Tampered();
        }

        JsonDocument baseline;
        try
        {
            baseline = JsonDocument.Parse(stamp.Baseline);
        }
        catch (JsonException)
        {
            return Tampered();
        }

        using (baseline)
        {
            if (baseline.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Tampered();
            }

            using var current = JsonDocument.Parse(Stamper.PlanJson(file));
            var deviations = DeviationFinder.Diff(baseline.RootElement, current.RootElement, at);
            if (deviations.Count == 0)
            {
                return new ProvenanceReport(ProvenanceStatus.AsRequested, AsRequestedMessage, deviations);
            }

            var message = "Differs from what was requested: " + deviations.Count + (deviations.Count == 1 ? " change" : " changes");
            return new ProvenanceReport(ProvenanceStatus.Deviations, message, deviations);
        }
    }

    private static ProvenanceReport Tampered() =>
        new(ProvenanceStatus.BaselineTampered, BaselineTamperedMessage, Array.Empty<Deviation>());

    private static bool BaselineMatchesItsHash(Generated stamp)
    {
        try
        {
            return string.Equals(Stamper.Sha256Hex(stamp.Baseline), stamp.BaselineSha256, StringComparison.Ordinal);
        }
        catch (CanonicalJsonException)
        {
            return false;
        }
    }
}
