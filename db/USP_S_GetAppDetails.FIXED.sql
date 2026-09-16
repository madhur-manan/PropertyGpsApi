/*
    USP_S_GetAppDetails - corrected for the mobile fetch flow.

    Review before running. Three changes, all in the parameter handling and the WHERE
    clause; the SELECT list is untouched.

    1. The ward scope was commented out:

           --and md.MD_ZoneId=@zoneId and md.MD_WardId=@wardId and ap.IsPushedToGps=0

       With that line inactive the procedure ignored @zoneId and @wardId entirely and
       returned every status-10, fee-paid application on the server. It is now active, so
       the procedure returns one ward's undelivered records - which is what the mobile
       fetch needs, and what makes IsPushedToGps meaningful.

    2. @AppID was being used as "TOP (@AppID)", i.e. a row limit, not an application id.
       Passing 0 therefore returned no rows at all, which reads exactly like "this ward is
       empty". It is now resolved through @RowLimit, which falls back to 1000 when the
       caller passes null or a non-positive value. The name is left alone so existing
       callers are unaffected.

    3. @Epid is now an optional single-record filter. It was accepted and ignored. Null,
       an empty string, or the literal 'null' (which the example call in the original
       header passes) all leave it inert, so default behaviour does not change.

    @Level remains unused, as in the original.

    The original definition is kept alongside this file as
    USP_S_GetAppDetails.ORIGINAL.sql.
*/

CREATE OR ALTER PROCEDURE [dbo].[USP_S_GetAppDetails]
    @AppID  int,
    @Epid   nvarchar(20),
    @zoneId int,
    @wardId int,
    @Level  int
AS
BEGIN
    SET NOCOUNT ON;

    -- @AppID is the row cap, despite the name. Guard it so a 0 or a null does not silently
    -- return an empty ward.
    DECLARE @RowLimit int = CASE WHEN ISNULL(@AppID, 0) <= 0 THEN 1000 ELSE @AppID END;

    -- Treat null, blank and the literal 'null' as "no EPID filter".
    DECLARE @EpidFilter nvarchar(20) = NULLIF(LTRIM(RTRIM(ISNULL(@Epid, ''))), '');
    IF @EpidFilter = 'null' SET @EpidFilter = NULL;

    SELECT DISTINCT TOP (@RowLimit)
        ISNULL([App_Id], 0) AS AppID
      ,[App_MotherEPID] MotherEPID
      ,[App_Type] Type
      ,[App_Status] Status
      ,[App_Remarks] Remarks
      ,[App_AdditionalInfo] AdditionalInfo
      ,[App_DisplayId] AppDisplayId
      ,CASE
          WHEN [App_Source] IS NULL OR [App_Source] = 'A' OR LEN([App_Source]) = 0 THEN 'BtoAKhata'
          WHEN [App_Source] = 'newkhata' THEN 'SinglePlotApproval'
       END AS ApplicationType
      ,[App_MotherSASID] MotherSASID
      ,ISNULL([isProcessingFeePaid], 0) isProcessingFeePaid
      ,[ProcessingFee] ProcessingFee
      ,[Site_Id]
      ,[Site_App_Id] ApplicationId
      ,[Site_EPID_Id] EpID
      ,aro.BBMPWardId WardId
      ,aro.GBAZoneID ZoneId
      ,aro.GBAZoneName_En
      ,[Site_StreetId] StreetId
      ,[Site_Roadtype] RoadType
      ,[Site_RoadId] RoadId
      ,[Site_RoadName] Roadname
      ,[Site_IsSameLocationAsKhata] IsSameLocationAsKhata
      ,[Site_Latitude] Latitude
      ,[Site_Longitude] Longitude
      ,[Site_Order] SiteOrder
      ,sd.[App_IsPropertySurvey]
      ,sd.[App_IsPropertySurveyLocated]
      ,md.MD_JSON EpidJSON
      ,md.MD_KhataType Khatatype
      ,md.MD_Latitude KhataLatitude
      ,md.MD_Longitude KhataLongitude
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
      ,sd.Site_isCornorPlot AS isCornorPlot
      ,sd.Site_numberOfRoadFacingSides
      ,CASE
          WHEN sd.Site_isCornorPlot = 1 THEN srd.Rd_RoadRow_ID
          ELSE sd.Site_Id
       END AS Rd_RoadRow_ID

    FROM [BtoA_SiteDtls ] sd WITH (NOLOCK)
    LEFT JOIN BtoAMainApp ap WITH (NOLOCK)
           ON ap.App_Id = sd.Site_App_Id
    LEFT JOIN BtoA_EPIDMetaData md WITH (NOLOCK)
           ON ap.App_MotherEPID = md.MD_MotherEPID
          AND md.MD_APP_ID = ap.App_Id
    JOIN masterDB_prod.dbo.mst_AROMapping aro
           ON aro.GBAZoneID = md.MD_ZoneId
          AND aro.BBMPWardId = md.MD_WardId
    LEFT JOIN BtoA_SiteRoadDetails srd
           ON srd.Rd_App_Id = ap.App_Id          -- Added for RoadRowID 24102025

    WHERE sd.Site_active = 1
      AND ap.App_Active = 1
      AND ap.App_Status = 10
      AND isProcessingFeePaid = 1
      -- Ward scope and undelivered-only. Previously commented out, which made @zoneId and
      -- @wardId dead parameters and returned the whole server.
      AND md.MD_ZoneId = @zoneId
      AND md.MD_WardId = @wardId
      AND ap.IsPushedToGps = 0
      -- Optional single-record filter; inert unless a real EPID is supplied.
      AND (@EpidFilter IS NULL OR ap.App_MotherEPID = @EpidFilter)

    ORDER BY ap.App_Cdte;
END
