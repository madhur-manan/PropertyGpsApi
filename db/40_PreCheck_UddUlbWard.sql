/*
    READ-ONLY PRE-CHECK: is the test server ready for Property GPS by ULB + ward number
    (db/41 for UDD_MASTERDB, db/42 for UDD_KHATABTOA, and the API that goes with them)?

    RUN IN UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74). It also reads
    UDD_MASTERDB on the same server. It writes nothing: it reads catalogs and data and fills
    #temp tables only. Run the WHOLE script in one go.

    WHY. From now on an RI sees the cases whose BtoA_EPIDMetaData.MD_ULBCode + MD_WardId (the
    ward number, as text) match a ULB + ward mapped to them in UDD_MASTERDB; zone,
    corporation, division, block and the GBA/BBMP columns are no longer read. Road and
    street ids become text. Before db/41 and db/42 change anything, this answers:
      - in UDD_KHATABTOA: does MD_ULBCode exist, and of what type; the types of MD_WardId,
        MD_StreetId, Site_StreetId, Rd_RoadId and Ofcr_RoadId; what depends on Ofcr_RoadId
        (index, constraint, default, computed column, foreign key, statistics, schema-bound
        module, trigger) and which OTHER modules use it - i.e. whether db/42's optional
        ALTER to nvarchar(50) is safe;
      - in UDD_MASTERDB: are dbo.GpsOfficerSession, dbo.USP_S_Officer_ValidateOTP and
        dbo.USP_S_GetRoadData there, and every table and column the new code reads;
      - how many applications wait at status 10 / 400 per (MD_ULBCode, MD_WardId), whether
        each of those wards is in mst_AROMapping, and how many RIs are mapped to it.
    No officer names or mobile numbers are shown anywhere.

    Result grids:
      1. Run info - server, database, collations.
      2. Findings - one row per finding, most serious first:
           MUST  - db/41 or db/42 or the API cannot work until this is fixed;
           CHECK - look at it before going on;
           INFO  - worth knowing; nothing to do;
           OK    - checked and fine (so an empty grid never reads as "all good").
      3. Column types the switch depends on, in both databases.
      4. Ofcr_RoadId: everything that depends on the column (what would block making it text).
      5. Modules in UDD_KHATABTOA that use BtoA_SiteRoadDetails_Officer or Ofcr_RoadId.
      6. Work per ULB + ward: status 10 / 400 per (MD_ULBCode, MD_WardId), counted the way the
         ward sync counts; whether the ward is in UDD_MASTERDB.dbo.mst_AROMapping; how many
         active role 116/117 officers are mapped to it.
    Send grids 2 and 6 back (screenshot, or right-click the grid > Save Results As...).
*/

USE UDD_KHATABTOA;
GO

SET NOCOUNT ON;
SELECT RunningIn     = DB_NAME(),
       OnServer      = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')),
       UDD_KHATABTOA = CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_KHATABTOA', 'Collation')),
       UDD_MASTERDB  = CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'UDD_MASTERDB', 'Collation'));

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
ELSE IF DB_ID(N'UDD_MASTERDB') IS NULL
BEGIN
    RAISERROR('MUST: there is no UDD_MASTERDB on this server. Nothing else can be checked.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- Working tables (#temp only)
-------------------------------------------------------------------------------
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#gaps') IS NOT NULL DROP TABLE #gaps;
IF OBJECT_ID('tempdb..#cols') IS NOT NULL DROP TABLE #cols;
IF OBJECT_ID('tempdb..#need') IS NOT NULL DROP TABLE #need;
IF OBJECT_ID('tempdb..#uobj') IS NOT NULL DROP TABLE #uobj;
IF OBJECT_ID('tempdb..#roadResult') IS NOT NULL DROP TABLE #roadResult;

CREATE TABLE #gaps (
    Seq      int IDENTITY PRIMARY KEY,
    Severity varchar(5)     NOT NULL,
    Area     nvarchar(60)   COLLATE DATABASE_DEFAULT NOT NULL,
    Item     nvarchar(300)  COLLATE DATABASE_DEFAULT NOT NULL,
    Finding  nvarchar(400)  COLLATE DATABASE_DEFAULT NOT NULL,
    Detail   nvarchar(2000) COLLATE DATABASE_DEFAULT NULL);

CREATE TABLE #cols (
    Db         nvarchar(20)  COLLATE DATABASE_DEFAULT NOT NULL,
    TableName  nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    ColumnName nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    TypeName   nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    TypeText   nvarchar(80)  COLLATE DATABASE_DEFAULT NOT NULL,
    IsNullable bit NOT NULL,
    IsComputed bit NOT NULL);

-- UDD_MASTERDB objects (dbo), for the "is it there" checks
CREATE TABLE #uobj (
    ObjectName nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    TypeDesc   nvarchar(60)  COLLATE DATABASE_DEFAULT NOT NULL,
    ObjectText nvarchar(max) COLLATE DATABASE_DEFAULT NULL);

INSERT INTO #uobj (ObjectName, TypeDesc, ObjectText)
SELECT o.name, o.type_desc, m.[definition]
FROM UDD_MASTERDB.sys.objects o
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN UDD_MASTERDB.sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V', 'P', 'SN');

-- Column types of the tables involved, in both databases
INSERT INTO #cols (Db, TableName, ColumnName, TypeName, TypeText, IsNullable, IsComputed)
SELECT N'UDD_KHATABTOA', o.name, c.name, TYPE_NAME(c.system_type_id),
       TYPE_NAME(c.system_type_id)
       + CASE WHEN TYPE_NAME(c.system_type_id) IN (N'varchar', N'char', N'varbinary', N'binary')
              THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
              WHEN TYPE_NAME(c.system_type_id) IN (N'nvarchar', N'nchar')
              THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
              WHEN TYPE_NAME(c.system_type_id) IN (N'decimal', N'numeric')
              THEN N'(' + CAST(c.[precision] AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
              ELSE N'' END,
       c.is_nullable, c.is_computed
FROM UDD_KHATABTOA.sys.columns c
JOIN UDD_KHATABTOA.sys.objects o ON o.object_id = c.object_id
JOIN UDD_KHATABTOA.sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V')
  AND (o.name IN (N'BtoA_EPIDMetaData', N'BtoA_SiteDtls', N'BtoA_SiteRoadDetails', N'BtoA_SiteRoadDetails_Officer')
       OR c.name = N'Ofcr_RoadId');                                -- e.g. a _Hist copy of the road table

INSERT INTO #cols (Db, TableName, ColumnName, TypeName, TypeText, IsNullable, IsComputed)
SELECT N'UDD_MASTERDB', o.name, c.name, TYPE_NAME(c.system_type_id),
       TYPE_NAME(c.system_type_id)
       + CASE WHEN TYPE_NAME(c.system_type_id) IN (N'varchar', N'char', N'varbinary', N'binary')
              THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
              WHEN TYPE_NAME(c.system_type_id) IN (N'nvarchar', N'nchar')
              THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
              WHEN TYPE_NAME(c.system_type_id) IN (N'decimal', N'numeric')
              THEN N'(' + CAST(c.[precision] AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
              ELSE N'' END,
       c.is_nullable, c.is_computed
FROM UDD_MASTERDB.sys.columns c
JOIN UDD_MASTERDB.sys.objects o ON o.object_id = c.object_id
JOIN UDD_MASTERDB.sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = N'dbo' AND o.type IN ('U', 'V')
  AND o.name IN (N'mst_AROMapping', N'mst_Officer', N'mst_Ofcr_WardMapping', N'OTP_Tran', N'Mst_Roles',
                 N'GpsOfficerSession', N'Mst_AppVersion', N'mst_KsrsacMasterPlan2015', N'Login_Tran');

-- What the new code reads (the shared contract). MUST: missing = something fails.
CREATE TABLE #need (
    Db         nvarchar(20)  COLLATE DATABASE_DEFAULT NOT NULL,
    TableName  nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    ColumnName nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    NeededBy   nvarchar(300) COLLATE DATABASE_DEFAULT NOT NULL);

INSERT INTO #need (Db, TableName, ColumnName, NeededBy) VALUES
 (N'UDD_KHATABTOA', N'BtoA_EPIDMetaData', N'MD_APP_ID',     N'ward sync, road check'),
 (N'UDD_KHATABTOA', N'BtoA_EPIDMetaData', N'MD_MotherEPID', N'ward sync'),
 (N'UDD_KHATABTOA', N'BtoA_EPIDMetaData', N'MD_ULBCode',    N'ward sync filter, road check (db/42 refuses without it)'),
 (N'UDD_KHATABTOA', N'BtoA_EPIDMetaData', N'MD_WardId',     N'ward sync filter, road check'),
 (N'UDD_KHATABTOA', N'BtoA_SiteDtls',     N'Site_StreetId', N'ward sync StreetId / StreetNumber'),
 (N'UDD_KHATABTOA', N'BtoA_SiteRoadDetails_Officer', N'Ofcr_RoadId', N'road submit (USP_IU_BtoA_SiteRoadDetails_Officer)'),
 -- streets and names
 (N'UDD_MASTERDB', N'mst_AROMapping', N'DistrictCode',  N'API road list (USP_S_GetRoadData @DistCode)'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'ULBCode',       N'names, road list, road check'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'ULBName_EN',    N'ULB name (sign-in, ward sync)'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'WardNumber',    N'names, road list fallback, road check'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'WardName_EN',   N'ward name (sign-in, ward sync)'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'StreetNumber',  N'road list fallback, road check'),
 (N'UDD_MASTERDB', N'mst_AROMapping', N'StreetName_EN', N'road list fallback, road check'),
 -- officers
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_Id',             N'sign-in, officer wards, submit officer lookup'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_Name',           N'sign-in, auth/me, submit officer lookup'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_MNo',            N'sign-in, auth/me, submit officer lookup'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_RoleId',         N'sign-in, officer wards'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_ULBId',          N'officer''s own ULB'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_WardId',         N'officer''s own ward number'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_Active',         N'sign-in, officer wards'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_AdditionalInfo', N'OTP lock note (db/41)'),
 (N'UDD_MASTERDB', N'mst_Officer', N'NoOfFailedAttempt',   N'OTP lock-out (db/41)'),
 (N'UDD_MASTERDB', N'mst_Officer', N'Ofcr_Locked_Date',    N'OTP lock-out (db/41)'),
 (N'UDD_MASTERDB', N'mst_Ofcr_WardMapping', N'Map_OfcrId',     N'officer wards'),
 (N'UDD_MASTERDB', N'mst_Ofcr_WardMapping', N'Map_OfcrRoleId', N'officer wards'),
 (N'UDD_MASTERDB', N'mst_Ofcr_WardMapping', N'Map_CorpId',     N'officer wards (the ULB code)'),
 (N'UDD_MASTERDB', N'mst_Ofcr_WardMapping', N'Map_WardId',     N'officer wards (the ward number)'),
 (N'UDD_MASTERDB', N'mst_Ofcr_WardMapping', N'isActive',       N'officer wards'),
 -- OTP and roles
 (N'UDD_MASTERDB', N'OTP_Tran', N'OTP',      N'OTP check (db/41)'),
 (N'UDD_MASTERDB', N'OTP_Tran', N'Cdte',     N'OTP check (db/41)'),
 (N'UDD_MASTERDB', N'OTP_Tran', N'MobileNO', N'OTP check (db/41)'),
 (N'UDD_MASTERDB', N'OTP_Tran', N'ACTIVE',   N'OTP check - used / unused (db/41)'),
 (N'UDD_MASTERDB', N'OTP_Tran', N'DeviceId', N'OTP check - USP_I_OTP stores the API''s Otp:Source here (db/41)'),
 (N'UDD_MASTERDB', N'OTP_Tran', N'Source',   N'OTP check (db/41)'),
 (N'UDD_MASTERDB', N'Mst_Roles', N'RoleId',   N'sign-in RoleName (db/41)'),
 (N'UDD_MASTERDB', N'Mst_Roles', N'RoleName', N'sign-in RoleName (db/41)'),
 -- read by UDD_KHATABTOA procedures after db/42
 (N'UDD_MASTERDB', N'mst_KsrsacMasterPlan2015', N'GpsLandUseTypeValue', N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 (N'UDD_MASTERDB', N'mst_KsrsacMasterPlan2015', N'BBMPLandUseTypeId',   N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 (N'UDD_MASTERDB', N'mst_KsrsacMasterPlan2015', N'BBMPLAndUseTypeNAme', N'land use on submit (USP_IU_BtoA_MainApp_Officer)'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'App_Platform',        N'version check (USP_CheckMobileAppVersion)'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'IsActive',            N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'Current_Version',     N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'AppVersion_Id',       N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'Latest_Version',      N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'MinRequired_Version', N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'Update_Message',      N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'Download_URL',        N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'File_Size_MB',        N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'Release_Notes',       N'version check'),
 (N'UDD_MASTERDB', N'Mst_AppVersion', N'IsForceUpdate',       N'version check');
GO

-------------------------------------------------------------------------------
-- A. Tables and columns the new code reads
-------------------------------------------------------------------------------
-- Missing tables (one row per table)
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT 'MUST', N'needed table', n.Db + N'.dbo.' + n.TableName, N'table missing',
       N'Needed by: ' + STRING_AGG(CONVERT(nvarchar(max), n.NeededBy), N'; ')
FROM #need n
WHERE NOT EXISTS (SELECT 1 FROM #cols c WHERE c.Db = n.Db AND c.TableName = n.TableName)
GROUP BY n.Db, n.TableName;

-- Missing columns in tables that exist
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT 'MUST', N'needed column', n.Db + N'.dbo.' + n.TableName + N'.' + n.ColumnName, N'column missing',
       N'Needed by: ' + n.NeededBy
FROM #need n
WHERE EXISTS (SELECT 1 FROM #cols c WHERE c.Db = n.Db AND c.TableName = n.TableName)
  AND NOT EXISTS (SELECT 1 FROM #cols c WHERE c.Db = n.Db AND c.TableName = n.TableName AND c.ColumnName = n.ColumnName);

IF NOT EXISTS (SELECT 1 FROM #gaps WHERE Area IN (N'needed table', N'needed column'))
    INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
    VALUES ('OK', N'needed column', N'(all)', N'every table and column the new code reads is there',
            CONVERT(nvarchar(10), (SELECT COUNT(*) FROM #need)) + N' columns checked');
GO

-------------------------------------------------------------------------------
-- B. UDD_KHATABTOA columns: MD_ULBCode, ward number, street and road ids
-------------------------------------------------------------------------------
DECLARE @t nvarchar(128), @tt nvarchar(80), @n int, @sql nvarchar(max);

-- B1. MD_ULBCode
SELECT @t = TypeName, @tt = TypeText FROM #cols
WHERE Db = N'UDD_KHATABTOA' AND TableName = N'BtoA_EPIDMetaData' AND ColumnName = N'MD_ULBCode';

IF @t IS NULL
    INSERT INTO #gaps VALUES ('MUST', N'UDD_KHATABTOA', N'BtoA_EPIDMetaData.MD_ULBCode', N'column missing',
        N'The ward sync filters on it and db/42 refuses to run without it. The citizen module has to write it.');
ELSE IF @t IN (N'int', N'bigint', N'smallint', N'tinyint')
    INSERT INTO #gaps VALUES ('OK', N'UDD_KHATABTOA', N'BtoA_EPIDMetaData.MD_ULBCode', N'exists, ' + @tt, NULL);
ELSE IF @t IN (N'varchar', N'nvarchar', N'char', N'nchar')
BEGIN
    SET @n = NULL;
    EXEC sys.sp_executesql
         N'SELECT @n = COUNT(*) FROM dbo.BtoA_EPIDMetaData WITH (NOLOCK)
           WHERE MD_ULBCode IS NOT NULL AND TRY_CONVERT(int, MD_ULBCode) IS NULL;',
         N'@n int OUTPUT', @n = @n OUTPUT;
    INSERT INTO #gaps VALUES (CASE WHEN @n > 0 THEN 'MUST' ELSE 'CHECK' END, N'UDD_KHATABTOA',
        N'BtoA_EPIDMetaData.MD_ULBCode', N'exists, ' + @tt + N' (text, compared with an int)',
        CASE WHEN @n > 0 THEN CONVERT(nvarchar(10), @n) + N' rows hold a value that is not a whole number: every ward sync would fail on a conversion error, and db/42 refuses to run.'
             ELSE N'Every value is a whole number today, so it works; a non-number written later would break every ward sync.' END);
END
ELSE
    INSERT INTO #gaps VALUES ('CHECK', N'UDD_KHATABTOA', N'BtoA_EPIDMetaData.MD_ULBCode', N'exists, ' + @tt,
        N'Not an integer type; the procedures compare it with an int ULB code.');

-- B2. MD_WardId: compared as LTRIM(RTRIM(CONVERT(nvarchar(20), MD_WardId)))
SET @t = NULL; SET @tt = NULL;
SELECT @t = TypeName, @tt = TypeText FROM #cols
WHERE Db = N'UDD_KHATABTOA' AND TableName = N'BtoA_EPIDMetaData' AND ColumnName = N'MD_WardId';
IF @t IS NOT NULL
BEGIN
    SET @n = NULL;
    EXEC sys.sp_executesql
         N'SELECT @n = COUNT(*) FROM dbo.BtoA_EPIDMetaData WITH (NOLOCK)
           WHERE LEN(LTRIM(RTRIM(CONVERT(nvarchar(4000), MD_WardId)))) > 20;',
         N'@n int OUTPUT', @n = @n OUTPUT;
    INSERT INTO #gaps VALUES (CASE WHEN @n > 0 THEN 'CHECK' ELSE 'INFO' END, N'UDD_KHATABTOA',
        N'BtoA_EPIDMetaData.MD_WardId', N'type ' + @tt + N' - matched as trimmed text (ward number)',
        CASE WHEN @n > 0 THEN CONVERT(nvarchar(10), @n) + N' rows hold more than 20 characters; they can never match a ward number.' ELSE NULL END);
END

-- B3. The other id columns, for the record
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN c.ColumnName = N'Site_StreetId'
             AND (c.TypeName IN (N'float', N'real')
                  OR (c.TypeName IN (N'decimal', N'numeric') AND c.TypeText NOT LIKE N'%,0)'))
            THEN 'CHECK' ELSE 'INFO' END,
       N'UDD_KHATABTOA', c.TableName + N'.' + c.ColumnName, N'type ' + c.TypeText,
       CASE c.ColumnName
            WHEN N'MD_StreetId'   THEN N'Not read any more (the road check no longer goes through the property''s street).'
            WHEN N'Site_StreetId' THEN N'Ward sync returns it only as text, StreetNumber (siteDetails.streetId). A float or decimal type would read as 1.23457e+006 or 123.00 - tell me if this row says CHECK.'
            WHEN N'Rd_RoadId'     THEN N'The citizen''s road id; the ward sync returns it as RoadId.' END
FROM #cols c
WHERE c.Db = N'UDD_KHATABTOA'
  AND ((c.TableName = N'BtoA_EPIDMetaData' AND c.ColumnName = N'MD_StreetId')
    OR (c.TableName = N'BtoA_SiteDtls' AND c.ColumnName = N'Site_StreetId')
    OR (c.TableName = N'BtoA_SiteRoadDetails' AND c.ColumnName = N'Rd_RoadId'));

-- B4. Ofcr_RoadId: can it hold a text road id?
SET @t = NULL; SET @tt = NULL;
SELECT @t = TypeName, @tt = TypeText FROM #cols
WHERE Db = N'UDD_KHATABTOA' AND TableName = N'BtoA_SiteRoadDetails_Officer' AND ColumnName = N'Ofcr_RoadId';
IF @t IN (N'int', N'bigint', N'smallint', N'tinyint')
    INSERT INTO #gaps VALUES ('CHECK', N'UDD_KHATABTOA', N'BtoA_SiteRoadDetails_Officer.Ofcr_RoadId',
        N'type ' + @tt + N' - numeric road ids only',
        N'After db/42 a road id that is not a whole number is refused (Status 0) until db/42 section 5 makes the column nvarchar(50). Numeric ids, 999 included, work either way. Grids 4 and 5 show whether that change is safe.');
ELSE IF @t IN (N'varchar', N'nvarchar', N'char', N'nchar')
    INSERT INTO #gaps VALUES (CASE WHEN @tt IN (N'nvarchar(max)', N'varchar(max)') OR TRY_CONVERT(int, SUBSTRING(@tt, CHARINDEX(N'(', @tt) + 1, CHARINDEX(N')', @tt) - CHARINDEX(N'(', @tt) - 1)) >= 50
                                   THEN 'OK' ELSE 'CHECK' END,
        N'UDD_KHATABTOA', N'BtoA_SiteRoadDetails_Officer.Ofcr_RoadId', N'type ' + @tt + N' - already text',
        N'Road ids are up to 50 characters; db/42 section 5 is not needed.');
GO

-------------------------------------------------------------------------------
-- C. Ofcr_RoadId: what depends on it (grid 4), who uses it (grid 5)
-------------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#dep') IS NOT NULL DROP TABLE #dep;
IF OBJECT_ID('tempdb..#users') IS NOT NULL DROP TABLE #users;

DECLARE @tab int = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer');
DECLARE @col int = COLUMNPROPERTY(@tab, N'Ofcr_RoadId', 'ColumnId');

SELECT Kind = CONVERT(nvarchar(60), N'index') COLLATE DATABASE_DEFAULT, Name = i.name COLLATE DATABASE_DEFAULT,
       Blocks = CONVERT(bit, 1)
INTO #dep
FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
WHERE ic.object_id = @tab AND ic.column_id = @col
UNION ALL
SELECT N'default constraint', d.name COLLATE DATABASE_DEFAULT, 1
FROM sys.default_constraints d WHERE d.parent_object_id = @tab AND d.parent_column_id = @col
UNION ALL
SELECT N'check constraint', k.name COLLATE DATABASE_DEFAULT, 1
FROM sys.check_constraints k WHERE k.parent_object_id = @tab AND (k.parent_column_id = @col OR k.[definition] LIKE N'%Ofcr[_]RoadId%')
UNION ALL
SELECT N'foreign key', OBJECT_NAME(f.constraint_object_id) COLLATE DATABASE_DEFAULT, 1
FROM sys.foreign_key_columns f
WHERE (f.parent_object_id = @tab AND f.parent_column_id = @col) OR (f.referenced_object_id = @tab AND f.referenced_column_id = @col)
UNION ALL
SELECT N'computed column', cc.name COLLATE DATABASE_DEFAULT, 1
FROM sys.computed_columns cc WHERE cc.object_id = @tab AND cc.[definition] LIKE N'%Ofcr[_]RoadId%'
UNION ALL
SELECT CASE WHEN s.auto_created = 1 THEN N'statistics, auto-created' ELSE N'statistics, created by hand' END,
       s.name COLLATE DATABASE_DEFAULT, CASE WHEN s.user_created = 1 THEN 1 ELSE 0 END
FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
WHERE s.object_id = @tab AND sc.column_id = @col AND (s.auto_created = 1 OR s.user_created = 1)
UNION ALL
SELECT N'schema-bound module', (OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id)) COLLATE DATABASE_DEFAULT, 1
FROM sys.sql_expression_dependencies d
WHERE d.referenced_id = @tab AND d.referenced_minor_id = @col AND d.is_schema_bound_reference = 1
UNION ALL
SELECT N'replicated column', c.name COLLATE DATABASE_DEFAULT, 1
FROM sys.columns c WHERE c.object_id = @tab AND c.column_id = @col AND c.is_replicated = 1
UNION ALL
SELECT N'system-versioned table', t.name COLLATE DATABASE_DEFAULT, 1
FROM sys.tables t WHERE t.object_id = @tab AND t.temporal_type <> 0
UNION ALL
SELECT N'trigger on the table', tr.name COLLATE DATABASE_DEFAULT, 0
FROM sys.triggers tr WHERE tr.parent_id = @tab;

INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN d.Blocks = 1 THEN 'CHECK' ELSE 'INFO' END, N'Ofcr_RoadId', d.Kind + N': ' + d.Name,
       CASE WHEN d.Blocks = 1 THEN N'blocks db/42 section 5 (ALTER to nvarchar(50))'
            WHEN d.Kind LIKE N'statistics%' THEN N'dropped by db/42 section 5 before the ALTER; SQL Server makes it again'
            ELSE N'does not block the ALTER - but read it: does it copy Ofcr_RoadId into an integer column?' END,
       NULL
FROM #dep d;

IF @tab IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #dep WHERE Blocks = 1)
    INSERT INTO #gaps VALUES ('OK', N'Ofcr_RoadId', N'BtoA_SiteRoadDetails_Officer.Ofcr_RoadId',
        N'nothing in the table''s own structure blocks making it nvarchar(50)', N'Modules that use it: grid 5.');

-- Other tables with a column of the same name (a history copy, an archive): an integer one
-- would refuse text ids copied into it.
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN c.TypeName IN (N'int', N'bigint', N'smallint', N'tinyint') THEN 'CHECK' ELSE 'INFO' END,
       N'Ofcr_RoadId', c.TableName + N'.Ofcr_RoadId', N'another table with this column, type ' + c.TypeText,
       N'If a trigger or job copies BtoA_SiteRoadDetails_Officer rows into it, a text road id would fail there.'
FROM #cols c
WHERE c.Db = N'UDD_KHATABTOA' AND c.ColumnName = N'Ofcr_RoadId' AND c.TableName <> N'BtoA_SiteRoadDetails_Officer';

-- Modules that use the table or the column (by dependency, and by text for dynamic SQL)
SELECT ObjectName = o.name COLLATE DATABASE_DEFAULT, TypeDesc = o.type_desc COLLATE DATABASE_DEFAULT,
       UsesTable  = CASE WHEN EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d
                                      WHERE d.referencing_id = o.object_id
                                        AND (d.referenced_id = @tab OR d.referenced_entity_name = N'BtoA_SiteRoadDetails_Officer'))
                           OR m.[definition] LIKE N'%BtoA[_]SiteRoadDetails[_]Officer%' THEN 1 ELSE 0 END,
       MentionsOfcrRoadId = CASE WHEN m.[definition] LIKE N'%Ofcr[_]RoadId%' THEN 1 ELSE 0 END,
       Writes     = CASE WHEN m.[definition] LIKE N'%INSERT%BtoA[_]SiteRoadDetails[_]Officer%'
                           OR m.[definition] LIKE N'%UPDATE%BtoA[_]SiteRoadDetails[_]Officer%' THEN 1 ELSE 0 END,
       Note       = CASE WHEN o.name = N'USP_IU_BtoA_SiteRoadDetails_Officer' THEN N'changed by db/42'
                         WHEN o.name IN (N'USP_U_BtoA_GpsRoadNoticeDocument', N'USP_U_BtoA_GpsCompleteAssignment') THEN N'Property GPS (db/13, db/19)'
                         ELSE N'' END
INTO #users
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.[definition] LIKE N'%BtoA[_]SiteRoadDetails[_]Officer%'
   OR m.[definition] LIKE N'%Ofcr[_]RoadId%'
   OR EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d
              WHERE d.referencing_id = o.object_id AND d.referenced_id = @tab);

INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN u.MentionsOfcrRoadId = 1 THEN 'CHECK' ELSE 'INFO' END, N'Ofcr_RoadId users',
       u.ObjectName + N' (' + u.TypeDesc + N')',
       CASE WHEN u.MentionsOfcrRoadId = 1 THEN N'another module that uses Ofcr_RoadId'
            ELSE N'uses the table but not Ofcr_RoadId by name' END,
       CASE WHEN u.MentionsOfcrRoadId = 1
            THEN N'Before db/42 section 5: read it. A comparison or copy of Ofcr_RoadId as a number fails once a text id is stored.'
            ELSE NULL END
FROM #users u
WHERE u.ObjectName <> N'USP_IU_BtoA_SiteRoadDetails_Officer';

IF NOT EXISTS (SELECT 1 FROM #users WHERE ObjectName <> N'USP_IU_BtoA_SiteRoadDetails_Officer' AND MentionsOfcrRoadId = 1)
    INSERT INTO #gaps VALUES ('OK', N'Ofcr_RoadId users', N'(other modules)',
        N'no other module in UDD_KHATABTOA names Ofcr_RoadId',
        N'Outside the database: the API''s EarlierSurveyReader reads it, and reports or other apps may too.');
GO

-------------------------------------------------------------------------------
-- D. UDD_MASTERDB objects: GpsOfficerSession, the sign-in and road procedures
-------------------------------------------------------------------------------
-- D1. GpsOfficerSession
IF NOT EXISTS (SELECT 1 FROM #uobj WHERE ObjectName = N'GpsOfficerSession')
    INSERT INTO #gaps VALUES ('MUST', N'UDD_MASTERDB', N'dbo.GpsOfficerSession', N'table missing',
        N'Every authenticated request reads it (one phone per officer). db/41 creates it. Until then the API needs Auth:SingleDeviceSessions=false.');
ELSE
BEGIN
    INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
    SELECT CASE WHEN c.ColumnName IS NULL THEN 'MUST' ELSE 'CHECK' END, N'UDD_MASTERDB',
           N'dbo.GpsOfficerSession.' + e.ColumnName,
           CASE WHEN c.ColumnName IS NULL THEN N'column missing'
                ELSE N'is ' + c.TypeText + CASE WHEN c.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END END,
           N'OfficerSessionStore.cs expects ' + e.TypeText + CASE WHEN e.Nullable = 1 THEN N' NULL' ELSE N' NOT NULL' END
    FROM (VALUES (N'OfficerId', N'bigint', 0), (N'SessionId', N'uniqueidentifier', 1), (N'DeviceId', N'nvarchar(64)', 1),
                 (N'ClientIp', N'nvarchar(64)', 1), (N'StartedAt', N'datetime', 0), (N'EndedAt', N'datetime', 1),
                 (N'ReplacedCount', N'int', 0)) e(ColumnName, TypeText, Nullable)
    LEFT JOIN #cols c ON c.Db = N'UDD_MASTERDB' AND c.TableName = N'GpsOfficerSession' AND c.ColumnName = e.ColumnName
    WHERE c.ColumnName IS NULL OR c.TypeText <> e.TypeText OR c.IsNullable <> e.Nullable;

    IF @@ROWCOUNT = 0
        INSERT INTO #gaps VALUES ('OK', N'UDD_MASTERDB', N'dbo.GpsOfficerSession', N'there, with the columns the API uses',
            N'db/41 leaves it as it is.');
END

-- D2. USP_S_Officer_ValidateOTP
IF NOT EXISTS (SELECT 1 FROM #uobj WHERE ObjectName = N'USP_S_Officer_ValidateOTP')
    INSERT INTO #gaps VALUES ('MUST', N'UDD_MASTERDB', N'dbo.USP_S_Officer_ValidateOTP', N'procedure missing',
        N'Sign-in (auth/otp/verify) calls it. db/41 creates it.');
ELSE IF EXISTS (SELECT 1 FROM #uobj WHERE ObjectName = N'USP_S_Officer_ValidateOTP' AND ObjectText LIKE N'%db/41[_]master[_]UddSignIn%')
    INSERT INTO #gaps VALUES ('OK', N'UDD_MASTERDB', N'dbo.USP_S_Officer_ValidateOTP', N'there - created by db/41', NULL);
ELSE
    INSERT INTO #gaps VALUES ('CHECK', N'UDD_MASTERDB', N'dbo.USP_S_Officer_ValidateOTP', N'there, but not created by db/41',
        N'db/41 will not replace it. Compare it with the contract (db/41 header): parameters @OTP, @MobileNO, @Source; columns IsValidOtp, Ofcr_Id, Ofcr_MNo, Ofcr_RoleId, Ofcr_Name, Ofcr_ULBId, Ofcr_WardId, RoleName, AdditionalInfo, NoOfFailedAttempt.');

-- D3. The sign-in procedures used as they are
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN u.ObjectName IS NULL THEN 'MUST' ELSE 'OK' END, N'UDD_MASTERDB', N'dbo.' + p.n,
       CASE WHEN u.ObjectName IS NULL THEN N'procedure missing' ELSE N'there' END, p.why
FROM (VALUES (N'USP_S_ValidateOfficer', N'Send OTP: is this mobile an officer of role 117?'),
             (N'USP_I_OTP',             N'Send OTP: stores the code in OTP_Tran.'),
             (N'usp_InsertLoginData',   N'Sign-in record.')) p(n, why)
LEFT JOIN #uobj u ON u.ObjectName = p.n AND u.TypeDesc = N'SQL_STORED_PROCEDURE';

-- D4. USP_S_GetRoadData: there, with the parameters the API sends
IF NOT EXISTS (SELECT 1 FROM #uobj WHERE ObjectName = N'USP_S_GetRoadData')
    INSERT INTO #gaps VALUES ('MUST', N'UDD_MASTERDB', N'dbo.USP_S_GetRoadData', N'procedure missing',
        N'The road list (masters/streets) calls it; only its fallback (mst_AROMapping by ward number) would be left.');
ELSE
BEGIN
    INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
    SELECT 'MUST', N'UDD_MASTERDB', N'dbo.USP_S_GetRoadData ' + e.p, N'parameter missing',
           N'The API sends @DistCode, @ULBCode, @wardId, @blockid, @Level (3).'
    FROM (VALUES (N'@DistCode'), (N'@ULBCode'), (N'@wardId'), (N'@blockid'), (N'@Level')) e(p)
    WHERE NOT EXISTS (SELECT 1 FROM UDD_MASTERDB.sys.parameters pa
                      WHERE pa.object_id = OBJECT_ID(N'UDD_MASTERDB.dbo.USP_S_GetRoadData')
                        AND pa.name COLLATE DATABASE_DEFAULT = e.p);

    INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
    SELECT 'INFO', N'UDD_MASTERDB', N'dbo.USP_S_GetRoadData ' + pa.name COLLATE DATABASE_DEFAULT,
           N'parameter the API does not send, ' + TYPE_NAME(pa.system_type_id),
           N'Fine only if it has a default value - look at its text.'
    FROM UDD_MASTERDB.sys.parameters pa
    WHERE pa.object_id = OBJECT_ID(N'UDD_MASTERDB.dbo.USP_S_GetRoadData')
      AND pa.name COLLATE DATABASE_DEFAULT NOT IN (N'@DistCode', N'@ULBCode', N'@wardId', N'@blockid', N'@Level');

    -- Its first result set: the API reads columns id and streetName.
    CREATE TABLE #roadResult (Ordinal int NULL, ColName nvarchar(128) NULL, TypeText nvarchar(256) NULL, ErrorMessage nvarchar(4000) NULL);
    BEGIN TRY
        INSERT INTO #roadResult (Ordinal, ColName, TypeText, ErrorMessage)
        EXEC UDD_MASTERDB.sys.sp_executesql
             N'SELECT r.column_ordinal, r.name, r.system_type_name, r.error_message
               FROM sys.dm_exec_describe_first_result_set_for_object(OBJECT_ID(N''dbo.USP_S_GetRoadData''), 0) r;';
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;   -- an error inside INSERT ... EXEC dooms its own transaction
        INSERT INTO #roadResult (ErrorMessage) VALUES (ERROR_MESSAGE());
    END CATCH;

    IF EXISTS (SELECT 1 FROM #roadResult WHERE ErrorMessage IS NOT NULL)
        INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
        SELECT TOP (1) 'INFO', N'UDD_MASTERDB', N'dbo.USP_S_GetRoadData (result columns)',
               N'could not be worked out from its text (temp tables or several branches?)', LEFT(ErrorMessage, 2000)
        FROM #roadResult WHERE ErrorMessage IS NOT NULL;
    ELSE
        INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
        SELECT CASE WHEN SUM(CASE WHEN ColName IN (N'id', N'streetName') THEN 1 ELSE 0 END) = 2 THEN 'OK' ELSE 'MUST' END,
               N'UDD_MASTERDB', N'dbo.USP_S_GetRoadData (result columns)',
               CASE WHEN SUM(CASE WHEN ColName IN (N'id', N'streetName') THEN 1 ELSE 0 END) = 2
                    THEN N'returns id and streetName' ELSE N'does not return both id and streetName' END,
               LEFT(STRING_AGG(CONVERT(nvarchar(max), ColName + N' ' + ISNULL(TypeText, N'')), N', '), 2000)
        FROM #roadResult;
END
GO

-------------------------------------------------------------------------------
-- E. UDD_KHATABTOA objects db/42 changes, and what role 117 means for the others
-------------------------------------------------------------------------------
-- E1. Each object db/42 changes: there? backed-up-able? what it reads today?
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT CASE WHEN o.object_id IS NULL AND p.n <> N'fn_GpsVersionKey' THEN 'CHECK'
            WHEN o.object_id IS NOT NULL AND m.[definition] IS NULL THEN 'MUST'
            ELSE 'INFO' END,
       N'db/42 objects', N'dbo.' + p.n,
       CASE WHEN o.object_id IS NULL THEN N'not there - db/42 creates it'
            WHEN m.[definition] IS NULL THEN N'no text can be read (encrypted?) - db/42 refuses, it could not be put back'
            ELSE N'there' + CASE WHEN m.[definition] LIKE N'%masterDB[_]prod%' THEN N', reads masterDB_prod today' ELSE N'' END
                 + CASE WHEN p.n = N'USP_S_GpsWardSync' AND m.[definition] LIKE N'%@ZoneId%' THEN N', by zone + ward (db/20)'
                        WHEN p.n = N'USP_S_GpsWardSync' AND m.[definition] LIKE N'%@UlbCode%' THEN N', already by ULB + ward number'
                        ELSE N'' END END,
       CONVERT(nvarchar(2000), N'last changed ' + CONVERT(nvarchar(19), o.modify_date, 120))
FROM (VALUES (N'USP_S_GpsWardSync'), (N'USP_U_GpsWardSyncAck'), (N'USP_IU_BtoA_SiteRoadDetails_Officer'),
             (N'USP_IU_BtoA_StatusDetail_Officer'), (N'USP_IU_BtoA_MainApp_Officer'), (N'USP_CheckMobileAppVersion'),
             (N'fn_GpsVersionKey')) p(n)
LEFT JOIN sys.objects o     ON o.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(p.n))
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id;

IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_ProcBackup', N'U') IS NOT NULL
    INSERT INTO #gaps VALUES ('INFO', N'db/42 objects', N'dbo.ZZ_PropertyGpsApi_ProcBackup',
        N'there - db/42 has run before', N'Its undo restores the copies saved on that first run, not today''s.');

-- E2. Role 117 is now the RI. db/42 teaches USP_IU_BtoA_StatusDetail_Officer; the other
--     procedures and triggers Property GPS reaches may still single out role 116.
INSERT INTO #gaps (Severity, Area, Item, Finding, Detail)
SELECT 'CHECK', N'role 117', o.name + N' (' + o.type_desc + N')', N'names role 116 in its text',
       N'The RI now signs in as 117. Read the branch: does 117 need the same treatment?'
FROM sys.objects o
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE (o.name IN (N'USP_IU_Architect_AssignedApp', N'USP_U_GpsPushedDetails', N'USP_I_GPS_Push_RequestResponse_Tran',
                  N'USP_I_BtoA_GPS_ApplicationWise_NotPushed_Tran', N'USP_U_BtoA_GpsSubmitExtras_App',
                  N'USP_U_BtoA_GpsRoadNoticeDocument', N'USP_U_BtoA_GpsCompleteAssignment',
                  N'USP_IU_BtoA_MainApp_Officer', N'USP_IU_BtoA_SiteRoadDetails_Officer',
                  N'USP_S_GpsWardSync', N'USP_U_GpsWardSyncAck', N'USP_CheckMobileAppVersion')
       OR (o.type = 'TR' AND o.parent_object_id IN (OBJECT_ID(N'dbo.BtoAMainApp'), OBJECT_ID(N'dbo.BtoA_MainApp_Officer'),
                                                    OBJECT_ID(N'dbo.BtoA_StatusDetail_Officer'), OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer'),
                                                    OBJECT_ID(N'dbo.BtoA_Architect_AssignedApp'))))
  AND m.[definition] LIKE N'%[^0-9]116[^0-9]%';
GO

-------------------------------------------------------------------------------
-- F. UDD_MASTERDB data (counts only): RIs, wards, district codes
-------------------------------------------------------------------------------
DECLARE @n int, @m int, @sql nvarchar(max);

IF COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Officer', N'Ofcr_ULBId') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Officer', N'Ofcr_WardId') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Officer', N'Ofcr_Active') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql
         N'SELECT @n = COUNT(*),
                  @m = SUM(CASE WHEN o.Ofcr_ULBId IS NOT NULL AND NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), N''''))), N'''') IS NOT NULL THEN 1 ELSE 0 END)
           FROM UDD_MASTERDB.dbo.mst_Officer o WITH (NOLOCK)
           WHERE o.Ofcr_RoleId = 117 AND ISNULL(o.Ofcr_Active, 0) = 1;',
         N'@n int OUTPUT, @m int OUTPUT', @n = @n OUTPUT, @m = @m OUTPUT;
    INSERT INTO #gaps VALUES (CASE WHEN ISNULL(@n, 0) = 0 THEN 'CHECK' ELSE 'INFO' END, N'UDD_MASTERDB data', N'mst_Officer, role 117',
        CONVERT(nvarchar(10), ISNULL(@n, 0)) + N' active role-117 officers, ' + CONVERT(nvarchar(10), ISNULL(@m, 0)) + N' with their own ULB + ward',
        CASE WHEN ISNULL(@n, 0) = 0 THEN N'Nobody can sign in to the app (Otp:DefaultRoleId 117).'
             ELSE N'The others can only work wards mapped in mst_Ofcr_WardMapping.' END);
END

IF COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'WardNumber') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql
         N'SELECT @n = COUNT(*)
           FROM (SELECT DISTINCT a.ULBCode, w = LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), N'''')))
                 FROM UDD_MASTERDB.dbo.mst_AROMapping a WITH (NOLOCK)) x
           WHERE TRY_CONVERT(int, x.w) IS NULL;',
         N'@n int OUTPUT', @n = @n OUTPUT;
    INSERT INTO #gaps VALUES ('INFO', N'UDD_MASTERDB data', N'mst_AROMapping.WardNumber',
        CONVERT(nvarchar(10), ISNULL(@n, 0)) + N' wards whose number is not a whole number',
        N'For these the API lists roads from mst_AROMapping (the fallback), not USP_S_GetRoadData.');
END
GO

-------------------------------------------------------------------------------
-- G. UDD_MASTERDB: the version row, and the OTP convention db/41 relies on
-------------------------------------------------------------------------------
DECLARE @n int, @rows nvarchar(max), @def nvarchar(max);

-- The version check (db/42) reads the active Android row; another UDD app's row would answer
-- for Property GPS with the wrong versions and download link.
IF COL_LENGTH(N'UDD_MASTERDB.dbo.Mst_AppVersion', N'App_Platform') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.Mst_AppVersion', N'IsActive') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.Mst_AppVersion', N'Latest_Version') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.Mst_AppVersion', N'MinRequired_Version') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.Mst_AppVersion', N'Download_URL') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql
         N'SELECT @n = COUNT(*),
                  @rows = STRING_AGG(CONVERT(nvarchar(max), CONCAT(N''latest '', a.Latest_Version, N'', minimum '', a.MinRequired_Version, N'', '', a.Download_URL)), N'' | '')
           FROM UDD_MASTERDB.dbo.Mst_AppVersion a WITH (NOLOCK)
           WHERE a.App_Platform = N''Android'' AND a.IsActive = 1;',
         N'@n int OUTPUT, @rows nvarchar(max) OUTPUT', @n = @n OUTPUT, @rows = @rows OUTPUT;
    INSERT INTO #gaps VALUES (CASE WHEN ISNULL(@n, 0) = 1 THEN 'INFO' ELSE 'CHECK' END, N'UDD_MASTERDB data', N'Mst_AppVersion, active Android row',
        CONVERT(nvarchar(10), ISNULL(@n, 0)) + N' active Android rows',
        CASE WHEN ISNULL(@n, 0) = 0 THEN N'The version check has nothing to compare with: add Property GPS''s Android row before switching.'
             ELSE LEFT(N'Must be Property GPS''s row (com.gba.property_gps): ' + @rows, 2000) END);
END

-- db/41 accepts only a code with ACTIVE NULL, stamped in server local time, within 100 minutes,
-- and marks it '0' once used. UDD's own USP_I_OTP has to leave codes that way.
SELECT @def = m.definition
FROM UDD_MASTERDB.sys.procedures p
JOIN UDD_MASTERDB.sys.sql_modules m ON m.object_id = p.object_id
WHERE p.name = N'USP_I_OTP';
IF @def IS NOT NULL
    INSERT INTO #gaps VALUES (CASE WHEN @def LIKE N'%ACTIVE%' OR @def LIKE N'%GETUTCDATE%' THEN 'CHECK' ELSE 'OK' END,
        N'UDD_MASTERDB', N'USP_I_OTP',
        CASE WHEN @def LIKE N'%ACTIVE%' OR @def LIKE N'%GETUTCDATE%'
             THEN N'writes ACTIVE or uses UTC time - send me its text'
             ELSE N'leaves ACTIVE NULL and uses server time, as db/41 expects' END,
        NULL);

IF COL_LENGTH(N'UDD_MASTERDB.dbo.OTP_Tran', N'ACTIVE') IS NOT NULL
   AND COL_LENGTH(N'UDD_MASTERDB.dbo.OTP_Tran', N'Cdte') IS NOT NULL
    EXEC sys.sp_executesql
         N'INSERT INTO #gaps
           SELECT ''INFO'', N''UDD_MASTERDB data'', N''OTP_Tran.ACTIVE, last 7 days'',
                  N''ACTIVE = '' + ISNULL(CONVERT(nvarchar(10), t.ACTIVE), N''NULL'') + N'': '' + CONVERT(nvarchar(10), COUNT(*)) + N'' codes'',
                  N''A fresh code must be NULL; db/41 marks a used one 0.''
           FROM UDD_MASTERDB.dbo.OTP_Tran t WITH (NOLOCK)
           WHERE t.Cdte >= DATEADD(day, -7, GETDATE())
           GROUP BY t.ACTIVE;';
GO

-------------------------------------------------------------------------------
-- RESULT GRID 2: findings
-------------------------------------------------------------------------------
SELECT Severity, Area, Item, Finding, Detail
FROM #gaps
ORDER BY CASE Severity WHEN 'MUST' THEN 1 WHEN 'CHECK' THEN 2 WHEN 'INFO' THEN 3 ELSE 4 END, Area, Seq;

-------------------------------------------------------------------------------
-- RESULT GRID 3: column types the switch depends on
-------------------------------------------------------------------------------
SELECT c.Db, c.TableName, c.ColumnName,
       [Type] = c.TypeText + CASE WHEN c.IsNullable = 1 THEN N' NULL' ELSE N' NOT NULL' END,
       Computed = c.IsComputed
FROM #cols c
WHERE EXISTS (SELECT 1 FROM #need n WHERE n.Db = c.Db AND n.TableName = c.TableName AND n.ColumnName = c.ColumnName)
   OR c.ColumnName = N'Ofcr_RoadId'
   OR (c.Db = N'UDD_MASTERDB' AND c.TableName = N'GpsOfficerSession')
   OR (c.Db = N'UDD_KHATABTOA' AND c.ColumnName IN (N'MD_StreetId', N'Rd_RoadId'))
ORDER BY c.Db, c.TableName, c.ColumnName;

-------------------------------------------------------------------------------
-- RESULT GRID 4: what depends on BtoA_SiteRoadDetails_Officer.Ofcr_RoadId
-------------------------------------------------------------------------------
SELECT Kind, Name,
       BlocksAlter = CASE WHEN Blocks = 1 THEN 'yes' ELSE 'no' END
FROM #dep
ORDER BY Blocks DESC, Kind, Name;

-------------------------------------------------------------------------------
-- RESULT GRID 5: modules that use BtoA_SiteRoadDetails_Officer or Ofcr_RoadId
-------------------------------------------------------------------------------
SELECT ObjectName, TypeDesc, UsesTable, MentionsOfcrRoadId, Writes, Note
FROM #users
ORDER BY MentionsOfcrRoadId DESC, ObjectName;
GO

-------------------------------------------------------------------------------
-- RESULT GRID 6: work per ULB + ward number (the ward sync's own definition: active, fee
-- paid, status 10 / 400, an EPID, an active site row, newest application per EPID)
-------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.BtoA_EPIDMetaData', N'MD_ULBCode') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'ULBCode') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'WardNumber') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'ULBName_EN') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'WardName_EN') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_AROMapping', N'DistrictCode') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Officer', N'Ofcr_ULBId') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Officer', N'Ofcr_WardId') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Ofcr_WardMapping', N'Map_CorpId') IS NULL
   OR COL_LENGTH(N'UDD_MASTERDB.dbo.mst_Ofcr_WardMapping', N'Map_WardId') IS NULL
BEGIN
    SELECT Grid6 = 'skipped: a column it needs is missing - see grid 2 (MUST rows)';
END
ELSE
BEGIN
    IF OBJECT_ID('tempdb..#aroWards') IS NOT NULL DROP TABLE #aroWards;
    CREATE TABLE #aroWards (
        UlbCode      int           NULL,
        WardNumber   nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
        UlbName      nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
        WardName     nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
        DistrictCode int           NULL,
        Streets      int           NOT NULL);

    -- One pass over mst_AROMapping (one row per street), to one row per ULB + ward.
    EXEC sys.sp_executesql N'
        INSERT INTO #aroWards (UlbCode, WardNumber, UlbName, WardName, DistrictCode, Streets)
        SELECT d.UlbCode, d.WardNumber, MAX(d.UlbName), MAX(d.WardName), MAX(d.DistrictCode), COUNT(*)
        FROM (SELECT UlbCode      = TRY_CONVERT(int, a.ULBCode),
                     WardNumber   = CONVERT(nvarchar(100), LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), N'''')))) COLLATE DATABASE_DEFAULT,
                     UlbName      = CONVERT(nvarchar(500), LTRIM(RTRIM(REPLACE(a.ULBName_EN, CHAR(9), N'''')))) COLLATE DATABASE_DEFAULT,
                     WardName     = CONVERT(nvarchar(500), LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), N'''')))) COLLATE DATABASE_DEFAULT,
                     DistrictCode = TRY_CONVERT(int, a.DistrictCode)
              FROM UDD_MASTERDB.dbo.mst_AROMapping a WITH (NOLOCK)) d
        GROUP BY d.UlbCode, d.WardNumber;';

    EXEC sys.sp_executesql N'
        WITH w AS (
            SELECT x.UlbCodeRaw, x.UlbCode, x.WardNumber, x.App_Status
            FROM (
                SELECT UlbCodeRaw = CONVERT(nvarchar(50), md.MD_ULBCode),
                       UlbCode    = TRY_CONVERT(int, md.MD_ULBCode),
                       WardNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) COLLATE DATABASE_DEFAULT,
                       ap.App_Status,
                       rn = ROW_NUMBER() OVER (PARTITION BY md.MD_ULBCode, LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))), ap.App_MotherEPID
                                               ORDER BY ap.App_Id DESC)
                FROM dbo.BtoAMainApp ap WITH (NOLOCK)
                JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
                  ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
                WHERE ap.App_Active = 1
                  AND ap.isProcessingFeePaid = 1
                  AND ap.App_Status IN (10, 400)
                  AND ISNULL(ap.App_MotherEPID, N'''') <> N''''
                  AND EXISTS (SELECT 1 FROM dbo.BtoA_SiteDtls sd WITH (NOLOCK)
                              WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
            ) x
            WHERE x.rn = 1
        ),
        g AS (
            SELECT UlbCodeRaw, UlbCode, WardNumber,
                   Waiting10   = SUM(CASE WHEN App_Status = 10  THEN 1 ELSE 0 END),
                   Returned400 = SUM(CASE WHEN App_Status = 400 THEN 1 ELSE 0 END)
            FROM w
            GROUP BY UlbCodeRaw, UlbCode, WardNumber
        ),
        ri AS (
            -- active role 116/117 officers: their own ULB + ward, and their active mapped wards
            SELECT UlbCode = CONVERT(int, o.Ofcr_ULBId),
                   WardNumber = LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), N''''))) COLLATE DATABASE_DEFAULT,
                   OfficerId = CONVERT(bigint, o.Ofcr_Id)
            FROM UDD_MASTERDB.dbo.mst_Officer o WITH (NOLOCK)
            WHERE ISNULL(o.Ofcr_Active, 0) = 1 AND o.Ofcr_RoleId IN (116, 117)
            UNION
            SELECT CONVERT(int, m.Map_CorpId),
                   CONVERT(nvarchar(20), m.Map_WardId) COLLATE DATABASE_DEFAULT,
                   CONVERT(bigint, m.Map_OfcrId)
            FROM UDD_MASTERDB.dbo.mst_Ofcr_WardMapping m WITH (NOLOCK)
            JOIN UDD_MASTERDB.dbo.mst_Officer o WITH (NOLOCK)
              ON o.Ofcr_Id = m.Map_OfcrId AND o.Ofcr_RoleId = m.Map_OfcrRoleId
            WHERE ISNULL(m.isActive, 1) = 1 AND ISNULL(o.Ofcr_Active, 0) = 1 AND o.Ofcr_RoleId IN (116, 117)
        )
        SELECT MD_ULBCode   = g.UlbCodeRaw,
               MD_WardId    = g.WardNumber,
               g.Waiting10,
               g.Returned400,
               Workable     = g.Waiting10 + g.Returned400,
               InMaster     = CASE WHEN g.UlbCode IS NULL THEN ''NO - ULB code is not a number''
                                   WHEN a.UlbCode IS NULL THEN ''NO - no names, no streets''
                                   ELSE ''yes'' END,
               UlbName      = a.UlbName,
               WardName     = a.WardName,
               DistrictCode = a.DistrictCode,
               Streets      = a.Streets,
               RIsMapped    = (SELECT COUNT(DISTINCT r.OfficerId) FROM ri r
                               WHERE r.UlbCode = g.UlbCode AND r.WardNumber = g.WardNumber)
        FROM g
        LEFT JOIN #aroWards a ON a.UlbCode = g.UlbCode AND a.WardNumber = g.WardNumber
        ORDER BY g.UlbCode, g.WardNumber;';
END
GO

SET NOEXEC OFF;
GO
