/*
    Test data for ward sync: two applications "Returned to RI From QC for Re-verification"
    (App_Status 400) in the test officer's ward.

    APPLIES TO UDD_KHATABTOA_TEST ONLY. Nothing in this database is at 400 today, so without
    this the returned-from-QC path of USP_S_GpsWardSync and the app's "Pending + Returned
    from Qc" tab cannot be exercised.

    THE TRIGGER. trg_BtoAMainApp_UpdateNewKhata fires on any App_Status update and writes to
    ekhata_test.dbo.KRS. That database does not exist here (nor, on 2026-10-03, on the new
    server), so every App_Status update fails with "Invalid object name
    'ekhata_test.dbo.krs'". Section 2 disables that one trigger INSIDE its transaction and
    re-enables it before committing; if anything fails the rollback restores it too. Its
    work is irrelevant here: status 400 is not one of the codes it acts on.

    Previous values are saved to ZZ_PropertyGpsApi_ReturnFromQc_udd first, so section 3
    restores each record exactly.

    Sections: 1 preview, 2 apply, 3 undo (commented out).
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- 1. PREVIEW - the two newest workable applications in ward 102/54
-------------------------------------------------------------------------------
SELECT TOP (2) ap.App_Id, ap.App_DisplayId, ap.App_Status
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
WHERE ap.App_Active = 1 AND ap.isProcessingFeePaid = 1 AND ap.App_Status = 10
  AND md.MD_ZoneId = 102 AND md.MD_WardId = 54
  AND EXISTS (SELECT 1 FROM dbo.[BtoA_SiteDtls] sd WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
ORDER BY ap.App_Id DESC;
GO

-------------------------------------------------------------------------------
-- 2. APPLY
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd
    (
        App_Id     int PRIMARY KEY,
        App_Status int      NULL,
        BackedUpOn datetime NOT NULL CONSTRAINT DF_ZZ_PgpsReturnFromQc_udd DEFAULT (GETDATE())
    );
GO

SET XACT_ABORT ON;
BEGIN TRAN;

    INSERT INTO dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd (App_Id, App_Status)
    SELECT TOP (2) ap.App_Id, ap.App_Status
    FROM dbo.BtoAMainApp ap
    JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
    WHERE ap.App_Active = 1 AND ap.isProcessingFeePaid = 1 AND ap.App_Status = 10
      AND md.MD_ZoneId = 102 AND md.MD_WardId = 54
      AND EXISTS (SELECT 1 FROM dbo.[BtoA_SiteDtls] sd WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
      AND NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd b WHERE b.App_Id = ap.App_Id)
    ORDER BY ap.App_Id DESC;

    DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

    UPDATE ap
       SET ap.App_Status = 400
    FROM dbo.BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd b ON b.App_Id = ap.App_Id;

    SELECT ReturnedCount = @@ROWCOUNT;

    ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

COMMIT TRAN;
GO

SELECT b.App_Id, ap.App_DisplayId, ap.App_Status, b.App_Status AS WasStatus
FROM dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd b
JOIN dbo.BtoAMainApp ap ON ap.App_Id = b.App_Id;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - restore every backed-up status exactly, then clear the backup
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;
    UPDATE ap SET ap.App_Status = b.App_Status
    FROM dbo.BtoAMainApp ap
    JOIN dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd b ON b.App_Id = ap.App_Id;
    SELECT RestoredCount = @@ROWCOUNT;
    ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;
    DELETE FROM dbo.ZZ_PropertyGpsApi_ReturnFromQc_udd;
COMMIT TRAN;
*/
