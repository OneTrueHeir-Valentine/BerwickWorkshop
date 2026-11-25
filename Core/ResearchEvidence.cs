namespace Berwick.Core;

/// <summary>What is known about the meaning of a source field, independent of patch safety.</summary>
public enum EvidenceLevel { Verified, SourceObserved, Inferred, Unresolved, Conflicting }

/// <summary>Whether a write can be made reliably, independent of gameplay acceptance.</summary>
public enum EvidenceBinding { Bound, ExperimentalBound, Unbound, Conflicting }

/// <summary>Do not infer game acceptance from the existence of parsed commands or C# test success.</summary>
public enum EvidenceAcceptance { Untested, EnglishObserved, JapaneseObserved, BothObserved, Failed }

/// <summary>
/// Immutable change provenance. Never automatically upgrade an experimental change to Verified.
/// A saved project and its exported manifest must retain the same labels.
/// </summary>
public sealed record ChangeEvidence(
    EvidenceLevel Interpretation,
    EvidenceBinding Binding,
    EvidenceAcceptance Gameplay,
    string Basis)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Interpretation switch
    {
        EvidenceLevel.Verified => "Verified (source)",
        EvidenceLevel.SourceObserved => "Source observed (unverified behavior)",
        EvidenceLevel.Inferred => "Inferred (unverified)",
        EvidenceLevel.Unresolved => "Unresolved (unverified)",
        _ => "Conflicting evidence"
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MayWrite => Binding is EvidenceBinding.Bound or EvidenceBinding.ExperimentalBound
        && Interpretation != EvidenceLevel.Conflicting;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Experimental => Interpretation != EvidenceLevel.Verified
        || Binding != EvidenceBinding.Bound || Gameplay == EvidenceAcceptance.Untested;

    /// <summary>Conservative default for edits. 'Source observed' is not a gameplay test.</summary>
    public static ChangeEvidence ForEdit(string table, string kind) =>
        new(EvidenceLevel.SourceObserved,
            table is EditSession.Scripts or EditSession.ScriptValues
                ? EvidenceBinding.ExperimentalBound : EvidenceBinding.Bound,
            EvidenceAcceptance.Untested,
            kind == "Promotion"
                ? "Source-bound promotion field; this project's changed value has not been accepted in both games."
                : "Project change mapped to a source record; edited behavior is not game-accepted.");
}

public sealed record ProjectEvidence(string Table, int Id, string Key, ChangeEvidence Status);

/// <summary>Research catalogue inclusion and filtering. Unknown entries are shown by default.</summary>
public static class EvidenceVisibility
{
    public static IEnumerable<T> Filter<T>(IEnumerable<T> rows, Func<T, EvidenceLevel> status, bool verifiedOnly) =>
        verifiedOnly ? rows.Where(r => status(r) == EvidenceLevel.Verified) : rows;
}
