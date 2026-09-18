/*
    USP_IU_BtoA_SiteRoadDetails_Officer - corrected for KhataBtoA_prod_v1.

    APPLIES TO KhataBtoA_prod_v1 ONLY. Do not run against KhataBtoA_prod: the live
    database still uses the pre-rename column names (BtoA_MainAppId, BtoA_RoadId,
    docTrn_KRS_Id, ...) and its own copy of this procedure is already correct.

    _v1 is a half-finished rename of the schema - BtoA_ prefixes moving to Ofcr_, and
    BtoA_DocumentTran.docTrn_KRS_Id renamed to docTrn_App_Id - and this procedure was
    never updated to match. Three consequences, all of which only surface at runtime
    because SQL Server defers name resolution:

      1. The UPDATE branch targeted [dbo].[Ofcr_SiteRoadDetails_Officer], which does not
         exist. Every RESUBMISSION of a road threw "Invalid object name" - that is the
         retry path, the post-QC re-verification path and the officer-corrects-a-mistake
         path.

      2. The UPDATE's SET list and WHERE clause named BtoA_Nearest_*,
         BtoA_ApplicationDisplayId, BtoA_SiteRoadRowID and BtoA_GpsRoadRowID. None of
         those columns exist here either. The WHERE now matches the INSERT guard exactly,
         so the two branches cannot disagree about which row they mean.

      3. The private-road path inserted docTrn_KRS_Id, the pre-rename name, so EVERY
         private road failed - even on a first submit.

    Two further corrections in the BtoA_DocumentTran writes, which are real defects
    rather than rename fallout:

      - The update matched only (app, Mdoc_id=4, UniqueIdentifier=444), with no road in
        the predicate, so a property with two private roads had them overwrite each
        other. DocumentId now carries the road.
      - docTrn_CBy and docTrn_CRole were hardcoded to 1, discarding which officer did
        the work. They now use @CBy and @CRole.

    SET XACT_ABORT ON is added because the API calls this inside a transaction it owns.

    The behaviour of the KSRSAC guard is deliberately unchanged: a road that does not
    match masterDB_prod.dbo.MstRoadKSRAC on both id and exact name still returns
    'Invalid KSRSAC Road Details' with Status = 0 rather than raising. The API reads
    that per-road Status and must not assume success.
*/

USE KhataBtoA_prod_v1;
GO

IF DB_NAME() <> 'KhataBtoA_prod_v1'
    THROW 50001, 'Refusing to run: this script is for KhataBtoA_prod_v1 only.', 1;
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



	 if exists (SELECT   R.Road_ID FROM BtoA_EPIDMetaData M INNER JOIN masterDB_prod.dbo.mst_AROMapping A
			ON A.GBAZoneID = M.MD_ZoneId
			AND A.BBMPWardId = M.MD_WardId
			AND A.BBMPStreetId = M.MD_StreetId
		INNER JOIN masterDB_prod.dbo.MstRoadKSRAC R
			ON R.BBMPZoneID = A.BBMPZoneID
			AND R.BBMPWardId = A.BBMPWardId
			AND R.Road_ID = @BtoA_RoadId and R.Road_Name=@BtoA_RoadName
		WHERE M.MD_APP_ID = @BtoA_MainAppId)

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

		if(@BtoA_RoadType='Private')
		begin
		-- DocumentId carries the road, so two private roads on one property no longer
		-- overwrite each other. CBy/CRole were hardcoded to 1, losing who did the work.
		insert into BtoA_DocumentTran(docTrn_App_Id,docTrn_Mdoc_id,docTrn_Active,docTrn_url,docTrn_CBy,docTrn_CDte,docTrn_CRole,DigitalSketchUpload_flag, UniqueIdentifier, DocumentId)
		values(@BtoA_MainAppId,4,1,@BtoA_Nearest_Private_Road_Document,@CBy,GETDATE(),@CRole,1,444,@BtoA_SiteRoadRowID)
		end
        SET @BtoA_RoadRowId = SCOPE_IDENTITY();
        SET @Message = 'Record inserted successfully';
        SET @ResultStatus = 1;
    END
    ELSE
    BEGIN
        -- UPDATE MODE
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
    SELECT
        @Message AS Message,
        @ResultStatus AS Status,
        @BtoA_RoadRowId AS RoadRowId,
		@BtoA_ApplicationDisplayId as DisplayRequestID;
	end
	else
	begin
	SELECT
        'Invalid KSRSAC Road Details' AS Message,
        cast(0 as bit) AS Status,
        0  AS RoadRowId,
		@BtoA_ApplicationDisplayId as DisplayRequestID;
	end
END



GO
