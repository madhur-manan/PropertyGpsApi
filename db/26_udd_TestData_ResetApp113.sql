/*
    TEST DATA: undo the 5 Oct 2026 emulator submit of App 113 (display 202510150000113).

    APPLIES TO UDD_KHATABTOA_TEST ON THE LAPTOP ONLY. There is no USE line: open
    UDD_KHATABTOA_TEST first. The guard refuses anything else.

    WHY. That submit was the first to pass the e-Khata trigger (db/23), but it was made
    while every road card showed the same road (fixed 2026-10-05, see db/20 and
    PropertyMapper): the card marked "correct" filed road id 0 / no name over the
    citizen's private road 9320. Resetting lets the survey be done again with the fix.

    WHAT IT CHANGES (all rows written at 13:05:51 on 5 Oct by officer 11320):
      - deletes the officer rows the submit inserted:
            BtoA_MainApp_Officer          41368
            BtoA_SiteRoadDetails_Officer  50267, 50268, 50269
            BtoA_StatusDetail_Officer     68819
      - restores, from the history the update triggers wrote, the three rows it updated:
            BtoAMainApp                   App 113: App_Status 13 -> 10, App_AdditionalInfo, App_UDte
            BtoA_Status_Master_Tran       2670: status and officer columns
            BtoA_Architect_AssignedApp    4186: Completed -> Pending (still held by 11320)
    Every row is copied to ZZ_PropertyGpsApi_Reset113_* first, so section 3 can put it back.
    The update and delete triggers add their usual history rows; nothing is disabled.

    The phone keeps its own copy of App 113 as completed; see the note at the end.

    Sections: 1 guard + preview, 2 apply, 3 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS, which a THROW would not)
-------------------------------------------------------------------------------
IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
   OR ISNULL(CAST(SERVERPROPERTY('InstanceName') AS sysname), N'') <> N'MSSQLSERVER2019'
BEGIN
    RAISERROR('Refusing to run: this script is for UDD_KHATABTOA_TEST on the laptop only.', 16, 1);
    SET NOEXEC ON;
END
GO

SELECT 'now' AS [when], App_Id, App_Status, App_UDte FROM dbo.BtoAMainApp WHERE App_Id = 113;
SELECT Assign_Id, Arch_Id, Status, IsActive, CompletedDate FROM dbo.BtoA_Architect_AssignedApp WHERE Assign_Id = 4186;
GO

-------------------------------------------------------------------------------
-- 2. APPLY
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @mainHist int = (SELECT MAX(AppHist_Id) FROM dbo.BtoAMainApp_Hist WHERE App_Id = 113);
DECLARE @stsHist  int = (SELECT MAX(Sts_TranId_HistId) FROM dbo.BtoA_Status_Master_Tran_Hist WHERE Sts_TranId = 2670);
DECLARE @asgHist  int = (SELECT MAX(Assign_HistId) FROM dbo.BtoA_Architect_AssignedApp_Hist WHERE Assign_Id = 4186);

-- Refuse unless every row is still exactly as the submit left it, and the history holds
-- the state from before it. Run twice, the second run stops here.
IF NOT EXISTS (SELECT 1 FROM dbo.BtoAMainApp WHERE App_Id = 113 AND App_Status = 13)
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoAMainApp_Hist WHERE AppHist_Id = @mainHist AND App_Status = 10)
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_Status_Master_Tran WHERE Sts_TranId = 2670 AND Sts_MainAppId = 113 AND Sts_MainAppStatus = 13)
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_Status_Master_Tran_Hist WHERE Sts_TranId_HistId = @stsHist AND Sts_MainAppStatus = 10)
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_Architect_AssignedApp WHERE Assign_Id = 4186 AND AppId = 113 AND Status = 'Completed')
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_Architect_AssignedApp_Hist WHERE Assign_HistId = @asgHist AND Status = 'Pending')
   OR (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_RowId IN (50267, 50268, 50269) AND Ofcr_App_Id = 113) <> 3
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_RowId = 68819 AND Ofcr_App_Id = 113)
   OR NOT EXISTS (SELECT 1 FROM dbo.BtoA_MainApp_Officer WHERE Ofcr_RowId = 41368 AND Ofcr_ApplicationDisplayId = '202510150000113')
    THROW 50002, 'App 113 is not in the state this script expects (already reset, or changed since). Nothing was changed.', 1;

-- Backups.
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_MainApp        FROM dbo.BtoAMainApp                  WHERE App_Id = 113;
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_StatusMaster   FROM dbo.BtoA_Status_Master_Tran      WHERE Sts_TranId = 2670;
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_Assignment     FROM dbo.BtoA_Architect_AssignedApp   WHERE Assign_Id = 4186;
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_OfficerMain    FROM dbo.BtoA_MainApp_Officer         WHERE Ofcr_RowId = 41368;
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_OfficerRoads   FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_RowId IN (50267, 50268, 50269);
SELECT * INTO dbo.ZZ_PropertyGpsApi_Reset113_OfficerStatus  FROM dbo.BtoA_StatusDetail_Officer    WHERE Ofcr_RowId = 68819;

-- Restore what the submit updated.
UPDATE ap
   SET ap.App_Status = h.App_Status,
       ap.App_AdditionalInfo = h.App_AdditionalInfo,
       ap.App_UDte = h.App_UDte
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoAMainApp_Hist h ON h.AppHist_Id = @mainHist
WHERE ap.App_Id = 113;

UPDATE s
   SET s.Sts_MainAppStatus       = h.Sts_MainAppStatus,
       s.Sts_PrevStatusId        = h.Sts_PrevStatusId,
       s.Sts_PrevOfficerId       = h.Sts_PrevOfficerId,
       s.Sts_PrevOfficerRoleId   = h.Sts_PrevOfficerRoleId,
       s.Sts_PrevOfficerName     = h.Sts_PrevOfficerName,
       s.Sts_PrevOfficerMobileNo = h.Sts_PrevOfficerMobileNo,
       s.Sts_CurrStatusId        = h.Sts_CurrStatusId,
       s.Sts_CurrOfficerId       = h.Sts_CurrOfficerId,
       s.Sts_CurrOfficerRoleId   = h.Sts_CurrOfficerRoleId,
       s.Sts_CurrOfficerName     = h.Sts_CurrOfficerName,
       s.Sts_CurrOfficerMobileNo = h.Sts_CurrOfficerMobileNo,
       s.Udt                     = h.Udt,
       s.Sts_rejectedID          = h.Sts_rejectedID,
       s.Sts_rejectedText        = h.Sts_rejectedText,
       s.Sts_comments            = h.Sts_comments
FROM dbo.BtoA_Status_Master_Tran s
JOIN dbo.BtoA_Status_Master_Tran_Hist h ON h.Sts_TranId_HistId = @stsHist
WHERE s.Sts_TranId = 2670;

UPDATE a
   SET a.Status        = h.Status,
       a.CompletedDate = h.CompletedDate,
       a.IsActive      = h.IsActive,
       a.UBy           = h.UBy,
       a.UDate         = h.UDate,
       a.URole         = h.URole,
       a.Remarks       = h.Remarks
FROM dbo.BtoA_Architect_AssignedApp a
JOIN dbo.BtoA_Architect_AssignedApp_Hist h ON h.Assign_HistId = @asgHist
WHERE a.Assign_Id = 4186;

-- Remove what the submit inserted.
DELETE FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_RowId IN (50267, 50268, 50269);
DELETE FROM dbo.BtoA_StatusDetail_Officer    WHERE Ofcr_RowId = 68819;
DELETE FROM dbo.BtoA_MainApp_Officer         WHERE Ofcr_RowId = 41368;

SELECT 'after' AS [when], App_Id, App_Status, App_UDte FROM dbo.BtoAMainApp WHERE App_Id = 113;
SELECT Assign_Id, Arch_Id, Status, IsActive, CompletedDate FROM dbo.BtoA_Architect_AssignedApp WHERE Assign_Id = 4186;

COMMIT;
GO

SET NOEXEC OFF;
GO

/*
-------------------------------------------------------------------------------
-- 3. UNDO - put the submit back exactly as it was, from the ZZ_ backups, then drop them.
--    (Identity values are kept with IDENTITY_INSERT.)
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

UPDATE ap SET ap.App_Status = b.App_Status, ap.App_AdditionalInfo = b.App_AdditionalInfo, ap.App_UDte = b.App_UDte
FROM dbo.BtoAMainApp ap JOIN dbo.ZZ_PropertyGpsApi_Reset113_MainApp b ON b.App_Id = ap.App_Id;

UPDATE s SET s.Sts_MainAppStatus = b.Sts_MainAppStatus, s.Sts_PrevStatusId = b.Sts_PrevStatusId,
       s.Sts_PrevOfficerId = b.Sts_PrevOfficerId, s.Sts_PrevOfficerRoleId = b.Sts_PrevOfficerRoleId,
       s.Sts_PrevOfficerName = b.Sts_PrevOfficerName, s.Sts_PrevOfficerMobileNo = b.Sts_PrevOfficerMobileNo,
       s.Sts_CurrStatusId = b.Sts_CurrStatusId, s.Sts_CurrOfficerId = b.Sts_CurrOfficerId,
       s.Sts_CurrOfficerRoleId = b.Sts_CurrOfficerRoleId, s.Sts_CurrOfficerName = b.Sts_CurrOfficerName,
       s.Sts_CurrOfficerMobileNo = b.Sts_CurrOfficerMobileNo, s.Udt = b.Udt, s.Sts_rejectedID = b.Sts_rejectedID,
       s.Sts_rejectedText = b.Sts_rejectedText, s.Sts_comments = b.Sts_comments
FROM dbo.BtoA_Status_Master_Tran s JOIN dbo.ZZ_PropertyGpsApi_Reset113_StatusMaster b ON b.Sts_TranId = s.Sts_TranId;

UPDATE a SET a.Status = b.Status, a.CompletedDate = b.CompletedDate, a.IsActive = b.IsActive,
       a.UBy = b.UBy, a.UDate = b.UDate, a.URole = b.URole, a.Remarks = b.Remarks
FROM dbo.BtoA_Architect_AssignedApp a JOIN dbo.ZZ_PropertyGpsApi_Reset113_Assignment b ON b.Assign_Id = a.Assign_Id;

-- The three officer tables: re-insert with their original ids. List the columns
-- explicitly (SELECT * into an identity table needs a column list); generate it with
--   SELECT STRING_AGG(QUOTENAME(name), ', ') FROM sys.columns WHERE object_id = OBJECT_ID('dbo.<table>')
-- then: SET IDENTITY_INSERT dbo.<table> ON; INSERT ... SELECT ... FROM dbo.ZZ_...; SET IDENTITY_INSERT dbo.<table> OFF;

COMMIT;

DROP TABLE dbo.ZZ_PropertyGpsApi_Reset113_MainApp, dbo.ZZ_PropertyGpsApi_Reset113_StatusMaster,
           dbo.ZZ_PropertyGpsApi_Reset113_Assignment, dbo.ZZ_PropertyGpsApi_Reset113_OfficerMain,
           dbo.ZZ_PropertyGpsApi_Reset113_OfficerRoads, dbo.ZZ_PropertyGpsApi_Reset113_OfficerStatus;
*/

/*
    THE PHONE. The emulator still holds App 113 as a completed, uploaded survey, and the
    ward sync leaves that alone (it is the officer's own work). To survey it again there,
    clear the app's storage (Settings > Apps > Property GPS > Storage > Clear storage),
    sign in, and run Fetch / Update: everything comes down fresh, one card per road.
*/
