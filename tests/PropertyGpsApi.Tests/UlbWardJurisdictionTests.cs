using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Property GPS works by ULB + ward number only (UDD_MASTERDB). An officer's wards are their
/// own (mst_Officer.Ofcr_ULBId + Ofcr_WardId) plus their active mst_Ofcr_WardMapping rows
/// (Map_CorpId as the ULB, Map_WardId as the number); a ward officer (116/117) may act in any
/// of them and in no other. Zones, corporations, divisions and the GBA/BBMP columns are read
/// nowhere; the JSON fields that carried them stay, null.
/// </summary>
public class UlbWardJurisdictionTests
{
    private static JurisdictionRow Own(int? ulb, string? ward, string? ulbName = null, string? wardName = null) =>
        new() { Src = 0, UlbCode = ulb, WardNumber = ward, UlbName = ulbName, WardName = wardName };

    private static JurisdictionRow Mapped(int? ulb, string? ward, string? ulbName = null, string? wardName = null) =>
        new() { Src = 1, UlbCode = ulb, WardNumber = ward, UlbName = ulbName, WardName = wardName };

    public class BuildingTheJurisdictions
    {
        [Fact]
        public void The_own_ward_comes_first_then_the_mapped_ones()
        {
            var wards = OfficerService.ToJurisdictions(
            [
                Mapped(1, "112", "Bengaluru", "Domlur"),
                Own(1, "54", "Bengaluru", "Hoodi"),
                Mapped(7, "12A", " Mysuru\t", " Ward 12A ")
            ]);

            Assert.Equal(
                [(1, "54", "Bengaluru", "Hoodi"), (1, "112", "Bengaluru", "Domlur"), (7, "12A", "Mysuru", "Ward 12A")],
                wards.Select(w => (w.UlbCode!.Value, w.WardNumber!, w.UlbName!, w.WardName!)));
        }

        /// <summary>A mapping row repeating the own ward (or another mapping) is one ward, matched trimmed, ignoring case.</summary>
        [Fact]
        public void A_ward_mapped_twice_or_also_owned_is_listed_once()
        {
            var wards = OfficerService.ToJurisdictions(
            [
                Own(1, "12a", wardName: "Own spelling"),
                Mapped(1, " 12A ", wardName: "Mapped spelling"),
                Mapped(1, "55"),
                Mapped(1, "55 ")
            ]);

            Assert.Equal([(1, "12a"), (1, "55")], wards.Select(w => (w.UlbCode!.Value, w.WardNumber!)));
            Assert.Equal("Own spelling", wards[0].WardName);
        }

        [Fact]
        public void The_same_ward_number_in_two_ulbs_is_two_wards()
        {
            var wards = OfficerService.ToJurisdictions([Own(1, "54"), Mapped(2, "54")]);
            Assert.Equal([1, 2], wards.Select(w => w.UlbCode!.Value));
        }

        [Fact]
        public void A_row_with_no_ulb_is_no_jurisdiction()
        {
            var wards = OfficerService.ToJurisdictions([Own(null, "54"), Own(0, "54"), Mapped(1, "55")]);
            Assert.Equal("55", Assert.Single(wards).WardNumber);
        }

        [Fact]
        public void Missing_names_stay_null_and_the_old_fields_are_never_filled()
        {
            var ward = Assert.Single(OfficerService.ToJurisdictions([Own(1, "54", "  ", null)]));

            Assert.Null(ward.UlbName);
            Assert.Null(ward.WardName);
            Assert.Null(ward.ZoneId);
            Assert.Null(ward.WardId);
            Assert.Null(ward.GbaZoneId);
            Assert.Null(ward.CorporationId);
        }

        [Theory]
        [InlineData(117)]
        public void A_ward_officer_gets_their_wards_and_never_a_fallback(int roleId)
        {
            IReadOnlyList<Jurisdiction> fallback = [new() { UlbCode = 9, WardNumber = "9" }];
            Assert.Empty(OfficerService.ChooseJurisdictions(roleId, [], fallback));
        }

        [Fact]
        public void Only_the_RI_117_is_a_ward_officer()
        {
            Assert.False(JurisdictionRules.IsWardScoped(116));
            Assert.True(JurisdictionRules.IsWardScoped(117));
            Assert.False(JurisdictionRules.IsWardScoped(118));
            Assert.False(JurisdictionRules.IsWardScoped(125));
        }
    }

    public class TheJurisdictionQuery
    {
        private static readonly string Sql = OfficerService.WardScopedJurisdictionsSql;

        [Fact]
        public void Reads_the_officers_own_ulb_and_ward_number()
        {
            Assert.Contains("UlbCode = o.Ofcr_ULBId", Sql);
            Assert.Contains("NULLIF(LTRIM(RTRIM(REPLACE(o.Ofcr_WardId, CHAR(9), ''))), '')", Sql);
        }

        [Fact]
        public void Reads_the_active_mapping_rows_of_the_officer_in_that_role()
        {
            Assert.Contains("UlbCode = wm.Map_CorpId", Sql);
            Assert.Contains("CONVERT(nvarchar(20), wm.Map_WardId)", Sql);
            Assert.Contains("wm.Map_OfcrId = o.Ofcr_Id", Sql);
            Assert.Contains("wm.Map_OfcrRoleId = o.Ofcr_RoleId", Sql);
            Assert.Contains("ISNULL(wm.isActive, 1) = 1", Sql);
            Assert.Contains("o.Ofcr_RoleId = 117", Sql);
        }

        [Fact]
        public void Names_come_from_the_aro_mapping_by_ulb_and_trimmed_ward_number()
        {
            Assert.Contains("LTRIM(RTRIM(REPLACE(a.ULBName_EN, CHAR(9), '')))", Sql);
            Assert.Contains("LTRIM(RTRIM(REPLACE(a.WardName_EN, CHAR(9), '')))", Sql);
            Assert.Contains("a.ULBCode = w.UlbCode", Sql);
            Assert.Contains("LTRIM(RTRIM(REPLACE(a.WardNumber, CHAR(9), ''))) = w.WardNumber", Sql);
        }

        /// <summary>UDD_MASTERDB's mst_Officer has no zone, corporation or division column.</summary>
        [Fact]
        public void No_officer_query_reads_a_column_that_is_gone()
        {
            foreach (var sql in new[] { OfficerService.WardScopedJurisdictionsSql, OfficerService.OwnWardSql })
            {
                foreach (var gone in new[] { "Ofcr_ZoneId", "Ofcr_CorporationId", "Ofcr_DivisionId", "GBA", "BBMP", "Map_ZoneID", "easthi" })
                    Assert.DoesNotContain(gone, sql, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Writes_nothing()
        {
            foreach (var word in new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC" })
                Assert.DoesNotContain(word, Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class TheOwnWardCheck
    {
        private const string Refused = "You can only act in your own ward.";

        private static readonly Jurisdiction[] TwoWards =
        [
            new() { UlbCode = 1, WardNumber = "54" },
            new() { UlbCode = 7, WardNumber = "12A" }
        ];

        [Theory]
        [InlineData(117, 1, "54")]
        [InlineData(117, 7, "12A")]    // the second ward, which the token does not carry
        [InlineData(117, 7, " 12a ")]  // text, trimmed, ignoring case
        public void A_ward_officer_may_act_in_any_mapped_ward(int roleId, int ulb, string ward) =>
            JurisdictionRules.RequireOwnWard(roleId, TwoWards, ulb, ward, Refused);

        [Theory]
        [InlineData(117, 1, "55")]
        [InlineData(117, 2, "54")]     // the same number in another ULB
        [InlineData(117, 0, "54")]
        [InlineData(117, 1, "")]
        [InlineData(117, 1, null)]
        public void Any_other_ward_is_refused(int roleId, int ulb, string? ward)
        {
            var ex = Assert.Throws<ApiException>(() => JurisdictionRules.RequireOwnWard(roleId, TwoWards, ulb, ward, Refused));

            Assert.Equal(403, ex.StatusCode);
            Assert.Equal(ApiErrorCodes.OutsideJurisdiction, ex.Code);
            Assert.Equal(Refused, ex.Message);
        }

        [Theory]
        [InlineData(117)]
        public void A_ward_officer_mapped_to_no_ward_may_act_in_none(int roleId)
        {
            Assert.Throws<ApiException>(() => JurisdictionRules.RequireOwnWard(roleId, [], 1, "54", Refused));
            Assert.Throws<ApiException>(() => JurisdictionRules.RequireOwnWard(roleId, null, 1, "54", Refused));
        }

        [Theory]
        [InlineData(118)]
        [InlineData(125)]
        public void Other_roles_are_not_limited_by_the_ward_rule(int roleId) =>
            JurisdictionRules.RequireOwnWard(roleId, null, 99, "99", Refused);

        [Fact]
        public void Media_follows_the_same_wards()
        {
            var second = new MediaScope { UlbCode = 7, WardNumber = "12A" };
            var other = new MediaScope { UlbCode = 1, WardNumber = "55" };

            Assert.True(JurisdictionRules.MayViewMedia(117, TwoWards, 1, second));
            Assert.False(JurisdictionRules.MayViewMedia(116, TwoWards, 1, second), "116 is not a Property GPS role on UDD");
            Assert.False(JurisdictionRules.MayViewMedia(117, TwoWards, 1, other));
            Assert.False(JurisdictionRules.MayViewMedia(117, [], 1, second));
        }

        [Fact]
        public void An_officer_above_ward_level_sees_their_ulbs_media()
        {
            var scope = new MediaScope { UlbCode = 7, WardNumber = "12A" };

            Assert.True(JurisdictionRules.MayViewMedia(125, null, 7, scope));
            Assert.True(JurisdictionRules.MayViewMedia(125, null, null, scope));
            Assert.False(JurisdictionRules.MayViewMedia(125, null, 1, scope));
            Assert.False(JurisdictionRules.MayViewMedia(118, TwoWards, 7, scope));
        }

        /// <summary>
        /// The old fetch filters on zone ids no ULB + ward check can vouch for, so a ward officer
        /// is refused it outright (they use the ward sync) before any query runs.
        /// </summary>
        [Theory]
        [InlineData(117)]
        public async Task The_old_fetch_refuses_a_ward_officer(int roleId)
        {
            var service = new PropertyService(null!, null!, null!, null!, null!);
            var e = await Assert.ThrowsAsync<ApiException>(() => service.FetchAsync(
                new FetchApplicationsRequest { CorporationId = 1, ZoneId = 102, WardId = 54 },
                officerId: 1, roleId, TwoWards, CancellationToken.None));
            Assert.Equal(ApiErrorCodes.Forbidden, e.Code);
        }
    }

    public class TheWardColumnsRead
    {
        [Fact]
        public void Media_is_scoped_by_ulb_and_ward_number()
        {
            Assert.Contains("UlbCode = TRY_CONVERT(int, md.MD_ULBCode)", MediaAccessService.Sql);
            Assert.Contains("WardNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId)))", MediaAccessService.Sql);
            Assert.DoesNotContain("MD_ZoneId", MediaAccessService.Sql);
        }

        [Fact]
        public void History_entries_carry_ulb_and_ward_number()
        {
            Assert.Contains("UlbCode       = TRY_CONVERT(int, md.MD_ULBCode)", HistoryService.PageSql);
            Assert.Contains("WardNumber    = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId)))", HistoryService.PageSql);
            Assert.DoesNotContain("MD_ZoneId", HistoryService.PageSql);

            var json = JsonSerializer.Serialize(new HistoryEntryDto { UlbCode = 1, WardNumber = "12A" });
            Assert.Contains("\"ulbCode\":1", json);
            Assert.Contains("\"wardNumber\":\"12A\"", json);
            Assert.Contains("\"zoneId\":null", json);
        }

        [Fact]
        public void A_ward_sync_row_carries_its_ulb_ward_and_street_as_text()
        {
            PropertyRow[] rows =
            [
                new() { AppID = 7, UlbCode = 1, UlbName = "Bengaluru", WardNumber = " 12A ", WardName = "Hoodi", StreetNumber = " 0042 ", StreetId = 42 }
            ];
            var dto = PropertyMapper.Map(
                rows.GroupBy(r => r.AppID).Single(),
                new Dictionary<int, AssignmentRow>(), new Dictionary<int, OwnerSummary>(), 11320);

            Assert.Equal((1, "Bengaluru", "12A", "Hoodi"), (dto.UlbCode, dto.UlbName, dto.WardNumber, dto.WardName));
            Assert.Equal("0042", dto.SiteDetails!.StreetId);
            Assert.Null(dto.ZoneId);
            Assert.Null(dto.WardId);
        }

        [Fact]
        public void The_old_fetchs_numeric_street_id_becomes_text()
        {
            Assert.Equal("42", PropertyMapper.StreetIdOf(new PropertyRow { StreetId = 42 }));
            Assert.Null(PropertyMapper.StreetIdOf(new PropertyRow()));
        }
    }

    public class TheSignInReply
    {
        private static readonly Officer RevenueInspector = new()
        {
            OfficerId = 11333, RoleId = 117, Mobile = "9000000001", Name = "Test RI", RoleName = "RI",
            UlbCode = 1, WardNumber = "54",
            Jurisdictions =
            [
                new() { UlbCode = 1, UlbName = "Bengaluru", WardNumber = "54", WardName = "Hoodi" },
                new() { UlbCode = 7, UlbName = "Mysuru", WardNumber = "12A", WardName = "Ward 12A" }
            ]
        };

        [Fact]
        public void The_top_level_ulb_and_ward_are_the_first_jurisdictions()
        {
            var reply = OfficerService.ToResponse(RevenueInspector, "t", default);

            Assert.Equal((1, "Bengaluru", "54", "Hoodi"), (reply.UlbCode, reply.UlbName, reply.WardNumber, reply.WardName));
            Assert.Equal([(1, "54"), (7, "12A")], reply.Jurisdictions.Select(j => (j.UlbCode, j.WardNumber!)));
            Assert.Equal("Ward 12A", reply.Jurisdictions[1].WardName);
            Assert.Equal("Mysuru", reply.Jurisdictions[1].UlbName);
        }

        [Fact]
        public void The_old_fields_stay_in_the_json_and_are_null()
        {
            var json = JsonSerializer.Serialize(OfficerService.ToResponse(RevenueInspector, "t", default));

            foreach (var field in new[] { "corporationId", "corporationName", "zoneId", "wardId" })
                Assert.Contains($"\"{field}\":null", json);
            foreach (var field in new[] { "gbaZoneId", "gbaZoneName", "zoneName" })
                Assert.Contains($"\"{field}\":null", json);
            Assert.Contains("\"ulbCode\":1", json);
            Assert.Contains("\"wardNumber\":\"54\"", json);
        }

        [Fact]
        public void An_officer_with_no_ward_has_null_top_level_fields_and_no_jurisdictions()
        {
            var reply = OfficerService.ToResponse(RevenueInspector with { Jurisdictions = [] }, "t", default);

            Assert.Null(reply.UlbCode);
            Assert.Null(reply.WardNumber);
            Assert.Empty(reply.Jurisdictions);
        }

        [Fact]
        public void The_token_carries_the_first_wards_ulb_and_ward_as_text_and_no_zone()
        {
            var jwt = new JwtTokenService(
                Options.Create(new JwtOptions { Key = new string('k', 40), Issuer = "i", Audience = "a" }),
                TimeProvider.System);

            var (token, _) = jwt.Issue(RevenueInspector with { UlbCode = 9, WardNumber = "99" }, Guid.NewGuid());
            var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

            Assert.Equal("1", claims.Single(c => c.Type == GpsClaims.UlbCode).Value);
            Assert.Equal("54", claims.Single(c => c.Type == GpsClaims.WardNumber).Value);
            Assert.Equal(("ulb", "ward"), (GpsClaims.UlbCode, GpsClaims.WardNumber));
            Assert.DoesNotContain(claims, c => c.Type is GpsClaims.ZoneId or GpsClaims.WardId or GpsClaims.CorporationId);
        }

        [Fact]
        public void With_no_jurisdiction_the_token_carries_the_officers_own_ward()
        {
            Assert.Equal((9, "99"), JwtTokenService.TokenWard(RevenueInspector with { Jurisdictions = [], UlbCode = 9, WardNumber = " 99 " }));
            Assert.Equal((null, null), JwtTokenService.TokenWard(RevenueInspector with { Jurisdictions = [], UlbCode = null }));
        }

        [Fact]
        public void The_app_signs_in_as_the_ri_by_default() =>
            Assert.Equal(117, new OtpOptions().DefaultRoleId);
    }
}
