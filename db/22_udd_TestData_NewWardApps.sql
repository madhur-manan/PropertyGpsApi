/*
    Test data for ward sync: 20 NEW applications in zone 102 / ward 54 (Hoodi), so a phone
    that already holds the ward can fetch "only what is new".

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    EVERYTHING HERE IS INVENTED. No row copies a real application or a real person:
      - EPIDs 9990000001..9990000020 (checked: none exist in BtoAMainApp, BtoA_EPIDMetaData or
        BtoA_SiteDtls);
      - owners "TEST OWNER 01".. and mobiles 1000000001.. (not a valid Indian mobile series, so
        the app's CALL button cannot ring a real person);
      - MD_JSON built from scratch in the real key structure, with no ID numbers.
    The roads are real master data (MstRoadKSRAC), chosen so the KSRSAC check accepts them:
      street 88 / road 976 'Bile Shivale', street 92 / road 978 'Byrthi',
      street 786 / road 627 'Varanasi gramatana'.

    Each application gets the rows the fetch and the app need: BtoAMainApp (status 10, fee
    paid, active), BtoA_EPIDMetaData (ward 102/54), an active BtoA_SiteDtls row, one public
    road in BtoA_SiteRoadDetails, and one owner in BtoA_OwnerDetails. Two of the twenty are
    Mixed use (type 3) with commercial and residential extents; every fourth is a
    SinglePlotApproval ('newkhata'). App_Cdte is now, so they are the newest in the ward.

    No INSERT triggers exist on these tables. App_DisplayId (yyyyMMdd + App_Id, 7 digits, the
    pattern every real row follows) needs the new App_Id, so it is set by a follow-up UPDATE of
    that column alone: trg_BtoAMainApp writes a history row, and trg_BtoAMainApp_UpdateNewKhata
    returns early because no status/fee/active column changes.

    KNOWN LIMIT: submitting a survey for these (or any) applications fails on this database,
    because the submit changes App_Status and trg_BtoAMainApp_UpdateNewKhata then writes to
    ekhata_test, which does not exist here.

    Every row carries the marker 'PROPERTY GPS TEST DATA (db/22)'. Re-running adds only the
    EPIDs not already present. Section 3 removes all of it.

    Sections: 1 apply, 2 check, 3 undo (commented out).
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO

-------------------------------------------------------------------------------
-- 1. APPLY
-------------------------------------------------------------------------------
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Marker nvarchar(60) = N'PROPERTY GPS TEST DATA (db/22)';
DECLARE @HowMany int = 20;
DECLARE @Now datetime = GETDATE();
DECLARE @i int = 1, @Added int = 0;

BEGIN TRAN;

WHILE @i <= @HowMany
BEGIN
    DECLARE @Epid nvarchar(30) = N'99900000' + RIGHT(N'0' + CAST(@i AS nvarchar(2)), 2);

    IF NOT EXISTS (SELECT 1 FROM dbo.BtoAMainApp WHERE App_MotherEPID = @Epid)
       AND NOT EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData WHERE MD_MotherEPID = @Epid)
    BEGIN
        -- Rotate through three streets, each with a road the KSRSAC check accepts.
        DECLARE @Street int, @RoadId int, @Road nvarchar(100);
        SELECT @Street = s.Street, @RoadId = s.RoadId, @Road = s.Road
        FROM (VALUES (0, 88, 976, N'Bile Shivale'),
                     (1, 92, 978, N'Byrthi'),
                     (2, 786, 627, N'Varanasi gramatana')) s (k, Street, RoadId, Road)
        WHERE s.k = @i % 3;

        DECLARE @Seq nvarchar(2) = RIGHT(N'0' + CAST(@i AS nvarchar(2)), 2);
        DECLARE @Lat float = 12.9905 + @i * 0.0006;      -- around Hoodi, invented
        DECLARE @Lng float = 77.7135 + @i * 0.0004;
        DECLARE @Area decimal(18,2) = 92.90 + @i * 7.43;  -- sq m
        DECLARE @EW int = 30 + (@i % 3) * 10, @NS int = 40 + (@i % 2) * 10;
        DECLARE @Mixed bit = CASE WHEN @i % 10 = 0 THEN 1 ELSE 0 END;
        DECLARE @Source nvarchar(50) = CASE WHEN @i % 4 = 0 THEN N'newkhata' ELSE N'' END;
        DECLARE @Category nvarchar(40) = CASE WHEN @i % 3 = 0 THEN N'Vacant Site' ELSE N'Plot with Building' END;
        DECLARE @AppId int;

        INSERT INTO dbo.BtoAMainApp
            (App_MotherEPID, App_Type, App_Status, App_Active, App_Remarks, App_AdditionalInfo,
             App_Cby, App_Cdte, App_CRole, App_UBy, App_UDte, App_URole, App_IpAddress,
             App_MotherSASID, isProcessingFeePaid, ProcessingFee, GBAId, GBA, App_Source)
        VALUES
            (@Epid, N'', 10, 1, N'', @Marker,
             0, @Now, 1, 0, @Now, 1, N'db22-test',
             N'99900000' + @Seq, 1, 500.00, 556, N'Bengaluru East City Corporation', @Source);

        SET @AppId = SCOPE_IDENTITY();

        UPDATE dbo.BtoAMainApp
           SET App_DisplayId = CONVERT(nvarchar(8), @Now, 112) + RIGHT(N'0000000' + CAST(@AppId AS nvarchar(10)), 7)
         WHERE App_Id = @AppId;

        INSERT INTO dbo.BtoA_EPIDMetaData
            (MD_MotherEPID, MD_KhataType, MD_ZoneId, MD_Zonename, MD_WardId, MD_WardName,
             MD_StreetId, MD_StreetName, MD_JSON, MD_Latitude, MD_Longitude, MD_Status, MD_Active,
             MD_Remarks, MD_AdditionalInfo, MD_Cby, MD_Cdte, MD_CRole, MD_IpAddress, MD_APP_ID, MD_SiteArea)
        VALUES
            (@Epid, N'B-Khata', 102, N'Mahadevapura', 54, N'Hoodi',
             @Street, @Road,
             CONCAT(
               N'{"PropertyID":"', @Epid, N'","PropertyCategory":"', @Category, N'",',
               N'"PropertyClassification":"TEST DATA","WardNumber":"54","WardName":"Hoodi",',
               N'"ZoneID":"102","ZoneName":"Mahadevapura",',
               N'"LATITUDE":"', CONVERT(nvarchar(30), CAST(@Lat AS decimal(10,6))), N'",',
               N'"LONGITUDE":"', CONVERT(nvarchar(30), CAST(@Lng AS decimal(10,6))), N'",',
               N'"StreetName":"', @Road, N'","Streetcode":"', RIGHT(N'00' + CAST(@Street AS nvarchar(5)), 3), N'",',
               N'"SASApplicationNumber":"99900000', @Seq, N'","IsMuation":"N","KaveriRegistrationNumber":[],',
               N'"AssessmentNumber":"TEST/', @Seq, N'","courtStay":"","enquiryDispute":"",',
               N'"CheckBandi":{"North":"Test site ', @Seq, N'A","South":"Road","East":"Test site ', @Seq, N'B","West":"Vacant land"},',
               N'"SiteDetails":{"SiteArea":', CONVERT(nvarchar(20), @Area), N',',
               N'"Dimensions":{"EastWest":"', CAST(@EW AS nvarchar(5)), N'","NorthSouth":"', CAST(@NS AS nvarchar(5)), N'"},',
               N'"isCornerPlot":false,"roadFacingSides":1},',
               N'"OwnerDetails":[{"IdType":"TEST","IdNumber":"","OwnerName":"TEST OWNER ', @Seq, N'",',
               N'"OwnerAddress":"Test address ', @Seq, N', Hoodi","IdentifierName":"","RelationShipType":"",',
               N'"Gender":"","MobileNumber":"10000000', @Seq, N'","ISCOMPANY":"N","COMPANYNAME":null}]}'),
             @Lat, @Lng, 0, 1,
             N'', @Marker, 0, @Now, 1, N'db22-test', @AppId, @Area);

        INSERT INTO dbo.[BtoA_SiteDtls]
            (Site_App_Id, Site_EPID_Id, Site_ZoneId, Site_WardId, Site_StreetId,
             Site_Roadtype, Site_RoadId, Site_RoadName, Site_IsSameLocationAsKhata,
             Site_Latitude, Site_Longitude, Site_active, Site_CBy, Site_CDte, Site_CRole,
             PrivateRoadId, PrivateRoadText, PrivateRoadName, propertyUseType,
             comercialExtentinSqft, residentailsExtentinSqft, App_IsPropertySurvey,
             App_IsPropertySurveyLocated, publicRoadname, Site_AdditionalInfo, DcConversionType,
             Site_isCornorPlot, Site_numberOfRoadFacingSides)
        VALUES
            (@AppId, @Epid, 102, 54, @Street,
             N'public', @RoadId, @Road, 1,
             @Lat, @Lng, 1, 0, @Now, 1,
             N'0', N'', N'', CASE WHEN @Mixed = 1 THEN 3 ELSE 2 END,
             CASE WHEN @Mixed = 1 THEN 600.00 ELSE 0 END, CASE WHEN @Mixed = 1 THEN 600.00 ELSE 0 END, 0,
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
            (@AppId, N'TEST OWNER ' + @Seq, N'10000000' + @Seq, N'TEST',
             @Marker, 1, 0, @Now, 1, 1);

        SET @Added += 1;
    END

    SET @i += 1;
END

COMMIT TRAN;

SELECT AddedNow = @Added;
GO

-------------------------------------------------------------------------------
-- 2. CHECK - what was added, and what a fully synced phone will now be sent
-------------------------------------------------------------------------------
SELECT ap.App_Id, ap.App_DisplayId, ap.App_MotherEPID, ap.App_Status, ap.App_Source,
       md.MD_ZoneId, md.MD_WardId, md.MD_StreetId,
       SiteRows  = (SELECT COUNT(*) FROM dbo.[BtoA_SiteDtls] s WHERE s.Site_App_Id = ap.App_Id AND s.Site_active = 1),
       RoadRows  = (SELECT COUNT(*) FROM dbo.BtoA_SiteRoadDetails r WHERE r.Rd_App_Id = ap.App_Id),
       OwnerRows = (SELECT COUNT(*) FROM dbo.BtoA_OwnerDetails o WHERE o.Own_App_Id = ap.App_Id)
FROM dbo.BtoAMainApp ap
JOIN dbo.BtoA_EPIDMetaData md ON md.MD_APP_ID = ap.App_Id
WHERE ap.App_AdditionalInfo = N'PROPERTY GPS TEST DATA (db/22)'
ORDER BY ap.App_Id;
GO

-------------------------------------------------------------------------------
-- 3. UNDO - removes every row this script added (and the history rows they caused)
-------------------------------------------------------------------------------
/*
SET XACT_ABORT ON;
BEGIN TRAN;
    DECLARE @Ids TABLE (App_Id int PRIMARY KEY);
    INSERT INTO @Ids
    SELECT App_Id FROM dbo.BtoAMainApp
    WHERE App_AdditionalInfo = N'PROPERTY GPS TEST DATA (db/22)' AND App_MotherEPID LIKE N'99900000%';

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
