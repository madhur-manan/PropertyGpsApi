/*
    Property GPS by ULB + ward number: the UDD_KHATABTOA procedures it calls.

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.
    Run db/40 first (read-only). This script also refuses to run while
    BtoA_EPIDMetaData has no MD_ULBCode (or holds a ULB code that is not a whole number),
    while UDD_MASTERDB lacks a table or column the procedures below read, or while one of
    them cannot be backed up (encrypted).

    WHY. Property GPS now works ONLY by ULB + ward number: an RI sees the cases whose
    BtoA_EPIDMetaData.MD_ULBCode + MD_WardId (the ward number) match a ULB + ward mapped to
    them. Zone, corporation, division, block and the GBA/BBMP columns are no longer read.
    The master database is UDD_MASTERDB (it was masterDB_prod), and the RI signs in as
    role 117 (it was 116).

    WHAT CHANGES - only what that needs; every other line is the db/20, db/24 and db/30 text:
      - USP_S_GpsWardSync, USP_U_GpsWardSyncAck (db/20): @UlbCode int and @WardNumber
        nvarchar(20) take the place of @ZoneId and @WardId; the other parameters are as
        before. The filter is
            md.MD_ULBCode = @UlbCode
            AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @WardNumber
        The batch no longer returns ZoneId, WardId or GBAZoneName_En, and returns instead
        UlbCode, UlbName, WardNumber, WardName (the names from UDD_MASTERDB.dbo.mst_AROMapping,
        tabs and spaces trimmed) and StreetNumber (Site_StreetId as text). Every other
        column, both other result sets, the batching and the ack are as before.
      - USP_IU_BtoA_SiteRoadDetails_Officer (db/30): @BtoA_RoadId is NVARCHAR(50) - road ids
        are text now; "999" still means "not in the list". The KSRSAC check
        (masterDB_prod MstRoadKSRAC through the BBMP zone/ward/street) becomes: is the road in
        the application's ward street list - a row of UDD_MASTERDB.dbo.mst_AROMapping with
        the application's MD_ULBCode, its ward number and StreetNumber = @BtoA_RoadId (all
        trimmed). The street number is the street (Eaasthi's StreetId); the name is not
        compared, because one street is spelled differently block by block. As before it only sets KsracMatched,
        which the API reports as a warning; the road is stored either way. Result columns
        are unchanged (the name KsracMatched and the message wording are kept for every
        caller). One addition: while Ofcr_RoadId is still an integer column (section 5), a
        road id that is not a whole number is answered Status 0 with a plain message instead
        of failing on a conversion error.
      - USP_IU_BtoA_StatusDetail_Officer (db/30): a new last parameter @Source NVARCHAR(50) = NULL.
        With @Source = 'PropertyGPS' the officer is looked up in UDD_MASTERDB.dbo.mst_Officer
        (the submitting row first); with @Source NULL - every other app - it still reads
        masterDB_prod exactly as before.
        The API sends @Source = N'PropertyGPS'; then role 117 takes exactly the path role 116
        takes (App_Status 13, "Data Received from RI"). Without @Source - every other app
        that calls this procedure - every role behaves exactly as before, 117 included.
      - USP_IU_BtoA_MainApp_Officer (db/30): land use from UDD_MASTERDB.dbo.mst_KsrsacMasterPlan2015.
      - USP_CheckMobileAppVersion (db/24): versions from UDD_MASTERDB.dbo.Mst_AppVersion.
        dbo.fn_GpsVersionKey (db/24, unchanged) is created with it, in case db/24 never ran
        on this server.
    NOT CHANGED: USP_S_GetAppDetails, the old fetch. Current app builds use the ward sync
    and never call it; it still reads zone/ward and masterDB_prod.

    BACKUP AND UNDO. Before anything changes, section 2 saves every object above - its text,
    SET options and GRANTs, or that it did not exist - in dbo.ZZ_PropertyGpsApi_ProcBackup.
    A re-run keeps the first copy, so the table always holds what was there before db/42.
    Section 6 (commented out) puts every object back from it.

    OPTIONAL (section 5, YOUR DECISION). BtoA_SiteRoadDetails_Officer.Ofcr_RoadId is an
    integer column today (db/40 says for certain). The procedure now takes the road id as
    text, but a text id that is not a number cannot be stored in an integer column. 5a shows
    (read-only) what depends on the column; 5b (commented out) makes it NVARCHAR(50), only
    if it is still an integer type and nothing blocks it. Until then numeric road ids work
    exactly as before. After 5b, anything else that compares Ofcr_RoadId with a number
    (db/40 grid 5 lists the modules, the API's EarlierSurveyReader included) fails as soon
    as a text id is stored.

    Sections: 1 guard, 2 backup, 3 the procedures, 4 checks (read-only),
    5 optional: Ofcr_RoadId as text (5a read-only, 5b commented out), 6 undo (commented out).
*/

USE UDD_KHATABTOA;
GO

-------------------------------------------------------------------------------
-- 1. GUARD (SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not)
-------------------------------------------------------------------------------
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));

DECLARE @why nvarchar(2000) = NULL, @ulbType sysname, @badUlb int = 0;

IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
    SET @why = N'Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.';
ELSE IF COL_LENGTH(N'dbo.BtoA_EPIDMetaData', N'MD_ULBCode') IS NULL
    SET @why = N'Refusing to run: dbo.BtoA_EPIDMetaData has no MD_ULBCode column, and the ward sync filters on it. Nothing changed.';
ELSE IF DB_ID(N'UDD_MASTERDB') IS NULL
    SET @why = N'Refusing to run: there is no UDD_MASTERDB on this server. Nothing changed.';
ELSE
BEGIN
    -- What the new procedures read in UDD_MASTERDB. A missing table would only fail when an
    -- officer submits; refuse now instead.
    SELECT @why = N'Refusing to run: missing in UDD_MASTERDB: '
                + STRING_AGG(CONVERT(nvarchar(max), n.t + N'.' + n.c), N', ') + N'. Nothing changed.'
    FROM (VALUES (N'mst_AROMapping', N'ULBCode'), (N'mst_AROMapping', N'ULBName_EN'), (N'mst_AROMapping', N'WardNumber'),
                 (N'mst_AROMapping', N'WardName_EN'), (N'mst_AROMapping', N'StreetNumber'), (N'mst_AROMapping', N'StreetName_EN'),
                 (N'mst_Officer', N'Ofcr_Id'), (N'mst_Officer', N'Ofcr_RoleId'), (N'mst_Officer', N'Ofcr_Name'), (N'mst_Officer', N'Ofcr_MNo'),
                 (N'mst_KsrsacMasterPlan2015', N'GpsLandUseTypeValue'), (N'mst_KsrsacMasterPlan2015', N'BBMPLandUseTypeId'),
                 (N'mst_KsrsacMasterPlan2015', N'BBMPLAndUseTypeNAme'),
                 (N'Mst_AppVersion', N'App_Platform'), (N'Mst_AppVersion', N'IsActive'), (N'Mst_AppVersion', N'Current_Version'),
                 (N'Mst_AppVersion', N'AppVersion_Id'), (N'Mst_AppVersion', N'Latest_Version'), (N'Mst_AppVersion', N'MinRequired_Version'),
                 (N'Mst_AppVersion', N'Update_Message'), (N'Mst_AppVersion', N'Download_URL'), (N'Mst_AppVersion', N'File_Size_MB'),
                 (N'Mst_AppVersion', N'Release_Notes'), (N'Mst_AppVersion', N'IsForceUpdate')) n(t, c)
    WHERE COL_LENGTH(N'UDD_MASTERDB.dbo.' + n.t, n.c) IS NULL;

    -- An encrypted procedure has no text to save, so it could not be put back.
    IF @why IS NULL
        SELECT @why = N'Refusing to run: no text can be saved for '
                    + STRING_AGG(CONVERT(nvarchar(max), n.p), N', ') + N' (encrypted?), so it could not be put back. Nothing changed.'
        FROM (VALUES (N'USP_S_GpsWardSync'), (N'USP_U_GpsWardSyncAck'), (N'USP_IU_BtoA_SiteRoadDetails_Officer'),
                     (N'USP_IU_BtoA_StatusDetail_Officer'), (N'USP_IU_BtoA_MainApp_Officer'), (N'USP_CheckMobileAppVersion'),
                     (N'fn_GpsVersionKey')) n(p)
        WHERE OBJECT_ID(N'dbo.' + QUOTENAME(n.p)) IS NOT NULL
          AND OBJECT_DEFINITION(OBJECT_ID(N'dbo.' + QUOTENAME(n.p))) IS NULL;

    -- MD_ULBCode is compared with an int. Stored as text, one value that is not a whole
    -- number would make every ward sync fail on a conversion error.
    IF @why IS NULL
    BEGIN
        SELECT @ulbType = TYPE_NAME(c.system_type_id)
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.BtoA_EPIDMetaData') AND c.name = N'MD_ULBCode';

        IF @ulbType IN (N'varchar', N'nvarchar', N'char', N'nchar')
        BEGIN
            EXEC sys.sp_executesql
                 N'SELECT @bad = COUNT(*) FROM dbo.BtoA_EPIDMetaData WITH (NOLOCK)
                   WHERE MD_ULBCode IS NOT NULL AND TRY_CONVERT(int, MD_ULBCode) IS NULL;',
                 N'@bad int OUTPUT', @bad = @badUlb OUTPUT;
            IF @badUlb > 0
                SET @why = N'Refusing to run: BtoA_EPIDMetaData.MD_ULBCode is ' + @ulbType + N' and '
                         + CONVERT(nvarchar(10), @badUlb) + N' rows hold a value that is not a whole number. Nothing changed.';
        END
        ELSE IF @ulbType NOT IN (N'int', N'bigint', N'smallint', N'tinyint', N'numeric', N'decimal', N'float', N'real')
            SET @why = N'Refusing to run: BtoA_EPIDMetaData.MD_ULBCode is ' + ISNULL(@ulbType, N'(unknown)')
                     + N', which cannot be compared with a ULB code. Nothing changed.';
    END
END

IF @why IS NOT NULL
BEGIN
    RAISERROR(N'%s', 16, 1, @why);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 2. BACKUP - what is there now, once. Undo (section 6) restores from here.
--      object_type NULL = the object did not exist before db/42 (undo drops it).
-------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_ProcBackup', N'U') IS NULL
    CREATE TABLE dbo.ZZ_PropertyGpsApi_ProcBackup
    (
        [name]                 sysname       NOT NULL PRIMARY KEY,
        [definition]           nvarchar(max) NULL,
        saved_at               datetime      NOT NULL CONSTRAINT DF_ZZ_PgpsProcBackup DEFAULT (GETDATE()),
        object_type            char(2)       NULL,
        uses_ansi_nulls        bit           NULL,
        uses_quoted_identifier bit           NULL,
        grants                 nvarchar(max) NULL    -- GRANT / DENY statements, run again after a restore
    );
GO

-- Kept on a re-run, so it always holds what was there before db/42.
INSERT INTO dbo.ZZ_PropertyGpsApi_ProcBackup ([name], [definition], object_type, uses_ansi_nulls, uses_quoted_identifier, grants)
SELECT n.p, m.[definition], o.[type], m.uses_ansi_nulls, m.uses_quoted_identifier, g.grants
FROM (VALUES (N'USP_S_GpsWardSync'), (N'USP_U_GpsWardSyncAck'), (N'USP_IU_BtoA_SiteRoadDetails_Officer'),
             (N'USP_IU_BtoA_StatusDetail_Officer'), (N'USP_IU_BtoA_MainApp_Officer'), (N'USP_CheckMobileAppVersion'),
             (N'fn_GpsVersionKey')) n(p)
LEFT JOIN sys.objects o     ON o.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(n.p))
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
OUTER APPLY (SELECT grants = STRING_AGG(CONVERT(nvarchar(max),
                        CASE dp.[state] WHEN 'D' THEN N'DENY ' ELSE N'GRANT ' END
                        -- sys.database_permissions and the database's own catalog can differ
                        -- in collation (Msg 4191 in STRING_AGG), so all three are pinned.
                        -- The object's name comes from the permission row's own join (ob), not
                        -- the outer o: an aggregate may not mix outer and inner columns (Msg 8124).
                        + dp.permission_name COLLATE DATABASE_DEFAULT
                        + N' ON dbo.' + QUOTENAME(ob.name) COLLATE DATABASE_DEFAULT
                        + N' TO ' + QUOTENAME(pr.name) COLLATE DATABASE_DEFAULT
                        + CASE dp.[state] WHEN 'W' THEN N' WITH GRANT OPTION' ELSE N'' END), N'; ')
             FROM sys.database_permissions dp
             JOIN sys.objects ob ON ob.object_id = dp.major_id
             JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
             WHERE dp.class = 1 AND dp.major_id = o.object_id AND dp.minor_id = 0) g
WHERE NOT EXISTS (SELECT 1 FROM dbo.ZZ_PropertyGpsApi_ProcBackup b WHERE b.[name] = n.p);
GO

SELECT [name], object_type, saved_at, HasText = CASE WHEN [definition] IS NULL THEN 0 ELSE 1 END, grants
FROM dbo.ZZ_PropertyGpsApi_ProcBackup
ORDER BY [name];

-- Nothing changes unless every object that exists has its text saved. In a batch of its own:
-- if the backup batch above fails - even to compile - this one still runs and stops the rest.
IF (SELECT COUNT(*) FROM dbo.ZZ_PropertyGpsApi_ProcBackup
    WHERE [name] IN (N'USP_S_GpsWardSync', N'USP_U_GpsWardSyncAck', N'USP_IU_BtoA_SiteRoadDetails_Officer',
                     N'USP_IU_BtoA_StatusDetail_Officer', N'USP_IU_BtoA_MainApp_Officer', N'USP_CheckMobileAppVersion',
                     N'fn_GpsVersionKey')
      AND (object_type IS NULL OR [definition] IS NOT NULL)) <> 7
BEGIN
    RAISERROR('The backup is incomplete (see the grid above). Nothing after this point runs.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 3.1 USP_S_GpsWardSync - what this phone is missing, 100 applications at a time
--     (db/20, by ULB + ward number)
--
--    Result sets, always all three, in this order:
--      1. ServerCount, LocalCount, MissingCount
--      2. the next batch (several rows per corner plot)
--      3. AppId, AppStatus for held App_Ids that are no longer workable here
--         (empty unless @IncludeClosed = 1; AppStatus NULL = no longer active)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_S_GpsWardSync]
    @UlbCode         int,
    @WardNumber      nvarchar(20),            -- ward number as text, matched trimmed
    @LocalCount      int           = 0,
    @KnownAppIds     nvarchar(max) = NULL,   -- comma-separated App_Ids the phone holds for this ward
    @ReturnedAppIds  nvarchar(max) = NULL,   -- of those, the ones the phone already shows as returned (400)
    @BatchSize       int           = 100,
    @IncludeClosed   bit           = 0,
    -- Of those, the ones the phone holds in an outdated shape (2026-10-05: road cards built
    -- from one road for all, before each row's own declaration was sent). Re-sent while
    -- still workable; still counted as held, so a closed one is reported closed.
    @RefreshAppIds   nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1 OR @BatchSize > 100
        SET @BatchSize = 100;

    SET @WardNumber = LTRIM(RTRIM(@WardNumber));

    CREATE TABLE #known    (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #returned (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #refresh  (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #ward     (App_Id int NOT NULL PRIMARY KEY, App_Status int NOT NULL, App_Cdte datetime NULL);
    CREATE TABLE #missing  (App_Id int NOT NULL PRIMARY KEY, App_Cdte datetime NULL);
    CREATE TABLE #batch    (App_Id int NOT NULL PRIMARY KEY);

    INSERT INTO #known (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@KnownAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    INSERT INTO #returned (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@ReturnedAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    INSERT INTO #refresh (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@RefreshAppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    -- Every workable application in the ward, newest per EPID. No IsPushedToGps here.
    INSERT INTO #ward (App_Id, App_Status, App_Cdte)
    SELECT x.App_Id, x.App_Status, x.App_Cdte
    FROM (
        SELECT ap.App_Id, ap.App_Status, ap.App_Cdte,
               rn = ROW_NUMBER() OVER (PARTITION BY ap.App_MotherEPID ORDER BY ap.App_Id DESC)
        FROM dbo.BtoAMainApp ap WITH (NOLOCK)
        JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
          ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
        WHERE ap.App_Active = 1
          AND ap.isProcessingFeePaid = 1
          AND ap.App_Status IN (10, 400)
          AND ISNULL(ap.App_MotherEPID, N'') <> N''
          AND md.MD_ULBCode = @UlbCode
          AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @WardNumber
          AND EXISTS (SELECT 1 FROM dbo.[BtoA_SiteDtls] sd WITH (NOLOCK)
                      WHERE sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1)
    ) x
    WHERE x.rn = 1;

    -- Missing: not on the phone at all, on it but not yet shown as returned by QC, or on it
    -- in an outdated shape the phone asked to have replaced.
    INSERT INTO #missing (App_Id, App_Cdte)
    SELECT w.App_Id, w.App_Cdte
    FROM #ward w
    WHERE NOT EXISTS (SELECT 1 FROM #known k WHERE k.App_Id = w.App_Id)
       OR (w.App_Status = 400 AND NOT EXISTS (SELECT 1 FROM #returned r WHERE r.App_Id = w.App_Id))
       OR EXISTS (SELECT 1 FROM #refresh f WHERE f.App_Id = w.App_Id);

    -- 1. Counts. LocalCount is echoed so the API log shows both sides of the comparison.
    SELECT ServerCount  = (SELECT COUNT(*) FROM #ward),
           LocalCount   = ISNULL(@LocalCount, 0),
           MissingCount = (SELECT COUNT(*) FROM #missing);

    -- 2. The next batch - oldest application first, as the queue is worked.
    INSERT INTO #batch (App_Id)
    SELECT TOP (@BatchSize) m.App_Id
    FROM #missing m
    ORDER BY m.App_Cdte, m.App_Id;

    -- The ULB and ward names, once: every row of the batch is in this ULB and ward.
    -- mst_AROMapping holds one row per street, with stray tabs and spaces in its text.
    DECLARE @UlbName nvarchar(500), @WardName nvarchar(500);
    SELECT TOP (1)
           @UlbName  = LTRIM(RTRIM(REPLACE(a.ULBName_EN,  CHAR(9), ''))),
           @WardName = LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), '')))
    FROM UDD_MASTERDB.dbo.mst_AROMapping a WITH (NOLOCK)
    WHERE a.ULBCode = @UlbCode
      AND LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = @WardNumber
    ORDER BY CASE WHEN a.WardName_EN IS NULL THEN 1 ELSE 0 END;

    SELECT DISTINCT
        ISNULL(ap.[App_Id], 0) AS AppID
        ,ap.[App_MotherEPID]   AS MotherEPID
        ,ap.[App_Type]         AS Type
        ,ap.[App_Status]       AS Status
        ,ap.[App_Remarks]      AS Remarks
        ,ap.[App_AdditionalInfo] AS AdditionalInfo
        ,ap.[App_DisplayId]    AS AppDisplayId
        ,CASE
            WHEN ap.[App_Source] IS NULL OR ap.[App_Source] = 'A' OR LEN(ap.[App_Source]) = 0 THEN 'BtoAKhata'
            WHEN ap.[App_Source] = 'newkhata' THEN 'SinglePlotApproval'
         END AS ApplicationType
        ,ap.[App_MotherSASID]  AS MotherSASID
        ,ISNULL(ap.[isProcessingFeePaid], 0) AS isProcessingFeePaid
        ,ap.[ProcessingFee]    AS ProcessingFee
        ,sd.[Site_Id]
        ,sd.[Site_App_Id]      AS ApplicationId
        ,sd.[Site_EPID_Id]     AS EpID
        -- ULB and ward number (db/42). ZoneId, WardId and GBAZoneName_En are no longer returned.
        ,CONVERT(int, md.MD_ULBCode) AS UlbCode
        ,@UlbName              AS UlbName
        ,LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) AS WardNumber
        ,@WardName             AS WardName
        -- The street as text only: street ids are text now (siteDetails.streetId). The raw
        -- Site_StreetId is not returned, so a text value there cannot break the API's int field.
        ,CONVERT(nvarchar(50), sd.Site_StreetId) AS StreetNumber
        ,srd.[Rd_Roadtype]     AS RoadType
        ,srd.[Rd_RoadId]       AS RoadId
        ,srd.[Rd_RoadName]     AS Roadname
        ,srd.Rd_isPresentInPublicRoadList
        ,srd.Rd_PrivateRoadId
        ,srd.Rd_PrivateRoadText
        ,srd.Rd_PrivateRoadName
        -- What the citizen typed for this road ("3rd Cross"), as opposed to Rd_RoadName,
        -- the street-master road it sits on or nearest to. Each road card needs its own.
        ,srd.Rd_EnteredRoadName
        ,sd.[Site_IsSameLocationAsKhata] AS IsSameLocationAsKhata
        ,sd.[Site_Latitude]    AS Latitude
        ,sd.[Site_Longitude]   AS Longitude
        ,sd.[Site_Order]       AS SiteOrder
        ,sd.[App_IsPropertySurvey]
        ,sd.[App_IsPropertySurveyLocated]
        ,md.MD_JSON            AS EpidJSON
        ,md.MD_KhataType       AS Khatatype
        ,md.MD_Latitude        AS KhataLatitude
        ,md.MD_Longitude       AS KhataLongitude
        ,ap.App_Cdte
        ,sd.DcConversionType
        ,sd.PrivateRoadId
        ,sd.PrivateRoadText
        ,sd.PrivateRoadName
        ,sd.Site_AdditionalInfo
        ,sd.publicRoadname
        ,sd.propertyUseType
        ,sd.comercialExtentinSqft
        ,sd.residentailsExtentinSqft
        ,sd.Site_isCornorPlot  AS isCornorPlot
        ,sd.Site_numberOfRoadFacingSides
        ,srd.Rd_RoadRow_ID     AS Rd_RoadRow_ID
    FROM #batch b
    JOIN dbo.BtoAMainApp ap WITH (NOLOCK) ON ap.App_Id = b.App_Id
    JOIN dbo.[BtoA_SiteDtls] sd WITH (NOLOCK) ON sd.Site_App_Id = ap.App_Id AND sd.Site_active = 1
    JOIN dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
      ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
    -- Active road rows only: a row the citizen's declaration no longer carries
    -- (Rd_RoadActive = 0) is not a road for the officer to verify. NULL counts as active.
    LEFT JOIN dbo.BtoA_SiteRoadDetails srd WITH (NOLOCK)
      ON srd.Rd_App_Id = ap.App_Id AND ISNULL(srd.Rd_RoadActive, 1) = 1
    ORDER BY App_Cdte, AppID;

    -- 3. Held but no longer workable here: submitted, decided, deactivated, moved ward,
    --    or superseded by a newer application on the same EPID.
    IF @IncludeClosed = 1
        SELECT AppId     = k.App_Id,
               AppStatus = CASE WHEN ap.App_Active = 1 THEN ap.App_Status END
        FROM #known k
        LEFT JOIN dbo.BtoAMainApp ap WITH (NOLOCK) ON ap.App_Id = k.App_Id
        WHERE NOT EXISTS (SELECT 1 FROM #ward w WHERE w.App_Id = k.App_Id);
    ELSE
        SELECT AppId = CAST(NULL AS int), AppStatus = CAST(NULL AS int) WHERE 1 = 0;
END
GO

-------------------------------------------------------------------------------
-- 3.2 USP_U_GpsWardSyncAck - the phone has saved these applications (db/20, by ULB + ward)
--
--    Sets IsPushedToGps = 1 and PushedToGpsDate only where it is NULL; an existing date
--    is kept. Scoped to the ward the phone asked about, so a phone cannot mark another
--    ward's applications. Status is not checked: this records what the phone holds.
--
--    Rows already at (1, date) are skipped only so trg_BtoAMainApp does not copy an
--    unchanged row into BtoAMainApp_Hist on every sync. It is not a fetch condition.
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_U_GpsWardSyncAck]
    @UlbCode     int,
    @WardNumber  nvarchar(20),     -- ward number as text, matched trimmed
    @AppIds      nvarchar(max)     -- comma-separated App_Ids, at most one batch
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @WardNumber = LTRIM(RTRIM(@WardNumber));

    CREATE TABLE #ack  (App_Id int NOT NULL PRIMARY KEY);
    CREATE TABLE #done (App_Id int NOT NULL);

    INSERT INTO #ack (App_Id)
    SELECT DISTINCT v.App_Id
    FROM STRING_SPLIT(ISNULL(@AppIds, N''), N',') s
    CROSS APPLY (SELECT App_Id = TRY_CAST(LTRIM(RTRIM(s.value)) AS int)) v
    WHERE v.App_Id IS NOT NULL;

    BEGIN TRAN;

        UPDATE ap
           SET ap.IsPushedToGps     = 1,
               ap.PushedToGpsDate   = ISNULL(ap.PushedToGpsDate, GETDATE()),
               ap.PushedToGpsRemark = ISNULL(ap.PushedToGpsRemark, N'Property GPS ward sync')
        OUTPUT inserted.App_Id INTO #done (App_Id)
        FROM dbo.BtoAMainApp ap
        JOIN #ack a ON a.App_Id = ap.App_Id
        WHERE EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData md
                      WHERE md.MD_APP_ID = ap.App_Id
                        AND md.MD_ULBCode = @UlbCode
                        AND LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId))) = @WardNumber)
          AND (ap.IsPushedToGps = 0 OR ap.PushedToGpsDate IS NULL);

    COMMIT TRAN;

    SELECT Requested = (SELECT COUNT(*) FROM #ack),
           Updated   = (SELECT COUNT(*) FROM #done);
END
GO

-------------------------------------------------------------------------------
-- 3.3 USP_IU_BtoA_SiteRoadDetails_Officer (db/30; road id as text, ward street-list check)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_IU_BtoA_SiteRoadDetails_Officer]
(
    @BtoA_RoadRowId INT = 0,              -- 0 = Insert, >0 = Update
    @BtoA_MainAppId INT,
	@BtoA_ApplicationDisplayId varchar(50),
    @BtoA_Corrected_Road_Details BIT,
    @BtoA_Correction_Type_Id INT,
    @BtoA_Correction_Type_Value NVARCHAR(50) = NULL,
    @BtoA_RoadType NVARCHAR(50) = NULL,
    @BtoA_IsPresentInPublicRoadList BIT,
    @BtoA_RoadId NVARCHAR(50) = NULL,     -- text since db/42; '999' = not in the list
    @BtoA_RoadName NVARCHAR(250) = NULL,
    @BtoA_ActualRoadName NVARCHAR(250) = NULL,
    @BtoA_Nearest_Public_Road_Latitude float = NULL,
    @BtoA_Nearest_Public_Road_Longitude float= NULL,
    @CBy BIGINT = NULL,
    @CRole INT = NULL,
	@BtoA_Nearest_Private_Road_Latitude float = NULL,
    @BtoA_Nearest_Private_Road_Longitude float= NULL,
	@BtoA_Nearest_Private_Road_Document NVARCHAR(max) = NULL,
    @BtoA_Nearest_Public_Road_Document NVARCHAR(max) = NULL,
	@BtoA_SiteRoadRowID nvarchar(1000)
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;   -- the API owns the transaction; never leave it doomed-but-open
    DECLARE @Message NVARCHAR(200),
            @ResultStatus BIT, @DisplayRequestID varchar(15);
	 select @BtoA_MainAppId=App_Id From BtoAMainApp where App_DisplayId=@BtoA_ApplicationDisplayId
	  DECLARE @EAASTHIZONEID INT;
	 DECLARE @EAASTHIWARDID INT;
	 DECLARE @EAASTHISTREETID INT;
	 DECLARE @ZoneID INT;
	 DECLARE @WARDID INT;

	 -- Road ids are text (db/42). While Ofcr_RoadId is still an integer column (db/42
	 -- section 5 not run), an id that is not a whole number cannot be stored: answer that
	 -- plainly - Status 0, nothing written - instead of failing on a conversion error.
	 IF @BtoA_RoadId IS NOT NULL
	    AND TRY_CONVERT(int, @BtoA_RoadId) IS NULL
	    AND EXISTS (SELECT 1 FROM sys.columns c
	                WHERE c.object_id = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer')
	                  AND c.name = N'Ofcr_RoadId'
	                  AND TYPE_NAME(c.system_type_id) IN (N'int', N'bigint', N'smallint', N'tinyint'))
	 BEGIN
	    SELECT
	        CONVERT(NVARCHAR(200), N'Road id ''' + LEFT(@BtoA_RoadId, 50) + N''' is not a number, and this server stores road ids as numbers only (db/42 section 5).') AS Message,
	        CAST(0 AS BIT) AS Status,
	        0 AS RoadRowId,
	        @BtoA_ApplicationDisplayId as DisplayRequestID,
	        CAST(0 AS BIT) AS KsracMatched;
	    RETURN;
	 END

	 -- The road check no longer decides whether the road is stored - only whether it is
	 -- reported as verified. Since db/42 it is the application's ward street list in
	 -- UDD_MASTERDB.dbo.mst_AROMapping, matched on ULB + ward number + STREET NUMBER only: the
	 -- street number is the street (Eaasthi's StreetId), and the table spells one street's
	 -- name differently block by block, so the name is not compared. The name KsracMatched
	 -- and the message wording are kept for every caller.
	 DECLARE @KsracMatched BIT = 0;

	 if exists (SELECT 1
	              FROM BtoA_EPIDMetaData M
	              INNER JOIN UDD_MASTERDB.dbo.mst_AROMapping A
	                 ON A.ULBCode = M.MD_ULBCode
	                AND LTRIM(RTRIM(REPLACE(A.WardNumber, CHAR(9), ''))) COLLATE DATABASE_DEFAULT
	                    = LTRIM(RTRIM(CONVERT(nvarchar(20), M.MD_WardId))) COLLATE DATABASE_DEFAULT
	                AND LTRIM(RTRIM(REPLACE(A.StreetNumber, CHAR(9), ''))) = LTRIM(RTRIM(REPLACE(@BtoA_RoadId, CHAR(9), '')))
	             WHERE M.MD_APP_ID = @BtoA_MainAppId)
		SET @KsracMatched = 1;

	BEGIN
    IF not exists(select * from [BtoA_SiteRoadDetails_Officer] where Ofcr_App_Id=@BtoA_MainAppId and Ofcr_RoadId=@BtoA_RoadId and  Ofcr_SiteRoadRowID=@BtoA_SiteRoadRowID  and CRole=@CRole and CBy=@CBy	)

    BEGIN
        -- INSERT MODE
        INSERT INTO [dbo].[BtoA_SiteRoadDetails_Officer]
        (
            [Ofcr_App_Id],
			[Ofcr_ApplicationDisplayId],
            [Ofcr_Corrected_Road_Details],
            [Ofcr_Correction_Type_Id],
            [Ofcr_Correction_Type_Value],
            [Ofcr_RoadType],
            [Ofcr_IsPresentInPublicRoadList],
            [Ofcr_RoadId],
            [Ofcr_RoadName],
            [Ofcr_ActualRoadName],
            [Ofcr_Nearest_Public_Road_Latitude],
            [Ofcr_Nearest_Public_Road_Longitude],
            [CBy],
            [CDte],
            [CRole],
			Ofcr_Nearest_Private_Road_Latitude ,
			Ofcr_Nearest_Private_Road_Longitude ,
			Ofcr_Nearest_Private_Road_Document,
			Ofcr_Nearest_Public_Road_Document ,
			Ofcr_SiteRoadRowID,
			isactive
        )
        VALUES
        (
            @BtoA_MainAppId,
			@BtoA_ApplicationDisplayId,
            @BtoA_Corrected_Road_Details,
            @BtoA_Correction_Type_Id,
            @BtoA_Correction_Type_Value,
            @BtoA_RoadType,
            @BtoA_IsPresentInPublicRoadList,
            @BtoA_RoadId,
            @BtoA_RoadName,
            @BtoA_ActualRoadName,
            @BtoA_Nearest_Public_Road_Latitude,
            @BtoA_Nearest_Public_Road_Longitude,
            @CBy,
            GETDATE(),
            @CRole,
			@BtoA_Nearest_Private_Road_Latitude ,
			@BtoA_Nearest_Private_Road_Longitude ,
			@BtoA_Nearest_Private_Road_Document,
			@BtoA_Nearest_Public_Road_Document ,
			@BtoA_SiteRoadRowID,
			 CASE
				WHEN @BtoA_Correction_Type_Value <> 'delete'
					 AND @BtoA_Correction_Type_Id <> 2
				THEN 1
				ELSE 0
			END





        );

        -- Captured immediately, before anything else inserts. Read after the
        -- BtoA_DocumentTran insert below, SCOPE_IDENTITY() returns the DOCUMENT id, so
        -- every private road handed the caller back a road row id that was not a road.
        SET @BtoA_RoadRowId = SCOPE_IDENTITY();

		if(@BtoA_RoadType='Private')
		begin
		-- DocumentId carries the road, so two private roads on one property no longer
		-- overwrite each other. CBy/CRole were hardcoded to 1, losing who did the work.
		insert into BtoA_DocumentTran(docTrn_App_Id,docTrn_Mdoc_id,docTrn_Active,docTrn_url,docTrn_CBy,docTrn_CDte,docTrn_CRole,DigitalSketchUpload_flag, UniqueIdentifier, DocumentId)
		values(@BtoA_MainAppId,4,1,@BtoA_Nearest_Private_Road_Document,@CBy,GETDATE(),@CRole,1,444,@BtoA_SiteRoadRowID)
		end
        SET @Message = 'Record inserted successfully';
        SET @ResultStatus = 1;
    END
    ELSE
    BEGIN
        -- UPDATE MODE
        -- The insert branch sets @BtoA_RoadRowId from SCOPE_IDENTITY(); this branch never
        -- set it at all, so a resubmission returned RoadRowId = 0 and any caller keying
        -- off it - ours writes the served-notice document - silently did nothing.
        SELECT @BtoA_RoadRowId = Ofcr_RowId
          FROM [dbo].[BtoA_SiteRoadDetails_Officer]
         WHERE Ofcr_App_Id = @BtoA_MainAppId
           AND Ofcr_RoadId = @BtoA_RoadId
           AND Ofcr_SiteRoadRowID = @BtoA_SiteRoadRowID
           AND CRole = @CRole
           AND CBy = @CBy;

        UPDATE [dbo].[BtoA_SiteRoadDetails_Officer]
        SET
            [Ofcr_App_Id] = @BtoA_MainAppId,
            [Ofcr_Corrected_Road_Details] = @BtoA_Corrected_Road_Details,
            [Ofcr_Correction_Type_Id] = @BtoA_Correction_Type_Id,
            [Ofcr_Correction_Type_Value] = @BtoA_Correction_Type_Value,
            [Ofcr_RoadType] = @BtoA_RoadType,
            [Ofcr_IsPresentInPublicRoadList] = @BtoA_IsPresentInPublicRoadList,
            [Ofcr_RoadId] = @BtoA_RoadId,
            [Ofcr_RoadName] = @BtoA_RoadName,
            [Ofcr_ActualRoadName] = @BtoA_ActualRoadName,
            [Ofcr_Nearest_Public_Road_Latitude] = @BtoA_Nearest_Public_Road_Latitude,
            [Ofcr_Nearest_Public_Road_Longitude] = @BtoA_Nearest_Public_Road_Longitude,
            [UBy] = @CBy,
            [UDte] = GETDATE(),
            [URole] = @CRole,
			Ofcr_Nearest_Private_Road_Latitude =@BtoA_Nearest_Private_Road_Latitude ,
			Ofcr_Nearest_Private_Road_Longitude=@BtoA_Nearest_Private_Road_Longitude ,
			Ofcr_Nearest_Private_Road_Document=@BtoA_Nearest_Private_Road_Document,
			Ofcr_Nearest_Public_Road_Document=@BtoA_Nearest_Public_Road_Document,

			isactive = CASE
				WHEN @BtoA_Correction_Type_Value <> 'delete'
					 AND @BtoA_Correction_Type_Id <> 2
				THEN 1
				ELSE 0
			END
        -- Must match the INSERT guard above exactly. The previous predicate named three
        -- columns that do not exist on this table (BtoA_ApplicationDisplayId,
        -- BtoA_SiteRoadRowID, BtoA_GpsRoadRowID), so this branch could never have run.
        WHERE Ofcr_App_Id = @BtoA_MainAppId
          AND Ofcr_RoadId = @BtoA_RoadId
          AND Ofcr_SiteRoadRowID = @BtoA_SiteRoadRowID
          AND CRole = @CRole
          AND CBy = @CBy
		if(@BtoA_RoadType='Private')
		begin


				update BtoA_DocumentTran set docTrn_url=@BtoA_Nearest_Private_Road_Document, docTrn_UDte=GETDATE(), docTrn_URole=@CRole, docTrn_UBy=@CBy
				where docTrn_App_Id=@BtoA_MainAppId and docTrn_Mdoc_id=4 and UniqueIdentifier=444 and DocumentId=@BtoA_SiteRoadRowID
		END
        SET @Message = 'Record updated successfully';
        SET @ResultStatus = 1;
    END

    -- OUTPUT
    --
    -- Status means STORED, on both paths. Whether the road was recognised is carried
    -- by KsracMatched, which the API reports as a warning rather than a failure.
    SELECT
        CASE WHEN @KsracMatched = 1 THEN @Message
             ELSE @Message + ' (road not recognised in KSRSAC)' END AS Message,
        @ResultStatus AS Status,
        @BtoA_RoadRowId AS RoadRowId,
		@BtoA_ApplicationDisplayId as DisplayRequestID,
		@KsracMatched AS KsracMatched;
	end
END
GO

-------------------------------------------------------------------------------
-- 3.4 USP_IU_BtoA_StatusDetail_Officer (db/30; UDD_MASTERDB officer, @Source, 117 for Property GPS)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_IU_BtoA_StatusDetail_Officer]
(
    @Ofcr_StatusRowId INT = 0,                -- 0 = Insert, >0 = Update
    @Ofcr_App_Id INT,
	@Ofcr_ApplicationDisplayId nvarchar(50),
    @Ofcr_Mobile_Number NVARCHAR(15) = NULL,
    @Status_Date DATETIME = NULL,
    @Status_Id INT = NULL,
    @Status_Remark NVARCHAR(MAX) = NULL,
	@Status_Rejected_Reason NVARCHAR(MAX) = NULL,
    @Status_Value NVARCHAR(100) = NULL,
    @CBy BIGINT = NULL,
    @CRole INT = NULL,
	@isAutoEscalated bit =null,
	@autoEscalatedDate datetime=null,
	-- Which app is calling (db/42). Property GPS sends N'PropertyGPS'; every other caller
	-- leaves it NULL and gets exactly the old behaviour.
	@Source NVARCHAR(50) = NULL

)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;   -- the API owns the transaction; never leave it doomed-but-open
	DECLARE @Sts_PrevStatusId INT,
            @Sts_PrevOfficerId INT,
	    @Sts_PrevOfficerRoleId INT,
            @Sts_PrevOfficerName VARCHAR(MAX),
            @Sts_PrevOfficerMobileNo BIGINT,
            @Sts_CurrOfficerId INT,
			@Sts_CurrOfficerRoleId INT,
            @Sts_CurrOfficerName VARCHAR(MAX),
            @Sts_CurrOfficerMobileNo varchar(50),
			@Sts_MainAppStatus int,
            @Sts_CurrStatusId INT;

 -- 117 from Property GPS is the RI submitting a survey: it takes the 116 path below.
 -- 117 from anywhere else keeps its own branch, exactly as before.
 if(@CRole=117 and ISNULL(@Source, N'') <> N'PropertyGPS')
 begin
	if(@Status_Id=10)
	begin
	select @Sts_MainAppStatus=15
	select  @Sts_CurrStatusId=15
	end
	else if(@Status_Id=11)
	begin
	select @Sts_MainAppStatus=12
	select  @Sts_CurrStatusId=12
	end


 end
  else if(@CRole=118)
 begin
	if(@Status_Id in(10,12) and @Status_Value='APPROVED')
	begin
		select @Sts_MainAppStatus=16
		select  @Sts_CurrStatusId=16
	end
	else if(@Status_Id=11 and  @Status_Value='REJECTED')
	begin
		select @Sts_MainAppStatus=12
		select  @Sts_CurrStatusId=12
	end
	else if((@Status_Id=11 and   @Status_Value='REJECTED'))
	begin
			select @Sts_MainAppStatus=12
		select  @Sts_CurrStatusId=12
	end
		else if(@Status_Id=11  or @Status_Value='REJECTED')
	begin
		select @Sts_MainAppStatus=12
		select  @Sts_CurrStatusId=12
	end

	else if(@Status_Id=10 and  @Status_Value='APPROVED')
	begin
		select @Sts_MainAppStatus=16
		select  @Sts_CurrStatusId=16
	end
	else
	begin
	select @Sts_MainAppStatus=16
		select  @Sts_CurrStatusId=16
	end




 end
 -- 116 is the Revenue Inspector role the GPS app signed in as until db/42; since then the
 -- RI is 117, sent with @Source = 'PropertyGPS'. Until 116 had a branch, nothing matched it,
 -- so @Sts_MainAppStatus stayed NULL and the unconditional update below set
 -- BtoAMainApp.App_Status to NULL - silently, because the column is nullable. The
 -- application then vanished from every query that filters on App_Status.
 -- 13 is "Data Received from RI" in Mst_AppStatus.
 else if(@CRole=116 or (@CRole=117 and @Source=N'PropertyGPS'))
 begin
	select @Sts_MainAppStatus=13
	select @Sts_CurrStatusId=13
 end

    DECLARE @Message NVARCHAR(200),
            @ResultStatus BIT, @DisplayRequestID nvarchar(15);
	 select @Ofcr_App_Id=App_Id From BtoAMainApp where App_DisplayId=@Ofcr_ApplicationDisplayId





	/*********************Master Status Logging start********************/
IF @Source = N'PropertyGPS'
    -- Property GPS (UDD): the officer row that submitted - one mobile can have both a 116 and
    -- a 117 row, and the master is UDD_MASTERDB.
    SELECT TOP (1) @Sts_CurrOfficerId = Ofcr_Id, @Sts_CurrOfficerRoleId = isnull(Ofcr_RoleId,@CRole), @Sts_CurrOfficerName = isnull( Ofcr_Name,0), @Sts_CurrOfficerMobileNo = isnull(Ofcr_MNo,@Ofcr_Mobile_Number),@Sts_CurrOfficerName=Ofcr_Name
    FROM UDD_MASTERDB.dbo.mst_Officer
    WHERE Ofcr_MNo=@Ofcr_Mobile_Number
    ORDER BY CASE WHEN Ofcr_Id = @CBy THEN 0 WHEN Ofcr_RoleId = @CRole THEN 1 ELSE 2 END, Ofcr_Id;
ELSE
    -- Every other caller: exactly as before this script.
    SELECT  @Sts_CurrOfficerId = Ofcr_Id, @Sts_CurrOfficerRoleId = isnull(Ofcr_RoleId,@CRole), @Sts_CurrOfficerName = isnull( Ofcr_Name,0), @Sts_CurrOfficerMobileNo = isnull(Ofcr_MNo,@Ofcr_Mobile_Number),@Sts_CurrOfficerName=Ofcr_Name  FROM masterDB_prod.dbo.mst_Officer  WHERE Ofcr_MNo=@Ofcr_Mobile_Number;


  if exists (select 1 from [BtoA_StatusDetail_Officer] where Ofcr_app_ID=@Ofcr_app_ID and CBy=@CBy and CRole=@CRole)
  begin
  update [BtoA_StatusDetail_Officer] set Status_Active=0 where Ofcr_app_ID=@Ofcr_app_ID and CBy=@CBy and CRole=@CRole
  end

	print '============INSERT ================'
        INSERT INTO [dbo].[BtoA_StatusDetail_Officer]
        (
            [Ofcr_App_Id],
			[Ofcr_ApplicationDisplayId],
            [Ofcr_Mobile_Number],
            [Status_Date],
            [Status_Id],
            [Status_Remark],
			[Status_Rejected_Reason],
            [Status_Value],
            [CBy],
            [CDte],
            [CRole],
			isAutoEscalated,
			autoEscalatedDate,
			Status_Active
        )
        VALUES
        (
            @Ofcr_App_Id,
			@Ofcr_ApplicationDisplayId,
            @Ofcr_Mobile_Number,
            ISNULL(@Status_Date, GETDATE()),
            @Status_Id,
            @Status_Remark,
			@Status_Rejected_Reason,
            @Status_Value,
            @CBy,
            GETDATE(),
            @CRole,
			@isAutoEscalated,
			@autoEscalatedDate,
			1

        );
			print '============INSERT END================'






-- Never blank the status. An unmapped role used to corrupt the application silently;
-- failing loudly here means the API classifies it retryable and nothing is lost.
IF @Sts_MainAppStatus IS NULL
BEGIN
    DECLARE @unmapped nvarchar(50) = ISNULL(CONVERT(nvarchar(50), @CRole), N'(null)');
    RAISERROR(N'USP_IU_BtoA_StatusDetail_Officer: no status mapping for CRole %s. Refusing to set App_Status to NULL.', 16, 1, @unmapped);
    RETURN;
END

update BtoAMainApp set App_Status=@Sts_MainAppStatus , App_AdditionalInfo='status details Received Over the GPS API', App_UDte=GETDATE()
where App_Id=@Ofcr_App_Id


UPDATE SM
            SET
                SM.Sts_MainAppStatus = @Sts_MainAppStatus,
                SM.Sts_CurrStatusId = @Sts_CurrStatusId,
				SM.Sts_CurrOfficerId = @Sts_CurrOfficerId,
                SM.Sts_CurrOfficerRoleId = @Sts_CurrOfficerRoleId,
                SM.Sts_CurrOfficerMobileNo = @Sts_CurrOfficerMobileNo,

				SM.Sts_PrevStatusId = @Sts_PrevStatusId,
                SM.Sts_PrevOfficerId = @Sts_PrevOfficerId,
                SM.Sts_PrevOfficerName = @Sts_PrevOfficerName,
                SM.Sts_PrevOfficerMobileNo = @Sts_PrevOfficerMobileNo,
                SM.Sts_PrevOfficerRoleId = @Sts_PrevOfficerRoleId,
				SM.Sts_comments=@Status_Remark,
				Sm.Sts_rejectedText=@Status_Rejected_Reason,
				SM.Sts_rejectedID=@Status_Rejected_Reason,
                SM.Udt = GETDATE()
            FROM BtoA_Status_Master_Tran SM
            WHERE SM.Sts_MainAppId = @Ofcr_App_Id;

		SET @DisplayRequestID = @Ofcr_ApplicationDisplayId;
        SET @Ofcr_StatusRowId = SCOPE_IDENTITY();
        SET @Message = 'Record inserted successfully';
        SET @ResultStatus = 1;
END
GO

-------------------------------------------------------------------------------
-- 3.5 USP_IU_BtoA_MainApp_Officer (db/30; land use from UDD_MASTERDB)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

-- select * From BtoA_MainApp_Officer
CREATE OR ALTER PROCEDURE [dbo].[USP_IU_BtoA_MainApp_Officer]
(
    @Ofcr_RowId INT = 0,  -- 0 = Insert, >0 = Update
    @Ofcr_ApplicationDisplayId NVARCHAR(50),
    @Ofcr_App_Id INT,
    @Ofcr_SsaId NVARCHAR(50) = NULL,
    @Ofcr_MotherEPID NVARCHAR(50) = NULL,
    @Ofcr_ApplicationDate DATETIME,
    @Ofcr_PropertyLandExistOnSpot BIT,
    @Ofcr_IsAllBhoomiSurveyNosCorrect BIT,
    @Ofcr_IsGovtProperty BIT,
    @Ofcr_CorrectedGpsLocation BIT,
    @Ofcr_CorrectedLatitude NVARCHAR(50) = NULL,
    @Ofcr_CorrectedLongitude NVARCHAR(50) = NULL,
    @Ofcr_CorrectedPropertyUseType BIT,
    @Ofcr_CorrectedPropertyUseTypeId INT = NULL,
    @Ofcr_CorrectedPropertyUseTypeValue NVARCHAR(100) = NULL,
    @Ofcr_CorrectedCommercialArea FLOAT = NULL,
    @Ofcr_CorrectedResidentialArea FLOAT = NULL,
	@Ofcr_CorrectedIndustrialArea FLOAT = NULL,
    @Ofcr_CorrectedRoadDetails BIT,
    @Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing BIT,
    @Ofcr_RoadFacingSides INT = NULL,
    @Ofcr_RoadWidthInFrontOfPropertyInSqft FLOAT = NULL,
    @Ofcr_RoadWidthInFrontOfPropertyInSqmt FLOAT = NULL,
	@Ofcr_nearest_public_Road_Latitude nvarchar(50)= NULL,
	@Ofcr_nearest_public_Road_Longitude nvarchar(50)= NULL,
	@Ofcr_gpsStatusFullWorkflowJSON nvarchar(max),
    @Ofcr_Aditional NVARCHAR(MAX) = NULL,
    @CBy BIGINT = NULL,
    @CRole INT = NULL ,
	@matchedSurveyNo int,  --added 28012026
	@surveyRemark NVARCHAR(MAX), --added 28012026,
	@Ofcr_IsBuildingExists BIT  , --New Added Coloumn 11022026,
	@mstPlanPropertyUseType  nvarchar(50)=null

)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Message NVARCHAR(MAX),
            @ResultStatus BIT,@DisplayRequestID Nvarchar(15);


	if(@Ofcr_CorrectedPropertyUseTypeValue<>'Mixed')
	begin
	select @Ofcr_CorrectedPropertyUseTypeId=isnull(BBMPLandUseTypeId,0),@Ofcr_CorrectedPropertyUseTypeValue=BBMPLAndUseTypeNAme from UDD_MASTERDB.dbo.mst_KsrsacMasterPlan2015 where GpsLandUseTypeValue=@Ofcr_CorrectedPropertyUseTypeValue

	end
	if exists(select 1 from BtoAMainApp where App_DisplayId= @Ofcr_ApplicationDisplayId)
	begin


	if not exists(select 1 from BtoA_MainApp_Officer where Ofcr_ApplicationDisplayId= @Ofcr_ApplicationDisplayId)
	begin

        INSERT INTO [dbo].BtoA_MainApp_Officer
        (
            [Ofcr_ApplicationDisplayId],
            [Ofcr_App_Id],
            [Ofcr_SsaId],
            [Ofcr_MotherEPID],
            [Ofcr_ApplicationDate],
            [Ofcr_PropertyLandExistOnSpot],
            [Ofcr_IsAllBhoomiSurveyNosCorrect],
            [Ofcr_IsGovtProperty],
            [Ofcr_CorrectedGpsLocation],
            [Ofcr_CorrectedLatitude],
            [Ofcr_CorrectedLongitude],
            [Ofcr_CorrectedPropertyUseType],
            [Ofcr_CorrectedPropertyUseTypeId],
            [Ofcr_CorrectedPropertyUseTypeValue],
            [Ofcr_CorrectedCommercialArea],
            [Ofcr_CorrectedResidentialArea],
			[Ofcr_Corrected_IndustrialArea],
            [Ofcr_CorrectedRoadDetails],
            [Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing],
            [Ofcr_RoadFacingSides],
            [Ofcr_RoadWidthInFrontOfPropertyInSqft],
            [Ofcr_RoadWidthInFrontOfPropertyInSqmt],
			[Ofcr_nearest_public_Road_Latitude],
			[Ofcr_nearest_public_Road_Longitude],
			[Ofcr_gpsStatusFullWorkflowJSON],
            [Ofcr_Aditional],
            [CBy],
            [CDte],
            [CRole],
			matchedSurveyNo,
			surveyRemark,
			IsBuildingExists,
			KSRSACMasterPlanLandUseType
        )
        VALUES
        (
            @Ofcr_ApplicationDisplayId,
            @Ofcr_App_Id,
            @Ofcr_SsaId,
            @Ofcr_MotherEPID,
            @Ofcr_ApplicationDate,
            @Ofcr_PropertyLandExistOnSpot,
            @Ofcr_IsAllBhoomiSurveyNosCorrect,
            @Ofcr_IsGovtProperty,
            @Ofcr_CorrectedGpsLocation,
            @Ofcr_CorrectedLatitude,
            @Ofcr_CorrectedLongitude,
            @Ofcr_CorrectedPropertyUseType,
            @Ofcr_CorrectedPropertyUseTypeId,
            @Ofcr_CorrectedPropertyUseTypeValue,
            @Ofcr_CorrectedCommercialArea,
            @Ofcr_CorrectedResidentialArea,
			@Ofcr_CorrectedIndustrialArea,
            @Ofcr_CorrectedRoadDetails,
            @Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing,
            @Ofcr_RoadFacingSides,
            @Ofcr_RoadWidthInFrontOfPropertyInSqft,
            @Ofcr_RoadWidthInFrontOfPropertyInSqmt,
			@Ofcr_nearest_public_Road_Latitude,
			@Ofcr_nearest_public_Road_Longitude,
			@Ofcr_gpsStatusFullWorkflowJSON,
            @Ofcr_Aditional,
            @CBy,
            GETDATE(),
            @CRole,

			@matchedSurveyNo,
			@surveyRemark,
			@Ofcr_IsBuildingExists,
			@mstPlanPropertyUseType
        );

        SET @Ofcr_RowId = SCOPE_IDENTITY();
        SET @Message = 'Record inserted successfully';
        SET @ResultStatus = 1;
	end

    ELSE
    BEGIN
        UPDATE [dbo].[BtoA_MainApp_Officer]
        SET
            [Ofcr_SsaId] = @Ofcr_SsaId,
            [Ofcr_MotherEPID] = @Ofcr_MotherEPID,
            [Ofcr_ApplicationDate] = @Ofcr_ApplicationDate,
            [Ofcr_PropertyLandExistOnSpot] = @Ofcr_PropertyLandExistOnSpot,
            [Ofcr_IsAllBhoomiSurveyNosCorrect] = @Ofcr_IsAllBhoomiSurveyNosCorrect,
            [Ofcr_IsGovtProperty] = @Ofcr_IsGovtProperty,
            [Ofcr_CorrectedGpsLocation] = @Ofcr_CorrectedGpsLocation,
            [Ofcr_CorrectedLatitude] = @Ofcr_CorrectedLatitude,
            [Ofcr_CorrectedLongitude] = @Ofcr_CorrectedLongitude,
            [Ofcr_CorrectedPropertyUseType] = @Ofcr_CorrectedPropertyUseType,
            [Ofcr_CorrectedPropertyUseTypeId] = @Ofcr_CorrectedPropertyUseTypeId,
            [Ofcr_CorrectedPropertyUseTypeValue] = @Ofcr_CorrectedPropertyUseTypeValue,
            [Ofcr_CorrectedCommercialArea] = @Ofcr_CorrectedCommercialArea,
            [Ofcr_CorrectedResidentialArea] = @Ofcr_CorrectedResidentialArea,
			[Ofcr_Corrected_IndustrialArea]=@Ofcr_CorrectedIndustrialArea,
            [Ofcr_CorrectedRoadDetails] = @Ofcr_CorrectedRoadDetails,
            [Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing] = @Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing,
            [Ofcr_RoadFacingSides] = @Ofcr_RoadFacingSides,
            [Ofcr_RoadWidthInFrontOfPropertyInSqft] = @Ofcr_RoadWidthInFrontOfPropertyInSqft,
            [Ofcr_RoadWidthInFrontOfPropertyInSqmt] = @Ofcr_RoadWidthInFrontOfPropertyInSqmt,
			[Ofcr_nearest_public_Road_Latitude]=@Ofcr_nearest_public_Road_Latitude,
			[Ofcr_nearest_public_Road_Longitude]=@Ofcr_nearest_public_Road_Longitude,
			[Ofcr_gpsStatusFullWorkflowJSON]=@Ofcr_gpsStatusFullWorkflowJSON,
            [Ofcr_Aditional] = @Ofcr_Aditional,
            [UBy] = @CBy,
            [UDte] = GETDATE(),
            [URole] = @CRole,
			matchedSurveyNo=@matchedSurveyNo,
			surveyRemark=@surveyRemark,
			IsBuildingExists=	@Ofcr_IsBuildingExists,
			KSRSACMasterPlanLandUseType=@mstPlanPropertyUseType
        WHERE Ofcr_ApplicationDisplayId=  @Ofcr_ApplicationDisplayId and Ofcr_App_Id=@Ofcr_App_Id

        SET @Message = 'Record updated successfully';
        SET @ResultStatus = 1;
    END

	END
    else
	begin

	SELECT @ResultStatus=0
		SELECT
        'Record not found in BtoAMainApp table' AS Message,
       @ResultStatus  AS Status,
        0 AS AppId,
		@Ofcr_ApplicationDisplayId as  DisplayRequestID ;
	end

	SELECT
        @Message AS Message,
        @ResultStatus AS Status,
        @Ofcr_RowId AS AppId,
		@Ofcr_ApplicationDisplayId as  DisplayRequestID ;
END
GO

-------------------------------------------------------------------------------
-- 3.6 dbo.fn_GpsVersionKey (db/24, unchanged) and USP_CheckMobileAppVersion
--     (db/24; Mst_AppVersion from UDD_MASTERDB)
-------------------------------------------------------------------------------
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER FUNCTION dbo.fn_GpsVersionKey (@Version NVARCHAR(50))
RETURNS TABLE
AS
RETURN
    WITH v AS
    (
        -- Trim, and drop build metadata ("1.2.3+45" -> "1.2.3").
        SELECT LTRIM(RTRIM(LEFT(ISNULL(@Version, N''), CHARINDEX(N'+', ISNULL(@Version, N'') + N'+') - 1))) AS s
    ),
    p AS
    (
        SELECT s, LEN(s) - LEN(REPLACE(s, N'.', N'')) AS dots
        FROM v
    ),
    parts AS
    (
        SELECT
            -- Digits and dots only: PARSENAME would otherwise strip [brackets] or "quotes".
            CASE WHEN s LIKE N'%[^0-9.]%' THEN 0 ELSE 1 END AS plain,
            CASE dots WHEN 0 THEN s     WHEN 1 THEN PARSENAME(s, 2) WHEN 2 THEN PARSENAME(s, 3) END AS major,
            CASE dots WHEN 0 THEN N'0'  WHEN 1 THEN PARSENAME(s, 1) WHEN 2 THEN PARSENAME(s, 2) END AS minor,
            CASE dots WHEN 0 THEN N'0'  WHEN 1 THEN N'0'            WHEN 2 THEN PARSENAME(s, 1) END AS patch
        FROM p
    )
    SELECT CASE
               WHEN plain = 1
                AND LEN(major) BETWEEN 1 AND 6
                AND LEN(minor) BETWEEN 1 AND 6
                AND LEN(patch) BETWEEN 1 AND 6
               THEN TRY_CAST(major AS BIGINT) * 1000000000000
                  + TRY_CAST(minor AS BIGINT) * 1000000
                  + TRY_CAST(patch AS BIGINT)
           END AS VersionKey
    FROM parts;
GO

CREATE OR ALTER PROCEDURE [dbo].[USP_CheckMobileAppVersion]
(
    @App_Platform           NVARCHAR(50),        -- 'Android' or 'iOS'
    @Client_Version         NVARCHAR(20),        -- Current version on mobile, e.g. 1.1.0 (1.0 from old builds)
    @Client_Device_Info     NVARCHAR(MAX) = NULL, -- Optional: device info for logging
    @Cby                    int,
    @Crole                  int
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NeedsUpdate        BIT = 0;
    DECLARE @IsForceUpdate      BIT = 0;
    DECLARE @Latest_Version     NVARCHAR(20) = NULL;
    DECLARE @MinRequired_Ver    NVARCHAR(20) = NULL;
    DECLARE @Update_Message     NVARCHAR(MAX) = NULL;
    DECLARE @Download_URL       NVARCHAR(500) = NULL;
    DECLARE @File_Size_MB       DECIMAL(10,2) = NULL;
    DECLARE @Release_Notes      NVARCHAR(MAX) = NULL;
    DECLARE @Status             NVARCHAR(100) = 'OK';
    DECLARE @ClientKey          BIGINT = NULL;
    DECLARE @MinKey             BIGINT = NULL;
    DECLARE @LatestKey          BIGINT = NULL;

    BEGIN TRY
        -- Validate input
        IF @App_Platform IS NULL OR @Client_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid platform or version' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        SELECT @ClientKey = VersionKey FROM dbo.fn_GpsVersionKey(@Client_Version);
        IF @ClientKey IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid version format' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        -- Latest version info for the platform: the active row with the highest
        -- Current_Version, compared as a version (not as text).
        SELECT TOP 1
            @Latest_Version = a.Latest_Version,
            @MinRequired_Ver = a.MinRequired_Version,
            @Update_Message = a.Update_Message,
            @Download_URL = a.Download_URL,
            @File_Size_MB = a.File_Size_MB,
            @Release_Notes = a.Release_Notes,
            @IsForceUpdate = a.IsForceUpdate
        FROM [UDD_MASTERDB].[dbo].[Mst_AppVersion] a
        CROSS APPLY dbo.fn_GpsVersionKey(a.Current_Version) k
        WHERE a.[App_Platform] = @App_Platform
          AND a.[IsActive] = 1
        ORDER BY k.VersionKey DESC, a.AppVersion_Id DESC;

        -- Check if version exists in the system
        IF @Latest_Version IS NULL
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'WARNING' AS Status,
                'Platform/Version not found in system' AS Message,
                @Latest_Version AS Latest_Version,
                @MinRequired_Ver AS MinRequired_Version,
                @Download_URL AS Download_URL,
                @File_Size_MB AS File_Size_MB,
                @Release_Notes AS Release_Notes;
            RETURN;
        END

        SELECT @LatestKey = VersionKey FROM dbo.fn_GpsVersionKey(@Latest_Version);
        SELECT @MinKey = VersionKey FROM dbo.fn_GpsVersionKey(@MinRequired_Ver);

        -- A non-version in Mst_AppVersion is a data mistake: say so rather than
        -- quietly reporting "up to date". A NULL minimum simply means "no minimum".
        IF @LatestKey IS NULL OR (@MinRequired_Ver IS NOT NULL AND @MinKey IS NULL)
        BEGIN
            SELECT
                CAST(0 AS BIT) AS NeedsUpdate,
                CAST(0 AS BIT) AS IsForceUpdate,
                'ERROR' AS Status,
                'Invalid version in Mst_AppVersion' AS Message,
                NULL AS Latest_Version,
                NULL AS MinRequired_Version,
                NULL AS Download_URL,
                NULL AS File_Size_MB,
                NULL AS Release_Notes;
            RETURN;
        END

        -- Determine if client version is below minimum required
        IF @ClientKey < @MinKey
        BEGIN
            SET @NeedsUpdate = 1;
            SET @IsForceUpdate = 1;
            SET @Status = 'FORCE_UPDATE_REQUIRED';
            SET @Update_Message = 'Your app version is no longer supported. Please update to continue.';
        END
        -- Determine if update is available (but not mandatory)
        ELSE IF @ClientKey < @LatestKey
        BEGIN
            SET @NeedsUpdate = 1;
            SET @Status = 'UPDATE_AVAILABLE';
            IF @Update_Message IS NULL
                SET @Update_Message = 'A new version is available. Please update to get the latest features.';
        END
        ELSE
        BEGIN
            SET @NeedsUpdate = 0;
            -- The row's IsForceUpdate says the update TO the latest version is mandatory.
            -- An app already on it has nothing to be forced to (the old procedure
            -- returned the flag regardless, telling current apps to update).
            SET @IsForceUpdate = 0;
            SET @Status = 'UP_TO_DATE';
            SET @Update_Message = 'Your app is up to date.';
        END

        -- Log the version check
        INSERT INTO [dbo].[BtoA_AppVersion_CheckLog]
        ([Platform], [Client_Version], [Latest_Version], [NeedsUpdate], [Status], [Device_Info], [Check_DateTime],[Cby],[Crole])
        VALUES
        (@App_Platform, @Client_Version, @Latest_Version, @NeedsUpdate, @Status, @Client_Device_Info, GETDATE(),@Cby,@Crole);

        -- Return result to client
        SELECT
            @NeedsUpdate AS NeedsUpdate,
            @IsForceUpdate AS IsForceUpdate,
            @Status AS Status,
            @Update_Message AS Message,
            @Latest_Version AS Latest_Version,
            @MinRequired_Ver AS MinRequired_Version,
            @Download_URL AS Download_URL,
            @File_Size_MB AS File_Size_MB,
            @Release_Notes AS Release_Notes;

    END TRY
    BEGIN CATCH
        SELECT
            CAST(0 AS BIT) AS NeedsUpdate,
            CAST(0 AS BIT) AS IsForceUpdate,
            'ERROR' AS Status,
            ERROR_MESSAGE() AS Message,
            NULL AS Latest_Version,
            NULL AS MinRequired_Version,
            NULL AS Download_URL,
            NULL AS File_Size_MB,
            NULL AS Release_Notes;
    END CATCH
END;
GO

-------------------------------------------------------------------------------
-- 4. CHECKS (read-only)
-------------------------------------------------------------------------------
-- 4.1 Expected: params 8, 3, 20, 14, 33, 5 (and 1 for the function); ReadsMasterDbProd 0
--     everywhere; ReadsUddMasterDb 1 except for the ack and the function.
SELECT o.name, o.type_desc, o.modify_date,
       params            = (SELECT COUNT(*) FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id > 0),
       ExpectedParams    = CASE o.name WHEN 'USP_S_GpsWardSync' THEN 8 WHEN 'USP_U_GpsWardSyncAck' THEN 3
                                       WHEN 'USP_IU_BtoA_SiteRoadDetails_Officer' THEN 20
                                       WHEN 'USP_IU_BtoA_StatusDetail_Officer' THEN 14
                                       WHEN 'USP_IU_BtoA_MainApp_Officer' THEN 33
                                       WHEN 'USP_CheckMobileAppVersion' THEN 5 WHEN 'fn_GpsVersionKey' THEN 1 END,
       ReadsMasterDbProd = CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE N'%masterDB[_]prod%' THEN 1 ELSE 0 END,
       ReadsUddMasterDb  = CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE N'%UDD[_]MASTERDB%' THEN 1 ELSE 0 END,
       RoadIdParam       = (SELECT TYPE_NAME(p.system_type_id) + '(' + CAST(p.max_length / 2 AS varchar(10)) + ')'
                            FROM sys.parameters p WHERE p.object_id = o.object_id AND p.name = '@BtoA_RoadId'),
       HasSourceParam    = CASE WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.name = '@Source') THEN 1 ELSE 0 END,
       HasUlbWardParams  = CASE WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.name = '@UlbCode')
                                 AND EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.name = '@WardNumber') THEN 1 ELSE 0 END
FROM sys.objects o
WHERE o.object_id IN (OBJECT_ID(N'dbo.USP_S_GpsWardSync'), OBJECT_ID(N'dbo.USP_U_GpsWardSyncAck'),
                      OBJECT_ID(N'dbo.USP_IU_BtoA_SiteRoadDetails_Officer'), OBJECT_ID(N'dbo.USP_IU_BtoA_StatusDetail_Officer'),
                      OBJECT_ID(N'dbo.USP_IU_BtoA_MainApp_Officer'), OBJECT_ID(N'dbo.USP_CheckMobileAppVersion'),
                      OBJECT_ID(N'dbo.fn_GpsVersionKey'))
ORDER BY o.name;

-- 4.2 What they read in other databases: only UDD_MASTERDB, every row Found = 1.
SELECT Procedure_ = OBJECT_NAME(d.referencing_id),
       Reads      = d.referenced_database_name + N'.' + ISNULL(d.referenced_schema_name, N'dbo') + N'.' + d.referenced_entity_name,
       Found      = CASE WHEN OBJECT_ID(QUOTENAME(d.referenced_database_name) + N'.' + QUOTENAME(ISNULL(d.referenced_schema_name, N'dbo'))
                                        + N'.' + QUOTENAME(d.referenced_entity_name)) IS NULL THEN 0 ELSE 1 END
FROM sys.sql_expression_dependencies d
WHERE d.referenced_database_name IS NOT NULL
  AND d.referencing_id IN (OBJECT_ID(N'dbo.USP_S_GpsWardSync'), OBJECT_ID(N'dbo.USP_U_GpsWardSyncAck'),
                           OBJECT_ID(N'dbo.USP_IU_BtoA_SiteRoadDetails_Officer'), OBJECT_ID(N'dbo.USP_IU_BtoA_StatusDetail_Officer'),
                           OBJECT_ID(N'dbo.USP_IU_BtoA_MainApp_Officer'), OBJECT_ID(N'dbo.USP_CheckMobileAppVersion'))
ORDER BY Procedure_, Reads;
GO

/*
-- 4.3 By hand (read-only): one ward's sync, as the API calls it. Take a ULB code and ward
--     number from db/40 grid 6.
EXEC dbo.USP_S_GpsWardSync @UlbCode = <ULB code>, @WardNumber = N'<ward number>', @LocalCount = 0,
     @KnownAppIds = NULL, @ReturnedAppIds = NULL, @BatchSize = 100, @IncludeClosed = 1;
*/

-------------------------------------------------------------------------------
-- 5. OPTIONAL - BtoA_SiteRoadDetails_Officer.Ofcr_RoadId as NVARCHAR(50). YOUR DECISION.
--
--    Why: road ids are text now. The procedure above takes them as text, but an integer
--    column can only hold the ones that are whole numbers; any other is refused with
--    Status 0 until this runs. Numeric ids ("999" included) work either way.
--    What it does: ALTER COLUMN to nvarchar(50), keeping NULL / NOT NULL, inside one
--    transaction, after dropping the column's auto-created statistics (SQL Server makes
--    them again when needed). It rewrites every row: on a large table expect log growth.
--    It refuses if the column is not an integer type, or if anything depends on it (5a).
--    Afterwards: anything that compares Ofcr_RoadId with a number (db/40 grid 5 - the
--    API's EarlierSurveyReader reads it too) fails once a text id is stored there.
-------------------------------------------------------------------------------
-- 5a. Read-only: the column now, and what would block the change (empty = nothing).
SELECT Ofcr_RoadId = TYPE_NAME(c.system_type_id)
                     + CASE WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar') THEN '(' + CAST(c.max_length / 2 AS varchar(10)) + ')'
                            WHEN TYPE_NAME(c.system_type_id) IN ('varchar', 'char') THEN '(' + CAST(c.max_length AS varchar(10)) + ')'
                            ELSE '' END
                     + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END,
       WouldChange  = CASE WHEN TYPE_NAME(c.system_type_id) IN ('int', 'bigint', 'smallint', 'tinyint') THEN 'yes - integer column'
                           ELSE 'no - not an integer column' END
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer') AND c.name = N'Ofcr_RoadId';

DECLARE @tab int = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer');
DECLARE @col int = COLUMNPROPERTY(@tab, N'Ofcr_RoadId', 'ColumnId');

SELECT Blocker = CONVERT(nvarchar(60), N'index') COLLATE DATABASE_DEFAULT, Name = i.name COLLATE DATABASE_DEFAULT
FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
WHERE ic.object_id = @tab AND ic.column_id = @col
UNION ALL
SELECT N'default constraint', d.name COLLATE DATABASE_DEFAULT
FROM sys.default_constraints d WHERE d.parent_object_id = @tab AND d.parent_column_id = @col
UNION ALL
SELECT N'check constraint', k.name COLLATE DATABASE_DEFAULT
FROM sys.check_constraints k WHERE k.parent_object_id = @tab AND (k.parent_column_id = @col OR k.[definition] LIKE N'%Ofcr[_]RoadId%')
UNION ALL
SELECT N'foreign key', OBJECT_NAME(f.constraint_object_id) COLLATE DATABASE_DEFAULT
FROM sys.foreign_key_columns f
WHERE (f.parent_object_id = @tab AND f.parent_column_id = @col) OR (f.referenced_object_id = @tab AND f.referenced_column_id = @col)
UNION ALL
SELECT N'computed column', cc.name COLLATE DATABASE_DEFAULT
FROM sys.computed_columns cc WHERE cc.object_id = @tab AND cc.[definition] LIKE N'%Ofcr[_]RoadId%'
UNION ALL
SELECT N'statistics created by hand', s.name COLLATE DATABASE_DEFAULT
FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
WHERE s.object_id = @tab AND sc.column_id = @col AND s.user_created = 1
UNION ALL
SELECT N'schema-bound module', (OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id)) COLLATE DATABASE_DEFAULT
FROM sys.sql_expression_dependencies d
WHERE d.referenced_id = @tab AND d.referenced_minor_id = @col AND d.is_schema_bound_reference = 1
UNION ALL
SELECT N'replicated column', c.name COLLATE DATABASE_DEFAULT
FROM sys.columns c WHERE c.object_id = @tab AND c.column_id = @col AND c.is_replicated = 1
UNION ALL
SELECT N'system-versioned table', t.name COLLATE DATABASE_DEFAULT
FROM sys.tables t WHERE t.object_id = @tab AND t.temporal_type <> 0;
GO

SET NOEXEC OFF;
GO

/*
-- 5b. THE CHANGE - select from here to the end of 5b and run it, only if you decide to.
USE UDD_KHATABTOA;
GO
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @type sysname, @nullable bit, @sql nvarchar(max) = N'';
DECLARE @tab int = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer');
DECLARE @col int = COLUMNPROPERTY(@tab, N'Ofcr_RoadId', 'ColumnId');

IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
    THROW 50001, 'Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 1;

SELECT @type = TYPE_NAME(c.system_type_id), @nullable = c.is_nullable
FROM sys.columns c WHERE c.object_id = @tab AND c.column_id = @col;

IF @type IS NULL
    THROW 50002, 'dbo.BtoA_SiteRoadDetails_Officer.Ofcr_RoadId was not found. Nothing changed.', 1;
IF @type NOT IN (N'int', N'bigint', N'smallint', N'tinyint')
    THROW 50003, 'Ofcr_RoadId is not an integer column (already text?). Nothing changed.', 1;

IF EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = @tab AND ic.column_id = @col)
   OR EXISTS (SELECT 1 FROM sys.default_constraints d WHERE d.parent_object_id = @tab AND d.parent_column_id = @col)
   OR EXISTS (SELECT 1 FROM sys.check_constraints k WHERE k.parent_object_id = @tab AND (k.parent_column_id = @col OR k.[definition] LIKE N'%Ofcr[_]RoadId%'))
   OR EXISTS (SELECT 1 FROM sys.foreign_key_columns f WHERE (f.parent_object_id = @tab AND f.parent_column_id = @col) OR (f.referenced_object_id = @tab AND f.referenced_column_id = @col))
   OR EXISTS (SELECT 1 FROM sys.computed_columns cc WHERE cc.object_id = @tab AND cc.[definition] LIKE N'%Ofcr[_]RoadId%')
   OR EXISTS (SELECT 1 FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
              WHERE s.object_id = @tab AND sc.column_id = @col AND s.user_created = 1)
   OR EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d WHERE d.referenced_id = @tab AND d.referenced_minor_id = @col AND d.is_schema_bound_reference = 1)
   OR EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = @tab AND c.column_id = @col AND c.is_replicated = 1)
   OR EXISTS (SELECT 1 FROM sys.tables t WHERE t.object_id = @tab AND t.temporal_type <> 0)
    THROW 50004, 'Something depends on Ofcr_RoadId (see 5a). Nothing changed.', 1;

-- Auto-created statistics would block the ALTER; SQL Server makes them again when needed.
SELECT @sql += N'DROP STATISTICS dbo.BtoA_SiteRoadDetails_Officer.' + QUOTENAME(s.name) + N'; '
FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
WHERE s.object_id = @tab AND sc.column_id = @col AND s.auto_created = 1;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

-- The type it had, for the undo (6b).
IF EXISTS (SELECT 1 FROM sys.extended_properties ep
           WHERE ep.class = 1 AND ep.major_id = @tab AND ep.minor_id = @col AND ep.name = N'PropertyGps_OriginalType')
    EXEC sys.sp_dropextendedproperty @name = N'PropertyGps_OriginalType',
         @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'BtoA_SiteRoadDetails_Officer',
         @level2type = N'COLUMN', @level2name = N'Ofcr_RoadId';
DECLARE @orig sql_variant = CONVERT(nvarchar(100), @type + CASE WHEN @nullable = 1 THEN N' NULL' ELSE N' NOT NULL' END);
EXEC sys.sp_addextendedproperty @name = N'PropertyGps_OriginalType', @value = @orig,
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'BtoA_SiteRoadDetails_Officer',
     @level2type = N'COLUMN', @level2name = N'Ofcr_RoadId';

SET @sql = N'ALTER TABLE dbo.BtoA_SiteRoadDetails_Officer ALTER COLUMN Ofcr_RoadId nvarchar(50) '
         + CASE WHEN @nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END + N';';
EXEC sys.sp_executesql @sql;

COMMIT;

SELECT Ofcr_RoadId = TYPE_NAME(c.system_type_id) + '(' + CAST(c.max_length / 2 AS varchar(10)) + ')'
FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer') AND c.name = N'Ofcr_RoadId';   -- nvarchar(50)
GO
-- end of 5b
*/

-------------------------------------------------------------------------------
-- 6. UNDO (commented out)
-------------------------------------------------------------------------------
/*
-- 6a. Every object db/42 changed, back as it was (from dbo.ZZ_PropertyGpsApi_ProcBackup):
--     dropped and re-created from its saved text with its saved SET options, then its
--     GRANTs run again. An object that did not exist before db/42 is dropped.
USE UDD_KHATABTOA;
GO
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @name sysname, @type char(2), @def nvarchar(max), @an bit, @qi bit, @grants nvarchar(max), @sql nvarchar(max);

IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
    THROW 50001, 'Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 1;
IF OBJECT_ID(N'dbo.ZZ_PropertyGpsApi_ProcBackup', N'U') IS NULL
    THROW 50002, 'There is no dbo.ZZ_PropertyGpsApi_ProcBackup - nothing to restore from.', 1;

DECLARE undo_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT [name], object_type, [definition], uses_ansi_nulls, uses_quoted_identifier, grants
    FROM dbo.ZZ_PropertyGpsApi_ProcBackup;
OPEN undo_cur;
FETCH NEXT FROM undo_cur INTO @name, @type, @def, @an, @qi, @grants;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + QUOTENAME(@name)) IS NOT NULL
    BEGIN
        SET @sql = CASE WHEN OBJECTPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME(@name)), 'IsProcedure') = 1
                        THEN N'DROP PROCEDURE dbo.' ELSE N'DROP FUNCTION dbo.' END + QUOTENAME(@name) + N';';
        EXEC sys.sp_executesql @sql;
    END

    IF @type IS NOT NULL
    BEGIN
        -- The saved text is a whole CREATE statement; it runs as a batch of its own under
        -- the SET options it was first created with.
        SET @sql = N'SET ANSI_NULLS ' + CASE WHEN @an = 1 THEN N'ON' ELSE N'OFF' END
                 + N'; SET QUOTED_IDENTIFIER ' + CASE WHEN @qi = 1 THEN N'ON' ELSE N'OFF' END
                 + N'; EXEC sys.sp_executesql @d;';
        EXEC sys.sp_executesql @sql, N'@d nvarchar(max)', @d = @def;
        IF @grants IS NOT NULL EXEC sys.sp_executesql @grants;
    END

    FETCH NEXT FROM undo_cur INTO @name, @type, @def, @an, @qi, @grants;
END
CLOSE undo_cur;
DEALLOCATE undo_cur;

COMMIT;

SELECT o.name, o.modify_date FROM sys.objects o
WHERE o.name IN (SELECT [name] FROM dbo.ZZ_PropertyGpsApi_ProcBackup)
ORDER BY o.name;
GO

-- Once the restore is checked:
-- DROP TABLE dbo.ZZ_PropertyGpsApi_ProcBackup;


-- 6b. Only if 5b ran: Ofcr_RoadId back to the integer type it had. Refuses while any stored
--     road id is not a number (those rows would be lost). Run 6a first or after - the
--     restored procedure works with either column type.
USE UDD_KHATABTOA;
GO
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @tab int = OBJECT_ID(N'dbo.BtoA_SiteRoadDetails_Officer');
DECLARE @col int = COLUMNPROPERTY(@tab, N'Ofcr_RoadId', 'ColumnId');
DECLARE @orig nvarchar(100), @sql nvarchar(max) = N'';

SELECT @orig = CONVERT(nvarchar(100), ep.value)
FROM sys.extended_properties ep
WHERE ep.class = 1 AND ep.major_id = @tab AND ep.minor_id = @col AND ep.name = N'PropertyGps_OriginalType';

IF @orig IS NULL
    THROW 50001, 'No record of the original type: 5b did not run here. Nothing changed.', 1;
IF @orig NOT IN (N'int NULL', N'int NOT NULL', N'bigint NULL', N'bigint NOT NULL',
                 N'smallint NULL', N'smallint NOT NULL', N'tinyint NULL', N'tinyint NOT NULL')
    THROW 50002, 'The recorded original type is not one 5b writes. Nothing changed.', 1;
IF EXISTS (SELECT 1 FROM dbo.BtoA_SiteRoadDetails_Officer WITH (UPDLOCK, HOLDLOCK)
           WHERE Ofcr_RoadId IS NOT NULL AND TRY_CONVERT(bigint, Ofcr_RoadId) IS NULL)
    THROW 50003, 'Some stored road ids are not numbers; going back would lose them. Nothing changed.', 1;

SELECT @sql += N'DROP STATISTICS dbo.BtoA_SiteRoadDetails_Officer.' + QUOTENAME(s.name) + N'; '
FROM sys.stats s JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
WHERE s.object_id = @tab AND sc.column_id = @col AND s.auto_created = 1;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'ALTER TABLE dbo.BtoA_SiteRoadDetails_Officer ALTER COLUMN Ofcr_RoadId ' + @orig + N';';
EXEC sys.sp_executesql @sql;

EXEC sys.sp_dropextendedproperty @name = N'PropertyGps_OriginalType',
     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'BtoA_SiteRoadDetails_Officer',
     @level2type = N'COLUMN', @level2name = N'Ofcr_RoadId';
COMMIT;
GO
*/
