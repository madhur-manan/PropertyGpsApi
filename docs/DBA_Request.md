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
| B4, D2, D3, D5 | For discussion | — |

Happy to walk through any of these at your desk, and to supply ready-to-review scripts for
A1, B1, B2 and C rather than asking you to write them.
