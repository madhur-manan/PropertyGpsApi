CREATE  proc [dbo].[USP_S_GetAppDetails]
 @AppID int,
 @Epid nvarchar(20),
 @zoneId int,
 @wardId int,
 @Level int
 as
 begin

/*SELECT  distinct
[App_Id] AppID
      ,[App_MotherEPID] MotherEPID
      ,[App_Type] Type
      ,[App_Status] Status      
      ,[App_Remarks] Remarks
      ,[App_AdditionalInfo]     AdditionalInfo   
      ,[App_DisplayId] AppDisplayId
      ,[App_MotherSASID] MotherSASID
      , isnull([isProcessingFeePaid],0) isProcessingFeePaid
      ,[ProcessingFee] ProcessingFee,
[Site_Id]
      ,[Site_App_Id] ApplicationId
      ,[Site_EPID_Id] EpID
      ,[Site_ZoneId] ZoneId
      ,[Site_WardId] WardId
      ,[Site_StreetId] StreetId
      ,[Site_Roadtype] RoadType
      ,[Site_RoadId] RoadId
      ,[Site_RoadName] Roadname
      ,[Site_IsSameLocationAsKhata] IsSameLocationAsKhata
      ,[Site_Latitude] Latitude
      ,[Site_Longitude] Longitude        
      ,[Site_Order] SiteOrder,
	  md.MD_JSON EpidJSON,
	  md.MD_KhataType Khatatype,
	  md.MD_Latitude KhataLatitude,
	  md.MD_Longitude KhataLongitude
  FROM [BtoA_SiteDtls ] sd with (nolock)
  join BtoAMainApp ap with (nolock) on ap.App_Id=sd.Site_App_Id
  join (select distinct MD_MotherEPID, MD_ZoneId,
MD_WardId,MD_Latitude
MD_Longitude,MD_KhataType,MD_JSON,MD_Latitude from BtoA_EPIDMetaData  with (nolock))md on ap.App_MotherEPID=md.MD_MotherEPID
  where sd.Site_active=1 and ap.App_Active=1 and md.MD_ZoneId=@zoneId and md.MD_WardId=@wardId
 */

 SELECT distinct TOP (@AppID) 
ISNULL([App_Id], 0) AS AppID
      ,[App_MotherEPID] MotherEPID
      ,[App_Type] Type
      ,[App_Status] Status      
      ,[App_Remarks] Remarks
      ,[App_AdditionalInfo]     AdditionalInfo   
      ,[App_DisplayId] AppDisplayId
	  ,CASE 
    WHEN [App_Source] IS NULL or [App_Source] = 'A' or LEN([App_Source]) = 0 THEN 'BtoAKhata'  --SinglePlotApproval
    WHEN [App_Source] = 'newkhata' THEN 'SinglePlotApproval'  --BtoAKhata
	END AS ApplicationType
	,[App_MotherSASID] MotherSASID
      , isnull([isProcessingFeePaid],0) isProcessingFeePaid
      ,[ProcessingFee] ProcessingFee,
[Site_Id]
      ,[Site_App_Id] ApplicationId
      ,[Site_EPID_Id] EpID
      --,[Site_ZoneId] ZoneId
      --,[Site_WardId] WardId
	  
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
      ,[Site_Order] SiteOrder,
	  sd.[App_IsPropertySurvey],
	  sd.[App_IsPropertySurveyLocated],
	  md.MD_JSON EpidJSON,
	  md.MD_KhataType Khatatype,
	  md.MD_Latitude KhataLatitude,
	  md.MD_Longitude KhataLongitude,
	  ap.App_Cdte,
	  sd.DcConversionType,
	  sd.PrivateRoadId,
	  sd.PrivateRoadText,
	  sd.PrivateRoadName,
	  sd.Site_AdditionalInfo,
	  sd.publicRoadname,
	  sd.propertyUseType,
	  sd.comercialExtentinSqft,
	  sd.residentailsExtentinSqft,
	  sd.Site_isCornorPlot as isCornorPlot,
	  sd.Site_numberOfRoadFacingSides,
	CASE 
    WHEN sd.Site_isCornorPlot = 1 THEN srd.Rd_RoadRow_ID
    ELSE sd.Site_Id
	END AS Rd_RoadRow_ID

  FROM [BtoA_SiteDtls ] sd with (nolock)
  left join BtoAMainApp ap with (nolock) on ap.App_Id=sd.Site_App_Id
  left join BtoA_EPIDMetaData md with(nolock) on ap.App_MotherEPID=md.MD_MotherEPID and md.MD_APP_ID = ap.App_Id 
  join masterDB_prod.dbo.mst_AROMapping aro on aro.GBAZoneID=md.MD_ZoneId and aro.BBMPWardId=md.MD_WardId
  left join BtoA_SiteRoadDetails srd on srd.Rd_App_Id=ap.App_Id   --Added for RoadRowID 24102025
--  join (select distinct MD_MotherEPID, MD_ZoneId,
--MD_WardId,MD_Latitude
--MD_Longitude,MD_KhataType,MD_JSON,MD_Latitude from BtoA_EPIDMetaData  with (nolock))md on ap.App_MotherEPID=md.MD_MotherEPID
  where sd.Site_active=1 and ap.App_Active=1 and ap.App_Status=10  and isProcessingFeePaid =1 
  --and md.MD_ZoneId=@zoneId and md.MD_WardId=@wardId and ap.IsPushedToGps=0  
  --and App_MotherEPID='7223957990'
  order by ap.App_Cdte



  end


