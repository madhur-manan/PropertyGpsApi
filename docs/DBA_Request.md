# Property GPS API — requests for the DBA

**From:** Madhur Manan · **Date:** 19 September 2026
**Server:** `172.31.0.113\MSSQLSERVER19` · **Databases:** `KhataBtoA_prod`, `masterDB_prod`

The mobile API for Single Plot GPS is built and working end to end against the test
database `UDD_KHATABTOA_TEST`. Everything below is what is needed to run the same flow
against **live**. Each item was verified on the server on 19 September; none of it is
theoretical.

Scripts referenced sit in the project repository under `db/`. They are written for the
test database and carry a `DB_NAME()` guard, so they need re-targeting before running on
live — I am happy to prepare live versions for your review rather than have you rewrite
them.

---

## A. Blocks the mobile submit on live

**A1. `USP_IU_BtoA_StatusDetail_Officer` has no branch for role 116.**

The procedure picks the new status from `@CRole`, with branches for 117 and 118 only. The
GPS app signs in as a Revenue Inspector, **role 116**, so nothing matches,
`@Sts_MainAppStatus` stays `NULL`, and this then runs unconditionally:

```sql
update BtoAMainApp set App_Status = @Sts_MainAppStatus ... where App_Id = @Ofcr_App_Id
```

`App_Status` is nullable, so it succeeds quietly and sets the status to `NULL`. The
application then disappears from everything that filters on it — the RI worklist
(`USP_S_GetAppDetails` requires 10), push-status, and the QC dashboards.

Requested: a branch for role 116 setting status **13** ("Data Received from RI" in
`masterDB_prod.dbo.Mst_AppStatus`), plus a guard that raises rather than writing a NULL
status, so any future unmapped role fails loudly instead of destroying an application.
Reference: `db/11_udd_USP_IU_BtoA_StatusDetail_Officer.sql`.

---

## B. Defects already on live, independent of this project

**B1. `USP_S_GetAppDetails` ignores zone and ward.** This line is commented out in the
live definition:

```sql
--and md.MD_ZoneId=@zoneId and md.MD_WardId=@wardId and ap.IsPushedToGps=0
```

With it inactive the procedure returns every status-10, fee-paid application on the
server, so an officer can retrieve applications outside their own ward. A corrected
version is in `db/USP_S_GetAppDetails.FIXED.sql`, with the original alongside for
comparison. It also guards `@AppID`, which the procedure uses as `TOP (n)` rather than as
an identifier — passing 0 currently returns nothing and reads exactly like an empty ward.

**B2. `USP_IU_BtoA_SiteRoadDetails_Officer` returns the wrong row id for a private road.**
`SCOPE_IDENTITY()` is read *after* the `BtoA_DocumentTran` insert, so when
`@BtoA_RoadType = 'Private'` the procedure hands back the **document's** identity as
`RoadRowId`. Any existing caller using that value is pointed at the wrong row. Moving
`SET @BtoA_RoadRowId = SCOPE_IDENTITY()` to immediately after the road insert fixes it.

**B3. `USP_S_Officer_ValidateOTP` never marks a code consumed.** It validates the OTP but
never updates `OTP_Tran`, so a valid code stays usable for its whole 100-minute window. If
consumption is handled somewhere I have not found, please correct me; otherwise the row
should be marked inactive on first successful use.

**B4. `USP_S_BtoA_GpsDashData_v1`, `@p_Level = 3` can never return a row.** Its filter
reads `where SD.Status_Id not in (11,200) and SD.Status_Id in (11,200)`, which is
unsatisfiable. Whatever screen relies on that branch has been showing an empty list. Not
blocking the mobile app — history is served by a direct read instead — but worth knowing.

---

## C. Schema additions needed on live

Six columns exist in the test database and not in live. All additive and nullable; no
existing row, index or query plan changes. Script: `db/12_udd_SubmitSchema_Additions.sql`.

| Table | Column | Holds |
|---|---|---|
| `BtoA_SiteRoadDetails_Officer` | `Ofcr_Notice_Document nvarchar(max)` | Served-notice photograph, per road |
| `BtoA_MainApp_Officer` | `Ofcr_Property_Document nvarchar(max)` | Property photograph |
| `BtoA_MainApp_Officer` | `Ofcr_Map_Document nvarchar(max)` | Map screenshot |
| `BtoA_MainApp_Officer` | `Ofcr_NoteSheet_Document nvarchar(max)` | Khata note sheet — image or PDF |
| `BtoA_MainApp_Officer` | `Ofcr_GovtPropertyDetails nvarchar(1000)` | Officer's written justification for asserting government land |
| `BtoA_MainApp_Officer` | `Ofcr_IsGovtProperty_Value int` | 0 no · 1 yes · 2 refer to surveyor · NULL not answered |

The last is needed because `Ofcr_IsGovtProperty` is `bit NOT NULL` while the app collects
**three** answers plus "not answered". The existing bit is left untouched for current
readers. This mirrors the pattern already in `BtoA_RejectedDetails_Officer`
(`RI_IsGovtProperty int NULL` + `RI_IsGovtProperty_comments varchar(255)`).

Two small UPDATE-only procedures write these columns:
`USP_U_BtoA_GpsSubmitExtras_App` and `USP_U_BtoA_GpsRoadNoticeDocument`
(`db/13_udd_GpsSubmitExtras.sql`). They are deliberately separate from the three push
procedures, which the JC portal and QC workflow also call — I did not want to alter
someone else's write path. If you would rather own them, please take them.

---

## D. Questions

**D1. A procedure to return QC-returned records to an officer.** Nothing today does this.
Returns are recorded as a status row with `Status_Id = 12 / RETURN_TO_RI` — 3,851 live
rows — but those applications carry `IsPushedToGps = 1`, so the ward fetch will never
re-offer them. `Mst_AppStatus` defines 400, "Returned to RI From QC", but nothing sets it.
The mobile app therefore cannot show an officer work that has come back to them. **This is
the one genuinely new procedure I would ask for.**

**D2. Eleven officer answers have no column anywhere.** I currently write them as JSON
into `Ofcr_Aditional` — recoverable, but not queryable or reportable:
`isLandUntraceable`, `isLandLocationUpdated`, `isGpsLocationUpdated`, `khataComments`,
`singleSiteId`, `applicationType`, `siteArea`, `eastWest`, `northSouth`,
`isDeclaredRoadFacingSidesCorrect`. Should any have real columns? `siteArea`, `eastWest`
and `northSouth` especially — they are measurements on a tax assessment, and someone will
eventually want to query them.

**D3. `Ofcr_IsAllBhoomiSurveyNosCorrect` is `bit NOT NULL`, but the app never asks it.**
The Bhoomi land details arrive from the server and are never shown to the officer. I will
not default it: writing 0 would record a confirmation nobody gave, about a survey number.
Either the question is added to the app or the column becomes nullable. That is a product
decision as much as a schema one.

**D4. A least-privilege login for the API.** It currently connects as `sa`, which is
server-wide sysadmin. It needs `EXECUTE` on the specific procedures and `SELECT` on
`mst_AROMapping` only — I can supply the exact object list.

Separately, and not about this project: that same `sa` credential appears in clear text in
another application's configuration on this estate, including in published output folders.
Worth rotating regardless of anything here.

**D5. 39 modules reference `masterDB_prod` as a three-part name** — 34 in
`KhataBtoA_prod`, 5 within `masterDB_prod` itself. They work on live but fail on any
restored or renamed copy, which is why no isolated staging environment can be stood up
today. This matters more now that databases are being renamed to `UDD_*`: if
`masterDB_prod` is renamed at a later stage, everything referencing it breaks at once.
Synonyms or two-part names would remove the dependency.

---

## E. The new test server (`10.40.119.74`), found 5 October 2026

**E1. RI submits fail there: the e-Khata database names do not exist.** Every update that
sets `App_Status`, `App_Active` or `isProcessingFeePaid` on `BtoAMainApp` fires
`trg_BtoAMainApp_UpdateNewKhata`, which updates `ekhata_test.dbo.KRS`. There is no
`ekhata_test` on that server, so the update fails with "Invalid object name
'ekhata_test.dbo.krs'" — including the RI submit, which sets status 13. The server's
e-Khata copy is **`BBMPEkhata`** (its `KRS` table has every column the trigger uses).

Nine objects in its `UDD_KHATABTOA` name e-Khata databases that are not there:

| Name used | Objects |
|---|---|
| `ekhata_test` | `trg_BtoAMainApp_UpdateNewKhata`, `fn_GetKRSID`, `fn_GetDeedScheduleType` |
| `ekhata` | `USP_S_BtoA_MainApp_Officer`, `USP_I_BtoA_OwnerDetails`, `USP_Sch_InsertBBMpEasthiSchedular`, `usp_S_GetPending_SSAApprovalOrReject_Count`, `usp_S_GetPending_SSAApprovalOrReject_Data`, `usp_S_GetPending_SSAApprovalOrReject_Data_21072026` |
| `EkhataTest` | `usp_S_GetPending_SSAApprovalOrReject_Count` |

Requested: a decision — point these at `BBMPEkhata`, or restore e-Khata under the names
they use (the way `masterDB` became `masterDB_prod`). On the laptop, where there is no
e-Khata at all, `db/23_udd_trg_BtoAMainApp_UpdateNewKhata.sql` adds one guard
(`IF DB_ID(N'ekhata_test') IS NULL RETURN;`) so the trigger skips the e-Khata step when the
database is absent and behaves exactly as before where it exists. It has not been applied
to the server.

**E2. `USP_CheckMobileAppVersion` compares versions as decimals.** It does
`CAST(@Client_Version AS FLOAT)`, so "1.0.0" or "1.1.1" returns ERROR, 1.10 ranks below
1.9, the latest row is picked with a text `MAX`, and `IsForceUpdate` is returned even to
apps that are already current. `db/24_udd_USP_CheckMobileAppVersion.sql` compares real
versions and keeps every parameter, result column and status. It must be on the server
before the 1.1.0 app is released there.

**E3. Road rows: blank and inactive rows reach the officer.** `USP_S_GetAppDetails` (as it
stands in `UDD_KHATABTOA_TEST`; the repo's `USP_S_GetAppDetails.FIXED.sql` is an older copy)
returns one row per `BtoA_SiteRoadDetails` row with that row's own road columns, but:

- every row, including those with `Rd_RoadActive = 0`, which the citizen's declaration no
  longer carries;
- every **blank** row: no road id, no road name, no typed name. The test copy has 10,027
  active blank rows on 10,026 applications — one each — none with a creator or a creation
  date. On 10,008 of those applications the real roads already match
  `Site_numberOfRoadFacingSides`, so the blank row is an extra road card the officer has to
  mark "not found";
- not `Rd_EnteredRoadName`, the name the citizen typed ("3rd Cross"), so the card cannot show it.

The API now leaves blank rows out for both fetches, and the ward sync (`db/20`,
`USP_S_GpsWardSync`) also leaves out inactive rows and returns `Rd_EnteredRoadName`.
Question: **where do the blank rows come from?** One per application with no creator or date
looks like a bulk insert or migration; if something still creates them, it is worth stopping.
No change to `USP_S_GetAppDetails` is needed if the ward sync replaces it.

---

## F. Road details: the officer's road answers, found 5–6 October 2026

A full test of the road-details flow (app, API, the procedures below on
`UDD_KHATABTOA_TEST`, and a live submit) found that some road answers are lost, duplicated
or reported wrongly at the database. The API now works around each of these itself, inside
its submit transaction, so nothing here blocks the app. The procedure changes below would
make those workarounds unnecessary and also protect the other callers of the same
procedures (the JC portal and QC workflow). `db/27_udd_Check_RoadScenarios.sql` replays
every case on the test database inside a transaction it rolls back.

**F1. `USP_IU_BtoA_MainApp_Officer` can report success without saving (defect D33).** Its
UPDATE branch is chosen by `Ofcr_ApplicationDisplayId` alone but filters
`WHERE Ofcr_ApplicationDisplayId = @id AND Ofcr_App_Id = @Ofcr_App_Id`, and it returns
Status 1 whether or not a row was updated. Until 6 October the API passed
`@Ofcr_App_Id = 0`, so for any application that already had an officer row with its real
App_Id (221 active status-10 applications in the test copy) the update matched nothing,
the officer's application answers were lost, and the roads and status 13 were still
written. The API now passes the real App_Id and refuses the submit if no row carries it.

Requested:
- In the UPDATE branch, filter on `Ofcr_ApplicationDisplayId` only (the same key the
  branch is chosen by), and return `Status = 0` with a message when `@@ROWCOUNT = 0`.
- Backfill the rows the API wrote with App_Id 0 (8 in the test copy, created
  18–23 September; the API also heals them on the next submit):

```sql
UPDATE o SET Ofcr_App_Id = a.App_Id
  FROM dbo.BtoA_MainApp_Officer o
  JOIN dbo.BtoAMainApp a ON a.App_DisplayId = o.Ofcr_ApplicationDisplayId
 WHERE o.Ofcr_App_Id = 0;
```

**F2. `USP_IU_BtoA_SiteRoadDetails_Officer` duplicates roads and documents on a
resubmission (D5, D22).** A survey is sent again after a QC return, or by a phone retrying
after a lost reply. The procedure chooses insert or update by
`Ofcr_App_Id AND Ofcr_RoadId = @BtoA_RoadId AND Ofcr_SiteRoadRowID = @BtoA_SiteRoadRowID
AND CRole AND CBy` with `=`, so:
- a road the officer added (`@BtoA_SiteRoadRowID` NULL) never matches and is inserted again
  on every resubmission;
- a road whose id the officer changed (976 to 999) is inserted as a new row and the old
  row stays active: the test replay ended with three active rows for one citizen road;
- every insert of a private road adds a `BtoA_DocumentTran` row (Mdoc 4 / 444), and
  nothing deactivates the earlier ones;
- the update branch rewrites `docTrn_url` for that road's documents of **every** officer
  (the UPDATE has no `docTrn_CBy` filter), never inserts one when a road changes from
  public to private, and never deactivates one when it changes from private to public.

The API now, after writing the roads and in the same transaction, deactivates this
officer's road rows for the application that the submit did not write, keeps active only
the newest private-road document per live private road, and inserts one where the update
branch left a private road without any.

Requested:
- Match on `Ofcr_App_Id + Ofcr_SiteRoadRowID + CBy + CRole`, without the road id, and
  always INSERT when `@BtoA_SiteRoadRowID IS NULL`.
- In the update branch: scope the document UPDATE by `docTrn_CBy = @CBy`; when the new
  type is Private and this officer has no active document for the road, insert one as the
  insert branch does; when the new type is not Private, set `docTrn_Active = 0` on this
  officer's documents for the road.
- Item B2 (`SCOPE_IDENTITY()` read after the document insert) is still needed: the
  served-notice photo is written to the row id the procedure returns. The API's own
  clean-up above no longer depends on that id.

**F3. `USP_U_BtoA_GpsRoadNoticeDocument` can never clear a notice (D22).** It sets
`Ofcr_Notice_Document = COALESCE(@NoticeDocument, Ofcr_Notice_Document)`, so a road that
was private (with a served notice) and is public on resubmission keeps the old notice. The
API now clears it itself with a direct UPDATE scoped to the row, the application and the
officer. Optional: change the procedure to `SET Ofcr_Notice_Document = @NoticeDocument`
and the API can call it for every road instead.

**F4. `USP_S_GetAppDetails` returns inactive road rows, no typed road name, and cuts
applications in half (D14; see E3).** Besides E3, `TOP (@AppID)` counts joined rows, not
applications, so the 500th row can end an application part-way through its roads (a corner
plot arriving with one of its two roads). The API now reads `Rd_RoadActive` and
`Rd_EnteredRoadName` from `BtoA_SiteRoadDetails` itself, drops inactive rows, and holds
back the last application of a full page until the next fetch. If the procedure is kept,
requested: `AND ISNULL(srd.Rd_RoadActive, 1) = 1` on the road join, `srd.Rd_EnteredRoadName`
in the select list, and the TOP applied to applications (a CTE of the first @AppID App_Ids,
then the join). If the ward sync (`db/20`) replaces it, nothing is needed.

**F5. The KSRSAC road check depends on the property's street (D31).** The road procedure
looks the ward up through
`mst_AROMapping A ON A.GBAZoneID = M.MD_ZoneId AND A.BBMPWardId = M.MD_WardId
AND A.BBMPStreetId = M.MD_StreetId` before reaching `MstRoadKSRAC`. When
`BtoA_EPIDMetaData.MD_StreetId` is NULL or not in `mst_AROMapping`, every road of the
application is reported "not recognised", even the citizen's own correct road (replayed on
the test copy for an application with no street). The result is only reported to the app
as a warning and stored in no column, so the API does not repeat the check. Requested:
resolve the BBMP zone / ward without the street:

```sql
IF EXISTS (SELECT 1
             FROM BtoA_EPIDMetaData M
             JOIN (SELECT DISTINCT GBAZoneID, BBMPWardId, BBMPZoneID
                     FROM masterDB_prod.dbo.mst_AROMapping) A
               ON A.GBAZoneID = M.MD_ZoneId AND A.BBMPWardId = M.MD_WardId
             JOIN masterDB_prod.dbo.MstRoadKSRAC R
               ON R.BBMPZoneID = A.BBMPZoneID AND R.BBMPWardId = A.BBMPWardId
              AND R.Road_ID = @BtoA_RoadId AND R.Road_Name = @BtoA_RoadName
            WHERE M.MD_APP_ID = @BtoA_MainAppId)
    SET @KsracMatched = 1;
```

**F6. For information: the street list no longer comes from
`USP_S_GetMasterStreetDetails` (D2).** Level 5 returns BBMP street ids
(`mst_AROMapping.BBMPStreetId`, joined on `Road_KSRACId`), which never equal a KSRSAC
`Road_ID` (0 of 272 joined rows in ward 102/54), while the citizen's `Rd_RoadId` and the
road procedure's check are in `Road_ID` space. Every road an officer picked was therefore
stored under a street id and reported unverified. The API now reads
`MstRoadKSRAC.Road_ID / Road_Name` for the ward directly (two-part names, the same join the
road procedure uses). Other screens calling level 5 for road ids may have the same problem.

**F7. Addition to D4 (least-privilege login).** For the above the API also needs:
`SELECT` on `BtoA_SiteRoadDetails` and on `masterDB_prod.dbo.MstRoadKSRAC`; `UPDATE` on
`BtoA_MainApp_Officer` (Ofcr_App_Id) and `BtoA_SiteRoadDetails_Officer` (isActive, UBy,
UDte, URole, Ofcr_Notice_Document); `SELECT, INSERT, UPDATE` on `BtoA_DocumentTran`.

---

## Summary

| | Item | Blocking? |
|---|---|---|
| A1 | Role 116 branch and NULL-status guard on `USP_IU_BtoA_StatusDetail_Officer` | **Yes** — submit on live |
| C | Six nullable columns, plus two UPDATE-only procedures | **Yes** — submit on live |
| B1 | Restore ward scope on `USP_S_GetAppDetails` | Jurisdiction leak today |
| B2 | `SCOPE_IDENTITY()` ordering in the road procedure | Wrong id returned today |
| B3 | Mark OTP consumed | Security |
| D4 | Least-privilege login | Before go-live |
| D1 | QC-returns procedure | New requirement |
| E1 | e-Khata database names on the new test server | **Yes** — submit on the test server |
| E2 | Version comparison in `USP_CheckMobileAppVersion` | **Yes** — before the 1.1.0 release |
| E3 | Blank and inactive road rows; origin of the blank rows | Question — handled in the API meanwhile |
| F1 | `USP_IU_BtoA_MainApp_Officer` update key, Status 0 on no match, backfill App_Id 0 | Handled in the API meanwhile |
| F2 | Road procedure: resubmission duplicates rows and documents | Handled in the API meanwhile |
| F3 | Notice procedure cannot clear a notice | Optional — handled in the API |
| F4 | `USP_S_GetAppDetails`: inactive rows, typed name, TOP over rows | Only if the old fetch stays |
| F5 | KSRSAC check without the property's street | Wrong warnings today |
| F6, F7 | Street list source; extra permissions for D4 | For information |
| B4, D2, D3, D5 | For discussion | — |

Happy to walk through any of these at your desk, and to supply ready-to-review scripts for
A1, B1, B2 and C rather than asking you to write them.
