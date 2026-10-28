/*
    Test data for Preview Notice on the TEST server: App 12 gets EPID 3547973374, an EPID
    BBMP's notice system knows (its test notice 21), so the notice renders on stage. Our
    synthetic 9990000xxx EPIDs get "Notice generation failed - EPID missing or invalid".

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.

    What it writes (section 2, one transaction), for App 12 only:
      - BtoAMainApp.App_MotherEPID
      - BtoA_EPIDMetaData.MD_MotherEPID - the ward sync joins the two on the EPID, so both
        must change together or the application drops off the phones' list.
    Before that it saves the old EPID to ZZ_PropertyGpsApi_App12Epid (kept on a re-run, so
    it always holds the original), and section 3 puts it back.
    Left alone: App 12's earlier survey rows (BtoA_MainApp_Officer.Ofcr_MotherEPID); only
    the History screen shows that EPID, and their update trigger would write history rows.

    Sections: 1 checks (read-only), 2 apply, 3 undo (commented out).
*/

USE UDD_KHATABTOA;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1. Checks (read-only)
-------------------------------------------------------------------------------
DECLARE @AppId int = 12, @NewEpid nvarchar(30) = N'3547973374';

SELECT ap.App_Id, ap.App_DisplayId, ap.App_MotherEPID, ap.App_Status, ap.App_Active
FROM dbo.BtoAMainApp ap WHERE ap.App_Id = @AppId;

SELECT md.MD_APP_ID, md.MD_MotherEPID, md.MD_ZoneId, md.MD_WardId
FROM dbo.BtoA_EPIDMetaData md WHERE md.MD_APP_ID = @AppId;

-- Other applications on this EPID. On the laptop copy there is one, App 6747 in 108/53; the
-- test server is likely the same. That is fine: the phones get the newest application of
-- an EPID WITHIN THE WARD (USP_S_GpsWardSync, db/20), so only one in App 12's own ward
-- (SameWardAsApp12 = 1) could hide it - section 2 refuses then.
SELECT OtherAppOnThisEpid = ap.App_Id, ap.App_Status, ap.App_Active, md.MD_ZoneId, md.MD_WardId,
       SameWardAsApp12 = CASE WHEN EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData m12
                                          WHERE m12.MD_APP_ID = @AppId
                                            AND m12.MD_ZoneId = md.MD_ZoneId AND m12.MD_WardId = md.MD_WardId)
                              THEN 1 ELSE 0 END
FROM dbo.BtoAMainApp ap
LEFT JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE ap.App_MotherEPID = @NewEpid AND ap.App_Id <> @AppId;
GO

-------------------------------------------------------------------------------
-- 2. Apply
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @AppId int = 12, @NewEpid nvarchar(30) = N'3547973374';

IF EXISTS (SELECT 1
           FROM dbo.BtoAMainApp ap
           JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
           JOIN dbo.BtoA_EPIDMetaData m12 ON m12.MD_APP_ID = @AppId
                                         AND m12.MD_ZoneId = md.MD_ZoneId AND m12.MD_WardId = md.MD_WardId
           WHERE ap.App_MotherEPID = @NewEpid AND ap.App_Id <> @AppId)
    THROW 50001, 'Another application in App 12''s ward already has this EPID - see section 1. Nothing changed.', 1;

IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_App12Epid') IS NULL
    SELECT App_Id = ap.App_Id, OldEpid = ap.App_MotherEPID, SavedOn = SYSDATETIME()
    INTO dbo.ZZ_PropertyGpsApi_App12Epid
    FROM dbo.BtoAMainApp ap WHERE ap.App_Id = @AppId;

DECLARE @OldEpid nvarchar(30) = (SELECT OldEpid FROM dbo.ZZ_PropertyGpsApi_App12Epid WHERE App_Id = @AppId);
IF @OldEpid IS NULL
    THROW 50002, 'App 12 or its saved EPID was not found. Nothing changed.', 1;

UPDATE dbo.BtoA_EPIDMetaData SET MD_MotherEPID = @NewEpid
WHERE MD_APP_ID = @AppId AND MD_MotherEPID = @OldEpid;

UPDATE dbo.BtoAMainApp SET App_MotherEPID = @NewEpid
WHERE App_Id = @AppId AND App_MotherEPID = @OldEpid;

COMMIT;

SELECT ap.App_Id, OldEpid = @OldEpid, ap.App_MotherEPID, md.MD_MotherEPID, ap.App_Status
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE ap.App_Id = @AppId;   -- one row: both tables now on 3547973374
GO

-------------------------------------------------------------------------------
-- 3. Undo (commented out): puts App 12's original EPID back.
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @AppId int = 12, @NewEpid nvarchar(30) = N'3547973374';
DECLARE @OldEpid nvarchar(30) = (SELECT OldEpid FROM dbo.ZZ_PropertyGpsApi_App12Epid WHERE App_Id = @AppId);
IF @OldEpid IS NULL THROW 50003, 'No saved EPID for App 12. Nothing changed.', 1;

UPDATE dbo.BtoA_EPIDMetaData SET MD_MotherEPID = @OldEpid WHERE MD_APP_ID = @AppId AND MD_MotherEPID = @NewEpid;
UPDATE dbo.BtoAMainApp SET App_MotherEPID = @OldEpid WHERE App_Id = @AppId AND App_MotherEPID = @NewEpid;
DROP TABLE dbo.ZZ_PropertyGpsApi_App12Epid;
COMMIT;
*/
