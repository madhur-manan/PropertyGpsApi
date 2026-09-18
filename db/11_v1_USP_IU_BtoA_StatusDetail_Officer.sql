/*
    USP_IU_BtoA_StatusDetail_Officer - corrected for KhataBtoA_prod_v1.

    APPLIES TO KhataBtoA_prod_v1 ONLY.

    The procedure chooses the new application status from @CRole, but only had branches
    for 117 and 118. The Single Plot GPS app signs in as a Revenue Inspector, role 116,
    so nothing matched, @Sts_MainAppStatus stayed NULL, and this ran anyway:

        update BtoAMainApp set App_Status=@Sts_MainAppStatus ... where App_Id=@Ofcr_App_Id

    App_Status is nullable, so that succeeded quietly and set it to NULL. The application
    then disappeared from everything that filters on App_Status - the RI's worklist
    (USP_S_GetAppDetails requires 10), the push-status acknowledgement, and the QC
    dashboards. The officer's survey would have looked submitted and been invisible.

    Two changes:

      1. A branch for role 116 setting status 13, which is "Data Received from RI" in
         masterDB_prod.dbo.Mst_AppStatus. (10 = sent to RI, 400 = returned from QC.)

      2. A guard that refuses to write a NULL status at all. This is the more important
         half: it converts every FUTURE unmapped role from silent corruption into a loud
         error, which the API reports as retryable so no field work is lost. If BBMP
         later adds roles, they fail visibly instead of destroying applications.

    Not changed: the procedure still deactivates prior rows and inserts a new
    BtoA_StatusDetail_Officer row on every call, and still updates BtoA_Status_Master_Tran.
    Those are the four destination tables and the behaviour is correct.

    SET XACT_ABORT ON is added because the API calls this inside a transaction it owns.
*/

USE KhataBtoA_prod_v1;
GO

IF DB_NAME() <> 'KhataBtoA_prod_v1'
    THROW 50001, 'Refusing to run: this script is for KhataBtoA_prod_v1 only.', 1;
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
