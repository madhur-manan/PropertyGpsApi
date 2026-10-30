/*
    Property GPS: a log of every RI call - fetch, submit, allot / unassign, sign-in - with its
    request and response JSON (user, 2026-10-30). Modelled on the SmsLog table.

    APPLIES TO UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE), or to the laptop copy
    UDD_KHATABTOA_TEST on localhost\MSSQLSERVER2019. The guard refuses anything else. For LIVE,
    hand this script to the DBA.

    WHAT IT DOES: creates dbo.GpsApiLog and three indexes, only if they are not there - running
    it twice changes nothing. Nothing else is touched.

    The API writes one row per call (ApiLog:Enabled in its appsettings.json). Until this script
    is run the API simply logs a warning and carries on.

    THE ROWS HOLD PERSONAL DATA (owner names, mobiles, survey answers - as the survey tables
    do). The OTP and the sign-in token are blanked by the API before they are written.

    Sections: 1 guard, 2 the table, 3 indexes, 4 check (read-only), 5 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS too)
-------------------------------------------------------------------------------
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
       Instance = CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'));

IF NOT (   (DB_NAME() = N'UDD_KHATABTOA'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) LIKE N'DB-NEW-KHATA-TE%')
        OR (DB_NAME() = N'UDD_KHATABTOA_TEST'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')) = N'MSSQLSERVER2019'))
BEGIN
    RAISERROR(N'Refusing to run: open UDD_KHATABTOA on the test DB server, or UDD_KHATABTOA_TEST on the laptop, first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. THE TABLE
-------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.GpsApiLog', N'U') IS NULL
    CREATE TABLE dbo.GpsApiLog
    (
        GpsApiLog_Id                   bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_GpsApiLog PRIMARY KEY,
        GpsApiLog_AppId                bigint         NULL,     -- App_Id (submit, allot, unassign)
        GpsApiLog_ApplicationDisplayId nvarchar(50)   NULL,     -- App_DisplayId (submit)
        GpsApiLog_OfficerId            bigint         NULL,     -- the RI; NULL before sign-in
        GpsApiLog_MobileNumber         varchar(15)    NULL,
        GpsApiLog_Action               varchar(50)    NOT NULL, -- FETCH, SUBMIT, ALLOT, UNASSIGN, OTP_SEND, OTP_VERIFY
        GpsApiLog_Endpoint             varchar(200)   NOT NULL,
        GpsApiLog_UlbCode              int            NULL,
        GpsApiLog_WardNumber           nvarchar(20)   NULL,
        GpsApiLog_RequestBody          nvarchar(max)  NOT NULL, -- JSON, decrypted; OTP blanked; photos by name and size
        GpsApiLog_ResponseBody         nvarchar(max)  NOT NULL, -- JSON; token blanked; a fetch is summarised
        GpsApiLog_HttpStatus           int            NOT NULL,
        GpsApiLog_IsSuccess            bit            NOT NULL,
        GpsApiLog_ErrorCode            varchar(50)    NULL,
        GpsApiLog_DurationMs           int            NULL,
        GpsApiLog_DeviceId             nvarchar(64)   NULL,
        GpsApiLog_AppVersion           varchar(20)    NULL,
        GpsApiLog_TraceId              varchar(64)    NULL,     -- the same id as in the API's own log file
        GpsApiLog_AdditionalInfo       nvarchar(200)  NULL,
        GpsApiLog_Remarks              nvarchar(200)  NULL,
        GpsApiLog_CreatedOn            datetime       NOT NULL CONSTRAINT DF_GpsApiLog_CreatedOn DEFAULT (GETDATE())
    );
GO

-------------------------------------------------------------------------------
-- 3. INDEXES - "this application", "this RI today", "by date"
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.GpsApiLog') AND name = N'IX_GpsApiLog_CreatedOn')
    CREATE INDEX IX_GpsApiLog_CreatedOn ON dbo.GpsApiLog (GpsApiLog_CreatedOn);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.GpsApiLog') AND name = N'IX_GpsApiLog_AppId')
    CREATE INDEX IX_GpsApiLog_AppId ON dbo.GpsApiLog (GpsApiLog_AppId) WHERE GpsApiLog_AppId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.GpsApiLog') AND name = N'IX_GpsApiLog_Officer')
    CREATE INDEX IX_GpsApiLog_Officer ON dbo.GpsApiLog (GpsApiLog_OfficerId, GpsApiLog_CreatedOn);
GO

-------------------------------------------------------------------------------
-- 4. CHECK (read-only): the columns, and the newest rows once the API writes them
-------------------------------------------------------------------------------
SELECT c.column_id, c.name, TYPE_NAME(c.user_type_id) AS type_name, c.is_nullable
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dbo.GpsApiLog')
ORDER BY c.column_id;

SELECT TOP (20) GpsApiLog_Id, GpsApiLog_Action, GpsApiLog_AppId, GpsApiLog_OfficerId, GpsApiLog_HttpStatus,
       GpsApiLog_IsSuccess, GpsApiLog_ErrorCode, GpsApiLog_DurationMs, GpsApiLog_CreatedOn
FROM dbo.GpsApiLog
ORDER BY GpsApiLog_Id DESC;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 5. UNDO (commented out) - removes the table and every log row in it.
-------------------------------------------------------------------------------
/*
DROP TABLE IF EXISTS dbo.GpsApiLog;
*/
