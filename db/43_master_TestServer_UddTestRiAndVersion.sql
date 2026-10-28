/*
    Test data for the TEST SERVER's UDD_MASTERDB, so the UDD build (API + app 1.2.0) can be
    signed into and version-checked there.

    APPLIES TO UDD_MASTERDB ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.

      1. One test RI: role 117, own area ULB 43 (Siruguppa) / ward 11 - today the only ULB + ward
         on the test server with cases the ward sync can send. FILL IN @name and @mobile (in
         sections 2 and 3). The mobile must be a real phone: on the server the OTP goes by SMS,
         and UDD's fixed test numbers (9999999991-5, code 999999) are refused outside Development.
      2. The Android row the version check reads: latest 1.2.0, minimum 1.1.0, NOT forced -
         a forced update would send phones to a Play Store page that does not exist yet.
         Added only when there is no active Android row; an existing one is shown, not changed.

    Every row carries the marker 'PROPERTY GPS TEST (db/43)'. Section 4 undoes it (commented out).
    Sections: 1 guard, 2 checks (read-only), 3 apply, 4 undo.
*/

USE UDD_MASTERDB;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_MASTERDB'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_MASTERDB on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. CHECKS (read-only)
-------------------------------------------------------------------------------
DECLARE @mobile nvarchar(30) = N'<10-digit mobile>';

-- Expect: no row (the mobile is free for a 117), or only rows of other roles.
SELECT Existing = Ofcr_Id, Ofcr_RoleId, Ofcr_Active, Ofcr_ULBId, Ofcr_WardId
FROM dbo.mst_Officer WHERE Ofcr_MNo = @mobile;

-- Expect: ULB 43 / ward 11 found, with its names.
SELECT TOP (1) ULBCode, ULBName = LTRIM(RTRIM(REPLACE(ULBName_EN, CHAR(9), ''))),
       WardNumber = LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))),
       WardName = LTRIM(RTRIM(REPLACE(WardName_EN, CHAR(9), ''))), DistrictCode
FROM dbo.mst_AROMapping
WHERE ULBCode = 43 AND LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))) = N'11';

-- The active Android row(s) today; none expected.
SELECT AndroidRow = AppVersion_Id, Current_Version, MinRequired_Version, Latest_Version, IsForceUpdate, Download_URL
FROM dbo.Mst_AppVersion WHERE App_Platform = N'Android' AND IsActive = 1;
GO

-------------------------------------------------------------------------------
-- 3. APPLY (one transaction)
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @name   nvarchar(200) = N'<officer name>';
    DECLARE @mobile nvarchar(30)  = N'<10-digit mobile>';
    DECLARE @Marker nvarchar(60)  = N'PROPERTY GPS TEST (db/43)';

    IF @name LIKE N'<%' OR @mobile NOT LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'
        THROW 50001, 'Fill in @name and a 10-digit @mobile in section 3. Nothing was changed.', 1;
    IF @mobile LIKE N'999999999[1-5]'
        THROW 50002, 'That is one of UDD''s fixed-OTP test numbers, refused on the server. Use a real phone. Nothing was changed.', 1;
    IF EXISTS (SELECT 1 FROM dbo.mst_Officer WHERE Ofcr_MNo = @mobile AND Ofcr_RoleId = 117)
        THROW 50003, 'That mobile is already an RI (117) here - see section 2. Nothing was changed.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.mst_AROMapping
                   WHERE ULBCode = 43 AND LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))) = N'11')
        THROW 50004, 'ULB 43 / ward 11 is not in mst_AROMapping here. Nothing was changed.', 1;

    INSERT INTO dbo.mst_Officer
        (Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_DistId, Ofcr_ULBId, Ofcr_WardId,
         Ofcr_Active, Ofcr_AdditionalInfo, Ofcr_CDte, NoOfFailedAttempt)
    VALUES
        (@name, @mobile, 117,
         (SELECT TOP (1) CONVERT(int, DistrictCode) FROM dbo.mst_AROMapping WHERE ULBCode = 43),
         43, N'11', 1, @Marker, GETDATE(), 0);

    IF NOT EXISTS (SELECT 1 FROM dbo.Mst_AppVersion WHERE App_Platform = N'Android' AND IsActive = 1)
        INSERT INTO dbo.Mst_AppVersion
            (App_Platform, Current_Version, MinRequired_Version, Latest_Version, IsForceUpdate, IsActive,
             Release_Date, Update_Message)
        VALUES (N'Android', N'1.2.0', N'1.1.0', N'1.2.0', 0, 1, GETDATE(), @Marker);

COMMIT TRAN;
GO

-- What the API will give the new RI at sign-in: one area, ULB 43 / ward 11, with names.
SELECT o.Ofcr_Id, o.Ofcr_Name, o.Ofcr_RoleId, o.Ofcr_Active, o.Ofcr_ULBId, o.Ofcr_WardId,
       ULBName = (SELECT TOP (1) LTRIM(RTRIM(REPLACE(a.ULBName_EN, CHAR(9), ''))) FROM dbo.mst_AROMapping a WHERE a.ULBCode = o.Ofcr_ULBId),
       WardName = (SELECT TOP (1) LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), ''))) FROM dbo.mst_AROMapping a
                   WHERE a.ULBCode = o.Ofcr_ULBId AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = o.Ofcr_WardId)
FROM dbo.mst_Officer o
WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS TEST (db/43)';

SELECT AndroidRow = AppVersion_Id, Current_Version, MinRequired_Version, Latest_Version, IsForceUpdate, IsActive
FROM dbo.Mst_AppVersion WHERE App_Platform = N'Android';
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. UNDO (commented out) - removes what db/43 added, the RI's phone binding included
-------------------------------------------------------------------------------
/*
USE UDD_MASTERDB;
SET XACT_ABORT ON;
BEGIN TRAN;
    DELETE s FROM dbo.GpsOfficerSession s JOIN dbo.mst_Officer o ON o.Ofcr_Id = s.OfficerId
    WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS TEST (db/43)';
    DELETE FROM dbo.mst_Officer    WHERE Ofcr_AdditionalInfo = N'PROPERTY GPS TEST (db/43)';
    DELETE FROM dbo.Mst_AppVersion WHERE Update_Message      = N'PROPERTY GPS TEST (db/43)';
COMMIT TRAN;
*/
