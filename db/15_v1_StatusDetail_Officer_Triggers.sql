/*
    Tr_BtoA_StatusDetail_Officer and Ofcr_StatusRowId_HistId - corrected for
    KhataBtoA_prod_v1.

    APPLIES TO KhataBtoA_prod_v1 ONLY.

    The same rename fallout as 14, on the status table. Both triggers copy the updated row
    into BtoA_StatusDetail_Officer_Hist, reading from "inserted" - which mirrors
    BtoA_StatusDetail_Officer, whose columns were renamed and, in three cases, removed:

        live                             this database
        Ofcr_StatusRowId                 Ofcr_RowId
        Ofcr_BtoA_MainAppId              Ofcr_App_Id
        Ofcr_BtoA_ApplicationDisplayId   Ofcr_ApplicationDisplayId
        Officer_Mobile_Number            Ofcr_Mobile_Number
        OfficerRoleId                    dropped - CRole now carries the role
        OfficerRoleValue                 dropped - no equivalent, written as NULL
        Gps_StatusRowId                  dropped - no equivalent, written as NULL

    So any UPDATE of BtoA_StatusDetail_Officer failed. That is not the first submit, which
    only inserts, but the SECOND one: USP_IU_BtoA_StatusDetail_Officer deactivates the
    previous row before inserting the new one, and that deactivation is an update. A
    resubmission therefore failed after the application and road rows had already been
    written - inside our transaction, so it rolled back cleanly, but it failed.

    The _Hist table still has all the original column names, so only the SELECT side is
    changed. Nothing is renamed or dropped here.

    Worth raising separately: BOTH of these triggers insert into the same history table on
    the same event, so every update writes two history rows. That is not something this
    rename caused - KhataBtoA_prod has both as well - but it looks unintended.
*/

USE KhataBtoA_prod_v1;
GO

IF DB_NAME() <> 'KhataBtoA_prod_v1'
    THROW 50001, 'Refusing to run: this script is for KhataBtoA_prod_v1 only.', 1;
GO

CREATE OR ALTER TRIGGER [dbo].[Tr_BtoA_StatusDetail_Officer]
ON [dbo].[BtoA_StatusDetail_Officer]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

INSERT INTO BtoA_StatusDetail_Officer_Hist
    (
       Ofcr_BtoA_MainAppId
      ,Ofcr_BtoA_ApplicationDisplayId
      ,Officer_Mobile_Number
      ,OfficerRoleId
      ,OfficerRoleValue
      ,Gps_StatusRowId
      ,Status_Date
      ,Status_Id
      ,Status_Remark
      ,Status_Rejected_Reason
      ,Status_Value
      ,CBy
      ,CDte
      ,CRole
      ,UBy
      ,UDte
      ,URole
      ,autoEscalatedDate
      ,isAutoEscalated,
	  status_Active,
	  Ofcr_StatusRowId
    )
    SELECT
       i.Ofcr_App_Id
      ,i.Ofcr_ApplicationDisplayId
      ,i.Ofcr_Mobile_Number
      ,i.CRole
      ,CAST(NULL AS nvarchar(100))
      ,CAST(NULL AS int)
      ,i.Status_Date
      ,i.Status_Id
      ,i.Status_Remark
      ,i.Status_Rejected_Reason
      ,i.Status_Value
      ,i.CBy
      ,i.CDte
      ,i.CRole
      ,i.UBy
      ,i.UDte
      ,i.URole
      ,i.autoEscalatedDate
      ,i.isAutoEscalated,
	  i.status_Active,
	  i.Ofcr_RowId
    FROM deleted i;
END
GO

CREATE OR ALTER TRIGGER [dbo].[Ofcr_StatusRowId_HistId]
ON [dbo].[BtoA_StatusDetail_Officer]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO BtoA_StatusDetail_Officer_Hist
    (
		Ofcr_StatusRowId,
       Ofcr_BtoA_MainAppId
      ,Ofcr_BtoA_ApplicationDisplayId
      ,Officer_Mobile_Number
      ,OfficerRoleId
      ,OfficerRoleValue
      ,Gps_StatusRowId
      ,Status_Date
      ,Status_Id
      ,Status_Remark
      ,Status_Rejected_Reason
      ,Status_Value
      ,CBy
      ,CDte
      ,CRole
      ,UBy
      ,UDte
      ,URole
      ,autoEscalatedDate
      ,isAutoEscalated,
	  status_Active
    )
    SELECT
	i.Ofcr_RowId,
       i.Ofcr_App_Id
      ,i.Ofcr_ApplicationDisplayId
      ,i.Ofcr_Mobile_Number
      ,i.CRole
      ,CAST(NULL AS nvarchar(100))
      ,CAST(NULL AS int)
      ,i.Status_Date
      ,i.Status_Id
      ,i.Status_Remark
      ,i.Status_Rejected_Reason
      ,i.Status_Value
      ,i.CBy
      ,i.CDte
      ,i.CRole
      ,i.UBy
      ,i.UDte
      ,i.URole
      ,i.autoEscalatedDate
      ,i.isAutoEscalated,
	  i.status_Active
    FROM deleted i;
END
GO
