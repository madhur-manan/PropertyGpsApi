/*
    trg_BtoAMainApp_UpdateNewKhata - skip the e-Khata step where e-Khata is not installed.

    APPLIES TO UDD_KHATABTOA_TEST (and, only after a DBA decision, the new server's
    UDD_KHATABTOA). There is no USE line: open the target database first. The guard below
    refuses anything else - in particular KhataBtoA_prod, the live database.

    WHY. This AFTER UPDATE trigger keeps e-Khata in step with single plot: a rejection or
    deactivation deactivates the matching KRS row, and a paid processing fee marks it
    SSA-applied. Both statements name ekhata_test.dbo.KRS directly. Whenever an UPDATE sets
    App_Status, App_Active or isProcessingFeePaid, the second statement runs even if no row
    qualifies - and where the ekhata_test database does not exist it fails with
    "Invalid object name 'ekhata_test.dbo.krs'", rolling back the whole update.

    That includes the RI submit: USP_IU_BtoA_StatusDetail_Officer sets App_Status = 13. So
    every survey submit failed on the laptop (no e-Khata database at all) and on the new
    test server 10.40.119.74 (scanned 2026-10-05: no ekhata_test; its e-Khata copy is
    BBMPEkhata - see docs/DBA_Request.md in the app repo).

    THE CHANGE. One guard after the existing UPDATE() check:

        IF DB_ID(N'ekhata_test') IS NULL RETURN;

    It skips ONLY when the database is absent. A missing table, or a permission problem in
    a database that does exist, still fails exactly as before - this hides nothing on a
    server where e-Khata is installed. It works because the ekhata_test statements are
    compiled only when they are reached (deferred name resolution); that is also why the
    trigger could be created without the database in the first place. Everything else in
    the body is unchanged.

    SAFETY. Refuses unless the trigger in this database is still the one this script was
    written against (it must still update ekhata_test.dbo.KRS), so a version a DBA has
    changed is never overwritten. SET NOEXEC ON makes the refusal stop every later batch
    in SSMS too - a bare THROW would not.

    ALTER TRIGGER keeps the trigger enabled; no firing order is set on BtoAMainApp's two
    UPDATE triggers (checked 2026-10-05), so none is lost.

    Sections: 1 guard, 2 apply, 3 check, 4 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD
-------------------------------------------------------------------------------
IF DB_NAME() NOT IN ('UDD_KHATABTOA_TEST', 'UDD_KHATABTOA')
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA_TEST (or the new server''s UDD_KHATABTOA) first.', 16, 1);
    SET NOEXEC ON;
END
ELSE IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.trg_BtoAMainApp_UpdateNewKhata')) NOT LIKE N'%ekhata[_]test.dbo.KRS%'
BEGIN
    RAISERROR('Refusing to run: dbo.trg_BtoAMainApp_UpdateNewKhata is missing or no longer updates ekhata_test.dbo.KRS. Compare it with section 4 first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. APPLY
-------------------------------------------------------------------------------
CREATE OR ALTER TRIGGER [dbo].[trg_BtoAMainApp_UpdateNewKhata]
ON [dbo].[BtoAMainApp]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

   -- Only proceed when isProcessingFeePaid,App_Status,App_Active was part of the update
    IF NOT (UPDATE(isProcessingFeePaid) or UPDATE(App_Status) or update(App_Active))
        RETURN;

    -- e-Khata is not on every server (missing locally and on the 2026-10 test server).
    -- Without it there is nothing to keep in step, so skip rather than fail the update.
    -- (db/23 in the Property GPS API repo.)
    IF DB_ID(N'ekhata_test') IS NULL
        RETURN;

    BEGIN TRY

		-- Newly added

		  IF EXISTS
        (
            SELECT 1
            FROM inserted
            WHERE App_Status IN (11,110,1111)
               OR App_Active = 0
        )
        BEGIN

            UPDATE k
            SET
                k.KRS_Active = 0,
                k.KRS_AdditionalInfo = 'Rejected in singleplot, updating new khata by trigger',
				k.KRS_UDte=getdate(),
				k.KRS_UBy=1,
				k.KRS_URole=99,
				k.KRS_MStsId=11
            FROM ekhata_test.dbo.KRS k
            INNER JOIN inserted i
                ON LTRIM(RTRIM(ISNULL(k.KRS_EPID,''))) = LTRIM(RTRIM(ISNULL(i.App_MotherEPID,'')))
            WHERE
                (i.App_Status IN (11,110,1111) OR i.App_Active = 0)
                AND ISNULL(k.KRS_IsSSA_applied,0) <> 0 AND k.KRS_MStsId > 7 AND k.KRS_Active = 1;

        END

        -- Collect distinct mother EPIDs where isProcessingFeePaid changed to 1
        ;WITH ChangedEPIDs AS
        (
            SELECT DISTINCT i.App_MotherEPID
            FROM inserted i
            JOIN deleted d
              ON ISNULL(i.App_MotherEPID,'') = ISNULL(d.App_MotherEPID,'')
            WHERE ISNULL(i.isProcessingFeePaid,0) <> ISNULL(d.isProcessingFeePaid,0)
              AND ISNULL(i.isProcessingFeePaid,0) = 1   -- only when payment became 'paid'
              AND ISNULL(i.App_MotherEPID,'') <> ''     -- skip null/empty EPIDs
			  and i.App_Source='newkhata'
        )
        -- Update the target KRS rows for all affected EPIDs
        UPDATE k
        SET
            k.KRS_IsSSA_applied = 2,
            k.KRS_UDte = getdate(),
            k.KRS_UBy  = 1,
            k.KRS_AdditionalInfo = 'Updated the SPA flag once payment is done'
        FROM ekhata_test.dbo.krs k
        JOIN ChangedEPIDs c
          ON ISNULL(k.KRS_EPID,'') = ISNULL(c.App_MotherEPID,'')
		  where KRS_Active=1 and KRS_IsSSA_applied is not null

    END TRY
    BEGIN CATCH

        DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
        RAISERROR('trg_BtoAMainApp_UpdateNewKhata failed: %s', 16, 1, @ErrMsg);

        THROW;
    END CATCH
END;
GO

-------------------------------------------------------------------------------
-- 3. CHECK - an App_Status update now succeeds here (nothing is kept: ROLLBACK).
-------------------------------------------------------------------------------
BEGIN TRAN;
    UPDATE TOP (1) dbo.BtoAMainApp SET App_Status = App_Status WHERE App_Status = 10;
    SELECT CONCAT('App_Status update OK on ', @@ROWCOUNT, ' row; ekhata_test present: ',
                  CASE WHEN DB_ID(N'ekhata_test') IS NULL THEN 'no (e-Khata step skipped)' ELSE 'yes' END) AS Result;
ROLLBACK;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. UNDO - the original trigger exactly as it was before db/23 (SHA-256 of its
--    definition on UDD_KHATABTOA_TEST, 2026-10-05:
--    3B5774499D0B29915CF7D4B0B7DB584491F138C0D51469DD792B98CB698F4DEE).
--    Restoring it brings back the submit failure wherever ekhata_test is missing.
-------------------------------------------------------------------------------
/*
CREATE OR ALTER TRIGGER [dbo].[trg_BtoAMainApp_UpdateNewKhata]
ON [dbo].[BtoAMainApp]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

   -- Only proceed when isProcessingFeePaid,App_Status,App_Active was part of the update
    IF NOT (UPDATE(isProcessingFeePaid) or UPDATE(App_Status) or update(App_Active))
        RETURN;

    BEGIN TRY

		-- Newly added

		  IF EXISTS
        (
            SELECT 1
            FROM inserted
            WHERE App_Status IN (11,110,1111)
               OR App_Active = 0
        )
        BEGIN

            UPDATE k
            SET
                k.KRS_Active = 0,
                k.KRS_AdditionalInfo = 'Rejected in singleplot, updating new khata by trigger',
				k.KRS_UDte=getdate(),
				k.KRS_UBy=1,
				k.KRS_URole=99,
				k.KRS_MStsId=11
            FROM ekhata_test.dbo.KRS k
            INNER JOIN inserted i
                ON LTRIM(RTRIM(ISNULL(k.KRS_EPID,''))) = LTRIM(RTRIM(ISNULL(i.App_MotherEPID,'')))
            WHERE
                (i.App_Status IN (11,110,1111) OR i.App_Active = 0)
                AND ISNULL(k.KRS_IsSSA_applied,0) <> 0 AND k.KRS_MStsId > 7 AND k.KRS_Active = 1;

        END

        -- Collect distinct mother EPIDs where isProcessingFeePaid changed to 1
        ;WITH ChangedEPIDs AS
        (
            SELECT DISTINCT i.App_MotherEPID
            FROM inserted i
            JOIN deleted d
              ON ISNULL(i.App_MotherEPID,'') = ISNULL(d.App_MotherEPID,'')
            WHERE ISNULL(i.isProcessingFeePaid,0) <> ISNULL(d.isProcessingFeePaid,0)
              AND ISNULL(i.isProcessingFeePaid,0) = 1   -- only when payment became 'paid'
              AND ISNULL(i.App_MotherEPID,'') <> ''     -- skip null/empty EPIDs
			  and i.App_Source='newkhata'
        )
        -- Update the target KRS rows for all affected EPIDs
        UPDATE k
        SET
            k.KRS_IsSSA_applied = 2,
            k.KRS_UDte = getdate(),
            k.KRS_UBy  = 1,
            k.KRS_AdditionalInfo = 'Updated the SPA flag once payment is done'
        FROM ekhata_test.dbo.krs k
        JOIN ChangedEPIDs c
          ON ISNULL(k.KRS_EPID,'') = ISNULL(c.App_MotherEPID,'')
		  where KRS_Active=1 and KRS_IsSSA_applied is not null

    END TRY
    BEGIN CATCH

        DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
        RAISERROR('trg_BtoAMainApp_UpdateNewKhata failed: %s', 16, 1, @ErrMsg);

        THROW;
    END CATCH
END;
*/
