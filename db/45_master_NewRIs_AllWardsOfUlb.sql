/*
    New Revenue Inspectors (role 117) from a list of MOBILE + ULB CODE, each mapped to EVERY
    ward of their ULB, in UDD_MASTERDB.

    Tables WRITTEN: only dbo.mst_Officer and dbo.mst_Ofcr_WardMapping.
    Table READ:     dbo.mst_AROMapping (the ULB's wards and their names).

    How each RI is set up:
      - mst_Officer: role 117, active, Ofcr_ULBId = the ULB, Ofcr_WardId = the ULB's lowest
        ward number (the "own ward"), name 'RI <mobile>' (rename later if you like).
      - mst_Ofcr_WardMapping: one active row for every OTHER ward of that ULB.
      The API gives the RI their own ward + every active mapping row, so the app's ward
      dropdown lists every ward of the ULB.

    The mapping table's ULB column is Map_ULBId on the test server and Map_CorpId on older
    copies. The script picks the right one itself, the same way the API does, and fills the
    name/audit columns only where they exist.

    BEFORE RUNNING:
      1. Fill in #ri (section 0): one row per RI, mobile + ULB code. Any number of rows.
         Keep the filled copy outside git - this repo copy has empty slots.
      2. Run the WHOLE script in one go (#ri lives for the SSMS session).
      3. Check the section 2 grids: every check column 'ok', and the ULB names / ward counts
         are what you expect. Check the server name in section 1 (test or live).

    Every officer carries the marker 'PROPERTY GPS RI (db/45)'. Section 4 undoes it (commented out).
    Sections: 0 the list, 1 guard, 2 checks (read-only), 3 create, 4 undo.
*/

USE UDD_MASTERDB;
GO

-------------------------------------------------------------------------------
-- 0. The RIs: 10-digit mobile + ULB code
-------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#ri') IS NOT NULL DROP TABLE #ri;
CREATE TABLE #ri (Seq int IDENTITY PRIMARY KEY, Mobile nvarchar(30) NOT NULL, ULBCode int NOT NULL);
INSERT INTO #ri (Mobile, ULBCode) VALUES
    (N'<mobile>', 0),
    (N'<mobile>', 0);
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
-- Every ward of every ULB in the list (ward number + names, TABs removed).
IF OBJECT_ID('tempdb..#wards') IS NOT NULL DROP TABLE #wards;
SELECT a.ULBCode,
       WardNumber   = LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), N''))),
       WardName     = MAX(LTRIM(RTRIM(REPLACE(a.WardName_EN,     CHAR(9), N'')))),
       ULBName      = MAX(LTRIM(RTRIM(REPLACE(a.ULBName_EN,      CHAR(9), N'')))),
       DistrictCode = MAX(CONVERT(int, a.DistrictCode)),
       DistrictName = MAX(LTRIM(RTRIM(REPLACE(a.DistrictName_EN, CHAR(9), N''))))
INTO #wards
FROM dbo.mst_AROMapping a
WHERE a.ULBCode IN (SELECT ULBCode FROM #ri)
  AND NULLIF(LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), N''))), N'') IS NOT NULL
GROUP BY a.ULBCode, LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), N'')));

-- Expect 'ok' in every check column.
SELECT r.Seq, r.Mobile, r.ULBCode,
       ULBName   = (SELECT TOP (1) w.ULBName FROM #wards w WHERE w.ULBCode = r.ULBCode),
       Wards     = (SELECT COUNT(*) FROM #wards w WHERE w.ULBCode = r.ULBCode),
       Mobile_   = CASE WHEN r.Mobile LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' THEN 'ok' ELSE 'NOT A 10-DIGIT MOBILE' END,
       InList    = CASE WHEN (SELECT COUNT(*) FROM #ri x WHERE x.Mobile = r.Mobile) > 1 THEN 'REPEATED' ELSE 'ok' END,
       TestNo    = CASE WHEN r.Mobile LIKE N'999999999[1-5]' THEN 'FIXED-OTP TEST NUMBER' ELSE 'ok' END,
       NewMobile = CASE WHEN EXISTS (SELECT 1 FROM dbo.mst_Officer o WHERE o.Ofcr_MNo = r.Mobile)
                        THEN 'ALREADY AN OFFICER' ELSE 'ok' END,
       ULB_      = CASE WHEN EXISTS (SELECT 1 FROM #wards w WHERE w.ULBCode = r.ULBCode)
                        THEN 'ok' ELSE 'ULB NOT IN mst_AROMapping' END
FROM #ri r
ORDER BY r.Seq;

-- How the mapping table looks here: which ULB column, which optional columns.
SELECT UlbColumn    = CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_ULBId')  IS NOT NULL THEN N'Map_ULBId'
                           WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_CorpId') IS NOT NULL THEN N'Map_CorpId'
                           ELSE N'NEITHER - STOP' END,
       Map_WardId   = CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_WardId')     IS NOT NULL THEN 'ok' ELSE 'MISSING - STOP' END,
       Map_OfcrId   = CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_OfcrId')     IS NOT NULL THEN 'ok' ELSE 'MISSING - STOP' END,
       Map_OfcrRole = CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'Map_OfcrRoleId') IS NOT NULL THEN 'ok' ELSE 'MISSING - STOP' END,
       isActive     = CASE WHEN COL_LENGTH(N'dbo.mst_Ofcr_WardMapping', N'isActive')       IS NOT NULL THEN 'ok' ELSE 'MISSING - STOP' END;
GO

-------------------------------------------------------------------------------
-- 3. CREATE (one transaction - every RI and every mapping, or nothing)
-------------------------------------------------------------------------------
SET XACT_ABORT ON;
BEGIN TRAN;

    DECLARE @Marker nvarchar(60) = N'PROPERTY GPS RI (db/45)';
    DECLARE @T nvarchar(200) = N'dbo.mst_Ofcr_WardMapping';
    DECLARE @UlbCol sysname =
        CASE WHEN COL_LENGTH(@T, N'Map_ULBId')  IS NOT NULL THEN N'Map_ULBId'
             WHEN COL_LENGTH(@T, N'Map_CorpId') IS NOT NULL THEN N'Map_CorpId' END;

    IF @UlbCol IS NULL OR COL_LENGTH(@T, N'Map_WardId') IS NULL OR COL_LENGTH(@T, N'Map_OfcrId') IS NULL
       OR COL_LENGTH(@T, N'Map_OfcrRoleId') IS NULL OR COL_LENGTH(@T, N'isActive') IS NULL
        THROW 50001, 'mst_Ofcr_WardMapping lacks a needed column - see section 2. Nothing was created.', 1;
    IF NOT EXISTS (SELECT 1 FROM #ri)
        THROW 50002, '#ri is empty. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri WHERE Mobile NOT LIKE '[6-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]')
        THROW 50003, 'Fill in a 10-digit mobile on every row of #ri (section 0). Nothing was created.', 1;
    IF EXISTS (SELECT Mobile FROM #ri GROUP BY Mobile HAVING COUNT(*) > 1)
        THROW 50004, 'A mobile is repeated in #ri. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri WHERE Mobile LIKE N'999999999[1-5]')
        THROW 50005, 'A mobile is one of the fixed-OTP test numbers. Use real phones. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r JOIN dbo.mst_Officer o ON o.Ofcr_MNo = r.Mobile)
        THROW 50006, 'A mobile already belongs to an officer - see section 2. Nothing was created.', 1;
    IF EXISTS (SELECT 1 FROM #ri r WHERE NOT EXISTS (SELECT 1 FROM #wards w WHERE w.ULBCode = r.ULBCode))
        THROW 50007, 'A ULB code is not in mst_AROMapping - see section 2. Nothing was created.', 1;

    -- Own ward = the ULB's lowest ward number (numeric order where the number is numeric).
    IF OBJECT_ID('tempdb..#own') IS NOT NULL DROP TABLE #own;
    SELECT ULBCode, WardNumber, DistrictCode
    INTO #own
    FROM (SELECT w.*, rn = ROW_NUMBER() OVER (PARTITION BY w.ULBCode
                                             ORDER BY TRY_CONVERT(int, w.WardNumber), w.WardNumber)
          FROM #wards w) x
    WHERE x.rn = 1;

    -- 3a. The officers.
    IF OBJECT_ID('tempdb..#new') IS NOT NULL DROP TABLE #new;
    CREATE TABLE #new (OfcrId bigint NOT NULL, Mobile nvarchar(30) NOT NULL);

    INSERT INTO dbo.mst_Officer
        (Ofcr_Name, Ofcr_MNo, Ofcr_RoleId, Ofcr_DistId, Ofcr_ULBId, Ofcr_WardId,
         Ofcr_Active, Ofcr_AdditionalInfo, Ofcr_CDte, NoOfFailedAttempt)
    OUTPUT inserted.Ofcr_Id, inserted.Ofcr_MNo INTO #new (OfcrId, Mobile)
    SELECT N'RI ' + r.Mobile, r.Mobile, 117, w.DistrictCode, r.ULBCode, w.WardNumber,
           1, @Marker, GETDATE(), 0
    FROM #ri r
    JOIN #own w ON w.ULBCode = r.ULBCode
    ORDER BY r.Seq;

    IF (SELECT COUNT(*) FROM #new) <> (SELECT COUNT(*) FROM #ri)
        THROW 50008, 'Not every RI was created. Nothing was created.', 1;

    -- 3b. One mapping row per OTHER ward of the RI's ULB. Optional columns only where they exist.
    DECLARE @cols nvarchar(max) = N'Map_OfcrId, Map_OfcrRoleId, ' + QUOTENAME(@UlbCol) + N', Map_WardId, isActive';
    DECLARE @vals nvarchar(max) = N'n.OfcrId, 117, r.ULBCode, w.WardNumber, 1';

    IF COL_LENGTH(@T, N'Map_DistId')   IS NOT NULL SELECT @cols += N', Map_DistId',   @vals += N', w.DistrictCode';
    IF COL_LENGTH(@T, N'Map_DistName') IS NOT NULL SELECT @cols += N', Map_DistName', @vals += N', w.DistrictName';
    IF COL_LENGTH(@T, N'Map_ULBName')  IS NOT NULL SELECT @cols += N', Map_ULBName',  @vals += N', w.ULBName';
    IF COL_LENGTH(@T, N'Map_CorpName') IS NOT NULL SELECT @cols += N', Map_CorpName', @vals += N', w.ULBName';
    IF COL_LENGTH(@T, N'Map_WardName') IS NOT NULL SELECT @cols += N', Map_WardName', @vals += N', w.WardName';
    IF COL_LENGTH(@T, N'Cby')          IS NOT NULL SELECT @cols += N', Cby',          @vals += N', n.OfcrId';
    IF COL_LENGTH(@T, N'Cdate')        IS NOT NULL SELECT @cols += N', Cdate',        @vals += N', GETDATE()';
    IF COL_LENGTH(@T, N'Crole')        IS NOT NULL SELECT @cols += N', Crole',        @vals += N', 117';

    DECLARE @sql nvarchar(max) = N'
        INSERT INTO dbo.mst_Ofcr_WardMapping (' + @cols + N')
        SELECT ' + @vals + N'
        FROM #new n
        JOIN #ri r    ON r.Mobile = n.Mobile
        JOIN #wards w ON w.ULBCode = r.ULBCode
        JOIN #own o   ON o.ULBCode = r.ULBCode
        WHERE w.WardNumber <> o.WardNumber;';
    EXEC sys.sp_executesql @sql;

COMMIT TRAN;
GO

-- Result: each new RI and how many wards the app will offer. Wards must equal UlbWards.
SELECT o.Ofcr_Id, o.Ofcr_Name, o.Ofcr_MNo, o.Ofcr_RoleId, o.Ofcr_Active,
       o.Ofcr_ULBId, OwnWard = o.Ofcr_WardId,
       UlbWards = (SELECT COUNT(*) FROM #wards w WHERE w.ULBCode = o.Ofcr_ULBId),
       Wards    = 1 + (SELECT COUNT(*) FROM dbo.mst_Ofcr_WardMapping m
                       WHERE m.Map_OfcrId = o.Ofcr_Id AND m.Map_OfcrRoleId = 117 AND ISNULL(m.isActive, 1) = 1)
FROM dbo.mst_Officer o
WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/45)'
ORDER BY o.Ofcr_ULBId, o.Ofcr_Id;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 4. UNDO (commented out) - removes the mappings, then the officers. Only these two tables.
-------------------------------------------------------------------------------
/*
USE UDD_MASTERDB;
SET XACT_ABORT ON;
BEGIN TRAN;
    DELETE m FROM dbo.mst_Ofcr_WardMapping m
    JOIN dbo.mst_Officer o ON o.Ofcr_Id = m.Map_OfcrId
    WHERE o.Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/45)';
    SELECT MappingsRemoved = @@ROWCOUNT;

    DELETE FROM dbo.mst_Officer WHERE Ofcr_AdditionalInfo = N'PROPERTY GPS RI (db/45)';
    SELECT OfficersRemoved = @@ROWCOUNT;
COMMIT TRAN;
*/
