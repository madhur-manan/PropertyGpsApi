namespace PropertyGpsApi.Models;


public enum OtpValidationOutcome
{
    /// <summary>The code matched and the officer is active.</summary>
    Valid,

    /// <summary>Officer active, but this code is wrong or expired.</summary>
    InvalidCode,

    /// <summary>
    /// No rows at all. The procedure filters on Ofcr_Active = 1, so this covers both an
    /// unregistered mobile and an account locked out by repeated failures - the procedure
    /// deliberately does not let us tell them apart, and neither should we.
    /// </summary>
    UnknownOrLockedOfficer
}

public sealed record OtpValidationResult(OtpValidationOutcome Outcome, Officer? Officer);
