# PropertyGpsApi

Backend for BBMP's **Single Plot GPS** field app (the Flutter app at `D:\bbmp_property_gps`),
built on .NET 10 + Dapper over SQL Server stored procedures.

The app was complete but had no HTTP layer at all. This API gives it one.

## What works today

The first flow, verified end to end against `172.31.0.113\MSSQLSERVER19`:

    mobile -> OTP -> token -> ward application list

| Endpoint | Backed by |
|---|---|
| `POST v1/api/gbagps/singlesite/auth/otp/send` | `USP_I_OTP` |
| `POST v1/api/gbagps/singlesite/auth/otp/verify` | `USP_S_Officer_ValidateOTP` + `usp_InsertLoginData` |
| `POST v1/api/gbagps/singlesite/propertyinfo/fetch` | `USP_S_BtoA_GpsDashData_v1` |
| `GET  v1/api/gbagps/singlesite/masters/zones?corporationId=` | `mst_AROMapping` |
| `GET  v1/api/gbagps/singlesite/masters/wards?zoneId=` | `mst_AROMapping` |
| `GET  /health/live`, `GET /health/ready` | - |

The route prefix `v1/api/gbagps/singlesite` is not invented: live image URLs stored in
`BtoA_SiteRoadDetails_Officer` use it, under `https://propertygps.bbmpgov.in/b2a-service/`.

## Running it

No credential is stored in this repository. Supply them locally:

    cd src/PropertyGpsApi
    dotnet user-secrets set "Database:Master" "Server=...;Database=masterDB_prod;..."
    dotnet user-secrets set "Database:B2A"    "Server=...;Database=KhataBtoA_prod;..."
    dotnet user-secrets set "Jwt:Key"         "<48+ random bytes, base64>"
    dotnet run

In production use environment variables instead. The app refuses to start if a connection
string is missing, if `Jwt:Key` is shorter than 32 bytes, or if it is still `CHANGE_ME`.

`Otp:Sender` is `Development` by default, which **logs the OTP instead of sending it** and
is refused outside the Development environment. Set it to `SmsGateway` and fill the `Sms`
section (all secrets) to use BBMP's real gateway.

## Test account

`mst_Officer` row `Ofcr_Id = 11320`, mobile `9000000001`, role 116, corp 556 / zone 102 /
ward 54 (Hoodi, ~638 applications). Synthetic - not a real person, safe to delete.

## Things found in the existing system that need BBMP's attention

These are not API bugs. They were discovered while wiring the procedures up, and each one
will bite someone later.

1. **32 procedures and functions hardcode `masterDB_prod` as a three-part name.**
   27 in `KhataBtoA_prod`, 5 in `masterDB_prod`, including `fn_GetGuidanceValueExists`
   (reached from the fetch), `USP_S_GetMasterStreetDetails` and
   `USP_IU_BtoA_SiteRoadDetails_Officer`. They fail outright on any date-stamped copy such
   as `masterDB_prod_30072026`, which is why the dev box cannot run them. Two-part names
   would make every copy work.

2. **`usp_s_GetZonesOrWardsByCorpId` is unusable for the same reason**, so the zone and
   ward lookups here read `mst_AROMapping` directly.

3. **The OTP is six digits, but the Flutter app expects four** (`otp_page.dart:28`,
   `_otpLength = 4`). `USP_I_OTP` hardcodes test values `999999` and `673489`, and
   **overrides whatever the caller generated** for those numbers - so for those accounts
   the code that gets stored is not the code the API sent.

4. **`USP_I_OTP` and `USP_S_Officer_ValidateOTP` disagree about a column.** The insert
   writes the device id into `OTP_Tran.DeviceId`, but the validate filters on
   `DeviceId = @Source`. The same value therefore has to be passed as `@DeviceId` on send
   and as `@Source` on verify or nothing ever validates. This API does that deliberately;
   see the comment in `OfficerRepository.StoreOtpAsync`.

5. **`USP_S_Officer_ValidateOTP` never marks an OTP consumed.** It filters on
   `ACTIVE IS NULL` but nothing sets `ACTIVE`, so a valid code appears replayable for its
   whole window - and that window is **100 minutes**, which is long for a login code.

6. **`USP_S_Officer_ValidateOTP` returns `BBMPWardName` but never `BBMPWardId`.** For
   role 116 this does not matter because `Ofcr_WardId` is populated, but for roles where it
   is null the jurisdiction list can be displayed and not acted on. Adding the column would
   let sign-in alone give the app everything it needs.

7. **Officer lockout already lives in SQL** - four failed attempts and
   `USP_S_Officer_ValidateOTP` deactivates the account. Anything calling it must avoid
   retrying with a wrong code. This API does not duplicate the counter.

8. **`USP_S_BtoA_GpsDashData_v1` has no paging parameters**, so a ward is returned whole.
   It also returns a different column list per `@p_Level` branch. Fine at current volumes
   (57 actionable records in the busiest ward tested), but it will need `OFFSET/FETCH`
   before this is exposed to a large ward over a mobile connection.

9. **Credentials.** The `sa` login is a server-wide sysadmin, and the same username and
   password appear in clear text in `D:\FeeCalculationApi\Api\appsettings.json` and three
   published output folders. A dedicated login with `EXECUTE` on only the procedures this
   API calls, plus `SELECT` on `mst_AROMapping`, would stop a compromise of this service
   from reaching the payment gateway database.

## Design notes

- **One response envelope** for every endpoint, replacing the five shapes the legacy
  contract used. All four ASP.NET paths that can emit a body without reaching a controller
  (exception handler, model-state 400, JWT challenge, status-code pages) are routed through
  `ErrorEnvelopeWriter`, so the client never receives RFC 9457 ProblemDetails it cannot parse.
- **`retryable` and `recoverable`** on every response. The Flutter client parks permanently
  failed records and retries transient ones forever, so this distinction decides whether a
  day of survey work survives. `401` is reported *retryable* on purpose.
- **Nulls are always serialized.** On this contract `null` means "not answered" and is
  distinct from `0`; omitting the key would collapse the two.
- **Officer identity comes from the token, never the body**, and a role-116 officer is
  refused any ward but their own.
