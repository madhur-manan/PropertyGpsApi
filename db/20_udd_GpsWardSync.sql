/*
    Ward sync: the new Fetch / Update, by App_Id, in batches of 100 applications.

    APPLIES TO UDD_KHATABTOA_TEST (and, when asked, the new server's UDD_KHATABTOA).
    There is no USE line: open the target database first. The guard below refuses anything
    else - in particular KhataBtoA_prod, the live database, which needs a DBA.

    WHY. USP_S_GetAppDetails hands an application to the first device that fetches it
    (IsPushedToGps = 0) and never to anyone again. The new flow brings a phone up to date
    with EVERY workable application in its ward that it does not hold yet:

      - the phone sends its count and the App_Ids it holds;
      - USP_S_GpsWardSync returns the next 100 it lacks, with the same columns as
        USP_S_GetAppDetails so the API maps them the same way;
      - once the phone has saved a batch, USP_U_GpsWardSyncAck records it.

    IsPushedToGps / PushedToGpsDate ARE A RECORD ONLY. They are written by the ack and are
    never read to decide what a phone is sent. An application another phone (or the old
    app) already took is still offered to a phone that does not have it.

    WORKABLE means: active, processing fee paid, an active site row, in the requested
    ward, and App_Status 10 (waiting for the RI) or 400 (returned to the RI by QC). Status
    13 ("Data Received from RI") and everything after it is never sent.

    ONE APPLICATION PER EPID. The phone still keys its records by EPID, and five EPIDs
    carry two live applications each. Offering both would leave the phone unable to hold
    one of them and the sync could never finish, so the newest App_Id per EPID is offered
    - the one the phone ended up holding under the old fetch anyway. Drop the ROW_NUMBER
    when the phone's key becomes App_Id.

    BATCHES COUNT APPLICATIONS, NOT ROWS. A corner plot returns one row per road; the batch
    is chosen as App_Ids first and the road rows follow, so an application is never split
    between two batches.

    Sections: 1 USP_S_GpsWardSync, 2 USP_U_GpsWardSyncAck, 3 checks (read-only).

    FOR THE DBA: both procedures are needed on live before the app's WARD_SYNC build is
    used there. BtoA_SiteRoadDetails is a heap with no index; an index on (Rd_App_Id)
    would help this and USP_S_GetAppDetails alike.
*/

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
IF DB_NAME() NOT IN ('UDD_KHATABTOA_TEST', 'UDD_KHATABTOA')
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA_TEST (or the new server''s UDD_KHATABTOA) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1. USP_S_GpsWardSync - what this phone is missing, 100 applications at a time
--
--    Result sets, always all three, in this order:
--      1. ServerCount, LocalCount, MissingCount
--      2. the next batch, columns as USP_S_GetAppDetails (several rows per corner plot)
--      3. AppId, AppStatus for held App_Ids that are no longer workable here
--         (empty unless @IncludeClosed = 1; AppStatus NULL = no longer active)
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_S_GpsWardSync]
    @ZoneId          int,
    @WardId          int,
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
          AND md.MD_ZoneId = @ZoneId
          AND md.MD_WardId = @WardId
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
        ,aro.BBMPWardId        AS WardId
        ,aro.GBAZoneID         AS ZoneId
        ,aro.GBAZoneName_En
        ,sd.[Site_StreetId]    AS StreetId
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
    -- One row per ward, not one per street: joined whole, mst_AROMapping repeats each
    -- application up to 964 times for DISTINCT to throw away.
    LEFT JOIN (SELECT DISTINCT GBAZoneID, BBMPWardId, GBAZoneName_En
               FROM masterDB_prod.dbo.mst_AROMapping WITH (NOLOCK)) aro
      ON aro.GBAZoneID = md.MD_ZoneId AND aro.BBMPWardId = md.MD_WardId
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
-- 2. USP_U_GpsWardSyncAck - the phone has saved these applications
--
--    Sets IsPushedToGps = 1 and PushedToGpsDate only where it is NULL; an existing date
--    is kept. Scoped to the ward the phone asked about, so a phone cannot mark another
--    ward's applications. Status is not checked: this records what the phone holds.
--
--    Rows already at (1, date) are skipped only so trg_BtoAMainApp does not copy an
--    unchanged row into BtoAMainApp_Hist on every sync. It is not a fetch condition.
-------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE [dbo].[USP_U_GpsWardSyncAck]
    @ZoneId  int,
    @WardId  int,
    @AppIds  nvarchar(max)   -- comma-separated App_Ids, at most one batch
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

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
                        AND md.MD_ZoneId = @ZoneId
                        AND md.MD_WardId = @WardId)
          AND (ap.IsPushedToGps = 0 OR ap.PushedToGpsDate IS NULL);

    COMMIT TRAN;

    SELECT Requested = (SELECT COUNT(*) FROM #ack),
           Updated   = (SELECT COUNT(*) FROM #done);
END
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 3. Checks (read-only). Ward 102/54 is the test officer's ward.
-------------------------------------------------------------------------------
/*
EXEC dbo.USP_S_GpsWardSync @ZoneId = 102, @WardId = 54, @LocalCount = 0,
     @KnownAppIds = NULL, @ReturnedAppIds = NULL, @BatchSize = 100, @IncludeClosed = 1;
*/
