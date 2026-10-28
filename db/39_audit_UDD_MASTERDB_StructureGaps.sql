/*
    READ-ONLY AUDIT: what UDD_MASTERDB is missing - STRUCTURE ONLY (tables, columns, procedures,
    parameters, result columns) - for Property GPS to use it instead of masterDB_prod.
    Rows / data are not compared.

    RUN ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74), which holds masterDB_prod,
    UDD_MASTERDB and UDD_KHATABTOA side by side. Any database can be open in SSMS.
    It writes nothing: it reads the three databases' catalogs and fills #temp tables only.
    Run the WHOLE script in one go.

    Result grids:
      1. Run info - server and the three databases' collations.
      2. Gaps - one row per gap, most serious first:
           MUST  - Property GPS fails on UDD_MASTERDB until this is fixed;
           CHECK - may break or behave differently; look before switching;
           INFO  - worth knowing; no action needed for the switch.
      3. Procedure differences - for each sign-in procedure present in both databases, the
         lines found in only one of the two versions (spaces, tabs and case ignored; blank
         lines skipped). Empty = the two versions match.
      4. Full text of USP_S_ValidateOfficer and USP_I_OTP in both databases - click a cell to
         open it. Only needed if grid 3 is hard to read (a block comment hides lines from it).
    Send grids 2 and 3 back (screenshot, or right-click the grid > Save Results As...).

    Where the checks come from:
      A. The 2026-10-16 inventory of D:\PropertyGpsApi: every table and column the API reads on
         its master connection (OfficerService, MasterService, OfficerSessionStore), the
         parameters and result columns of the 4 sign-in procedures, and what our UDD_KHATABTOA
         procedures read from masterDB_prod (road check, officer lookup, version check, land use,
         ward sync). Columns only our test scripts use (db/25, db/31, db/37, db/38) are CHECK.
      B. Worked out here from the server itself:
         - every table and column the masterDB_prod sign-in procedures use, and those
           UDD_MASTERDB's own versions use;
         - every masterDB_prod reference reachable in UDD_KHATABTOA from what Property GPS
           calls or writes: its procedures, the functions/views they use, and the triggers on
           its tables - including BBMP's own objects that are not in the repo. These are the
           objects to re-point at the switch.
*/

SET NOCOUNT ON;
SELECT RunningOn     = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
       masterDB_prod = CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'masterDB_prod', 'Collation')),
       UDD_MASTERDB  = CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_MASTERDB', 'Collation')),
       UDD_KHATABTOA = CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_KHATABTOA', 'Collation'));

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
IF DB_ID(N'masterDB_prod') IS NULL OR DB_ID(N'UDD_MASTERDB') IS NULL OR DB_ID(N'UDD_KHATABTOA') IS NULL
BEGIN
    RAISERROR('Refusing to run: masterDB_prod, UDD_MASTERDB and UDD_KHATABTOA must all be on this server - run it on the TEST DB server (DB-NEW-KHATA-TE).', 16, 1);
    SET NOEXEC ON;
END
GO

-- No user transaction anywhere: each CATCH below only has to undo a failed INSERT ... EXEC.
SET NOCOUNT ON;
SET XACT_ABORT OFF;
SET IMPLICIT_TRANSACTIONS OFF;

-- Every name below is compared case-insensitively (Latin1_General_CI_AS), whatever the
-- databases' own collations are.
IF OBJECT_ID('tempdb..#gaps')       IS NOT NULL DROP TABLE #gaps;
IF OBJECT_ID('tempdb..#need')       IS NOT NULL DROP TABLE #need;
IF OBJECT_ID('tempdb..#objs')       IS NOT NULL DROP TABLE #objs;
IF OBJECT_ID('tempdb..#cols')       IS NOT NULL DROP TABLE #cols;
IF OBJECT_ID('tempdb..#deps')       IS NOT NULL DROP TABLE #deps;
IF OBJECT_ID('tempdb..#params')     IS NOT NULL DROP TABLE #params;
IF OBJECT_ID('tempdb..#apiParams')  IS NOT NULL DROP TABLE #apiParams;
IF OBJECT_ID('tempdb..#results')    IS NOT NULL DROP TABLE #results;
IF OBJECT_ID('tempdb..#apiResult')  IS NOT NULL DROP TABLE #apiResult;
IF OBJECT_ID('tempdb..#roots')      IS NOT NULL DROP TABLE #roots;
IF OBJECT_ID('tempdb..#start')      IS NOT NULL DROP TABLE #start;
IF OBJECT_ID('tempdb..#reach')      IS NOT NULL DROP TABLE #reach;
IF OBJECT_ID('tempdb..#mref')       IS NOT NULL DROP TABLE #mref;
IF OBJECT_ID('tempdb..#xcols')      IS NOT NULL DROP TABLE #xcols;
IF OBJECT_ID('tempdb..#lines')      IS NOT NULL DROP TABLE #lines;

CREATE TABLE #gaps (
    Severity   varchar(5)     NOT NULL,
    Area       nvarchar(60)   COLLATE Latin1_General_CI_AS NOT NULL,
    ObjectName nvarchar(400)  COLLATE Latin1_General_CI_AS NOT NULL,
    Item       nvarchar(400)  COLLATE Latin1_General_CI_AS NOT NULL,
    Gap        nvarchar(400)  COLLATE Latin1_General_CI_AS NOT NULL,
    Detail     nvarchar(2000) COLLATE Latin1_General_CI_AS NULL);

-------------------------------------------------------------------------------
-- Catalogs of the two master databases (schema dbo)
-------------------------------------------------------------------------------
CREATE TABLE #objs (
    Db         nvarchar(20)  COLLATE Latin1_General_CI_AS NOT NULL,
    ObjectName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    TypeDesc   nvarchar(60)  COLLATE Latin1_General_CI_AS NOT NULL,
    Base       nvarchar(1035) COLLATE Latin1_General_CI_AS NULL);       -- synonyms: what they point at

INSERT INTO #objs (Db, ObjectName, TypeDesc, Base)
SELECT N'masterDB_prod', o.name, o.type_desc, sy.base_object_name
FROM masterDB_prod.sys.objects o
JOIN masterDB_prod.sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN masterDB_prod.sys.synonyms sy ON sy.object_id = o.object_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SN');

INSERT INTO #objs (Db, ObjectName, TypeDesc, Base)
SELECT N'UDD_MASTERDB', o.name, o.type_desc, sy.base_object_name
FROM UDD_MASTERDB.sys.objects o
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN UDD_MASTERDB.sys.synonyms sy ON sy.object_id = o.object_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SN');

CREATE TABLE #cols (
    Db         nvarchar(20)  COLLATE Latin1_General_CI_AS NOT NULL,
    ObjectName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ColumnName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    TypeText   nvarchar(80)  COLLATE Latin1_General_CI_AS NOT NULL,
    IsNullable bit NOT NULL,
    IsIdentity bit NOT NULL);

INSERT INTO #cols (Db, ObjectName, ColumnName, TypeText, IsNullable, IsIdentity)
SELECT N'masterDB_prod', o.name, c.name,
       t.name + CASE WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary')
                     THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'nvarchar', N'nchar')
                     THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'decimal', N'numeric')
                     THEN N'(' + CAST(c.precision AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
                     ELSE N'' END,
       c.is_nullable, c.is_identity
FROM masterDB_prod.sys.columns c
JOIN masterDB_prod.sys.objects o ON o.object_id = c.object_id
JOIN masterDB_prod.sys.schemas s ON s.schema_id = o.schema_id
JOIN masterDB_prod.sys.types   t ON t.user_type_id = c.user_type_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V');

INSERT INTO #cols (Db, ObjectName, ColumnName, TypeText, IsNullable, IsIdentity)
SELECT N'UDD_MASTERDB', o.name, c.name,
       t.name + CASE WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary')
                     THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'nvarchar', N'nchar')
                     THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'decimal', N'numeric')
                     THEN N'(' + CAST(c.precision AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
                     ELSE N'' END,
       c.is_nullable, c.is_identity
FROM UDD_MASTERDB.sys.columns c
JOIN UDD_MASTERDB.sys.objects o ON o.object_id = c.object_id
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = o.schema_id
JOIN UDD_MASTERDB.sys.types   t ON t.user_type_id = c.user_type_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V');

IF CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_MASTERDB', 'Collation')) LIKE N'%[_]CS[_]%'
   OR CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_MASTERDB', 'Collation')) LIKE N'%[_]BIN%'
    INSERT INTO #gaps VALUES ('CHECK', N'collation', N'UDD_MASTERDB', N'(database)',
        N'case-sensitive collation',
        N'Table, column and parameter names must match the API''s spelling exactly (for example @MobileNo for USP_I_OTP but @MobileNO for USP_S_Officer_ValidateOTP).');

-------------------------------------------------------------------------------
-- A. What Property GPS needs (from the code). MUST = the app; CHECK = our test scripts only.
-------------------------------------------------------------------------------
CREATE TABLE #need (
    Severity   varchar(5)    NOT NULL,
    ObjectName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ColumnName nvarchar(128) COLLATE Latin1_General_CI_AS NULL,          -- NULL = a procedure
    NeededBy   nvarchar(300) COLLATE Latin1_General_CI_AS NOT NULL);

INSERT INTO #need (Severity, ObjectName, ColumnName, NeededBy) VALUES
 -- officers
 ('MUST',  N'mst_Officer', N'Ofcr_Id',            N'API sign-in, officer wards, auth/me; officer lookup on submit (USP_IU_BtoA_StatusDetail_Officer)'),
 ('MUST',  N'mst_Officer', N'Ofcr_RoleId',        N'API sign-in, officer wards, auth/me; officer lookup on submit'),
 ('MUST',  N'mst_Officer', N'Ofcr_MNo',           N'API auth/me; officer lookup on submit'),
 ('MUST',  N'mst_Officer', N'Ofcr_Name',          N'API auth/me; officer lookup on submit'),
 ('MUST',  N'mst_Officer', N'Ofcr_ZoneId',        N'API officer wards, auth/me'),
 ('MUST',  N'mst_Officer', N'Ofcr_WardId',        N'API officer wards, auth/me'),
 ('MUST',  N'mst_Officer', N'Ofcr_CorporationId', N'API officer wards'),
 ('MUST',  N'mst_Officer', N'Ofcr_Active',        N'API officer wards, auth/me'),
 ('MUST',  N'mst_Officer', N'Ofcr_DivisionId',    N'USP_S_Officer_ValidateOTP result'),
 ('MUST',  N'mst_Officer', N'NoOfFailedAttempt',  N'USP_S_Officer_ValidateOTP lockout'),
 ('CHECK', N'mst_Officer', N'Ofcr_DistId',        N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_AprCode',       N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_RejCode',       N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_AdditionalInfo',N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_CBy',           N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_CDte',          N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_CRole',         N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_Level',         N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_ApplicationId', N'test-officer scripts db/31, db/37'),
 ('CHECK', N'mst_Officer', N'Ofcr_Center',        N'test-officer scripts db/31, db/37'),
 -- officer wards
 ('MUST',  N'mst_Ofcr_WardMapping', N'Map_OfcrId',     N'API officer wards (OfficerService)'),
 ('MUST',  N'mst_Ofcr_WardMapping', N'Map_OfcrRoleId', N'API officer wards (OfficerService)'),
 ('MUST',  N'mst_Ofcr_WardMapping', N'isActive',       N'API officer wards (OfficerService)'),
 ('MUST',  N'mst_Ofcr_WardMapping', N'Map_ZoneID',     N'API officer wards (OfficerService)'),
 ('MUST',  N'mst_Ofcr_WardMapping', N'Map_CorpId',     N'API officer wards (OfficerService)'),
 ('MUST',  N'mst_Ofcr_WardMapping', N'Map_WardId',     N'API officer wards (OfficerService)'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Map_CorpName',   N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Map_ZoneName',   N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Map_WardName',   N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Map_Additionalinfo', N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Cby',            N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Cdate',          N'test-officer script db/31'),
 ('CHECK', N'mst_Ofcr_WardMapping', N'Crole',          N'test-officer script db/31'),
 -- GBA zone / ward mapping
 ('MUST',  N'mst_AROMapping', N'GBAZoneID',            N'API zones, wards, streets, officer wards; ward sync; road check'),
 ('MUST',  N'mst_AROMapping', N'GBAZoneName_En',       N'API zones, officer wards; ward sync'),
 ('MUST',  N'mst_AROMapping', N'BBMPZoneID',           N'API streets, officer wards; road check'),
 ('MUST',  N'mst_AROMapping', N'BBMPZoneName',         N'API officer wards'),
 ('MUST',  N'mst_AROMapping', N'BBMPWardId',           N'API wards, streets, officer wards; ward sync; road check'),
 ('MUST',  N'mst_AROMapping', N'BBMPWardName',         N'API wards, officer wards'),
 ('MUST',  N'mst_AROMapping', N'easthiULBNAME_CorpId', N'API zones, streets, officer wards'),
 ('MUST',  N'mst_AROMapping', N'cityCorportationName', N'API officer wards'),
 ('MUST',  N'mst_AROMapping', N'BBMPStreetId',         N'road check on submit (USP_IU_BtoA_SiteRoadDetails_Officer)'),
 -- KSRSAC roads
 ('MUST',  N'MstRoadKSRAC', N'Road_ID',      N'API street list; road check on submit'),
 ('MUST',  N'MstRoadKSRAC', N'Road_Name',    N'API street list; road check on submit'),
 ('MUST',  N'MstRoadKSRAC', N'BBMPZoneID',   N'API street list; road check on submit'),
 ('MUST',  N'MstRoadKSRAC', N'BBMPWardId',   N'API street list; road check on submit'),
 ('MUST',  N'MstRoadKSRAC', N'Road_Active',  N'API street list'),
 ('CHECK', N'MstRoadKSRAC', N'Road_KSRACId', N'test-case script db/38'),
 -- one phone per officer
 ('MUST',  N'GpsOfficerSession', N'OfficerId',     N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'SessionId',     N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'DeviceId',      N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'ClientIp',      N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'StartedAt',     N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'EndedAt',       N'API one-phone sign-in (OfficerSessionStore)'),
 ('MUST',  N'GpsOfficerSession', N'ReplacedCount', N'API one-phone sign-in (OfficerSessionStore)'),
 -- OTP and login records
 ('MUST',  N'OTP_Tran',   N'DeviceId', N'USP_I_OTP writes it; USP_S_Officer_ValidateOTP matches on it'),
 ('MUST',  N'OTP_Tran',   N'ACTIVE',   N'USP_S_Officer_ValidateOTP (fresh / used code)'),
 ('MUST',  N'Login_Tran', N'AppID',    N'usp_InsertLoginData (login record)'),
 -- app version check
 ('MUST',  N'Mst_AppVersion', N'App_Platform',        N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'IsActive',            N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'Current_Version',     N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'AppVersion_Id',       N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'Latest_Version',      N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'MinRequired_Version', N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'Update_Message',      N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'Download_URL',        N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'File_Size_MB',        N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'Release_Notes',       N'version check (USP_CheckMobileAppVersion)'),
 ('MUST',  N'Mst_AppVersion', N'IsForceUpdate',       N'version check (USP_CheckMobileAppVersion)'),
 ('CHECK', N'Mst_AppVersion', N'Release_Date',        N'version-row script db/25'),
 ('CHECK', N'Mst_AppVersion', N'UDte',                N'version-row script db/25'),
 -- land use on submit
 ('MUST',  N'mst_KsrsacMasterPlan2015', N'GpsLandUseTypeValue', N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 ('MUST',  N'mst_KsrsacMasterPlan2015', N'BBMPLandUseTypeId',   N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 ('MUST',  N'mst_KsrsacMasterPlan2015', N'BBMPLAndUseTypeNAme', N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 -- sign-in procedures the API calls on its master connection
 ('MUST',  N'USP_S_ValidateOfficer',     NULL, N'API: send OTP (is this an officer?)'),
 ('MUST',  N'USP_I_OTP',                 NULL, N'API: send OTP (store the code)'),
 ('MUST',  N'USP_S_Officer_ValidateOTP', NULL, N'API: verify OTP / sign in'),
 ('MUST',  N'usp_InsertLoginData',       NULL, N'API: login record');

-- Needed objects missing from UDD_MASTERDB
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT CASE WHEN MAX(CASE WHEN n.Severity = 'MUST' THEN 1 ELSE 0 END) = 1 THEN 'MUST' ELSE 'CHECK' END,
       N'needed object', n.ObjectName, N'(whole object)', N'missing in UDD_MASTERDB',
       LEFT(N'Needed by: ' + (SELECT STRING_AGG(CAST(x.NeededBy AS nvarchar(max)), N'; ')
                              FROM (SELECT DISTINCT n2.NeededBy FROM #need n2 WHERE n2.ObjectName = n.ObjectName) x)
            + CASE WHEN EXISTS (SELECT 1 FROM #objs m WHERE m.Db = N'masterDB_prod' AND m.ObjectName = n.ObjectName)
                   THEN N' | present in masterDB_prod' ELSE N' | not in masterDB_prod either' END, 2000)
FROM #need n
WHERE NOT EXISTS (SELECT 1 FROM #objs u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = n.ObjectName)
GROUP BY n.ObjectName;

-- Needed objects that are synonyms in UDD_MASTERDB (their columns are checked at the base object)
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT DISTINCT 'CHECK', N'needed object', n.ObjectName, N'(whole object)', N'is a synonym in UDD_MASTERDB',
       N'Points at ' + u.Base + N' - its columns were NOT checked; check them there before switching.'
FROM #need n
JOIN #objs u ON u.Db = N'UDD_MASTERDB' AND u.ObjectName = n.ObjectName AND u.TypeDesc = N'SYNONYM';

-- Needed columns missing from tables / views that do exist in UDD_MASTERDB
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT n.Severity, N'needed column', n.ObjectName, n.ColumnName, N'column missing in UDD_MASTERDB',
       N'Needed by: ' + n.NeededBy
       + ISNULL(N' | masterDB_prod has it as ' + m.TypeText + CASE WHEN m.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END,
                N' | not in masterDB_prod either')
FROM #need n
LEFT JOIN #cols m ON m.Db = N'masterDB_prod' AND m.ObjectName = n.ObjectName AND m.ColumnName = n.ColumnName
WHERE n.ColumnName IS NOT NULL
  AND EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = n.ObjectName
                                      AND o.TypeDesc IN (N'USER_TABLE', N'VIEW'))
  AND NOT EXISTS (SELECT 1 FROM #cols u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = n.ObjectName
                                          AND u.ColumnName = n.ColumnName);

-- Needed columns present in both but defined differently
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'CHECK', N'needed column', n.ObjectName, n.ColumnName,
       CASE WHEN m.TypeText <> u.TypeText THEN N'type differs from masterDB_prod'
            WHEN m.IsIdentity <> u.IsIdentity THEN N'identity differs from masterDB_prod'
            ELSE N'NOT NULL in UDD_MASTERDB but nullable in masterDB_prod' END,
       N'masterDB_prod: ' + m.TypeText + CASE WHEN m.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END
                          + CASE WHEN m.IsIdentity = 1 THEN N' IDENTITY' ELSE N'' END
       + N' | UDD_MASTERDB: ' + u.TypeText + CASE WHEN u.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END
                          + CASE WHEN u.IsIdentity = 1 THEN N' IDENTITY' ELSE N'' END
FROM #need n
JOIN #cols m ON m.Db = N'masterDB_prod' AND m.ObjectName = n.ObjectName AND m.ColumnName = n.ColumnName
JOIN #cols u ON u.Db = N'UDD_MASTERDB'  AND u.ObjectName = n.ObjectName AND u.ColumnName = n.ColumnName
WHERE n.ColumnName IS NOT NULL
  AND (m.TypeText <> u.TypeText OR m.IsIdentity <> u.IsIdentity OR (m.IsNullable = 1 AND u.IsNullable = 0));

-- Needed tables whose identity column is missing in UDD_MASTERDB (USP_I_OTP returns the new
-- OTP row's identity; the test-officer scripts read the new Ofcr_Id the same way)
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT DISTINCT 'CHECK', N'needed column', t.ObjectName, m.ColumnName, N'identity column has no identity in UDD_MASTERDB',
       N'masterDB_prod: ' + m.ColumnName + N' is IDENTITY; UDD_MASTERDB''s table has no identity column'
FROM (VALUES (N'OTP_Tran'), (N'mst_Officer')) t(ObjectName)
JOIN #cols m ON m.Db = N'masterDB_prod' AND m.ObjectName = t.ObjectName AND m.IsIdentity = 1
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = t.ObjectName AND o.TypeDesc = N'USER_TABLE')
  AND NOT EXISTS (SELECT 1 FROM #cols u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = t.ObjectName AND u.IsIdentity = 1)
  -- an identity column listed in #need is already reported by the check above
  AND NOT EXISTS (SELECT 1 FROM #need n WHERE n.ObjectName = t.ObjectName AND n.ColumnName = m.ColumnName);

-------------------------------------------------------------------------------
-- B1. What the sign-in procedures use, worked out from their compiled bodies, in both
--     databases. The masterDB_prod versions are what UDD_MASTERDB gets if they are copied.
-------------------------------------------------------------------------------
CREATE TABLE #deps (
    SourceDb     nvarchar(20)  COLLATE Latin1_General_CI_AS NOT NULL,
    SourceObject nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    RefDb        nvarchar(128) COLLATE Latin1_General_CI_AS NULL,
    RefSchema    nvarchar(128) COLLATE Latin1_General_CI_AS NULL,
    RefEntity    nvarchar(128) COLLATE Latin1_General_CI_AS NULL,
    RefColumn    nvarchar(128) COLLATE Latin1_General_CI_AS NULL);

DECLARE @db nvarchar(20), @obj nvarchar(128), @exec nvarchar(300);

DECLARE dep_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT o.Db, o.ObjectName FROM #objs o
    WHERE o.TypeDesc = N'SQL_STORED_PROCEDURE'
      AND o.ObjectName IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData');
OPEN dep_cur;
FETCH NEXT FROM dep_cur INTO @db, @obj;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @exec = QUOTENAME(@db) + N'.sys.sp_executesql';
    BEGIN TRY
        INSERT INTO #deps (SourceDb, SourceObject, RefDb, RefSchema, RefEntity, RefColumn)
        EXEC @exec
             N'DECLARE @n nvarchar(300) = N''dbo.'' + QUOTENAME(@obj);
               SELECT @db, @obj, d.referenced_database_name, d.referenced_schema_name,
                      d.referenced_entity_name, d.referenced_minor_name
               FROM sys.dm_sql_referenced_entities(@n, N''OBJECT'') d
               WHERE d.referenced_class_desc = N''OBJECT_OR_COLUMN'';',
             N'@db nvarchar(20), @obj nvarchar(128)', @db = @db, @obj = @obj;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;   -- an error inside INSERT ... EXEC dooms its own transaction
        INSERT INTO #gaps VALUES ('INFO', N'sign-in procedure', @db + N'.dbo.' + @obj, N'(what it uses)',
            N'column-level list not available - object-level only', LEFT(ERROR_MESSAGE(), 2000));
        BEGIN TRY
            INSERT INTO #deps (SourceDb, SourceObject, RefDb, RefSchema, RefEntity, RefColumn)
            EXEC @exec
                 N'SELECT @db, @obj, d.referenced_database_name, d.referenced_schema_name, d.referenced_entity_name, NULL
                   FROM sys.sql_expression_dependencies d
                   WHERE d.referencing_id = OBJECT_ID(N''dbo.'' + QUOTENAME(@obj)) AND d.referenced_class = 1;',
                 N'@db nvarchar(20), @obj nvarchar(128)', @db = @db, @obj = @obj;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            INSERT INTO #gaps VALUES ('INFO', N'sign-in procedure', @db + N'.dbo.' + @obj, N'(what it uses)',
                N'could not list what it uses', LEFT(ERROR_MESSAGE(), 2000));
        END CATCH;
    END CATCH;
    FETCH NEXT FROM dep_cur INTO @db, @obj;
END
CLOSE dep_cur;
DEALLOCATE dep_cur;

-- Objects they use that UDD_MASTERDB lacks
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT CASE WHEN MAX(d.IsMust) = 1 THEN 'MUST' ELSE 'CHECK' END,
       N'used by sign-in procedures', d.RefEntity, N'(whole object)', N'missing in UDD_MASTERDB',
       LEFT(N'Used by: ' + STRING_AGG(CAST(d.SourceDb + N'.dbo.' + d.SourceObject AS nvarchar(max)), N', '), 2000)
FROM (SELECT DISTINCT dd.SourceDb, dd.SourceObject, dd.RefEntity,
             IsMust = CASE WHEN dd.SourceDb = N'UDD_MASTERDB'
                             OR NOT EXISTS (SELECT 1 FROM #objs p WHERE p.Db = N'UDD_MASTERDB' AND p.ObjectName = dd.SourceObject)
                           THEN 1 ELSE 0 END
      FROM #deps dd
      WHERE (dd.RefDb IS NULL OR dd.RefDb = dd.SourceDb)            -- own database, with or without its name
        AND ISNULL(dd.RefSchema, N'dbo') = N'dbo' AND dd.RefEntity IS NOT NULL) d
WHERE NOT EXISTS (SELECT 1 FROM #objs u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = d.RefEntity)
GROUP BY d.RefEntity;

-- Columns they use that UDD_MASTERDB's table lacks
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT CASE WHEN MAX(d.IsMust) = 1 THEN 'MUST' ELSE 'CHECK' END,
       N'used by sign-in procedures', d.RefEntity, d.RefColumn, N'column missing in UDD_MASTERDB',
       LEFT(N'Used by: ' + STRING_AGG(CAST(d.SourceDb + N'.dbo.' + d.SourceObject AS nvarchar(max)), N', '), 2000)
FROM (SELECT DISTINCT dd.SourceDb, dd.SourceObject, dd.RefEntity, dd.RefColumn,
             IsMust = CASE WHEN dd.SourceDb = N'UDD_MASTERDB'
                             OR NOT EXISTS (SELECT 1 FROM #objs p WHERE p.Db = N'UDD_MASTERDB' AND p.ObjectName = dd.SourceObject)
                           THEN 1 ELSE 0 END
      FROM #deps dd
      WHERE (dd.RefDb IS NULL OR dd.RefDb = dd.SourceDb)
        AND ISNULL(dd.RefSchema, N'dbo') = N'dbo' AND dd.RefColumn IS NOT NULL) d
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = d.RefEntity
                                    AND o.TypeDesc IN (N'USER_TABLE', N'VIEW'))
  AND NOT EXISTS (SELECT 1 FROM #cols u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = d.RefEntity
                                          AND u.ColumnName = d.RefColumn)
GROUP BY d.RefEntity, d.RefColumn;

-- Database names written inside the sign-in procedures. masterDB_prod named in either version
-- would keep reading the old master from UDD_MASTERDB (a copied procedure must be rewritten).
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT DISTINCT CASE WHEN d.RefDb = N'masterDB_prod' THEN 'CHECK' ELSE 'INFO' END,
       N'used by sign-in procedures', d.SourceDb + N'.dbo.' + d.SourceObject,
       d.RefDb + N'.' + ISNULL(d.RefSchema, N'dbo') + N'.' + d.RefEntity,
       CASE WHEN d.RefDb = N'masterDB_prod' THEN N'names masterDB_prod - from UDD_MASTERDB it would still read the old master'
            ELSE N'reads another database by name' END,
       NULL
FROM #deps d
WHERE d.RefDb IS NOT NULL AND d.RefEntity IS NOT NULL;

-------------------------------------------------------------------------------
-- B2. Sign-in procedure parameters: what the API sends vs UDD_MASTERDB's versions
-------------------------------------------------------------------------------
CREATE TABLE #params (
    Db        nvarchar(20)  COLLATE Latin1_General_CI_AS NOT NULL,
    ProcName  nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ParamName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    TypeText  nvarchar(80)  COLLATE Latin1_General_CI_AS NOT NULL,
    IsOutput  bit NOT NULL);

INSERT INTO #params (Db, ProcName, ParamName, TypeText, IsOutput)
SELECT N'masterDB_prod', p.name, pa.name,
       t.name + CASE WHEN t.name IN (N'varchar', N'char') THEN N'(' + CASE WHEN pa.max_length = -1 THEN N'max' ELSE CAST(pa.max_length AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'nvarchar', N'nchar') THEN N'(' + CASE WHEN pa.max_length = -1 THEN N'max' ELSE CAST(pa.max_length / 2 AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'decimal', N'numeric') THEN N'(' + CAST(pa.precision AS nvarchar(5)) + N',' + CAST(pa.scale AS nvarchar(5)) + N')'
                     ELSE N'' END,
       pa.is_output
FROM masterDB_prod.sys.procedures p
JOIN masterDB_prod.sys.schemas s ON s.schema_id = p.schema_id
JOIN masterDB_prod.sys.parameters pa ON pa.object_id = p.object_id
JOIN masterDB_prod.sys.types t ON t.user_type_id = pa.user_type_id
WHERE s.name = N'dbo'
  AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData');

INSERT INTO #params (Db, ProcName, ParamName, TypeText, IsOutput)
SELECT N'UDD_MASTERDB', p.name, pa.name,
       t.name + CASE WHEN t.name IN (N'varchar', N'char') THEN N'(' + CASE WHEN pa.max_length = -1 THEN N'max' ELSE CAST(pa.max_length AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'nvarchar', N'nchar') THEN N'(' + CASE WHEN pa.max_length = -1 THEN N'max' ELSE CAST(pa.max_length / 2 AS nvarchar(10)) END + N')'
                     WHEN t.name IN (N'decimal', N'numeric') THEN N'(' + CAST(pa.precision AS nvarchar(5)) + N',' + CAST(pa.scale AS nvarchar(5)) + N')'
                     ELSE N'' END,
       pa.is_output
FROM UDD_MASTERDB.sys.procedures p
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = p.schema_id
JOIN UDD_MASTERDB.sys.parameters pa ON pa.object_id = p.object_id
JOIN UDD_MASTERDB.sys.types t ON t.user_type_id = pa.user_type_id
WHERE s.name = N'dbo'
  AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData');

CREATE TABLE #apiParams (
    ProcName  nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ParamName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL);
INSERT INTO #apiParams (ProcName, ParamName) VALUES
 (N'USP_S_ValidateOfficer', N'@USR_MOBILENO'), (N'USP_S_ValidateOfficer', N'@Ofcr_RoleId'),
 (N'USP_I_OTP', N'@OTP'), (N'USP_I_OTP', N'@MobileNo'), (N'USP_I_OTP', N'@DeviceId'), (N'USP_I_OTP', N'@Source'),
 (N'USP_S_Officer_ValidateOTP', N'@OTP'), (N'USP_S_Officer_ValidateOTP', N'@MobileNO'), (N'USP_S_Officer_ValidateOTP', N'@Source'),
 (N'usp_InsertLoginData', N'@login_tran_id'), (N'usp_InsertLoginData', N'@mobile'), (N'usp_InsertLoginData', N'@macaddress'),
 (N'usp_InsertLoginData', N'@lanip'), (N'usp_InsertLoginData', N'@localip'), (N'usp_InsertLoginData', N'@additionalinfo'),
 (N'usp_InsertLoginData', N'@cBy'), (N'usp_InsertLoginData', N'@CRole'), (N'usp_InsertLoginData', N'@AppID');

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'MUST', N'sign-in parameters', a.ProcName, a.ParamName, N'parameter missing in UDD_MASTERDB''s version',
       N'The API sends ' + a.ParamName + N'; the call fails if the procedure has no such parameter.'
FROM #apiParams a
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = a.ProcName AND o.TypeDesc = N'SQL_STORED_PROCEDURE')
  AND NOT EXISTS (SELECT 1 FROM #params p WHERE p.Db = N'UDD_MASTERDB' AND p.ProcName = a.ProcName AND p.ParamName = a.ParamName);

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'CHECK', N'sign-in parameters', p.ProcName, p.ParamName, N'extra parameter in UDD_MASTERDB''s version',
       N'The API does not send it (' + p.TypeText + N'): fine only if the procedure gives it a default value.'
FROM #params p
WHERE p.Db = N'UDD_MASTERDB'
  AND NOT EXISTS (SELECT 1 FROM #apiParams a WHERE a.ProcName = p.ProcName AND a.ParamName = p.ParamName);

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'CHECK', N'sign-in parameters', u.ProcName, u.ParamName, N'parameter type differs from masterDB_prod',
       N'masterDB_prod: ' + m.TypeText + CASE WHEN m.IsOutput = 1 THEN N' OUTPUT' ELSE N'' END
       + N' | UDD_MASTERDB: ' + u.TypeText + CASE WHEN u.IsOutput = 1 THEN N' OUTPUT' ELSE N'' END
FROM #params u
JOIN #params m ON m.Db = N'masterDB_prod' AND m.ProcName = u.ProcName AND m.ParamName = u.ParamName
WHERE u.Db = N'UDD_MASTERDB' AND (u.TypeText <> m.TypeText OR u.IsOutput <> m.IsOutput);

-------------------------------------------------------------------------------
-- B3. Sign-in procedure result columns (the API maps them by name; a missing one is
--     silently empty, e.g. no IsValidOtp = every code "wrong")
-------------------------------------------------------------------------------
CREATE TABLE #results (
    Db           nvarchar(20)   COLLATE Latin1_General_CI_AS NOT NULL,
    ProcName     nvarchar(128)  COLLATE Latin1_General_CI_AS NOT NULL,
    Ordinal      int            NULL,
    ColName      nvarchar(128)  COLLATE Latin1_General_CI_AS NULL,
    TypeText     nvarchar(256)  COLLATE Latin1_General_CI_AS NULL,
    ErrorMessage nvarchar(4000) COLLATE Latin1_General_CI_AS NULL);

DECLARE res_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT o.Db, o.ObjectName FROM #objs o
    WHERE o.TypeDesc = N'SQL_STORED_PROCEDURE'
      AND o.ObjectName IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData');
OPEN res_cur;
FETCH NEXT FROM res_cur INTO @db, @obj;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @exec = QUOTENAME(@db) + N'.sys.sp_executesql';
    BEGIN TRY
        INSERT INTO #results (Db, ProcName, Ordinal, ColName, TypeText, ErrorMessage)
        EXEC @exec
             N'DECLARE @id int = OBJECT_ID(N''dbo.'' + QUOTENAME(@obj));
               SELECT @db, @obj, r.column_ordinal, r.name, r.system_type_name, r.error_message
               FROM sys.dm_exec_describe_first_result_set_for_object(@id, 0) r;',
             N'@db nvarchar(20), @obj nvarchar(128)', @db = @db, @obj = @obj;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        INSERT INTO #results (Db, ProcName, Ordinal, ColName, TypeText, ErrorMessage)
        VALUES (@db, @obj, NULL, NULL, NULL, ERROR_MESSAGE());
    END CATCH;
    FETCH NEXT FROM res_cur INTO @db, @obj;
END
CLOSE res_cur;
DEALLOCATE res_cur;

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT DISTINCT 'INFO', N'sign-in result columns', r.Db + N'.dbo.' + r.ProcName, N'(result columns)',
       N'could not be worked out from the definition', LEFT(r.ErrorMessage, 2000)
FROM #results r
WHERE r.ErrorMessage IS NOT NULL;

CREATE TABLE #apiResult (
    ProcName nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ColName  nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL);
INSERT INTO #apiResult (ProcName, ColName) VALUES
 (N'USP_S_Officer_ValidateOTP', N'IsValidOtp'),   (N'USP_S_Officer_ValidateOTP', N'Ofcr_Id'),
 (N'USP_S_Officer_ValidateOTP', N'Ofcr_MNo'),     (N'USP_S_Officer_ValidateOTP', N'Ofcr_RoleId'),
 (N'USP_S_Officer_ValidateOTP', N'Ofcr_Name'),    (N'USP_S_Officer_ValidateOTP', N'Ofcr_ZoneId'),
 (N'USP_S_Officer_ValidateOTP', N'Ofcr_DivisionId'), (N'USP_S_Officer_ValidateOTP', N'Ofcr_WardId'),
 (N'USP_S_Officer_ValidateOTP', N'ZoneId'),       (N'USP_S_Officer_ValidateOTP', N'ZoneName'),
 (N'USP_S_Officer_ValidateOTP', N'CorporationId'), (N'USP_S_Officer_ValidateOTP', N'CorporationName'),
 (N'USP_S_Officer_ValidateOTP', N'BBMPWardName'), (N'USP_S_Officer_ValidateOTP', N'RoleName');

-- A column the API reads is not returned
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT CASE WHEN x.Db = N'UDD_MASTERDB' THEN 'MUST' ELSE 'INFO' END,
       N'sign-in result columns', x.Db + N'.dbo.' + a.ProcName, a.ColName, N'result column the API reads is not returned',
       N'The API reads it by name; without it the value is silently empty.'
FROM #apiResult a
JOIN (SELECT DISTINCT Db, ProcName FROM #results r1
      WHERE r1.ColName IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM #results r2 WHERE r2.Db = r1.Db AND r2.ProcName = r1.ProcName AND r2.ErrorMessage IS NOT NULL)) x
  ON x.ProcName = a.ProcName
WHERE NOT EXISTS (SELECT 1 FROM #results r WHERE r.Db = x.Db AND r.ProcName = a.ProcName AND r.ColName = a.ColName);

-- USP_I_OTP: the API converts the first column of the first row to a decimal (the new OTP
-- row id). No result set is survivable (the id becomes 0, which verify never uses); a
-- non-numeric first column makes POST auth/otp/send fail.
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT CASE WHEN r.TypeText IS NULL THEN 'CHECK' ELSE 'MUST' END,
       N'sign-in result columns', N'UDD_MASTERDB.dbo.USP_I_OTP', N'(first column)',
       CASE WHEN r.TypeText IS NULL THEN N'returns no result set' ELSE N'does not return a number first' END,
       CASE WHEN r.TypeText IS NULL THEN N'The API then reports otpRequestId 0, which verify does not use - sign-in still works.'
            ELSE N'The API converts the first column to decimal; this type makes POST auth/otp/send fail. It returns: ' + r.TypeText END
FROM (SELECT one = 1) s
LEFT JOIN #results r ON r.Db = N'UDD_MASTERDB' AND r.ProcName = N'USP_I_OTP' AND r.Ordinal = 1
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = N'USP_I_OTP' AND o.TypeDesc = N'SQL_STORED_PROCEDURE')
  AND NOT EXISTS (SELECT 1 FROM #results e WHERE e.Db = N'UDD_MASTERDB' AND e.ProcName = N'USP_I_OTP' AND e.ErrorMessage IS NOT NULL)
  AND (r.TypeText IS NULL
       OR (r.TypeText NOT LIKE N'%int%' AND r.TypeText NOT LIKE N'numeric%' AND r.TypeText NOT LIKE N'decimal%'
           AND r.TypeText NOT LIKE N'float%' AND r.TypeText NOT LIKE N'real%' AND r.TypeText NOT LIKE N'%money%'
           AND r.TypeText <> N'bit'));

-- USP_S_ValidateOfficer: the API only checks that a row comes back for an officer
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'MUST', N'sign-in result columns', N'UDD_MASTERDB.dbo.USP_S_ValidateOfficer', N'(result set)',
       N'returns no result set',
       N'The API only checks that a row comes back; with no result set every OTP request is refused as not registered.'
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = N'USP_S_ValidateOfficer' AND o.TypeDesc = N'SQL_STORED_PROCEDURE')
  AND NOT EXISTS (SELECT 1 FROM #results r WHERE r.Db = N'UDD_MASTERDB' AND r.ProcName = N'USP_S_ValidateOfficer');

-- Result columns that differ between the two versions of a procedure present in both
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'INFO', N'sign-in result columns', ISNULL(m.ProcName, u.ProcName), ISNULL(m.ColName, u.ColName),
       CASE WHEN u.ColName IS NULL THEN N'returned only by masterDB_prod''s version'
            ELSE N'returned only by UDD_MASTERDB''s version' END,
       NULL
FROM (SELECT ProcName, ColName FROM #results WHERE Db = N'masterDB_prod' AND ColName IS NOT NULL) m
FULL JOIN (SELECT ProcName, ColName FROM #results WHERE Db = N'UDD_MASTERDB' AND ColName IS NOT NULL) u
       ON u.ProcName = m.ProcName AND u.ColName = m.ColName
WHERE (m.ColName IS NULL OR u.ColName IS NULL)
  AND ISNULL(m.ProcName, u.ProcName) IN (SELECT ProcName FROM #results WHERE Db = N'masterDB_prod' AND ColName IS NOT NULL)
  AND ISNULL(m.ProcName, u.ProcName) IN (SELECT ProcName FROM #results WHERE Db = N'UDD_MASTERDB'  AND ColName IS NOT NULL)
  AND ISNULL(m.ProcName, u.ProcName) <> N'USP_S_ValidateOfficer';   -- the API reads none of its columns

-------------------------------------------------------------------------------
-- B4. UDD_KHATABTOA: every masterDB_prod reference reachable from what Property GPS calls
--     or writes. Each must read UDD_MASTERDB after the switch, and what it reads must exist.
-------------------------------------------------------------------------------
CREATE TABLE #roots (
    Name nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL PRIMARY KEY,
    Why  nvarchar(200) COLLATE Latin1_General_CI_AS NOT NULL);
INSERT INTO #roots (Name, Why) VALUES
 -- procedures the API calls on UDD_KHATABTOA (appsettings StoredProcedures + VerificationSubmitService)
 (N'USP_S_GpsWardSync',                  N'ward sync'),
 (N'USP_U_GpsWardSyncAck',               N'ward sync'),
 (N'USP_S_GetAppDetails',                N'old fetch'),
 (N'USP_IU_Architect_AssignedApp',       N'allot'),
 (N'USP_U_GpsPushedDetails',             N'push status'),
 (N'USP_I_GPS_Push_RequestResponse_Tran',          N'push status'),
 (N'USP_I_BtoA_GPS_ApplicationWise_NotPushed_Tran', N'push status'),
 (N'USP_IU_BtoA_MainApp_Officer',        N'submit'),
 (N'USP_IU_BtoA_SiteRoadDetails_Officer',N'submit'),
 (N'USP_IU_BtoA_StatusDetail_Officer',   N'submit'),
 (N'USP_U_BtoA_GpsSubmitExtras_App',     N'submit'),
 (N'USP_U_BtoA_GpsRoadNoticeDocument',   N'submit'),
 (N'USP_U_BtoA_GpsCompleteAssignment',   N'submit'),
 (N'USP_CheckMobileAppVersion',          N'version check'),
 -- tables the API reads or writes directly (their triggers are followed too)
 (N'BtoAMainApp',                  N'API reads/writes'),
 (N'BtoA_EPIDMetaData',            N'API reads'),
 (N'BtoA_Architect_AssignedApp',   N'API reads/writes'),
 (N'BtoA_MainApp_Officer',         N'API reads/writes'),
 (N'BtoA_StatusDetail_Officer',    N'API reads/writes'),
 (N'BtoA_SiteRoadDetails_Officer', N'API reads/writes'),
 (N'BtoA_SiteRoadDetails',         N'API reads'),
 (N'BtoA_OwnerDetails',            N'API reads'),
 (N'BtoA_DocumentTran',            N'API reads/writes');

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'INFO', N'UDD_KHATABTOA', r.Name, N'(whole object)', N'used by Property GPS but not in UDD_KHATABTOA', r.Why
FROM #roots r
WHERE NOT EXISTS (SELECT 1 FROM UDD_KHATABTOA.sys.objects o
                  JOIN UDD_KHATABTOA.sys.schemas s ON s.schema_id = o.schema_id
                  WHERE s.name = N'dbo' AND o.name COLLATE Latin1_General_CI_AS = r.Name);

CREATE TABLE #start (
    object_id int NOT NULL PRIMARY KEY,
    Root      nvarchar(400) COLLATE Latin1_General_CI_AS NOT NULL);
INSERT INTO #start (object_id, Root)
SELECT o.object_id, r.Name + N' (' + r.Why + N')'
FROM UDD_KHATABTOA.sys.objects o
JOIN UDD_KHATABTOA.sys.schemas s ON s.schema_id = o.schema_id
JOIN #roots r ON r.Name = o.name COLLATE Latin1_General_CI_AS
WHERE s.name = N'dbo';

CREATE TABLE #reach (
    object_id int NOT NULL,
    Root      nvarchar(400) COLLATE Latin1_General_CI_AS NOT NULL);

-- Who uses what inside UDD_KHATABTOA. A call written without "dbo." (EXEC SomeProc) is stored
-- with no referenced_id; it is resolved here by name in dbo, so the walk does not stop there.
IF OBJECT_ID('tempdb..#edges') IS NOT NULL DROP TABLE #edges;
CREATE TABLE #edges (referencing_id int NOT NULL, ref_id int NOT NULL);
INSERT INTO #edges (referencing_id, ref_id)
SELECT DISTINCT d.referencing_id, COALESCE(d.referenced_id, o.object_id)
FROM UDD_KHATABTOA.sys.sql_expression_dependencies d
LEFT JOIN UDD_KHATABTOA.sys.objects o
       ON d.referenced_id IS NULL
      AND o.schema_id = 1                                      -- dbo
      AND o.name = d.referenced_entity_name
WHERE d.referencing_class = 1
  AND d.referenced_class = 1
  AND d.referenced_server_name IS NULL
  AND (d.referenced_database_name IS NULL
       OR d.referenced_database_name COLLATE Latin1_General_CI_AS = N'UDD_KHATABTOA')
  AND COALESCE(d.referenced_id, o.object_id) IS NOT NULL;

-- Walk from the roots; then add the triggers on every table reached (except tables the API
-- only reads - a trigger never fires on a read) and walk again, until no new trigger appears.
DECLARE @added int = 1, @rounds int = 0;
WHILE @added > 0 AND @rounds < 10
BEGIN
    TRUNCATE TABLE #reach;

    WITH walk AS (
        SELECT s.object_id, s.Root, Depth = 0
        FROM #start s
        UNION ALL
        SELECT e.ref_id, w.Root, w.Depth + 1
        FROM walk w
        JOIN #edges e ON e.referencing_id = w.object_id
        WHERE w.Depth < 5)
    INSERT INTO #reach (object_id, Root)
    SELECT DISTINCT object_id, Root FROM walk
    OPTION (MAXRECURSION 10);

    INSERT INTO #start (object_id, Root)
    SELECT t.object_id,
           N'trigger ' + t.name COLLATE Latin1_General_CI_AS + N' on ' + p.name COLLATE Latin1_General_CI_AS
    FROM UDD_KHATABTOA.sys.triggers t
    JOIN UDD_KHATABTOA.sys.objects p ON p.object_id = t.parent_id
    WHERE t.parent_id IN (SELECT object_id FROM #reach WHERE Root NOT LIKE N'% (API reads)')
      AND t.object_id NOT IN (SELECT object_id FROM #start);
    SET @added = @@ROWCOUNT;
    SET @rounds += 1;
END

IF @added > 0
    INSERT INTO #gaps VALUES ('INFO', N'UDD_KHATABTOA', N'(triggers)', N'(walk)',
        N'trigger chain deeper than 10 levels - the last triggers found were not followed', NULL);

CREATE TABLE #mref (
    ViaId      int NOT NULL,
    ViaObject  nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    ViaType    nvarchar(60)  COLLATE Latin1_General_CI_AS NOT NULL,
    RefEntity  nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL);

INSERT INTO #mref (ViaId, ViaObject, ViaType, RefEntity)
SELECT DISTINCT o.object_id, o.name, o.type_desc, d.referenced_entity_name
FROM (SELECT DISTINCT object_id FROM #reach) r
JOIN UDD_KHATABTOA.sys.objects o ON o.object_id = r.object_id
JOIN UDD_KHATABTOA.sys.sql_expression_dependencies d ON d.referencing_id = r.object_id
WHERE d.referenced_database_name COLLATE Latin1_General_CI_AS = N'masterDB_prod'
  AND d.referenced_entity_name IS NOT NULL;

-- Synonyms in UDD_KHATABTOA that point into masterDB_prod (they name it in no module text)
INSERT INTO #mref (ViaId, ViaObject, ViaType, RefEntity)
SELECT DISTINCT o.object_id, o.name, o.type_desc, PARSENAME(sy.base_object_name, 1)
FROM (SELECT DISTINCT object_id FROM #reach) r
JOIN UDD_KHATABTOA.sys.objects  o  ON o.object_id = r.object_id
JOIN UDD_KHATABTOA.sys.synonyms sy ON sy.object_id = r.object_id
WHERE PARSENAME(sy.base_object_name, 3) COLLATE Latin1_General_CI_AS = N'masterDB_prod'
  AND PARSENAME(sy.base_object_name, 1) IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM #mref x WHERE x.ViaId = o.object_id);

-- Every object to re-point at the switch
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'MUST', N're-point in UDD_KHATABTOA', N'UDD_KHATABTOA.dbo.' + m.ViaObject, N'masterDB_prod.dbo.' + m.RefEntity,
       N'reads masterDB_prod by name - must read UDD_MASTERDB after the switch',
       LEFT(N'(' + m.ViaType + N') Reached from: '
            + (SELECT STRING_AGG(CAST(x.Root AS nvarchar(max)), N'; ')
               FROM (SELECT DISTINCT r.Root FROM #reach r WHERE r.object_id = m.ViaId) x), 2000)
FROM #mref m;

-- What they read that UDD_MASTERDB lacks
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'MUST', N'used by UDD_KHATABTOA', m.RefEntity, N'(whole object)', N'missing in UDD_MASTERDB',
       LEFT(N'Read by: ' + STRING_AGG(CAST(N'UDD_KHATABTOA.dbo.' + m.ViaObject AS nvarchar(max)), N', '), 2000)
FROM #mref m
WHERE NOT EXISTS (SELECT 1 FROM #objs u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = m.RefEntity)
GROUP BY m.RefEntity;

-- Columns they read (best effort: SQL Server reports columns only where it can bind them)
CREATE TABLE #xcols (
    ViaObject nvarchar(128) COLLATE Latin1_General_CI_AS NOT NULL,
    RefEntity nvarchar(128) COLLATE Latin1_General_CI_AS NULL,
    RefColumn nvarchar(128) COLLATE Latin1_General_CI_AS NULL);

DECLARE @via nvarchar(128);
DECLARE via_cur CURSOR LOCAL FAST_FORWARD FOR SELECT DISTINCT ViaObject FROM #mref WHERE ViaType <> N'SYNONYM';
OPEN via_cur;
FETCH NEXT FROM via_cur INTO @via;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        INSERT INTO #xcols (ViaObject, RefEntity, RefColumn)
        EXEC [UDD_KHATABTOA].sys.sp_executesql
             N'DECLARE @n nvarchar(300) = N''dbo.'' + QUOTENAME(@obj);
               SELECT @obj, d.referenced_entity_name, d.referenced_minor_name
               FROM sys.dm_sql_referenced_entities(@n, N''OBJECT'') d
               WHERE d.referenced_database_name COLLATE Latin1_General_CI_AS = N''masterDB_prod''
                 AND d.referenced_minor_name IS NOT NULL;',
             N'@obj nvarchar(128)', @obj = @via;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        INSERT INTO #gaps VALUES ('INFO', N'used by UDD_KHATABTOA', N'UDD_KHATABTOA.dbo.' + @via, N'(columns)',
            N'column-level list not available', LEFT(ERROR_MESSAGE(), 2000));
    END CATCH;
    FETCH NEXT FROM via_cur INTO @via;
END
CLOSE via_cur;
DEALLOCATE via_cur;

INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'MUST', N'used by UDD_KHATABTOA', x.RefEntity, x.RefColumn, N'column missing in UDD_MASTERDB',
       LEFT(N'Read by: ' + STRING_AGG(CAST(N'UDD_KHATABTOA.dbo.' + x.ViaObject AS nvarchar(max)), N', '), 2000)
FROM (SELECT DISTINCT ViaObject, RefEntity, RefColumn FROM #xcols WHERE RefEntity IS NOT NULL) x
WHERE EXISTS (SELECT 1 FROM #objs o WHERE o.Db = N'UDD_MASTERDB' AND o.ObjectName = x.RefEntity
                                    AND o.TypeDesc IN (N'USER_TABLE', N'VIEW'))
  AND NOT EXISTS (SELECT 1 FROM #cols u WHERE u.Db = N'UDD_MASTERDB' AND u.ObjectName = x.RefEntity
                                          AND u.ColumnName = x.RefColumn)
GROUP BY x.RefEntity, x.RefColumn;

-- Reached objects that mention masterDB_prod only in their text (dynamic SQL, or a comment)
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'CHECK', N're-point in UDD_KHATABTOA', N'UDD_KHATABTOA.dbo.' + o.name COLLATE Latin1_General_CI_AS, N'(text)',
       N'mentions masterDB_prod but not as a plain reference',
       N'Look for dynamic SQL; a comment needs no change.'
FROM (SELECT DISTINCT object_id FROM #reach) r
JOIN UDD_KHATABTOA.sys.objects o     ON o.object_id = r.object_id
JOIN UDD_KHATABTOA.sys.sql_modules sm ON sm.object_id = r.object_id
WHERE sm.definition COLLATE Latin1_General_CI_AS LIKE N'%masterDB[_]prod%'
  AND NOT EXISTS (SELECT 1 FROM #mref m WHERE m.ViaId = r.object_id);

-- Everything else in UDD_KHATABTOA that reads masterDB_prod (other apps; not moved by us)
INSERT INTO #gaps (Severity, Area, ObjectName, Item, Gap, Detail)
SELECT 'INFO', N'other apps', N'UDD_KHATABTOA', N'(count)',
       N'objects outside Property GPS that also read masterDB_prod',
       CAST(COUNT(DISTINCT d.referencing_id) AS nvarchar(10))
       + N' objects. They keep reading masterDB_prod after Property GPS moves, so the two masters must stay in step until their owners move them.'
FROM UDD_KHATABTOA.sys.sql_expression_dependencies d
WHERE d.referenced_database_name COLLATE Latin1_General_CI_AS = N'masterDB_prod'
  AND d.referencing_id NOT IN (SELECT object_id FROM #reach);

-------------------------------------------------------------------------------
-- RESULT GRID 2: the gaps
-------------------------------------------------------------------------------
SELECT Severity = CASE MIN(CASE g.Severity WHEN 'MUST' THEN 1 WHEN 'CHECK' THEN 2 ELSE 3 END)
                      WHEN 1 THEN 'MUST' WHEN 2 THEN 'CHECK' ELSE 'INFO' END,
       Area = MIN(g.Area), g.ObjectName, g.Item, g.Gap,
       Detail = LEFT(STRING_AGG(CAST(ISNULL(g.Detail, N'') AS nvarchar(max)), N' || '), 4000)
FROM (SELECT DISTINCT Severity, Area, ObjectName, Item, Gap, Detail FROM #gaps) g
GROUP BY g.ObjectName, g.Item, g.Gap
ORDER BY MIN(CASE g.Severity WHEN 'MUST' THEN 1 WHEN 'CHECK' THEN 2 ELSE 3 END), MIN(g.Area), g.ObjectName, g.Item;

-------------------------------------------------------------------------------
-- RESULT GRID 3: line-by-line difference of the sign-in procedures present in both.
-- A line counts as matched only as often as it occurs on the other side (Occ), so a check
-- dropped from one of two statements that share it still shows.
-------------------------------------------------------------------------------
WITH defs AS (
    SELECT Db = CAST(N'masterDB_prod' AS nvarchar(20)) COLLATE Latin1_General_CI_AS,
           ProcName = CAST(p.name AS nvarchar(128)) COLLATE Latin1_General_CI_AS,
           Def = CAST(REPLACE(m.definition, NCHAR(13), N'') + NCHAR(10) AS nvarchar(max)) COLLATE Latin1_General_CI_AS
    FROM masterDB_prod.sys.procedures p
    JOIN masterDB_prod.sys.schemas s ON s.schema_id = p.schema_id
    JOIN masterDB_prod.sys.sql_modules m ON m.object_id = p.object_id
    WHERE s.name = N'dbo'
      AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData')
    UNION ALL
    SELECT CAST(N'UDD_MASTERDB' AS nvarchar(20)) COLLATE Latin1_General_CI_AS,
           CAST(p.name AS nvarchar(128)) COLLATE Latin1_General_CI_AS,
           CAST(REPLACE(m.definition, NCHAR(13), N'') + NCHAR(10) AS nvarchar(max)) COLLATE Latin1_General_CI_AS
    FROM UDD_MASTERDB.sys.procedures p
    JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = p.schema_id
    JOIN UDD_MASTERDB.sys.sql_modules m ON m.object_id = p.object_id
    WHERE s.name = N'dbo'
      AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP', N'USP_S_Officer_ValidateOTP', N'usp_InsertLoginData')),
lines AS (
    SELECT Db, ProcName, LineNum = 1,
           Line = CAST(LEFT(Def, CHARINDEX(NCHAR(10), Def) - 1) AS nvarchar(max)),
           Rest = CAST(STUFF(Def, 1, CHARINDEX(NCHAR(10), Def), N'') AS nvarchar(max))
    FROM defs
    UNION ALL
    SELECT Db, ProcName, LineNum + 1,
           CAST(LEFT(Rest, CHARINDEX(NCHAR(10), Rest) - 1) AS nvarchar(max)),
           CAST(STUFF(Rest, 1, CHARINDEX(NCHAR(10), Rest), N'') AS nvarchar(max))
    FROM lines
    WHERE Rest <> N'')
SELECT Db, ProcName, LineNum, Line,
       -- spaces, tabs and case ignored
       LineKey = HASHBYTES('SHA2_256', UPPER(REPLACE(REPLACE(Line, N' ', N''), NCHAR(9), N'')))
INTO #lines
FROM lines
OPTION (MAXRECURSION 0);

SELECT a.ProcName, OnlyIn = a.Db, a.LineNum,
       Line = LEFT(LTRIM(RTRIM(REPLACE(a.Line, NCHAR(9), N' '))), 300)
FROM (SELECT l.*, Occ = ROW_NUMBER() OVER (PARTITION BY l.Db, l.ProcName, l.LineKey ORDER BY l.LineNum)
      FROM #lines l
      WHERE LTRIM(RTRIM(REPLACE(l.Line, NCHAR(9), N' '))) <> N'') a
WHERE EXISTS (SELECT 1 FROM #lines b WHERE b.ProcName = a.ProcName AND b.Db <> a.Db)         -- present in both
  AND NOT EXISTS (SELECT 1
                  FROM (SELECT l.Db, l.ProcName, l.LineKey,
                               Occ = ROW_NUMBER() OVER (PARTITION BY l.Db, l.ProcName, l.LineKey ORDER BY l.LineNum)
                        FROM #lines l
                        WHERE LTRIM(RTRIM(REPLACE(l.Line, NCHAR(9), N' '))) <> N'') b
                  WHERE b.ProcName = a.ProcName AND b.Db <> a.Db AND b.LineKey = a.LineKey AND b.Occ = a.Occ)
ORDER BY a.ProcName, a.Db, a.LineNum;

-------------------------------------------------------------------------------
-- RESULT GRID 4 (only if grid 3 is unclear): the full text of the two procedures that differ.
-- Click a FullText cell to open it in its own tab; there it can be saved as a .sql file.
-------------------------------------------------------------------------------
SELECT Db = N'masterDB_prod', ProcName = p.name COLLATE Latin1_General_CI_AS,
       FullText = (SELECT m.definition AS [processing-instruction(def)] FOR XML PATH(''), TYPE)
FROM masterDB_prod.sys.procedures p
JOIN masterDB_prod.sys.schemas s ON s.schema_id = p.schema_id
JOIN masterDB_prod.sys.sql_modules m ON m.object_id = p.object_id
WHERE s.name = N'dbo' AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP')
UNION ALL
SELECT N'UDD_MASTERDB', p.name COLLATE Latin1_General_CI_AS,
       (SELECT m.definition AS [processing-instruction(def)] FOR XML PATH(''), TYPE)
FROM UDD_MASTERDB.sys.procedures p
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = p.schema_id
JOIN UDD_MASTERDB.sys.sql_modules m ON m.object_id = p.object_id
WHERE s.name = N'dbo' AND p.name COLLATE Latin1_General_CI_AS IN (N'USP_S_ValidateOfficer', N'USP_I_OTP')
ORDER BY ProcName, Db;
GO

SET NOEXEC OFF;
GO
