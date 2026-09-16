namespace PropertyGpsApi;

public static class ApiRoutes
{
    /// <summary>
    /// Confirmed genuine: live image URLs stored in BtoA_SiteRoadDetails_Officer use this
    /// exact prefix under https://propertygps.bbmpgov.in/b2a-service/. Keeping it means
    /// existing stored URLs keep resolving and the routes stay recognisable to BBMP.
    /// </summary>
    public const string Base = "v1/api/gbagps/singlesite";
}

public static class RateLimitPolicies
{
    public const string OtpSend = "otp-send";
    public const string OtpVerify = "otp-verify";
}
