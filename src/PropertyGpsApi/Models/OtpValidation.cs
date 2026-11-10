namespace PropertyGpsApi.Models;


public enum OtpValidationOutcome
{
    /// <summary>The code matched and the officer is active.</summary>
    Valid,

    /// <summary>Officer active, but this code is wrong or expired.</summary>
    InvalidCode,

    /// <summary>
    /// No rows at all. The procedure filters on Ofcr_Active = 1, so this covers both an
    /// unregistered mobile and an account locked out by repeated failures (including the
    /// call that locked it). OtpService asks IOfficerService.IsLockedAfterWrongOtpsAsync to
    /// tell the lock apart, so the officer is told why.
    /// </summary>
    UnknownOrLockedOfficer
}

/// <param name="AttemptsUsed">
/// On InvalidCode: wrong codes in a row so far, this one included (the procedure's
/// NoOfFailedAttempt). Null when the procedure did not say.
/// </param>
public sealed record OtpValidationResult(OtpValidationOutcome Outcome, Officer? Officer, int? AttemptsUsed = null);
