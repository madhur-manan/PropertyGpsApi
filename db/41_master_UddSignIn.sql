/*
    UDD_MASTERDB sign-in for Property GPS: the one-phone table and the OTP check.

    APPLIES TO UDD_MASTERDB ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.
    Run db/40 first: it says whether either object below is already there.

    WHY. Property GPS now signs in against UDD_MASTERDB (config key Database:Master is
    unchanged) and works by ULB + ward number only. Two things it needs are not there:
      - dbo.GpsOfficerSession, the one-phone-per-officer table (docs/DBA_Request.md G1). The
        API reads and writes it with three statements of its own (OfficerSessionStore.cs:
        StartSql, CheckSql, EndSql); there is no procedure. The columns below are exactly
        what those statements use. The INSERT names neither EndedAt nor ReplacedCount, so
        ReplacedCount must default to 0.
      - dbo.USP_S_Officer_ValidateOTP, the code check at sign-in. masterDB_prod's version
        cannot be copied: UDD_MASTERDB's mst_Officer has no Ofcr_ZoneId, Ofcr_CorporationId
        or Ofcr_DivisionId, and the GBA/BBMP zone and ward columns are no longer read. This
        one returns the officer's own ULB (Ofcr_ULBId) and ward number (Ofcr_WardId); the
        API adds the mapped wards itself (OfficerService, mst_Ofcr_WardMapping).
    USP_S_ValidateOfficer, USP_I_OTP and usp_InsertLoginData already exist in UDD_MASTERDB
    and are used as they are.

    THE CODE CHECK - USP_S_Officer_ValidateOTP @OTP int, @MobileNO nvarchar(20), @Source nvarchar(20)
      - No active officer row for the mobile: no rows, nothing written. The API answers "not
        registered, or the account has been locked" - one message for both, so nobody can
        find out which mobiles belong to officers.
      - The code is good when OTP_Tran holds a row with this mobile and code, sent by this
        app, not yet used (ACTIVE IS NULL) and sent within the last 100 minutes.
          "Sent by this app": USP_I_OTP writes its @DeviceId into OTP_Tran.DeviceId, and the
          API passes Otp:Source as BOTH @DeviceId (send) and @Source (verify) - see
          OfficerService.StoreOtpAsync - so the row matches on DeviceId = @Source. Source =
          @Source is accepted too, in case USP_I_OTP fills that column as well.
          "100 minutes": the window AppOptions.cs documents (Otp:ValidForSeconds 6000, which
          is informational there - this procedure is the authority).
      - A good code is used up by the same statement that finds it (ACTIVE = '0', inactive;
        UDD's USP_I_OTP leaves a fresh code NULL - db/40 shows what OTP_Tran holds), so it
        cannot be replayed, not even by two requests at once (DBA_Request B3: masterDB_prod's
        version never marked a code used). NoOfFailedAttempt goes back to 0.
      - A wrong or expired code adds 1 to NoOfFailedAttempt. The 4th wrong code in a row
        locks the account, as UDD's citizen USP_S_ValidateOTP does for mst_citizen:
        Ofcr_Active = 0, Ofcr_Locked_Date = now, and a note ADDED to the end of
        Ofcr_AdditionalInfo (what was there is kept: db/31 finds its test officer by the
        beginning of that text). The call that locks already returns no rows, so the officer
        is told at once. Section 4.4 shows how an administrator unlocks; it takes the note
        off again, because db/37's undo matches the whole text.
      - Only RI rows (role 117) count: another role on the same mobile (116, 125, ...) is
        neither signed in nor counted nor locked. Counting, resetting and locking apply to
        every active 117 row of the mobile together.
      - Result, one row per active role-117 row of the mobile, lowest Ofcr_Id first (the API
        takes the first row):
          IsValidOtp bit, Ofcr_Id bigint, Ofcr_MNo, Ofcr_RoleId int, Ofcr_Name,
          Ofcr_ULBId int, Ofcr_WardId nvarchar (trimmed, tabs removed, blank = NULL),
          RoleName (Mst_Roles), AdditionalInfo (what happened, in words), NoOfFailedAttempt int.
        The columns masterDB_prod's version returned and the old API read (ZoneId, ZoneName,
        CorporationId, CorporationName, BBMPWardName, Ofcr_ZoneId, Ofcr_DivisionId) are not
        returned; the API reads them as null.

    GRANTS. If the API does not sign in as a sysadmin or db_owner here, its login needs
    SELECT, INSERT, UPDATE on dbo.GpsOfficerSession and EXECUTE on
    dbo.USP_S_Officer_ValidateOTP - commented template at the end of section 3.

    Nothing here touches another table's structure or another procedure.

    Sections: 1 guard, 2 GpsOfficerSession, 3 USP_S_Officer_ValidateOTP, 4 checks (read-only),
    5 undo (commented out).
*/

USE UDD_MASTERDB;
GO

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not)
-------------------------------------------------------------------------------
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_MASTERDB'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_MASTERDB on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. dbo.GpsOfficerSession - one row per officer (OfficerSessionStore.cs)
--      OfficerId     the officer (mst_Officer.Ofcr_Id), primary key - every request looks it up
--      SessionId     the sign-in live on the phone; NULL after sign-out
--      DeviceId      the phone the officer is bound to ("a:" + ANDROID_ID, at most 64)
--      ClientIp      address of the last sign-in
--      StartedAt     last sign-in;  EndedAt  last sign-out or administrator move
--      ReplacedCount sign-ins that replaced a live session on the same phone
--    Created only if missing; an existing table is left exactly as it is (section 4 compares it).
-------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.GpsOfficerSession', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GpsOfficerSession
    (
        OfficerId     bigint           NOT NULL CONSTRAINT PK_GpsOfficerSession PRIMARY KEY,
        SessionId     uniqueidentifier NULL,
        DeviceId      nvarchar(64)     NULL,
        ClientIp      nvarchar(64)     NULL,
        StartedAt     datetime         NOT NULL,
        EndedAt       datetime         NULL,
        ReplacedCount int              NOT NULL CONSTRAINT DF_GpsOfficerSession_ReplacedCount DEFAULT (0)
    );

    -- So the undo can tell this table from one somebody else created.
    EXEC sys.sp_addextendedproperty
         @name = N'CreatedBy', @value = N'PropertyGpsApi db/41_master_UddSignIn.sql',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'GpsOfficerSession';

    SELECT GpsOfficerSession = 'created';
END
ELSE
    SELECT GpsOfficerSession = 'already there - left as it is (see section 4)';
GO

-------------------------------------------------------------------------------
-- 3. dbo.USP_S_Officer_ValidateOTP
--
--    Refuses (and stops here) if UDD_MASTERDB already has a USP_S_Officer_ValidateOTP that
--    this script did not create: somebody else's procedure is not replaced unseen. Compare it
--    with the header first. It also refuses if a column it reads is missing.
-------------------------------------------------------------------------------
DECLARE @why nvarchar(2000) = NULL;

IF OBJECT_ID(N'dbo.USP_S_Officer_ValidateOTP', N'P') IS NOT NULL
   AND ISNULL(OBJECT_DEFINITION(OBJECT_ID(N'dbo.USP_S_Officer_ValidateOTP')), N'') NOT LIKE N'%db/41[_]master[_]UddSignIn%'
    SET @why = N'UDD_MASTERDB already has a dbo.USP_S_Officer_ValidateOTP that this script did not create. '
             + N'It was not replaced. Compare it with the header of db/41 and decide.';
ELSE
BEGIN
    SELECT @why = N'Columns missing in UDD_MASTERDB, so the procedure was not created: '
                + STRING_AGG(CONVERT(nvarchar(max), n.t + N'.' + n.c), N', ')
    FROM (VALUES (N'mst_Officer', N'Ofcr_Id'), (N'mst_Officer', N'Ofcr_Name'), (N'mst_Officer', N'Ofcr_MNo'),
                 (N'mst_Officer', N'Ofcr_RoleId'), (N'mst_Officer', N'Ofcr_ULBId'), (N'mst_Officer', N'Ofcr_WardId'),
                 (N'mst_Officer', N'Ofcr_Active'), (N'mst_Officer', N'Ofcr_AdditionalInfo'),
                 (N'mst_Officer', N'NoOfFailedAttempt'), (N'mst_Officer', N'Ofcr_Locked_Date'),
                 (N'OTP_Tran', N'OTP'), (N'OTP_Tran', N'Cdte'), (N'OTP_Tran', N'MobileNO'), (N'OTP_Tran', N'ACTIVE'),
                 (N'OTP_Tran', N'DeviceId'), (N'OTP_Tran', N'Source'),
                 (N'Mst_Roles', N'RoleId'), (N'Mst_Roles', N'RoleName')) n(t, c)
    WHERE COL_LENGTH(N'dbo.' + n.t, n.c) IS NULL;
END

IF @why IS NOT NULL
BEGIN
    RAISERROR(N'%s', 16, 1, @why);
    SET NOEXEC ON;
END
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_S_Officer_ValidateOTP]
    @OTP      int,
    @MobileNO nvarchar(20),
    @Source   nvarchar(20)
AS
BEGIN
    -- Created by db/41_master_UddSignIn.sql (Property GPS). Its undo drops only a procedure
    -- that carries this line, and db/41 replaces only a procedure that carries it.
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MaxFailures  int      = 4,      -- the 4th wrong code in a row locks the account
            @ValidMinutes int      = 100,    -- AppOptions.cs: Otp:ValidForSeconds 6000
            @Now          datetime = GETDATE(),
            @IsValidOtp   bit      = 0,
            @InfoLen      int;

    DECLARE @officers TABLE (Ofcr_Id bigint NOT NULL PRIMARY KEY);

    SET @MobileNO = LTRIM(RTRIM(@MobileNO));

    BEGIN TRAN;

        -- The officer rows this mobile signs in as. UPDLOCK holds them to the end, so two
        -- requests for one mobile run one after the other: they cannot both use one code,
        -- both count from the same number, or both miss the lock.
        INSERT INTO @officers (Ofcr_Id)
        SELECT o.Ofcr_Id
        FROM dbo.mst_Officer o WITH (UPDLOCK)
        WHERE o.Ofcr_MNo = @MobileNO
          AND o.Ofcr_RoleId = 117                   -- the RI: the only role Property GPS signs in
          AND ISNULL(o.Ofcr_Active, 0) = 1;

        IF EXISTS (SELECT 1 FROM @officers)
        BEGIN
            -- Found and used up in one statement: only one request can ever get the row.
            UPDATE t
               SET t.ACTIVE = '0'                  -- used: inactive (a fresh code is NULL)
              FROM dbo.OTP_Tran t
             WHERE t.MobileNO = @MobileNO
               AND t.OTP = @OTP
               AND (t.DeviceId = @Source OR t.[Source] = @Source)
               AND t.ACTIVE IS NULL
               AND t.Cdte >= DATEADD(MINUTE, -@ValidMinutes, @Now);

            SET @IsValidOtp = CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END;

            IF @IsValidOtp = 1
                UPDATE o
                   SET o.NoOfFailedAttempt = 0
                  FROM dbo.mst_Officer o
                  JOIN @officers x ON x.Ofcr_Id = o.Ofcr_Id
                 WHERE ISNULL(o.NoOfFailedAttempt, 0) <> 0;
            ELSE
            BEGIN
                -- The lock note is added to the end of Ofcr_AdditionalInfo and cut to fit,
                -- so the update can never fail on length.
                SELECT @InfoLen = CHARACTER_MAXIMUM_LENGTH
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'mst_Officer' AND COLUMN_NAME = N'Ofcr_AdditionalInfo';
                IF @InfoLen IS NULL OR @InfoLen < 1 SET @InfoLen = 4000;   -- (max)

                -- One count for the mobile, not one per row: every row of this mobile gets the
                -- highest count + 1, and they lock together, so no row is left to fall back on.
                DECLARE @Fails int = 1 + (SELECT MAX(ISNULL(o.NoOfFailedAttempt, 0))
                                          FROM dbo.mst_Officer o
                                          JOIN @officers x ON x.Ofcr_Id = o.Ofcr_Id);

                UPDATE o
                   SET o.NoOfFailedAttempt   = @Fails,
                       o.Ofcr_Active         = CASE WHEN @Fails >= @MaxFailures THEN 0 ELSE o.Ofcr_Active END,
                       o.Ofcr_Locked_Date    = CASE WHEN @Fails >= @MaxFailures THEN @Now ELSE o.Ofcr_Locked_Date END,
                       o.Ofcr_AdditionalInfo = CASE WHEN @Fails >= @MaxFailures
                                                    THEN LEFT(ISNULL(o.Ofcr_AdditionalInfo + N' | ', N'')
                                                              + N'Locked ' + CONVERT(nvarchar(19), @Now, 120)
                                                              + N' after ' + CONVERT(nvarchar(10), @MaxFailures)
                                                              + N' wrong OTPs (Property GPS sign-in)', @InfoLen)
                                                    ELSE o.Ofcr_AdditionalInfo END
                  FROM dbo.mst_Officer o
                  JOIN @officers x ON x.Ofcr_Id = o.Ofcr_Id;
            END
        END

    COMMIT TRAN;

    -- One row per officer row still active: none for an unknown mobile, and none on the call
    -- that locked the account.
    SELECT IsValidOtp        = @IsValidOtp,
           Ofcr_Id           = CONVERT(bigint, o.Ofcr_Id),
           o.Ofcr_MNo,
           Ofcr_RoleId       = CONVERT(int, o.Ofcr_RoleId),
           o.Ofcr_Name,
           Ofcr_ULBId        = CONVERT(int, o.Ofcr_ULBId),
           Ofcr_WardId       = NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), N''))), N''),
           RoleName          = r.RoleName,
           AdditionalInfo    = CASE WHEN @IsValidOtp = 1 THEN N'OTP verified'
                                    ELSE N'Wrong or expired OTP. Attempts left before the account is locked: '
                                         + CONVERT(nvarchar(10), @MaxFailures - ISNULL(o.NoOfFailedAttempt, 0)) END,
           NoOfFailedAttempt = ISNULL(o.NoOfFailedAttempt, 0)
    FROM dbo.mst_Officer o
    JOIN @officers x ON x.Ofcr_Id = o.Ofcr_Id
    OUTER APPLY (SELECT TOP (1) mr.RoleName
                 FROM dbo.Mst_Roles mr
                 WHERE mr.RoleId = o.Ofcr_RoleId) r
    WHERE ISNULL(o.Ofcr_Active, 0) = 1
    ORDER BY o.Ofcr_Id;
END
GO

-- Grants, only if the API's login is not a sysadmin / db_owner here. Fill in the login's
-- database user and run these lines on their own. This is everything Property GPS uses in
-- UDD_MASTERDB: the API's master connection and the UDD_KHATABTOA procedures (db/42) that
-- read it across databases, where ownership chaining does not apply.
/*
GRANT SELECT, INSERT, UPDATE ON dbo.GpsOfficerSession TO [<API database user>];
GRANT SELECT ON dbo.mst_Officer               TO [<API database user>];
GRANT SELECT ON dbo.mst_Ofcr_WardMapping      TO [<API database user>];
GRANT SELECT ON dbo.mst_AROMapping            TO [<API database user>];
GRANT SELECT ON dbo.mst_KsrsacMasterPlan2015  TO [<API database user>];
GRANT SELECT ON dbo.Mst_AppVersion            TO [<API database user>];
GRANT SELECT ON dbo.Mst_Roles                 TO [<API database user>];
GRANT EXECUTE ON dbo.USP_S_Officer_ValidateOTP TO [<API database user>];
GRANT EXECUTE ON dbo.USP_S_ValidateOfficer     TO [<API database user>];
GRANT EXECUTE ON dbo.USP_I_OTP                 TO [<API database user>];
GRANT EXECUTE ON dbo.usp_InsertLoginData       TO [<API database user>];
GRANT EXECUTE ON dbo.USP_S_GetRoadData         TO [<API database user>];
*/

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. Checks (read-only)
-------------------------------------------------------------------------------
-- 4.1 GpsOfficerSession against what OfficerSessionStore.cs expects: every Matches = 'ok'.
SELECT e.ColumnName,
       Expected = e.TypeText + CASE WHEN e.Nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END,
       Actual   = CASE WHEN c.name IS NULL THEN '(missing)'
                       ELSE TYPE_NAME(c.system_type_id)
                            + CASE WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar')
                                   THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                                   ELSE '' END
                            + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END END,
       Matches  = CASE WHEN c.name IS NULL THEN 'MISSING'
                       WHEN TYPE_NAME(c.system_type_id)
                            + CASE WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar')
                                   THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                                   ELSE '' END <> e.TypeText THEN 'TYPE DIFFERS'
                       WHEN c.is_nullable <> e.Nullable THEN 'NULLABILITY DIFFERS'
                       ELSE 'ok' END
FROM (VALUES ('OfficerId', 'bigint', 0, 1), ('SessionId', 'uniqueidentifier', 1, 2), ('DeviceId', 'nvarchar(64)', 1, 3),
             ('ClientIp', 'nvarchar(64)', 1, 4), ('StartedAt', 'datetime', 0, 5), ('EndedAt', 'datetime', 1, 6),
             ('ReplacedCount', 'int', 0, 7)) e(ColumnName, TypeText, Nullable, Ord)
LEFT JOIN sys.columns c
       ON c.object_id = OBJECT_ID(N'dbo.GpsOfficerSession') AND c.name = e.ColumnName
ORDER BY e.Ord;

-- 4.2 The procedure: expect 3 parameters and CreatedByDb41 = 1.
SELECT name, modify_date,
       params        = (SELECT COUNT(*) FROM sys.parameters p WHERE p.object_id = o.object_id),
       CreatedByDb41 = CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE N'%db/41[_]master[_]UddSignIn%' THEN 1 ELSE 0 END
FROM sys.objects o
WHERE o.object_id = OBJECT_ID(N'dbo.USP_S_Officer_ValidateOTP');

-- 4.3 Officers locked by it (no mobiles or names shown).
SELECT LockedBySignIn = COUNT(*)
FROM dbo.mst_Officer
WHERE ISNULL(Ofcr_Active, 0) = 0
  AND Ofcr_AdditionalInfo LIKE N'%wrong OTPs (Property GPS sign-in)%';
GO

/*
-- 4.4 Trying it does WRITE: a wrong code counts toward the lock, a right one is used up.
--     Only with a test officer's mobile and a code from the server log (Otp:Sender ServerLog).
EXEC dbo.USP_S_Officer_ValidateOTP @OTP = <code>, @MobileNO = N'<test mobile>', @Source = N'<Otp:Source>';

-- Unlocking an officer (administrator). Only rows this procedure locked: a row deactivated for
-- any other reason stays as it is. The lock note(s) come off Ofcr_AdditionalInfo, leaving the
-- text that was there before the first lock (db/37's undo matches that text whole).
UPDATE dbo.mst_Officer
   SET Ofcr_Active = 1, NoOfFailedAttempt = 0, Ofcr_Locked_Date = NULL,
       Ofcr_AdditionalInfo = CASE WHEN Ofcr_AdditionalInfo LIKE N'Locked [0-9][0-9][0-9][0-9]-%' THEN NULL
                                  WHEN PATINDEX(N'% | Locked [0-9][0-9][0-9][0-9]-%', Ofcr_AdditionalInfo) > 0
                                  THEN LEFT(Ofcr_AdditionalInfo, PATINDEX(N'% | Locked [0-9][0-9][0-9][0-9]-%', Ofcr_AdditionalInfo) - 1)
                                  ELSE Ofcr_AdditionalInfo END
 WHERE Ofcr_MNo = N'<officer mobile>'
   AND ISNULL(Ofcr_Active, 0) = 0
   AND Ofcr_AdditionalInfo LIKE N'%wrong OTPs (Property GPS sign-in)%';
*/

-------------------------------------------------------------------------------
-- 5. UNDO (commented out) - drops only what this script created. Lock-outs it made are not
--    undone (see the unlock statement in 4.4). Dropping the table signs every officer out
--    and forgets every phone binding.
-------------------------------------------------------------------------------
/*
USE UDD_MASTERDB;
GO
IF DB_NAME() = 'UDD_MASTERDB'
   AND CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    IF OBJECT_DEFINITION(OBJECT_ID(N'dbo.USP_S_Officer_ValidateOTP')) LIKE N'%db/41[_]master[_]UddSignIn%'
        DROP PROCEDURE dbo.USP_S_Officer_ValidateOTP;

    IF EXISTS (SELECT 1 FROM sys.extended_properties ep
               WHERE ep.class = 1 AND ep.major_id = OBJECT_ID(N'dbo.GpsOfficerSession') AND ep.minor_id = 0
                 AND ep.name = N'CreatedBy'
                 AND CONVERT(nvarchar(200), ep.value) = N'PropertyGpsApi db/41_master_UddSignIn.sql')
        DROP TABLE dbo.GpsOfficerSession;
END
GO
*/
