/*
    Test data for the fetch -> submit pipeline in KhataBtoA_prod_v1.

    APPLIES TO KhataBtoA_prod_v1 ONLY. The equivalent for the live database is
    TestData_ResetPushedFlag.sql; keep the two separate so neither can be run against the
    wrong server by accident.

    Why this is needed: every status-10 application in this database is already flagged
    IsPushedToGps = 1, so USP_S_GetAppDetails correctly returns nothing and the fetch
    endpoint answers with an empty list. That is the procedure working, not failing.

    Deliberately bounded to @HowMany records. Ward 102/54 alone holds 1,363 eligible
    applications; resetting all of them would hand a device an unrealistic first sync and
    churn a lot of state for no extra test value.

    Previous values are saved to ZZ_PropertyGpsApi_PushedFlagBackup_v1 first, so section 3
    restores each record exactly rather than blanket-setting them back to 1.
*/

USE KhataBtoA_prod_v1;
GO

IF DB_NAME() <> 'KhataBtoA_prod_v1'
    THROW 50001, 'Refusing to run: this script is for KhataBtoA_prod_v1 only.', 1;
GO

DECLARE @ZoneId  int = 102;   -- Mahadevapura
DECLARE @WardId  int = 54;    -- Hoodi, the test officer's ward
DECLARE @HowMany int = 25;

-------------------------------------------------------------------------------
-- 1. PREVIEW
-------------------------------------------------------------------------------
SELECT
    Eligible          = SUM(CASE WHEN ap.App_Status = 10 AND ISNULL(ap.isProcessingFeePaid,0) = 1 THEN 1 ELSE 0 END),
    CurrentlyUnpushed = SUM(CASE WHEN ap.App_Status = 10 AND ISNULL(ap.isProcessingFeePaid,0) = 1
                                  AND ISNULL(ap.IsPushedToGps,0) = 0 THEN 1 ELSE 0 END),
    AlreadySubmitted  = SUM(CASE WHEN ap.App_Status = 13 THEN 1 ELSE 0 END)
FROM BtoAMainApp ap
JOIN BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE ap.App_Active = 1 AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId;

-------------------------------------------------------------------------------
-- 2. APPLY - mark @HowMany of them undelivered
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1
    (
        App_Id            int PRIMARY KEY,
        IsPushedToGps     bit           NULL,
        PushedToGpsDate   datetime      NULL,
        PushedToGpsRemark nvarchar(max) NULL,
        BackedUpOn        datetime      NOT NULL CONSTRAINT DF_ZZ_PgpsBackup_v1 DEFAULT (GETDATE())
    );

BEGIN TRAN;

    ;WITH target AS (
        SELECT TOP (@HowMany) ap.App_Id, ap.IsPushedToGps, ap.PushedToGpsDate, ap.PushedToGpsRemark
        FROM BtoAMainApp ap
        JOIN BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
        JOIN [BtoA_SiteDtls ] sd  ON sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1
        WHERE ap.App_Active = 1
          AND ap.App_Status = 10
          AND ISNULL(ap.isProcessingFeePaid,0) = 1
          AND ISNULL(ap.IsPushedToGps,0) = 1          -- only touch ones currently marked pushed
          AND md.MD_ZoneId = @ZoneId
          AND md.MD_WardId = @WardId
        ORDER BY ap.App_Id DESC
    )
    INSERT INTO dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1 (App_Id, IsPushedToGps, PushedToGpsDate, PushedToGpsRemark)
    SELECT t.App_Id, t.IsPushedToGps, t.PushedToGpsDate, t.PushedToGpsRemark
    FROM target t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1 b WHERE b.App_Id = t.App_Id);

    UPDATE ap
       SET ap.IsPushedToGps     = 0,
           ap.PushedToGpsDate   = NULL,
           ap.PushedToGpsRemark = NULL
    FROM BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1 b ON b.App_Id = ap.App_Id;

    SELECT ResetCount = @@ROWCOUNT;

COMMIT TRAN;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - restore every backed-up record exactly, then clear the backup
-------------------------------------------------------------------------------
/*
BEGIN TRAN;
    UPDATE ap
       SET ap.IsPushedToGps     = b.IsPushedToGps,
           ap.PushedToGpsDate   = b.PushedToGpsDate,
           ap.PushedToGpsRemark = b.PushedToGpsRemark
    FROM BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1 b ON b.App_Id = ap.App_Id;
    SELECT RestoredCount = @@ROWCOUNT;
    DELETE FROM dbo.ZZ_PropertyGpsApi_PushedFlagBackup_v1;
COMMIT TRAN;
*/
