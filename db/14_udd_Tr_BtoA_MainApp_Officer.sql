/*
    Tr_BtoA_MainApp_Officer - corrected for UDD_KHATABTOA_TEST.

    APPLIES TO UDD_KHATABTOA_TEST ONLY.

    The fourth casualty of the half-finished rename, and the least visible: this AFTER
    UPDATE trigger copies the previous row into BtoA_MainApp_Officer_Hist, reading it from
    the "deleted" pseudo-table. "deleted" mirrors BtoA_MainApp_Officer, whose columns were
    renamed - but the trigger still reads the old names:

        d.ApplicationDisplayId   d.Ofcr_BtoA_MainAppId   d.SsaId
        d.PropertyId             d.ApplicationDate

    The effect is that ANY update to BtoA_MainApp_Officer in this database fails with
    "Invalid column name". That includes the update branch of
    USP_IU_BtoA_MainApp_Officer, so a first submit works (it inserts) and every
    resubmission of the same property fails - which is exactly the retry and post-QC
    re-verification path.

    Only the five SELECT references are changed. The INSERT column list is left alone: the
    _Hist table genuinely still uses the old names, and renaming its columns would be a
    much larger change with other readers.

    Also noted, not changed: this database has no Tr_BtoA_SiteRoadDetails_Officer, though
    KhataBtoA_prod does. Road-level history is therefore not being captured here. That is
    a gap in the copy rather than something this work introduced, and it needs a decision
    rather than a guess.
*/

USE UDD_KHATABTOA_TEST;
GO

IF DB_NAME() <> 'UDD_KHATABTOA_TEST'
    THROW 50001, 'Refusing to run: this script is for UDD_KHATABTOA_TEST only.', 1;
GO



CREATE OR ALTER TRIGGER [dbo].[Tr_BtoA_MainApp_Officer]
ON [dbo].[BtoA_MainApp_Officer]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
 --select * from BtoA_MainApp_Officer_Hist
 --sp_help BtoA_MainApp_Officer_Hist
 --ALTER TABLE BtoA_MainApp_Officer_Hist ADD Ofcr_Corrected_IndustrialArea float
--update BtoA_MainApp_Officer set Ofcr_Aditional='Tr Test' where ApplicationDisplayId='202511030004033'
    INSERT INTO BtoA_MainApp_Officer_Hist
    (
       Ofcr_RowId
      ,ApplicationDisplayId
      ,Ofcr_BtoA_MainAppId
      ,SsaId
      ,PropertyId
      ,ApplicationDate
      ,Ofcr_PropertyLandExistOnSpot
      ,Ofcr_IsAllBhoomiSurveyNosCorrect
      ,Ofcr_IsGovtProperty
      ,Ofcr_CorrectedGpsLocation
      ,Ofcr_CorrectedLatitude
      ,Ofcr_CorrectedLongitude
      ,Ofcr_CorrectedPropertyUseType
      ,Ofcr_CorrectedPropertyUseTypeId
      ,Ofcr_CorrectedPropertyUseTypeValue
      ,Ofcr_CorrectedCommercialArea
      ,Ofcr_CorrectedResidentialArea
      ,Ofcr_Corrected_IndustrialArea
      ,Ofcr_CorrectedRoadDetails
      ,Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing
      ,Ofcr_RoadFacingSides
      ,Ofcr_RoadWidthInFrontOfPropertyInSqft
      ,Ofcr_RoadWidthInFrontOfPropertyInSqmt
      ,Ofcr_nearest_public_Road_Latitude
      ,Ofcr_nearest_public_Road_Longitude
      ,Ofcr_gpsStatusFullWorkflowJSON
      ,Ofcr_Aditional
      ,Ofcr_JsonRQ
      ,Ofcr_JsonRS
      ,Ofcr_IsMasterPlanProcessed
      ,Ofcr_IsMasterPlanVerified
      ,CBy
      ,CDte
      ,CRole
      ,UBy
      ,UDte
      ,URole
      ,ResidentialPlotRMPWidnedAreainSqmt
      ,ResidentialPlotareaAfterRoadWideninginSqmt
      ,isPlotinBufferZone
      ,BufferZoneArea
      ,CommercialPlotRMPWidnedAreainSqmt
      ,CommercialPlotareaAfterRoadWideninginSqmt
      ,IndustrialPlotRMPWidnedAreainSqmt
      ,IndustrialPlotareaAfterRoadWideninginSqmt
      ,matchedSurveyNo
      ,surveyRemark
      ,IsBuildingExists
    )
    SELECT
       d.Ofcr_RowId
      ,d.Ofcr_ApplicationDisplayId
      ,d.Ofcr_App_Id
      ,d.Ofcr_SsaId
      ,d.Ofcr_MotherEPID
      ,d.Ofcr_ApplicationDate
      ,d.Ofcr_PropertyLandExistOnSpot
      ,d.Ofcr_IsAllBhoomiSurveyNosCorrect
      ,d.Ofcr_IsGovtProperty
      ,d.Ofcr_CorrectedGpsLocation
      ,d.Ofcr_CorrectedLatitude
      ,d.Ofcr_CorrectedLongitude
      ,d.Ofcr_CorrectedPropertyUseType
      ,d.Ofcr_CorrectedPropertyUseTypeId
      ,d.Ofcr_CorrectedPropertyUseTypeValue
      ,d.Ofcr_CorrectedCommercialArea
      ,d.Ofcr_CorrectedResidentialArea
      ,d.Ofcr_Corrected_IndustrialArea
      ,d.Ofcr_CorrectedRoadDetails
      ,d.Ofcr_IsCornerPlotOrMoreThanTwoSidesRoadFacing
      ,d.Ofcr_RoadFacingSides
      ,d.Ofcr_RoadWidthInFrontOfPropertyInSqft
      ,d.Ofcr_RoadWidthInFrontOfPropertyInSqmt
      ,d.Ofcr_nearest_public_Road_Latitude
      ,d.Ofcr_nearest_public_Road_Longitude
      ,d.Ofcr_gpsStatusFullWorkflowJSON
      ,d.Ofcr_Aditional
      ,d.Ofcr_JsonRQ
      ,d.Ofcr_JsonRS
      ,d.Ofcr_IsMasterPlanProcessed
      ,d.Ofcr_IsMasterPlanVerified
      ,d.CBy
      ,d.CDte
      ,d.CRole
      ,d.UBy
      ,d.UDte
      ,d.URole
      ,d.ResidentialPlotRMPWidnedAreainSqmt
      ,d.ResidentialPlotareaAfterRoadWideninginSqmt
      ,d.isPlotinBufferZone
      ,d.BufferZoneArea
      ,d.CommercialPlotRMPWidnedAreainSqmt
      ,d.CommercialPlotareaAfterRoadWideninginSqmt
      ,d.IndustrialPlotRMPWidnedAreainSqmt
      ,d.IndustrialPlotareaAfterRoadWideninginSqmt
      ,d.matchedSurveyNo
      ,d.surveyRemark
      ,d.IsBuildingExists

    FROM DELETED d


END;


GO
