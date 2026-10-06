/*
    Closes an officer's assignment when their survey is submitted.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    THE DEFECT. Submitting a survey never touched BtoA_Architect_AssignedApp: none of the
    submit procedures reference it, and USP_U_Status_Master_Tran - the legacy procedure that
    does close assignments - is not on this path. So the row stayed Status='Pending',
    IsActive=1 forever. USP_IU_Architect_AssignedApp refuses a third pending assignment
    ("You currently have two applications pending in your queue"), so every officer was
    permanently blocked after their second survey and only a DBA could release them.
    Observed 2026-09-23 on a test officer: both held applications already at App_Status 13.

    WHY A NEW PROCEDURE rather than calling USP_IU_Architect_AssignedApp: its only update
    branch is the unassign path. It forces IsActive = @IsActive (it cannot keep 1 while
    changing Status) and always writes Remarks = 'Unassigned by Architect himself', which
    would be a false audit trail on every completed survey. This mirrors instead the exact
    update USP_U_Status_Master_Tran performs when it completes an assignment:

        UPDATE a set a.CompletedDate=getdate(), a.Status='Completed', a.UDate=GETDATE(),
                     a.UBy=@Cby, a.URole=@Sts_CurrOfficerRoleId ...

    so a row closed by this API is indistinguishable from one closed by the legacy
    workflow: IsActive stays 1, Remarks is untouched.

    Only the SUBMITTING officer's own pending row is closed. A survey submitted by someone
    who does not hold the application changes nothing here.

    Sections: 1 procedure, 2 backfill preview, 3 backfill apply, 4 undo (commented out).
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- 1. The procedure
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_U_BtoA_GpsCompleteAssignment]
    @AppId      int,
    @OfficerId  bigint,
    @RoleId     int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE dbo.BtoA_Architect_AssignedApp
       SET CompletedDate = GETDATE(),
           Status        = 'Completed',
           UDate         = GETDATE(),
           UBy           = @OfficerId,
           URole         = @RoleId
     WHERE AppId    = @AppId
       AND Arch_Id  = @OfficerId
       AND IsActive = 1
       AND Status   = 'Pending';

    SELECT RowsAffected = @@ROWCOUNT;
END
GO

-------------------------------------------------------------------------------
-- 2. Backfill preview - assignments already stuck
--
--    Pending and active, where the officer holding it has in fact submitted a survey
--    for that application (a BtoA_StatusDetail_Officer row by them) and the application
--    has moved past pending (App_Status <> 10). Both conditions, so an application the
--    officer merely opened, or one sent back to them, is left alone.
-------------------------------------------------------------------------------
SELECT a.Assign_Id, a.AppId, m.App_DisplayId, m.App_Status, a.Arch_Id, a.Arch_Name,
       a.AssignmentDate,
       SubmittedOn = (SELECT MAX(s.CDte) FROM dbo.BtoA_StatusDetail_Officer s
                      WHERE s.Ofcr_App_Id = a.AppId AND s.CBy = a.Arch_Id)
FROM dbo.BtoA_Architect_AssignedApp a
JOIN dbo.BtoAMainApp m ON m.App_Id = a.AppId AND m.App_Active = 1
WHERE a.IsActive = 1 AND a.Status = 'Pending'
  AND m.App_Status <> 10
  AND EXISTS (SELECT 1 FROM dbo.BtoA_StatusDetail_Officer s
              WHERE s.Ofcr_App_Id = a.AppId AND s.CBy = a.Arch_Id);
GO

-------------------------------------------------------------------------------
-- 3. Backfill apply - previous values saved first so section 4 restores exactly
-------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd
    (
        Assign_Id     int PRIMARY KEY,
        Status        nvarchar(30) NULL,
        CompletedDate datetime     NULL,
        UDate         datetime     NULL,
        UBy           bigint       NULL,
        URole         int          NULL,
        BackedUpOn    datetime     NOT NULL CONSTRAINT DF_ZZ_PgpsAssignBackfill DEFAULT (GETDATE())
    );
GO

BEGIN TRAN;

    INSERT INTO dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd (Assign_Id, Status, CompletedDate, UDate, UBy, URole)
    SELECT a.Assign_Id, a.Status, a.CompletedDate, a.UDate, a.UBy, a.URole
    FROM dbo.BtoA_Architect_AssignedApp a
    JOIN dbo.BtoAMainApp m ON m.App_Id = a.AppId AND m.App_Active = 1
    WHERE a.IsActive = 1 AND a.Status = 'Pending'
      AND m.App_Status <> 10
      AND EXISTS (SELECT 1 FROM dbo.BtoA_StatusDetail_Officer s
                  WHERE s.Ofcr_App_Id = a.AppId AND s.CBy = a.Arch_Id)
      AND NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd b
                      WHERE b.Assign_Id = a.Assign_Id);

    UPDATE a
       SET a.CompletedDate = COALESCE(
               (SELECT MAX(s.CDte) FROM dbo.BtoA_StatusDetail_Officer s
                WHERE s.Ofcr_App_Id = a.AppId AND s.CBy = a.Arch_Id), GETDATE()),
           a.Status = 'Completed',
           a.UDate  = GETDATE(),
           a.UBy    = a.Arch_Id,
           a.URole  = a.Arch_Role
    FROM dbo.BtoA_Architect_AssignedApp a
    JOIN dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd b ON b.Assign_Id = a.Assign_Id
    WHERE a.Status = 'Pending';

    SELECT BackfilledCount = @@ROWCOUNT;

COMMIT TRAN;
GO

-------------------------------------------------------------------------------
-- 4. UNDO the backfill - restore every backed-up row exactly, then clear the backup
-------------------------------------------------------------------------------
/*
BEGIN TRAN;
    UPDATE a
       SET a.Status = b.Status, a.CompletedDate = b.CompletedDate,
           a.UDate = b.UDate, a.UBy = b.UBy, a.URole = b.URole
    FROM dbo.BtoA_Architect_AssignedApp a
    JOIN dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd b ON b.Assign_Id = a.Assign_Id;
    SELECT RestoredCount = @@ROWCOUNT;
    DELETE FROM dbo.ZZ_PropertyGpsApi_AssignmentBackfill_udd;
COMMIT TRAN;
*/
