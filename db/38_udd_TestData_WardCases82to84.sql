/*
    Test data for the TEST SERVER: 15 NEW applications, 5 in each of the Mahadevapura wards
    82 Garudacharpalya, 83 Kadugodi and 84 Hagadooru (GBA zone 102), for the test RIs db/37
    puts there. Hoodi (54) already has db/29's.

    APPLIES TO UDD_KHATABTOA ON THE TEST DB SERVER (DB-NEW-KHATA-TE, 10.40.119.74) ONLY.
    The guard checks the server as well as the database name and refuses everything else.

    Every case is the plain "Allot to Me" kind: status 10, active, fee paid, not allotted,
    residential, one public road. The two RIs of a ward share its five.

    EVERYTHING HERE IS INVENTED, as in db/29:
      - EPIDs 9990 + ward (3 digits) + case (3 digits): 9990082001..9990084005, each skipped
        if already present;
      - owners "TEST OWNER W82-1".. and mobiles 1000082001.. (no Indian mobile starts with 1,
        so the app's CALL button cannot ring anyone);
      - coordinates near the middle of each ward (the average of the laptop copy's
        applications there, 2026-10-16), a little apart per case;
      - MD_JSON built from scratch in the real key structure, with no ID numbers.
    The roads are real: the ward's own KSRSAC roads (masterDB_prod.dbo.MstRoadKSRAC, the list
    the app's street picker shows), the first five by Road_ID with plain names. So the
    declared road verifies on submit (Road_ID + Road_Name), and MD_StreetId / Site_StreetId
    carry the road's Road_KSRACId, as db/29's rows do.

    Submitting works on the test server for the reason db/29 gives (its
    trg_BtoAMainApp_UpdateNewKhata is disabled there).

    Every row carries the marker 'PROPERTY GPS TEST DATA (db/38)'. Re-running adds only the
    EPIDs not already present. Section 3 removes all of it.

    Sections: 1 checks (read-only), 2 apply + check, 3 undo (commented out).
*/

USE UDD_KHATABTOA;
GO

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_KHATABTOA'
   OR CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) NOT LIKE 'DB-NEW-KHATA-TE%'
BEGIN
    RAISERROR('Refusing to run: open UDD_KHATABTOA on the TEST DB server (DB-NEW-KHATA-TE) first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1. Checks (read-only)
--    Expect: three wards, each with its name and 5 roads; and no EPID already taken
--    (unless this script ran before - then those are skipped).
-------------------------------------------------------------------------------
WITH w AS (SELECT DISTINCT BBMPZoneID, BBMPWardId,
                  WardName = LTRIM(RTRIM(REPLACE(BBMPWardName, CHAR(9), N'')))
           FROM masterDB_prod.dbo.mst_AROMapping
           WHERE easthiULBNAME_CorpId = 556 AND GBAZoneID = 102 AND BBMPWardId IN (82, 83, 84)
             AND BBMPZoneID IS NOT NULL)
SELECT w.BBMPWardId, w.WardName,
       RoadsWithPlainNames = (SELECT COUNT(*) FROM masterDB_prod.dbo.MstRoadKSRAC R
                              WHERE R.BBMPZoneID = w.BBMPZoneID AND R.BBMPWardId = w.BBMPWardId
                                AND R.Road_ID > 0 AND ISNULL(R.Road_Active, 1) = 1
                                AND LTRIM(RTRIM(ISNULL(R.Road_Name, N''))) <> N''
                                AND R.Road_Name NOT LIKE N'%[^A-Za-z0-9 ]%'
                                AND TRY_CAST(R.Road_KSRACId AS int) IS NOT NULL)
FROM w
ORDER BY w.BBMPWardId;

SELECT AlreadyTaken = App_Id, App_MotherEPID, App_AdditionalInfo
FROM dbo.BtoAMainApp
WHERE App_MotherEPID LIKE N'999008[234]0[0-9][0-9]';
GO

-------------------------------------------------------------------------------
-- 2. APPLY
-------------------------------------------------------------------------------
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Marker nvarchar(60) = N'PROPERTY GPS TEST DATA (db/38)';
DECLARE @PerWard int = 5;
DECLARE @Now datetime = GETDATE();
DECLARE @Added int = 0;

-- The wards: names from the master, centres from the laptop copy's applications there.
DECLARE @Wards TABLE (WardId int PRIMARY KEY, BbmpZoneId int, WardName nvarchar(200), Lat float, Lng float);
INSERT INTO @Wards (WardId, Lat, Lng)
VALUES (82, 12.9897, 77.7075),
       (83, 12.9973, 77.7527),
       (84, 12.9653, 77.7499);

UPDATE wd SET BbmpZoneId = a.BBMPZoneID, WardName = a.WardName
FROM @Wards wd
CROSS APPLY (SELECT TOP (1) m.BBMPZoneID, WardName = LTRIM(RTRIM(REPLACE(m.BBMPWardName, CHAR(9), N'')))
             FROM masterDB_prod.dbo.mst_AROMapping m
             WHERE m.easthiULBNAME_CorpId = 556 AND m.GBAZoneID = 102 AND m.BBMPWardId = wd.WardId
               AND m.BBMPZoneID IS NOT NULL) a;

IF EXISTS (SELECT 1 FROM @Wards WHERE BbmpZoneId IS NULL OR ISNULL(WardName, N'') = N'')
    THROW 50001, 'A ward is missing from masterDB_prod.dbo.mst_AROMapping (corporation 556, zone 102). Nothing was added.', 1;

-- Each ward's first five KSRSAC roads with plain names (letters, digits and spaces).
DECLARE @Roads TABLE (WardId int, n int, RoadId int, StreetId int, Road nvarchar(400), PRIMARY KEY (WardId, n));
INSERT INTO @Roads (WardId, n, RoadId, StreetId, Road)
SELECT r.WardId, r.n, r.Road_ID, r.StreetId, r.Road_Name
FROM (SELECT wd.WardId, R.Road_ID, StreetId = TRY_CAST(R.Road_KSRACId AS int), R.Road_Name,
             n = ROW_NUMBER() OVER (PARTITION BY wd.WardId ORDER BY R.Road_ID)
      FROM @Wards wd
      JOIN masterDB_prod.dbo.MstRoadKSRAC R ON R.BBMPZoneID = wd.BbmpZoneId AND R.BBMPWardId = wd.WardId
      WHERE R.Road_ID > 0 AND ISNULL(R.Road_Active, 1) = 1
        AND LTRIM(RTRIM(ISNULL(R.Road_Name, N''))) <> N''
        AND R.Road_Name NOT LIKE N'%[^A-Za-z0-9 ]%'
        AND TRY_CAST(R.Road_KSRACId AS int) IS NOT NULL) r
WHERE r.n <= @PerWard;

IF EXISTS (SELECT 1 FROM @Wards wd WHERE (SELECT COUNT(*) FROM @Roads r WHERE r.WardId = wd.WardId) < @PerWard)
    THROW 50002, 'A ward has fewer than 5 usable KSRSAC roads - see section 1. Nothing was added.', 1;

BEGIN TRAN;

DECLARE @w int = (SELECT MIN(WardId) FROM @Wards);
WHILE @w IS NOT NULL
BEGIN
    DECLARE @i int = 1;
    WHILE @i <= @PerWard
    BEGIN
        DECLARE @W3   nvarchar(3)  = RIGHT(N'00' + CAST(@w AS nvarchar(3)), 3);
        DECLARE @C3   nvarchar(3)  = RIGHT(N'00' + CAST(@i AS nvarchar(3)), 3);
        DECLARE @Epid nvarchar(30) = N'9990' + @W3 + @C3;

        IF NOT EXISTS (SELECT 1 FROM dbo.BtoAMainApp WHERE App_MotherEPID = @Epid)
           AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData WHERE MD_MotherEPID = @Epid)
        BEGIN
            DECLARE @Street int = NULL, @RoadId int = NULL, @Road nvarchar(400) = NULL;
            SELECT @Street = StreetId, @RoadId = RoadId, @Road = Road
            FROM @Roads WHERE WardId = @w AND n = @i;

            DECLARE @WardName nvarchar(200), @Lat float, @Lng float;
            SELECT @WardName = WardName,
                   @Lat = Lat + (@i - 3) * 0.0006,     -- invented, around the ward's middle
                   @Lng = Lng + (@i - 3) * 0.0004
            FROM @Wards WHERE WardId = @w;

            DECLARE @Area decimal(18,2) = 92.90 + @i * 7.43;  -- sq m
            DECLARE @EW int = 30 + (@i % 3) * 10, @NS int = 40 + (@i % 2) * 10;
            DECLARE @Category nvarchar(40) = CASE WHEN @i % 3 = 0 THEN N'Vacant Site' ELSE N'Plot with Building' END;
            DECLARE @Owner nvarchar(40) = N'TEST OWNER W' + CAST(@w AS nvarchar(3)) + N'-' + CAST(@i AS nvarchar(3));
            DECLARE @Mobile nvarchar(15) = N'1000' + @W3 + @C3;
            DECLARE @AppId int;

            INSERT INTO dbo.BtoAMainApp
                (App_MotherEPID, App_Type, App_Status, App_Active, App_Remarks, App_AdditionalInfo,
                 App_Cby, App_Cdte, App_CRole, App_UBy, App_UDte, App_URole, App_IpAddress,
                 App_MotherSASID, isProcessingFeePaid, ProcessingFee, GBAId, GBA, App_Source)
            VALUES
                (@Epid, N'', 10, 1, N'', @Marker,
                 0, @Now, 1, 0, @Now, 1, N'db38-test',
                 @Epid, 1, 500.00, 556, N'Bengaluru East City Corporation', N'');

            SET @AppId = SCOPE_IDENTITY();

            UPDATE dbo.BtoAMainApp
               SET App_DisplayId = CONVERT(nvarchar(8), @Now, 112) + RIGHT(N'0000000' + CAST(@AppId AS nvarchar(10)), 7)
             WHERE App_Id = @AppId;

            INSERT INTO dbo.BtoA_EPIDMetaData
                (MD_MotherEPID, MD_KhataType, MD_ZoneId, MD_Zonename, MD_WardId, MD_WardName,
                 MD_StreetId, MD_StreetName, MD_JSON, MD_Latitude, MD_Longitude, MD_Status, MD_Active,
                 MD_Remarks, MD_AdditionalInfo, MD_Cby, MD_Cdte, MD_CRole, MD_IpAddress, MD_APP_ID, MD_SiteArea)
            VALUES
                (@Epid, N'B-Khata', 102, N'Mahadevapura', @w, @WardName,
                 @Street, @Road,
                 CONCAT(
                   N'{"PropertyID":"', @Epid, N'","PropertyCategory":"', @Category, N'",',
                   N'"PropertyClassification":"TEST DATA","WardNumber":"', CAST(@w AS nvarchar(3)), N'","WardName":"', @WardName, N'",',
                   N'"ZoneID":"102","ZoneName":"Mahadevapura",',
                   N'"LATITUDE":"', CONVERT(nvarchar(30), CAST(@Lat AS decimal(10,6))), N'",',
                   N'"LONGITUDE":"', CONVERT(nvarchar(30), CAST(@Lng AS decimal(10,6))), N'",',
                   N'"StreetName":"', @Road, N'","Streetcode":"', RIGHT(N'00' + CAST(@Street AS nvarchar(10)), 3), N'",',
                   N'"SASApplicationNumber":"', @Epid, N'","IsMuation":"N","KaveriRegistrationNumber":[],',
                   N'"AssessmentNumber":"TEST/W', CAST(@w AS nvarchar(3)), N'/', CAST(@i AS nvarchar(3)), N'","courtStay":"","enquiryDispute":"",',
                   N'"CheckBandi":{"North":"Test site ', @C3, N'A","South":"Road","East":"Test site ', @C3, N'B","West":"Vacant land"},',
                   N'"SiteDetails":{"SiteArea":', CONVERT(nvarchar(20), @Area), N',',
                   N'"Dimensions":{"EastWest":"', CAST(@EW AS nvarchar(5)), N'","NorthSouth":"', CAST(@NS AS nvarchar(5)), N'"},',
                   N'"isCornerPlot":false,"roadFacingSides":1},',
                   N'"OwnerDetails":[{"IdType":"TEST","IdNumber":"","OwnerName":"', @Owner, N'",',
                   N'"OwnerAddress":"Test address ', @C3, N', ', @WardName, N'","IdentifierName":"","RelationShipType":"",',
                   N'"Gender":"","MobileNumber":"', @Mobile, N'","ISCOMPANY":"N","COMPANYNAME":null}]}'),
                 @Lat, @Lng, 0, 1,
                 N'', @Marker, 0, @Now, 1, N'db38-test', @AppId, @Area);

            INSERT INTO dbo.[BtoA_SiteDtls]
                (Site_App_Id, Site_EPID_Id, Site_ZoneId, Site_WardId, Site_StreetId,
                 Site_Roadtype, Site_RoadId, Site_RoadName, Site_IsSameLocationAsKhata,
                 Site_Latitude, Site_Longitude, Site_active, Site_CBy, Site_CDte, Site_CRole,
                 PrivateRoadId, PrivateRoadText, PrivateRoadName, propertyUseType,
                 comercialExtentinSqft, residentailsExtentinSqft, App_IsPropertySurvey,
                 App_IsPropertySurveyLocated, publicRoadname, Site_AdditionalInfo, DcConversionType,
                 Site_isCornorPlot, Site_numberOfRoadFacingSides)
            VALUES
                (@AppId, @Epid, 102, @w, @Street,
                 N'public', @RoadId, @Road, 1,
                 @Lat, @Lng, 1, 0, @Now, 1,
                 N'0', N'', N'', 2,
                 0, 0, 0,
                 0, @Road, @Marker, N'',
                 0, 1);

            INSERT INTO dbo.BtoA_SiteRoadDetails
                (Rd_App_Id, Rd_isPresentInPublicRoadList, Rd_Roadtype, Rd_RoadId, Rd_RoadName, Rd_RoadActive,
                 Rd_AdditionalInfo, Rd_PrivateRoadId, Rd_PrivateRoadText, Rd_PrivateRoadName)
            VALUES
                (@AppId, 1, N'public', @RoadId, @Road, 1,
                 NULL, N'0', N'', N'');

            INSERT INTO dbo.BtoA_OwnerDetails
                (Own_App_Id, Own_OwnerName, Own_Mobile, Own_Type_FromDeedOrManuallyAdded,
                 Own_AdditionalInfo, own_active, Own_CBy, Own_CDte, Own_CRole, Own_Order)
            VALUES
                (@AppId, @Owner, @Mobile, N'TEST',
                 @Marker, 1, 0, @Now, 1, 1);

            SET @Added += 1;
        END

        SET @i += 1;
    END

    SET @w = (SELECT MIN(WardId) FROM @Wards WHERE WardId > @w);
END

COMMIT TRAN;

SELECT AddedNow = @Added;
GO

-- CHECK - what was added. Expect 15 rows, 5 per ward, every count 1 and RoadVerifies 'yes'.
SELECT ap.App_Id, ap.App_DisplayId, ap.App_MotherEPID, ap.App_Status, ap.App_Active, ap.isProcessingFeePaid,
       md.MD_ZoneId, md.MD_WardId, md.MD_WardName, rd.Rd_RoadId, rd.Rd_RoadName,
       SiteRows  = (SELECT COUNT(*) FROM dbo.[BtoA_SiteDtls] s WHERE s.Site_App_Id = ap.App_Id AND s.Site_active = 1),
       RoadRows  = (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails r WHERE r.Rd_App_Id = ap.App_Id),
       OwnerRows = (SELECT COUNT(*) FROM dbo.BtoA_OwnerDetails o WHERE o.Own_App_Id = ap.App_Id),
       RoadVerifies = CASE WHEN EXISTS (
                          SELECT 1 FROM masterDB_prod.dbo.MstRoadKSRAC R
                          JOIN masterDB_prod.dbo.mst_AROMapping a
                            ON a.BBMPZoneID = R.BBMPZoneID AND a.BBMPWardId = R.BBMPWardId
                          WHERE a.GBAZoneID = md.MD_ZoneId AND a.BBMPWardId = md.MD_WardId
                            AND R.Road_ID = rd.Rd_RoadId AND R.Road_Name = rd.Rd_RoadName)
                      THEN 'yes' ELSE 'NO' END
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
LEFT JOIN dbo.BtoA_SiteRoadDetails rd ON rd.Rd_App_Id = ap.App_Id
WHERE ap.App_AdditionalInfo = N'PROPERTY GPS TEST DATA (db/38)'
ORDER BY md.MD_WardId, ap.App_MotherEPID;
GO

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - removes every row this script added, any surveys of them, and their history
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DECLARE @Ids TABLE (App_Id int PRIMARY KEY);
    INSERT INTO @Ids
    SELECT App_Id FROM dbo.BtoAMainApp
    WHERE App_AdditionalInfo = N'PROPERTY GPS TEST DATA (db/38)' AND App_MotherEPID LIKE N'999008[234]%';

    DELETE FROM dbo.BtoA_SiteRoadDetails_Officer WHERE Ofcr_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_StatusDetail_Officer    WHERE Ofcr_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_MainApp_Officer         WHERE Ofcr_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_DocumentTran            WHERE docTrn_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_OwnerDetails    WHERE Own_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_SiteRoadDetails WHERE Rd_App_Id  IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.[BtoA_SiteDtls]      WHERE Site_App_Id IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_EPIDMetaData    WHERE MD_APP_ID  IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoA_Architect_AssignedApp WHERE AppId IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoAMainApp_Hist     WHERE App_Id     IN (SELECT App_Id FROM @Ids);
    DELETE FROM dbo.BtoAMainApp          WHERE App_Id     IN (SELECT App_Id FROM @Ids);
    SELECT RemovedApplications = (SELECT COUNT(*) FROM @Ids);
COMMIT TRAN;
*/
