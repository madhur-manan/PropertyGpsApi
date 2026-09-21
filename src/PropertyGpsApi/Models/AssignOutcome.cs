using PropertyGpsApi.Common;

namespace PropertyGpsApi.Models;

/// <summary>
/// What USP_IU_Architect_AssignedApp reports back. StatusCode is 200 on success and 400 for
/// a business rejection - already assigned, or the officer's queue is full.
/// </summary>
public sealed record AssignOutcome(int StatusCode, int AssignId, int AppId, string? Message)
{
    public bool Succeeded => StatusCode == 200;

    /// <summary>
    /// The error code a rejection maps to, recovered from the procedure's own
    /// wording.
    ///
    /// The procedure rejects with a single 400 and an English sentence, so the
    /// sentence is all there is to go on. That is a property of this
    /// procedure's contract, so the matching belongs here beside it rather than
    /// in a controller — where it sat next to HTTP concerns and had to be read
    /// to discover that "pending in your queue" is load-bearing text.
    ///
    /// Every case is the officer's own situation to resolve — a record someone
    /// else took, or their own queue being full — so all of them are permanent
    /// for this request and recoverable once they act.
    ///
    /// ASSUMPTION: these three phrases are stable. They are matched
    /// case-insensitively on a substring, and an unrecognised sentence falls
    /// back to AssignRejected and is still shown to the officer verbatim, so a
    /// reworded procedure degrades to a generic code rather than a wrong one.
    /// </summary>
    public string RejectionCode =>
        Text.Contains("Already Assigned", StringComparison.OrdinalIgnoreCase)
            ? ApiErrorCodes.AlreadyAssigned
            : Text.Contains("pending in your queue", StringComparison.OrdinalIgnoreCase)
                ? ApiErrorCodes.QueueFull
                : Text.Contains("maximum allowed limit", StringComparison.OrdinalIgnoreCase)
                    ? ApiErrorCodes.ReassignLimitReached
                    : ApiErrorCodes.AssignRejected;

    /// <summary>
    /// The sentence to show the officer, trimmed, with a fallback so a
    /// procedure that rejects without saying why still produces something
    /// readable.
    /// </summary>
    public string Text => Message?.Trim() is { Length: > 0 } text
        ? text
        : "The record could not be allotted.";
}
