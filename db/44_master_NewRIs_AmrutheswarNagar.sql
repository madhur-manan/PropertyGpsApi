/*
    18 new Revenue Inspectors (role 117) for the Amrutheswar Nagar ward, in UDD_MASTERDB.

    Each RI gets the ward as their OWN ward in mst_Officer (Ofcr_ULBId + Ofcr_WardId), so no
    mst_Ofcr_WardMapping rows are needed: the API falls back to the officer's own ward.
    All 18 see the same cases; the allotment decides who works one.

    BEFORE RUNNING:
      1. Run the ward query first and put Amrutheswar Nagar's ULBCode and WardNumber into
         @ULBCode / @WardNumber in BOTH section 2 and section 3.
      2. Fill in the 18 names and 10-digit mobiles in #ri (section 0). Keep the filled copy
         outside git - this repo copy has empty slots.
      3. Run the WHOLE script in one go (#ri lives for the SSMS session). Look at the section 2
         grids: every column must say 'ok' and the ward name must be Amrutheswar Nagar.

    Every row carries the marker 'PROPERTY GPS RI (db/44)'. Section 4 undoes it (commented out).
    Each RI signs in with an OTP to their mobile and is bound to the first phone they use.
    Sections: 0 the list, 1 guard, 2 checks (read-only), 3 create, 4 undo.
*/

USE UDD_MASTERDB;
GO

-------------------------------------------------------------------------------
-- 0. The RIs: name and 10-digit mobile
-------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#ri') IS NOT NULL DROP TABLE #ri;
CREATE TABLE #ri (Seq int IDENTITY PRIMARY KEY, Name nvarchar(200) NOT NULL, Mobile nvarchar(30) NOT NULL);
INSERT INTO #ri (Name, Mobile) VALUES
    (N'<name>', N'<mobile>'),   --  1
    (N'<name>', N'<mobile>'),   --  2
    (N'<name>', N'<mobile>'),   --  3
    (N'<name>', N'<mobile>'),   --  4
    (N'<name>', N'<mobile>'),   --  5
    (N'<name>', N'<mobile>'),   --  6
    (N'<name>', N'<mobile>'),   --  7
    (N'<name>', N'<mobile>'),   --  8
    (N'<name>', N'<mobile>'),   --  9
    (N'<name>', N'<mobile>'),   -- 10
    (N'<name>', N'<mobile>'),   -- 11
    (N'<name>', N'<mobile>'),   -- 12
    (N'<name>', N'<mobile>'),   -- 13
    (N'<name>', N'<mobile>'),   -- 14
    (N'<name>', N'<mobile>'),   -- 15
    (N'<name>', N'<mobile>'),   -- 16
    (N'<name>', N'<mobile>'),   -- 17
    (N'<name>', N'<mobile>');   -- 18
GO

-------------------------------------------------------------------------------
-- 1. GUARD - shows where you are; refuses anything but UDD_MASTERDB
-------------------------------------------------------------------------------
-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_MASTERDB'
BEGIN
    RAISERROR('Refusing to run: open UDD_MASTERDB first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. CHECKS (read-only)
-------------------------------------------------------------------------------
DECLARE @ULBCode    int          = 0;        -- <-- Amrutheswar Nagar's ULBCode
DECLARE @WardNumber nvarchar(20) = N'<ward>'; -- <-- Amrutheswar Nagar's WardNumber

-- Expect ONE row, and the name must be Amrutheswar Nagar.
SELECT TOP (1) ULBCode, ULBName = LTRIM(RTRIM(REPLACE(ULBName_EN, CHAR(9), ''))),
       WardNumber = LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))),
       WardName   = LTRIM(RTRIM(REPLACE(WardName_EN, CHAR(9), ''))), DistrictCode
FROM dbo.mst_AROMapping
WHERE ULBCode = @ULBCode AND LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))) = @WardNumber;

-- Expect 'ok' in every column and AlreadyRI empty.
SELECT r.Seq, r.Name, r.Mobile,
       Name_     = CASE WHEN r.Name LIKE N'<%' OR LTRIM(r.Name) = N'' THEN 'FILL IN' ELSE 'ok' END,
       Mobile_   = CASE WHEN r.Mobile LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' THEN 'ok' ELSE 'NOT A 10-DIGIT MOBILE' END,
       InList    = CASE WHEN (SELECT COUNT(*) FROM #ri x WHERE x.Mobile = r.Mobile) > 1 THEN 'REPEATED' ELSE 'ok' END,
       TestNo    = CASE WHEN r.Mobile LIKE N'999999999[1-5]' THEN 'FIXED-OTP TEST NUMBER' ELSE 'ok' END,
       AlreadyRI = (SELECT TOP (1) CONCAT(o.Ofcr_Id, ' ', o.Ofcr_Name, ' (ward ', o.Ofcr_WardId, ')')
                    FROM dbo.mst_Officer o WHERE o.Ofcr_MNo = r.Mobile AND o.Ofcr_RoleId = 117)
FROM #ri r
ORDER BY r.Seq;

-- RIs already on this ward (for information).
SELECT ExistingRI = Ofcr_Id, Ofcr_Name, Ofcr_Active
FROM dbo.mst_Officer
WHERE Ofcr_RoleId = 117 AND Ofcr_ULBId = @ULBCode AND Ofcr_WardId = @WardNumber;
GO

-------------------------------------------------------------------------------
-- 3. CREATE (one transaction - all 18 or none)
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @ULBCode    int          = 0;        -- <-- same as section 2
    DECLARE @WardNumber nvarchar(20) = N'<ward>'; -- <-- same as section 2
    DECLARE @Marker     nvarchar(60) = N'PROPERTY GPS RI (db/44)';

    IF NOT EXISTS (SELECT 1 FROM dbo.mst_AROMapping
                   WHERE ULBCode = @ULBCode AND LTRIM(RTRIM(REPLACE(WardNumber, CHAR(9), ''))) = @WardNumber)
        THROW 50001, 'Set @ULBCode / @WardNumber to a ward that is in mst_AROMapping. Nothing was created.', 1;
    IF (SELECT COUNT(*) FROM #ri) <> 18
        THROW 50002, '#ri must have exactly 18 rows. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri WHERE Name LIKE N'<%' OR LTRIM(Name) = N''
                  OR Mobile NOT LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]')
        THROW 50003, 'Fill in every name and a 10-digit mobile in #ri (section 0). Nothing was created.', 1;
    IF EXISTS (SELECT Mobile FROM #ri GROUP BY Mobile HAVING COUNT(*) > 1)
        THROW 50004, 'A mobile is repeated in #ri. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri WHERE Mobile LIKE N'999999999[1-5]')
        THROW 50005, 'A mobile is one of the fixed-OTP test numbers. Use real phones. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r JOIN dbo.mst_Officer o ON o.Ofcr_MNo = r.Mobile AND o.Ofcr_RoleId = 117)
        THROW 50006, 'A mobile is already an RI (117) - see section 2. Nothing was created.', 1;

    INSERT INTO dbo.mst_Officer
        (Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_DistId, Ofcr_ULBId, Ofcr_WardId,
         Ofcr_Active, Ofcr_AdditionalInfo, Ofcr_CDte, NoOfFailedAttempt)
    SELECT LTRIM(RTRIM(r.Name)), r.Mobile, 117,
           (SELECT TOP (1) CONVERT(int, DistrictCode) FROM dbo.mst_AROMapping WHERE ULBCode = @ULBCode),
           @ULBCode, @WardNumber, 1, @Marker, GETDATE(), 0
    FROM #ri r
    ORDER BY r.Seq;

    IF @@ROWCOUNT <> 18
        THROW 50007, 'Did not insert exactly 18 officers. Nothing was created.', 1;

COMMIT TRAN;
GO

-- What the API will give each new RI at sign-in: expect 18 rows, all Amrutheswar Nagar.
SELECT o.Ofcr_Id, o.Ofcr_Name, o.Ofcr_MNo, o.Ofcr_RoleId, o.Ofcr_Active, o.Ofcr_ULBId, o.Ofcr_WardId,
       WardName = (SELECT TOP (1) LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), ''))) FROM dbo.mst_AROMapping a
                   WHERE a.ULBCode = o.Ofcr_ULBId AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = o.Ofcr_WardId)
FROM dbo.mst_Officer o
WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/44)'
ORDER BY o.Ofcr_Id;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. UNDO (commented out) - removes the 18 RIs and their phone bindings
-------------------------------------------------------------------------------
/*
USE UDD_MASTERDB;
SET XACT_ABORT ON;
BEGIN TRAN;
    DELETE s FROM dbo.GpsOfficerSession s JOIN dbo.mst_Officer o ON o.Ofcr_Id = s.OfficerId
    WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/44)';
    DELETE FROM dbo.mst_Officer WHERE Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/44)';
    SELECT OfficersRemoved = @@ROWCOUNT;
COMMIT TRAN;
*/
