/*
    Test data: a second Revenue Inspector for the TEST server, copied from test officer 11320
    (same corporation, zone and ward - the ward the test applications are in), with its own
    name and mobile.

    APPLIES TO masterDB_prod ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    masterDB_prod is also the name of the LIVE master database, so the guard checks the
    server as well as the database name and refuses everything else - including the laptop.

    Fill in @name and @mobile in BOTH sections before running. Nothing is created if the
    mobile already belongs to an officer.

    The new officer signs in like 11320: OTP to the mobile (from the server log until the SMS
    gateway is switched on there), and is bound to the first phone they sign in on.
    Two RIs in one ward both see the ward's applications; the allotment decides who works one.

    Sections: 1 checks (read-only), 2 create, 3 undo (commented out).
*/

USE masterDB_prod;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'masterDB_prod'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open masterDB_prod on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1. Checks (read-only)
--    Expect: officer 11320 (role 116, active, with its corporation/zone/ward), no other row,
--    and 'ok'. If the mobile shows on another officer, pick another number.
-------------------------------------------------------------------------------
DECLARE @mobile nvarchar(30) = N'<10-digit mobile>';

SELECT Ofcr_Id, Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_CorporationId, Ofcr_ZoneId, Ofcr_WardId, Ofcr_Active
FROM dbo.mst_Officer WHERE Ofcr_Id = 11320 OR Ofcr_MNo = @mobile;

SELECT Map_OfcrId, Map_CorpId, Map_ZoneID, Map_WardId, Map_WardName, isActive
FROM dbo.mst_Ofcr_WardMapping WHERE Map_OfcrId = 11320;

SELECT FixedOtpList = CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.USP_I_OTP')) LIKE N'%' + @mobile + N'%'
                           THEN 'ON THE LIST - pick another number' ELSE 'ok' END;
GO

-------------------------------------------------------------------------------
-- 2. Create
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @name     nvarchar(200) = N'<officer name>';
    DECLARE @mobile   nvarchar(30)  = N'<10-digit mobile>';
    DECLARE @template bigint        = 11320;

    IF @mobile NOT LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'
        THROW 50001, 'Fill in a 10-digit mobile number in @mobile.', 1;
    IF @name LIKE N'<%'
        THROW 50002, 'Fill in the officer name in @name.', 1;
    IF EXISTS (SELECT 1 FROM dbo.mst_Officer WHERE Ofcr_MNo = @mobile)
        THROW 50003, 'That mobile already belongs to an officer. Nothing was created.', 1;

    INSERT INTO dbo.mst_Officer
        (Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_DistId, Ofcr_CorporationId, Ofcr_ZoneId,
         Ofcr_DivisionId, Ofcr_WardId, Ofcr_Active, Ofcr_AprCode, Ofcr_RejCode,
         Ofcr_AdditionalInfo, Ofcr_CBy, Ofcr_CDte, Ofcr_CRole, NoOfFailedAttempt,
         Ofcr_Level, Ofcr_ApplicationId, Ofcr_Center)
    SELECT @name, @mobile, t.Ofcr_RoleId, t.Ofcr_DistId, t.Ofcr_CorporationId, t.Ofcr_ZoneId,
           t.Ofcr_DivisionId, t.Ofcr_WardId, 1, t.Ofcr_AprCode, t.Ofcr_RejCode,
           N'PROPERTY GPS TEST OFFICER (db/31, copied from 11320)', NULL, GETDATE(), t.Ofcr_CRole, 0,
           t.Ofcr_Level, t.Ofcr_ApplicationId, t.Ofcr_Center
    FROM dbo.mst_Officer t
    WHERE t.Ofcr_Id = @template;

    IF @@ROWCOUNT <> 1
        THROW 50004, 'Test officer 11320 was not found. Nothing was created.', 1;

    DECLARE @newId bigint = SCOPE_IDENTITY();

    -- The template's extra wards, if it has any (11320 may rely on its own ward alone).
    INSERT INTO dbo.mst_Ofcr_WardMapping
        (Map_OfcrId, Map_OfcrRoleId, Map_CorpId, Map_CorpName, Map_ZoneID, Map_ZoneName,
         Map_WardId, Map_WardName, Map_Additionalinfo, isActive, Cby, Cdate, Crole)
    SELECT @newId, m.Map_OfcrRoleId, m.Map_CorpId, m.Map_CorpName, m.Map_ZoneID, m.Map_ZoneName,
           m.Map_WardId, m.Map_WardName, N'PROPERTY GPS TEST OFFICER (db/31)', 1, NULL, GETDATE(), m.Crole
    FROM dbo.mst_Ofcr_WardMapping m
    WHERE m.Map_OfcrId = @template AND ISNULL(m.isActive, 1) = 1;

    SELECT NewOfficerId = @newId, WardMappingsCopied = @@ROWCOUNT;

COMMIT TRAN;
GO

-- What the API will give the new officer (the same query it runs at sign-in): expect ward 54.
SELECT o.Ofcr_Id, o.Ofcr_Name, o.Ofcr_RoleId, o.Ofcr_Active,
       GbaZoneId = aro.GBAZoneID, BbmpWardId = aro.BBMPWardId, BbmpWardName = aro.BBMPWardName
FROM dbo.mst_Officer o
LEFT JOIN dbo.mst_Ofcr_WardMapping wm
       ON wm.Map_OfcrId = o.Ofcr_Id AND wm.Map_OfcrRoleId = o.Ofcr_RoleId AND ISNULL(wm.isActive, 1) = 1
JOIN (SELECT DISTINCT GBAZoneID, easthiULBNAME_CorpId, BBMPWardId, BBMPWardName FROM dbo.mst_AROMapping) aro
       ON aro.GBAZoneID = ISNULL(wm.Map_ZoneID, o.Ofcr_ZoneId)
      AND aro.easthiULBNAME_CorpId = ISNULL(wm.Map_CorpId, o.Ofcr_CorporationId)
      AND aro.BBMPWardId = ISNULL(wm.Map_WardId, o.Ofcr_WardId)
WHERE o.Ofcr_AdditionalInfo LIKE N'PROPERTY GPS TEST OFFICER (db/31%';
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 3. Undo - removes every officer this script created, its wards and its phone binding
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DELETE s FROM dbo.GpsOfficerSession s
    JOIN dbo.mst_Officer o ON o.Ofcr_Id = s.OfficerId
    WHERE o.Ofcr_AdditionalInfo LIKE N'PROPERTY GPS TEST OFFICER (db/31%';

    DELETE m FROM dbo.mst_Ofcr_WardMapping m
    WHERE m.Map_Additionalinfo = N'PROPERTY GPS TEST OFFICER (db/31)';

    DELETE FROM dbo.mst_Officer
    WHERE Ofcr_AdditionalInfo LIKE N'PROPERTY GPS TEST OFFICER (db/31%';
    SELECT OfficersRemoved = @@ROWCOUNT;
COMMIT TRAN;
*/
