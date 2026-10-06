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

    public const string ApplicationNotFound = "APPLICATION_NOT_FOUND";
    public const string NotAssignedToYou = "NOT_ASSIGNED_TO_YOU";
    public const string RoadNotRecognised = "ROAD_NOT_RECOGNISED";
    public const string UnknownRoadRow = "UNKNOWN_ROAD_ROW";
    public const string DeclaredRoadNotAnswered = "DECLARED_ROAD_NOT_ANSWERED";
    public const string SubmitRejected = "SUBMIT_REJECTED";
    public const string SubmitInFlight = "SUBMIT_IN_FLIGHT";

    public const string MediaMissing = "MEDIA_MISSING";
    public const string MediaUnsupported = "MEDIA_UNSUPPORTED";
    public const string MediaMismatch = "MEDIA_MISMATCH";
    public const string MediaTruncated = "MEDIA_TRUNCATED";
    public const string MediaTooLarge = "MEDIA_TOO_LARGE";

    // Request-body encryption (pgps-body/1). The first three are retryable so a finished
    // survey stays queued on the phone rather than being parked as failed.
    public const string BodyEncryptionRequired = "BODY_ENCRYPTION_REQUIRED";
    public const string BodyKeyUnknown = "BODY_KEY_UNKNOWN";
    public const string BodyDecryptFailed = "BODY_DECRYPT_FAILED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
}
