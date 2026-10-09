/*
    Test server only: put back the three survey-save procedures the API was built and
    tested against (the laptop copy, UDD_KHATABTOA_TEST, which they match exactly).

    APPLIES TO THE TEST SERVER'S UDD_KHATABTOA ONLY (DB server 10.40.119.74). There is no USE
    line: open UDD_KHATABTOA first. The guard below refuses every other database.

    WHY. On 2026-09-28 all three were replaced on the test server (2026-10-09 check):
      - USP_IU_BtoA_MainApp_Officer: parameters renamed (@ApplicationDisplayId, ...), so every
        API submit failed with "expects parameter @Ofcr_ApplicationDisplayId"; its insert guard
        compared the parameter with itself, so a new survey was never inserted; and it stored
        road width in feet as metres x 10.7639 (the square-metre factor).
      - USP_IU_BtoA_SiteRoadDetails_Officer: body commented out; always answers
        "Invalid KSRSAC Road Details", Status 0. No road can be saved.
      - USP_IU_BtoA_StatusDetail_Officer: never inserts the status row and never sets
        App_Status. No survey can reach 13.
    The three OfcrJC_Existing_* columns added to BtoA_MainApp_Officer the same day are left
    alone: these procedures do not touch them.

    UNDO. D:PropertyGpsTestServerUSP_TestServer_Before_2026-10-09.sql restores the three
    exactly as they were on the server before this script.

    Sections: 1 the three procedures, 2 checks (read-only).
*/

-- SET NOEXEC ON stops every later batch in SSMS too; a bare THROW would not.
SELECT RunningIn = DB_NAME(), OnServer = CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'));
IF DB_NAME() <> 'UDD_KHATABTOA'
BEGIN
    RAISERROR('Refusing to run: open the test server''s UDD_KHATABTOA first.', 16, 1);
    SET NOEXEC ON;
END
GO

-------------------------------------------------------------------------------
-- 1.1 USP_IU_BtoA_MainApp_Officer
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
	select @Ofcr_CorrectedPropertyUseTypeId=isnull(BBMPLandUseTypeId,0),@Ofcr_CorrectedPropertyUseTypeValue=BBMPLAndUseTypeNAme from masterDB_prod.dbo.mst_KsrsacMasterPlan2015 where GpsLandUseTypeValue=@Ofcr_CorrectedPropertyUseTypeValue
		
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
-- 1.2 USP_IU_BtoA_SiteRoadDetails_Officer
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
    @BtoA_RoadId INT = NULL,
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



	 -- The KSRSAC lookup is unchanged, but it no longer decides whether the road is
	 -- stored - only whether it is reported as verified. See the header note.
	 DECLARE @KsracMatched BIT = 0;

	 if exists (SELECT   R.Road_ID FROM BtoA_EPIDMetaData M INNER JOIN masterDB_prod.dbo.mst_AROMapping A
			ON A.GBAZoneID = M.MD_ZoneId
			AND A.BBMPWardId = M.MD_WardId
			AND A.BBMPStreetId = M.MD_StreetId
		INNER JOIN masterDB_prod.dbo.MstRoadKSRAC R
			ON R.BBMPZoneID = A.BBMPZoneID
			AND R.BBMPWardId = A.BBMPWardId
			AND R.Road_ID = @BtoA_RoadId and R.Road_Name=@BtoA_RoadName
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
-- 1.3 USP_IU_BtoA_StatusDetail_Officer
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
	@autoEscalatedDate datetime=null

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

 if(@CRole=117 )
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
 -- 116 is the Revenue Inspector, the role this GPS app signs in as. Until now no branch
 -- matched it, so @Sts_MainAppStatus stayed NULL and the unconditional update below set
 -- BtoAMainApp.App_Status to NULL - silently, because the column is nullable. The
 -- application then vanished from every query that filters on App_Status.
 -- 13 is "Data Received from RI" in masterDB_prod.dbo.Mst_AppStatus.
 else if(@CRole=116)
 begin
	select @Sts_MainAppStatus=13
	select @Sts_CurrStatusId=13
 end

    DECLARE @Message NVARCHAR(200),
            @ResultStatus BIT, @DisplayRequestID nvarchar(15);
	 select @Ofcr_App_Id=App_Id From BtoAMainApp where App_DisplayId=@Ofcr_ApplicationDisplayId





	/*********************Master Status Logging start********************/
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

SET NOEXEC OFF;
GO

-------------------------------------------------------------------------------
-- 2. Checks (read-only). Expected: 33, 20 and 13 parameters, and every Fixed = 1.
-------------------------------------------------------------------------------
SELECT name = o.name,
       modified = o.modify_date,
       params = (SELECT COUNT(*) FROM sys.parameters p WHERE p.object_id = o.object_id),
       Fixed = CASE o.name
                 WHEN 'USP_IU_BtoA_MainApp_Officer'
                   THEN CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE '%where Ofcr_ApplicationDisplayId= @Ofcr_ApplicationDisplayId%' THEN 1 ELSE 0 END
                 WHEN 'USP_IU_BtoA_SiteRoadDetails_Officer'
                   THEN CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE '%INSERT%BtoA_SiteRoadDetails_Officer%' THEN 1 ELSE 0 END
                 WHEN 'USP_IU_BtoA_StatusDetail_Officer'
                   THEN CASE WHEN OBJECT_DEFINITION(o.object_id) LIKE '%@CRole=116%' THEN 1 ELSE 0 END
               END
FROM sys.objects o
WHERE o.name IN ('USP_IU_BtoA_MainApp_Officer', 'USP_IU_BtoA_SiteRoadDetails_Officer', 'USP_IU_BtoA_StatusDetail_Officer')
ORDER BY o.name;
GO
