/*
    Test data for N2 on the TEST server: QC returns one surveyed test application to the RIs,
    exactly as the QC portal will on "Return to RI" (docs/DBA_Request.md D1).

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.

    Which application: by default the newest one that
      - is in ward 102 / 54 (zone 102), active, processing fee paid, with an active site row,
        and the newest application of its EPID (so the phones receive it);
      - is at App_Status 13 or 15 (an RI survey received, waiting for QC);
      - whose last RI survey was sent by a TEST officer (11320, or one created by db/31).
    Section 1 lists the candidates. To pick a different one, set @AppId in section 2.

    What it writes (section 2, one transaction):
      - BtoA_StatusDetail_Officer: one QC row - CRole 118, Status_Id 12, 'RETURN_TO_RI',
        Status_Remark = the test reason the RI will see, Status_Active 1;
      - BtoAMainApp.App_Status = 400.
    Nothing else: the earlier officer's allotment and survey rows stay as they are.
    Before that, it saves App_Status and the application's allotment rows to two ZZ_ tables,
    so section 3 can put them back.

    Then, on the phones (app 1.1.6): Fetch / Update in ward 54 shows the application as
    "QC Returned" with the reason, free for either test officer to allot.

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
-- 1. Checks (read-only): the candidates, newest survey first
-------------------------------------------------------------------------------
DECLARE @ZoneId int = 102, @WardId int = 54;

WITH ward AS (
    SELECT ap.App_Id, ap.App_DisplayId, ap.App_Status,
           rn = ROW_NUMBER() OVER (PARTITION BY ap.App_MotherEPID ORDER BY ap.App_Id DESC)
    FROM dbo.BtoAMainApp ap
    JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
    WHERE ap.App_Active = 1 AND ap.isProcessingFeePaid = 1 AND ISNULL(ap.App_MotherEPID, '') <> ''
      AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId
      AND EXISTS (SELECT 1 FROM dbo.BtoA_SiteDtls sd WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
)
SELECT TOP (10) w.App_Id, w.App_DisplayId, w.App_Status, LastRiOfficer = ri.CBy, LastRiRole = ri.CRole,
       LastRiSurveyOn = ri.CDte
FROM ward w
CROSS APPLY (SELECT TOP (1) s.CBy, s.CRole, s.CDte FROM dbo.BtoA_StatusDetail_Officer s
             WHERE s.Ofcr_App_Id = w.App_Id AND s.Status_Active = 1 AND s.CRole IN (116, 117)
             ORDER BY s.CDte DESC, s.Ofcr_RowId DESC) ri
WHERE w.rn = 1 AND w.App_Status IN (13, 15)
  AND EXISTS (SELECT 1 FROM masterDB_prod.dbo.mst_Officer o
              WHERE o.Ofcr_Id = ri.CBy
                AND (o.Ofcr_Id = 11320 OR o.Ofcr_AdditionalInfo LIKE N'PROPERTY GPS TEST OFFICER (db/31%'))
  AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_StatusDetail_Officer q
                  WHERE q.Ofcr_App_Id = w.App_Id AND q.CRole NOT IN (116, 117) AND q.CDte >= ri.CDte)
ORDER BY ri.CDte DESC;

-- Anything already returned by an earlier run (section 3 undoes it).
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_QcReturnTest') IS NOT NULL
    SELECT AlreadyReturnedByThisScript = App_Id, WasStatus = App_Status, BackedUpOn
    FROM dbo.ZZ_PropertyGpsApi_QcReturnTest;
GO

-------------------------------------------------------------------------------
-- 2. Apply
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_QcReturnTest') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_QcReturnTest
    (
        App_Id         int PRIMARY KEY,
        App_Status     int NULL,
        QcStatusRowId  int NULL,
        MaxAssignId    int NULL,
        BackedUpOn     datetime NOT NULL CONSTRAINT DF_ZZ_PgpsQcReturnTest DEFAULT (GETDATE())
    );
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_QcReturnTest_Assign') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_QcReturnTest_Assign
    (
        Assign_Id     int PRIMARY KEY,
        AppId         int NOT NULL,
        IsActive      bit NULL,
        Status        nvarchar(30) NULL,
        Remarks       nvarchar(max) NULL,
        CompletedDate datetime NULL,
        UBy           bigint NULL,
        UDate         datetime NULL,
        URole         int NULL
    );
GO

SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @AppId  int = NULL;   -- NULL = the first candidate of section 1; or set an App_Id
    DECLARE @Reason nvarchar(400) =
        N'TEST: The GPS point is on the road, not the plot. Recheck the location and the road width.';
    DECLARE @ZoneId int = 102, @WardId int = 54;

    IF EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_QcReturnTest)
        THROW 50002, 'An application returned by this script is still recorded: run section 3 (undo) first.', 1;

    DECLARE @candidates TABLE (App_Id int PRIMARY KEY, App_DisplayId nvarchar(50), LastRiOn datetime);
    WITH ward AS (
        SELECT ap.App_Id, ap.App_DisplayId, ap.App_Status,
               rn = ROW_NUMBER() OVER (PARTITION BY ap.App_MotherEPID ORDER BY ap.App_Id DESC)
        FROM dbo.BtoAMainApp ap
        JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
        WHERE ap.App_Active = 1 AND ap.isProcessingFeePaid = 1 AND ISNULL(ap.App_MotherEPID, '') <> ''
          AND md.MD_ZoneId = @ZoneId AND md.MD_WardId = @WardId
          AND EXISTS (SELECT 1 FROM dbo.BtoA_SiteDtls sd WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
    )
    INSERT INTO @candidates (App_Id, App_DisplayId, LastRiOn)
    SELECT w.App_Id, w.App_DisplayId, ri.CDte
    FROM ward w
    CROSS APPLY (SELECT TOP (1) s.CBy, s.CDte FROM dbo.BtoA_StatusDetail_Officer s
                 WHERE s.Ofcr_App_Id = w.App_Id AND s.Status_Active = 1 AND s.CRole IN (116, 117)
                 ORDER BY s.CDte DESC, s.Ofcr_RowId DESC) ri
    WHERE w.rn = 1 AND w.App_Status IN (13, 15)
      AND EXISTS (SELECT 1 FROM masterDB_prod.dbo.mst_Officer o
                  WHERE o.Ofcr_Id = ri.CBy
                    AND (o.Ofcr_Id = 11320 OR o.Ofcr_AdditionalInfo LIKE N'PROPERTY GPS TEST OFFICER (db/31%'))
      AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_StatusDetail_Officer q
                      WHERE q.Ofcr_App_Id = w.App_Id AND q.CRole NOT IN (116, 117) AND q.CDte >= ri.CDte);

    IF @AppId IS NULL
        SELECT TOP (1) @AppId = App_Id FROM @candidates ORDER BY LastRiOn DESC;
    IF @AppId IS NULL OR NOT EXISTS (SELECT 1 FROM @candidates WHERE App_Id = @AppId)
        THROW 50003, 'No such candidate: it must be a test officer''s survey in ward 54, waiting for QC (see section 1). Nothing was changed.', 1;

    -- What section 3 puts back.
    INSERT INTO dbo.ZZ_PropertyGpsApi_QcReturnTest (App_Id, App_Status, MaxAssignId)
    SELECT ap.App_Id, ap.App_Status,
           (SELECT MAX(a.Assign_Id) FROM dbo.BtoA_Architect_AssignedApp a WHERE a.AppId = ap.App_Id)
    FROM dbo.BtoAMainApp ap WHERE ap.App_Id = @AppId;

    INSERT INTO dbo.ZZ_PropertyGpsApi_QcReturnTest_Assign
           (Assign_Id, AppId, IsActive, Status, Remarks, CompletedDate, UBy, UDate, URole)
    SELECT Assign_Id, AppId, IsActive, Status, Remarks, CompletedDate, UBy, UDate, URole
    FROM dbo.BtoA_Architect_AssignedApp WHERE AppId = @AppId;

    -- 1. QC's row, as the portal writes it.
    INSERT INTO dbo.BtoA_StatusDetail_Officer
           (Ofcr_App_Id, Ofcr_ApplicationDisplayId, Status_Date, Status_Id, Status_Remark,
            Status_Value, CBy, CDte, CRole, Status_Active, isAutoEscalated)
    SELECT App_Id, App_DisplayId, GETDATE(), 12, @Reason, N'RETURN_TO_RI', 1, GETDATE(), 118, 1, 0
    FROM dbo.BtoAMainApp WHERE App_Id = @AppId;

    UPDATE dbo.ZZ_PropertyGpsApi_QcReturnTest SET QcStatusRowId = SCOPE_IDENTITY() WHERE App_Id = @AppId;

    -- 2. App_Status 400. The e-Khata trigger writes to a database this server lacks; it is
    --    switched off only for this one update if it is on, and switched back after.
    DECLARE @khataTriggerOn bit =
        CASE WHEN OBJECTPROPERTY(OBJECT_ID('dbo.trg_BtoAMainApp_UpdateNewKhata'), 'ExecIsTriggerDisabled') = 0
             THEN 1 ELSE 0 END;
    IF @khataTriggerOn = 1 DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

    UPDATE dbo.BtoAMainApp SET App_Status = 400 WHERE App_Id = @AppId;

    IF @khataTriggerOn = 1 ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

COMMIT TRAN;
GO

-- Result: expect App_Status 400 and QC's reason.
SELECT ap.App_Id, ap.App_DisplayId, ap.App_Status, WasStatus = b.App_Status, QcReason = s.Status_Remark
FROM dbo.ZZ_PropertyGpsApi_QcReturnTest b
JOIN dbo.BtoAMainApp ap ON ap.App_Id = b.App_Id
LEFT JOIN dbo.BtoA_StatusDetail_Officer s ON s.Ofcr_RowId = b.QcStatusRowId;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 3. Undo
--    Still at 400 (nobody has sent the correction yet): removes QC's row, puts App_Status and
--    the allotment rows back as they were, and removes any allotment made since.
--    Already corrected and sent (no longer 400): that is a real survey now - it is left as it
--    is, and only QC's test row is kept as history. Either way the ZZ_ records are cleared.
--    Run it on the same server and database (the guard at the top must pass first).
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DECLARE @AppId int, @was int, @qcRow int, @maxAssign int;
    SELECT @AppId = App_Id, @was = App_Status, @qcRow = QcStatusRowId, @maxAssign = MaxAssignId
    FROM dbo.ZZ_PropertyGpsApi_QcReturnTest;

    IF @AppId IS NOT NULL AND (SELECT App_Status FROM dbo.BtoAMainApp WHERE App_Id = @AppId) = 400
    BEGIN
        DELETE FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_RowId = @qcRow AND Ofcr_App_Id = @AppId;

        DELETE FROM dbo.BtoA_Architect_AssignedApp
        WHERE AppId = @AppId AND Assign_Id > ISNULL(@maxAssign, 0);

        UPDATE a SET a.IsActive = b.IsActive, a.Status = b.Status, a.Remarks = b.Remarks,
                     a.CompletedDate = b.CompletedDate, a.UBy = b.UBy, a.UDate = b.UDate, a.URole = b.URole
        FROM dbo.BtoA_Architect_AssignedApp a
        JOIN dbo.ZZ_PropertyGpsApi_QcReturnTest_Assign b ON b.Assign_Id = a.Assign_Id;

        DECLARE @khataTriggerOn bit =
            CASE WHEN OBJECTPROPERTY(OBJECT_ID('dbo.trg_BtoAMainApp_UpdateNewKhata'), 'ExecIsTriggerDisabled') = 0
                 THEN 1 ELSE 0 END;
        IF @khataTriggerOn = 1 DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;
        UPDATE dbo.BtoAMainApp SET App_Status = @was WHERE App_Id = @AppId;
        IF @khataTriggerOn = 1 ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

        SELECT Undone = @AppId, StatusNow = @was;
    END
    ELSE
        SELECT LeftAsCorrected = @AppId;

    DELETE FROM dbo.ZZ_PropertyGpsApi_QcReturnTest_Assign;
    DELETE FROM dbo.ZZ_PropertyGpsApi_QcReturnTest;
COMMIT TRAN;
*/
