/*
    Property GPS: how many applications one RI may hold becomes a setting (default still 2).

    APPLIES TO UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE), or to the laptop copy
    UDD_KHATABTOA_TEST on localhost\MSSQLSERVER2019. The guard refuses anything else, and refuses
    unless USP_IU_Architect_AssignedApp is still the version this script was written against
    (the "having count(*)>=2" queue check and its "two applications pending" message). For LIVE,
    hand this script to the DBA: the guard must be widened there on purpose, not by accident.

    WHY. USP_IU_Architect_AssignedApp refuses a third pending assignment with the number 2 written
    into it. The API now reads Survey:MaxAllottedPerOfficer from its appsettings.json and passes
    it, so the limit changes without touching the database again.

    WHAT CHANGES - USP_IU_Architect_AssignedApp only:
      - a new LAST parameter @MaxPending int = 2. Every other caller (the legacy apps) sends
        nothing and keeps exactly 2.
      - the queue check:   having count(*) >= 2   ->   having count(*) >= @MaxPending
      - the insert branch: having count(*) <= 2   ->   having count(*) <= @MaxPending
      - the message says the number: 'You currently have N applications pending in your queue.
        Please complete them before taking up a new application.' The phrase "pending in your
        queue" is kept: the API recognises the refusal by it (QUEUE_FULL).
    Everything else is the existing text unchanged - including the "maximum allowed limit" check
    and the unassign branch.
    NOT CHANGED: every other procedure, table, index and trigger.

    BACKUP AND UNDO. Section 2 saves the current text, SET options and GRANTs in
    dbo.ZZ_PropertyGpsApi_ProcBackup_45 (once; a re-run keeps the first copy). Section 5
    (commented out) puts it back.

    Sections: 1 guard, 2 backup, 3 the procedure, 4 checks (read-only), 5 undo (commented out).
*/

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS too)
-------------------------------------------------------------------------------
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
       Instance = CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'));

DECLARE @why nvarchar(2000) = NULL;
DECLARE @def nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp'));

IF NOT (   (DB_NAME() = N'UDD_KHATABTOA'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) LIKE N'DB-NEW-KHATA-TE%')
        OR (DB_NAME() = N'UDD_KHATABTOA_TEST'
            AND CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')) = N'MSSQLSERVER2019'))
    SET @why = N'Refusing to run: open UDD_KHATABTOA on the test DB server, or UDD_KHATABTOA_TEST on the laptop, first.';
ELSE IF OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp', N'P') IS NULL
    SET @why = N'Refusing to run: dbo.USP_IU_Architect_AssignedApp does not exist here. Nothing changed.';
ELSE IF @def IS NULL
    SET @why = N'Refusing to run: no text can be saved for USP_IU_Architect_AssignedApp (encrypted?), so it could not be put back. Nothing changed.';
ELSE IF EXISTS (SELECT 1 FROM sys.parameters
                WHERE object_id = OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp') AND name = N'@MaxPending')
    SET @why = N'Already done: USP_IU_Architect_AssignedApp already has @MaxPending. Nothing changed.';
ELSE IF @def NOT LIKE N'%Status=''Pending'' having count(*)>=2%'
     OR @def NOT LIKE N'%two applications pending in your queue%'
     OR @def NOT LIKE N'%Status =''Pending'' having count(*)<=2%'
    SET @why = N'Refusing to run: USP_IU_Architect_AssignedApp is not the version this script expects (someone has changed it). Send its text to the developer. Nothing changed.';

IF @why IS NOT NULL
BEGIN
    RAISERROR(N'%s', 16, 1, @why);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. BACKUP - what is there now, once. Undo (section 5) restores from here.
-------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_ProcBackup_45', N'U') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_ProcBackup_45
    (
        [name]                 sysname       NOT NULL PRIMARY KEY,
        [definition]           nvarchar(max) NULL,
        saved_at               datetime      NOT NULL CONSTRAINT DF_ZZ_PgpsProcBackup45 DEFAULT (GETDATE()),
        uses_ansi_nulls        bit           NULL,
        uses_quoted_identifier bit           NULL,
        grants                 nvarchar(max) NULL    -- GRANT / DENY statements, run again after a restore
    );
GO

-- Kept on a re-run, so it always holds what was there before db/45.
INSERT INTO dbo.ZZ_PropertyGpsApi_ProcBackup_45 ([name], [definition], uses_ansi_nulls, uses_quoted_identifier, grants)
SELECT N'USP_IU_Architect_AssignedApp', m.[definition], m.uses_ansi_nulls, m.uses_quoted_identifier, g.grants
FROM sys.sql_modules m
OUTER APPLY (SELECT grants = STRING_AGG(CONVERT(nvarchar(max),
                        CASE dp.[state] WHEN 'D' THEN N'DENY ' ELSE N'GRANT ' END
                        -- Pinned collations: see db/42 (Msg 4191 / 8124).
                        + dp.permission_name COLLATE DATABASE_DEFAULT
                        + N' ON dbo.' + QUOTENAME(ob.name) COLLATE DATABASE_DEFAULT
                        + N' TO ' + QUOTENAME(pr.name) COLLATE DATABASE_DEFAULT
                        + CASE dp.[state] WHEN 'W' THEN N' WITH GRANT OPTION' ELSE N'' END), N'; ')
             FROM sys.database_permissions dp
             JOIN sys.objects ob ON ob.object_id = dp.major_id
             JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
             WHERE dp.class = 1 AND dp.major_id = m.object_id AND dp.minor_id = 0) g
WHERE m.object_id = OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp')
  AND NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ProcBackup_45 b WHERE b.[name] = N'USP_IU_Architect_AssignedApp');
GO

SELECT [name], saved_at, HasText = CASE WHEN [definition] IS NULL THEN 0 ELSE 1 END, grants
FROM dbo.ZZ_PropertyGpsApi_ProcBackup_45;

-- Nothing changes unless the text is saved. In a batch of its own, so a failed backup batch
-- above still stops the rest.
IF NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ProcBackup_45
               WHERE [name] = N'USP_IU_Architect_AssignedApp' AND [definition] IS NOT NULL)
BEGIN
    RAISERROR('The backup is incomplete (see the grid above). Nothing after this point runs.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 3. USP_IU_Architect_AssignedApp - the existing text, with @MaxPending in place of 2
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- USP_IU_Architect_AssignedApp 1612,25,115,'Architect','Pending',1
CREATE OR ALTER PROCEDURE [dbo].[USP_IU_Architect_AssignedApp]
(
    @AppId INT,
    @Arch_Id INT,
    @Arch_Role INT,
    @Arch_Name NVARCHAR(150),
    @Status NVARCHAR(30),
    @IsActive BIT,
    -- db/45: how many Pending applications one officer may hold. Sent by the Property GPS API
    -- from Survey:MaxAllottedPerOfficer; every other caller sends nothing and keeps 2.
    @MaxPending INT = 2
)
AS
BEGIN
    SET NOCOUNT ON;
	declare @Assign_Id int
	declare @Message nvarchar(max)
	declare @StatusCode int

    -- Default Assignment Date
	 select @Assign_Id=Assign_Id from BtoA_Architect_AssignedApp where AppId=@AppId and IsActive=1

	print @AppId
	begin	try

	BEGIN TRAN

	if(@IsActive=1)
	begin
	IF EXISTS (select 1 from BtoA_Architect_AssignedApp where AppId=@AppId and IsActive=1)
	begin

		print '1  already assigned'
		SELECT @Message='Application is Already Assigned .'
		 SELECT 0 AS Assign_Id, @AppId as App_id, @Message  as Message,400 as StatusCode ;

	end
	ELSE IF exists (select count(*) from BtoA_Architect_AssignedApp where AppId=@AppId and IsActive=1 and Arch_Id=@Arch_Id and UDate is null having count(*)>=2)
	begin

		print '3  already assigned'
		SELECT @Message='You have exceeded the maximum allowed limit for assigning this application.'
		 SELECT 0 AS Assign_Id, @AppId as App_id, @Message  as Message,400 as StatusCode ;

	end

	ELSE IF exists (select count(*) from BtoA_Architect_AssignedApp where Arch_Id= @Arch_Id and IsActive=1 and Status='Pending' having count(*)>=@MaxPending)
	begin
		select @Message ='You currently have ' + CONVERT(nvarchar(10), @MaxPending) + ' applications pending in your queue. Please complete them before taking up a new application.'
	  SELECT 0 AS Assign_Id, @AppId as App_id, @Message  as Message,400 as StatusCode ;
	end

    else IF exists (select 1 from BtoA_Architect_AssignedApp where Arch_Id= @Arch_Id and IsActive=1 and Status ='Pending' having count(*)<=@MaxPending)
    BEGIN

		print '2'
        INSERT INTO BtoA_Architect_AssignedApp
        (
            AppId,
            Arch_Id,
            Arch_Role,
            Arch_Name,
            AssignmentDate,
            CompletedDate,
            Status,
            IsActive,
            CBy,
            CDate,
            CRole
        )
        VALUES
        (
            @AppId,
            @Arch_Id,
            @Arch_Role,
            @Arch_Name,
            GETDATE(),
            CASE
                WHEN @Status = 'Completed' THEN GETDATE()
            END,
            @Status,
            @IsActive,
            @Arch_Id,
            GETDATE(),
            @Arch_Role
        );

		select @Message='Application is assigned successfull';
        -- Return newly created ID

		set @Assign_Id =SCOPE_IDENTITY();
        SELECT @Assign_Id AS Assign_Id, @AppId as App_id, @Message  as Message,200 as StatusCode ;

    END
    -- =========================
    -- UPDATE
    -- =========================
    End
	ELSE
    BEGIN
        UPDATE BtoA_Architect_AssignedApp
        SET
            AppId = @AppId,
            Arch_Id = @Arch_Id,
            Arch_Role = @Arch_Role,
            Arch_Name = @Arch_Name,
            CompletedDate = CASE
                                WHEN @Status = 'Completed'
                                THEN GETDATE()
                                ELSE CompletedDate
                            END,
            Status = @Status,
            IsActive = @IsActive,
			Remarks ='Unassigned by Architect himself',
            UBy = @Arch_Id,
            UDate =  GETDATE(),
            URole = @Arch_Role
        WHERE Arch_Id = @Arch_Id and AppId=@AppId and Status='Pending';
	if (@@ROWCOUNT >0)
	begin
	DECLARE @DisplayId nvarchar(50)
	select @DisplayId =App_DisplayId from BtoAMainApp where App_Id=@AppId and App_Active=1

	select @Message='Application No '+ CONVERT(nvarchar, @DisplayId ) +'  Unassigned successfully'

	SELECT @Assign_Id = Assign_Id,@Assign_Id  = AppID from BtoA_Architect_AssignedApp where AppId=@AppId

	SELECT @Assign_Id AS Assign_Id, @AppId as App_id, @Message  as Message,200 as StatusCode ;
	end
	else
	begin
	select @Message='No records found'
	 SELECT 0 AS Assign_Id, @AppId as App_id, @Message  as Message,400 as StatusCode ;
	end
   END

	 -- INSERT  select * from BtoA_Architect_AssignedApp

	COMMIT TRAN
	end try
	begin catch
	ROLLBACK
	select @Message = ERROR_MESSAGE()
	  SELECT 0 AS Assign_Id, @AppId as App_id, @Message  as Message,400 as StatusCode ;
	end catch

END
GO
-- CREATE OR ALTER keeps the procedure's existing GRANTs; nothing to re-grant.

-------------------------------------------------------------------------------
-- 4. CHECKS (read-only)
-------------------------------------------------------------------------------
-- 4.1 The parameters: 7, the last one @MaxPending int (default 2 - SQL Server does not report
--     the default value here; it is in the text above).
SELECT p.parameter_id, p.name, TYPE_NAME(p.user_type_id) AS type_name
FROM sys.parameters p
WHERE p.object_id = OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp')
ORDER BY p.parameter_id;

-- 4.2 Who holds how many Pending applications now (no names, ids only).
SELECT Arch_Id, PendingNow = COUNT(*)
FROM dbo.BtoA_Architect_AssignedApp WITH (NOLOCK)
WHERE IsActive = 1 AND Status = 'Pending'
GROUP BY Arch_Id
ORDER BY COUNT(*) DESC, Arch_Id;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 5. UNDO (commented out) - puts back the text saved in section 2, with its GRANTs.
-------------------------------------------------------------------------------
/*
DECLARE @def nvarchar(max), @grants nvarchar(max);
SELECT @def = [definition], @grants = grants
FROM dbo.ZZ_PropertyGpsApi_ProcBackup_45 WHERE [name] = N'USP_IU_Architect_AssignedApp';

IF @def IS NULL
    RAISERROR('No saved text for USP_IU_Architect_AssignedApp. Nothing changed.', 16, 1);
ELSE
BEGIN
    EXEC (N'DROP PROCEDURE IF EXISTS dbo.USP_IU_Architect_AssignedApp');
    EXEC (@def);                                     -- the saved CREATE PROCEDURE text
    IF @grants IS NOT NULL EXEC (@grants);
    SELECT Restored = N'USP_IU_Architect_AssignedApp', ParamCount =
        (SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.USP_IU_Architect_AssignedApp'));
END
*/
