/*
    Test data for the fetch -> push-status pipeline.

    Makes a bounded number of applications in one ward look "not yet delivered to a device",
    so the fetch returns them and push-status can acknowledge them.

    Deliberately limited to @HowMany records rather than the whole ward. Ward 102/54 alone
    has 1,364 status-10 fee-paid applications; resetting all of them would change a lot of
    production state and hand the device an unrealistically large first sync.

    Run section 1 to preview, section 2 to apply, section 3 to undo.
*/

USE KhataBtoA_prod;
GO

DECLARE @ZoneId  int = 102;   -- Mahadevapura
DECLARE @WardId  int = 54;    -- Hoodi (the test officer's ward)
DECLARE @HowMany int = 25;

-------------------------------------------------------------------------------
-- 1. PREVIEW - what is eligible, and what is currently unpushed
-------------------------------------------------------------------------------
SELECT
    Eligible       = SUM(CASE WHEN ap.App_Status = 10 AND ISNULL(ap.isProcessingFeePaid,0) = 1 THEN 1 ELSE 0 END),
    CurrentlyUnpushed = SUM(CASE WHEN ap.App_Status = 10 AND ISNULL(ap.isProcessingFeePaid,0) = 1
                                  AND ISNULL(ap.IsPushedToGps,0) = 0 THEN 1 ELSE 0 END)
FROM BtoAMainApp ap
JOIN BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE ap.App_Active = 1 AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId;

-------------------------------------------------------------------------------
-- 2. APPLY - mark @HowMany of them undelivered
--
--    The previous values are saved to dbo.ZZ_PropertyGpsApi_PushedFlagBackup first, so
--    section 3 can restore each record exactly rather than blanket-setting them back to 1.
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_PushedFlagBackup') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_PushedFlagBackup
    (
        App_Id            int PRIMARY KEY,
        IsPushedToGps     bit          NULL,
        PushedToGpsDate   datetime     NULL,
        PushedToGpsRemark nvarchar(max) NULL,
        BackedUpOn        datetime     NOT NULL CONSTRAINT DF_ZZ_PgpsBackup DEFAULT (GETDATE())
    );

BEGIN TRAN;

    ;WITH target AS (
        SELECT TOP (@HowMany) ap.App_Id, ap.IsPushedToGps, ap.PushedToGpsDate, ap.PushedToGpsRemark
        FROM BtoAMainApp ap
        JOIN BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
        JOIN [BtoA_SiteDtls ] sd ON sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1
        WHERE ap.App_Active = 1
          AND ap.App_Status = 10
          AND ISNULL(ap.isProcessingFeePaid,0) = 1
          AND ISNULL(ap.IsPushedToGps,0) = 1          -- only touch ones currently marked pushed
          AND md.MD_ZoneId = @ZoneId
          AND md.MD_WardId = @WardId
        ORDER BY ap.App_Id DESC
    )
    INSERT INTO dbo.ZZ_PropertyGpsApi_PushedFlagBackup (App_Id, IsPushedToGps, PushedToGpsDate, PushedToGpsRemark)
    SELECT t.App_Id, t.IsPushedToGps, t.PushedToGpsDate, t.PushedToGpsRemark
    FROM target t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_PushedFlagBackup b WHERE b.App_Id = t.App_Id);

    UPDATE ap
       SET ap.IsPushedToGps     = 0,
           ap.PushedToGpsDate   = NULL,
           ap.PushedToGpsRemark = NULL
    FROM BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_PushedFlagBackup b ON b.App_Id = ap.App_Id;

    SELECT ResetCount = @@ROWCOUNT;

COMMIT TRAN;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - put every backed-up record back exactly as it was, then clear the backup
-------------------------------------------------------------------------------
/*
BEGIN TRAN;

    UPDATE ap
       SET ap.IsPushedToGps     = b.IsPushedToGps,
           ap.PushedToGpsDate   = b.PushedToGpsDate,
           ap.PushedToGpsRemark = b.PushedToGpsRemark
    FROM BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_PushedFlagBackup b ON b.App_Id = ap.App_Id;

    SELECT RestoredCount = @@ROWCOUNT;

    DELETE FROM dbo.ZZ_PropertyGpsApi_PushedFlagBackup;

COMMIT TRAN;
-- DROP TABLE dbo.ZZ_PropertyGpsApi_PushedFlagBackup;
*/
