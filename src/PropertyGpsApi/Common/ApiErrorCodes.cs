namespace PropertyGpsApi.Common;

public static class ApiErrorCodes
{
    public const string Ok = "OK";

    public const string BadRequest = "BAD_REQUEST";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string AuthRequired = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string TooManyRequests = "RATE_LIMITED";
    public const string ServerError = "SERVER_ERROR";
    public const string Upstream = "UPSTREAM_UNAVAILABLE";

    public const string OtpResendTooSoon = "OTP_RESEND_TOO_SOON";
    public const string OtpSendFailed = "OTP_SEND_FAILED";
    public const string OtpInvalid = "OTP_INVALID";
    public const string OfficerNotRegistered = "OFFICER_NOT_REGISTERED";
    public const string OfficerLocked = "OFFICER_LOCKED";

    public const string OutsideJurisdiction = "OUTSIDE_JURISDICTION";

    public const string AlreadyAssigned = "ALREADY_ASSIGNED";
    public const string QueueFull = "ASSIGNMENT_QUEUE_FULL";
    public const string ReassignLimitReached = "REASSIGN_LIMIT_REACHED";
    public const string AssignRejected = "ASSIGN_REJECTED";
}
