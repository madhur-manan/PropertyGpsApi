/*
    Test data for N2: QC returns App 113 to the RIs, as the QC portal will on "Return to RI".

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    App 113 (202510150000113) was surveyed by test officer 11320 and is at App_Status 13 with
    its assignment Completed. This does what the QC portal is asked to do (DBA_Request.md D1):
      - a QC status row (CRole 118, Status_Id 12, RETURN_TO_RI) carrying QC's remark;
      - App_Status 400.
    Nothing else: the earlier officer's completed assignment stays as it is - the API treats
    it as no longer holding a returned survey, and retires it when someone allots it.

    The e-Khata trigger is disabled inside the transaction, as in db/21 (it writes to a
    database that does not exist here).

    Sections: 1 preview, 2 apply, 3 undo (commented out).
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- 1. PREVIEW
-------------------------------------------------------------------------------
SELECT ap.App_Id, ap.App_DisplayId, ap.App_Status FROM dbo.BtoAMainApp ap WHERE ap.App_Id = 113;
SELECT Assign_Id, Arch_Id, Status, IsActive, Remarks FROM dbo.BtoA_Architect_AssignedApp WHERE AppId = 113 ORDER BY Assign_Id;
GO

-------------------------------------------------------------------------------
-- 2. APPLY
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_QcReturn113') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_QcReturn113
    (
        App_Id         int PRIMARY KEY,
        App_Status     int NULL,
        MaxStatusRowId int NULL,
        MaxAssignId    int NULL,
        BackedUpOn     datetime NOT NULL CONSTRAINT DF_ZZ_PgpsQcReturn113 DEFAULT (GETDATE())
    );
GO

SET XACT_ABORT ON;
BEGIN TRAN;

    IF EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_QcReturn113)
        THROW 50002, 'Already applied: run section 3 (undo) first.', 1;

    INSERT INTO dbo.ZZ_PropertyGpsApi_QcReturn113 (App_Id, App_Status, MaxStatusRowId, MaxAssignId)
    SELECT 113, App_Status,
           (SELECT MAX(Ofcr_RowId) FROM dbo.BtoA_StatusDetail_Officer),
           (SELECT MAX(Assign_Id) FROM dbo.BtoA_Architect_AssignedApp)
    FROM dbo.BtoAMainApp WHERE App_Id = 113;

    INSERT INTO dbo.BtoA_StatusDetail_Officer
           (Ofcr_App_Id, Ofcr_ApplicationDisplayId, Status_Date, Status_Id, Status_Remark,
            Status_Value, CBy, CDte, CRole, Status_Active, isAutoEscalated)
    SELECT App_Id, App_DisplayId, GETDATE(), 12,
           N'TEST: The GPS point is on the road, not the plot. Recheck the location and the road width.',
           N'RETURN_TO_RI', 1, GETDATE(), 118, 1, 0
    FROM dbo.BtoAMainApp WHERE App_Id = 113;

    DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;
    UPDATE dbo.BtoAMainApp SET App_Status = 400 WHERE App_Id = 113;
    ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

COMMIT TRAN;
GO

SELECT ap.App_Id, ap.App_Status, b.App_Status AS WasStatus
FROM dbo.ZZ_PropertyGpsApi_QcReturn113 b JOIN dbo.BtoAMainApp ap ON ap.App_Id = b.App_Id;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - back to before section 2, including anything a test resubmit wrote
--    (rows added since the backup are removed; rows it made inactive are made active again)
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DECLARE @status int, @maxStatus int, @maxAssign int, @since datetime;
    SELECT @status = App_Status, @maxStatus = MaxStatusRowId, @maxAssign = MaxAssignId, @since = BackedUpOn
    FROM dbo.ZZ_PropertyGpsApi_QcReturn113 WHERE App_Id = 113;

    DELETE FROM dbo.BtoA_StatusDetail_Officer WHERE Ofcr_App_Id = 113 AND Ofcr_RowId > @maxStatus;
    UPDATE dbo.BtoA_StatusDetail_Officer SET Status_Active = 1
     WHERE Ofcr_App_Id = 113 AND CRole IN (116, 117) AND Ofcr_RowId <= @maxStatus AND UDte >= @since;

    DELETE FROM dbo.BtoA_Architect_AssignedApp WHERE AppId = 113 AND Assign_Id > @maxAssign;
    UPDATE dbo.BtoA_Architect_AssignedApp SET IsActive = 1, Remarks = NULL
     WHERE AppId = 113 AND Assign_Id <= @maxAssign AND Remarks = 'Returned by QC';

    DISABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;
    UPDATE dbo.BtoAMainApp SET App_Status = @status WHERE App_Id = 113;
    ENABLE TRIGGER dbo.trg_BtoAMainApp_UpdateNewKhata ON dbo.BtoAMainApp;

    DELETE FROM dbo.ZZ_PropertyGpsApi_QcReturn113;
COMMIT TRAN;
-- Road rows and documents a test resubmit wrote are left: check them with db/27.
*/
