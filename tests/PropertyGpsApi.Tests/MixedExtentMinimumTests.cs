using System.ComponentModel.DataAnnotations;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;
using Xunit;

namespace PropertyGpsApi.Tests;

/// <summary>
/// A Mixed plot's two extents must each be at least Survey:MinMixedExtentPercent of the site
/// (user, 2026-10-28). The value is a server setting, so the phone gets it with the ward sync
/// and submit checks it again for older installs.
/// </summary>
public sealed class MixedExtentMinimumTests
{
    private static SubmitSiteDetails Mixed(double commercial, double residential, double site = 159.77) => new()
    {
        SiteArea = site,
        IsPropertyUseTypeCorrect = 0,
        PropertyUseTypeId = VerificationSubmitService.MixedUseTypeId,
        CommercialArea = commercial,
        ResidentialArea = residential,
    };

    private static List<ApiError> Check(SubmitSiteDetails site, double percent = 5)
    {
        var errors = new List<ApiError>();
        VerificationSubmitService.ValidateMixedExtents(errors, site, percent);
        return errors;
    }

    [Fact]
    public void Exactly_the_minimum_on_either_side_is_accepted()
    {
        // 5% of 159.77 is 7.99.
        Assert.Empty(Check(Mixed(7.99, 151.78)));
        Assert.Empty(Check(Mixed(151.78, 7.99)));
    }

    [Fact]
    public void Under_the_minimum_is_refused_naming_the_side_and_the_area()
    {
        var error = Assert.Single(Check(Mixed(151.79, 7.98)));
        Assert.Equal("siteDetails.residentialArea", error.Field);
        Assert.Equal("Residential Extent must be at least 5% of the site area (7.99 sq.m).", error.Message);

        Assert.Equal("siteDetails.commercialArea", Assert.Single(Check(Mixed(7.98, 151.79))).Field);
    }

    [Fact]
    public void Follows_the_configured_percent()
    {
        Assert.Empty(Check(Mixed(15.98, 143.79), percent: 10));
        Assert.Contains("10%", Assert.Single(Check(Mixed(15.97, 143.80), percent: 10)).Message);
    }

    [Fact]
    public void Zero_percent_still_requires_both_uses()
    {
        Assert.Empty(Check(Mixed(0.01, 159.76), percent: 0));
        Assert.Single(Check(Mixed(0, 159.77), percent: 0));
    }

    [Fact]
    public void Only_a_corrected_Mixed_use_is_checked()
    {
        var confirmed = new SubmitSiteDetails { SiteArea = 159.77, IsPropertyUseTypeCorrect = 1, PropertyUseTypeId = 3 };
        var commercial = new SubmitSiteDetails { SiteArea = 159.77, IsPropertyUseTypeCorrect = 0, PropertyUseTypeId = 1 };
        Assert.Empty(Check(confirmed));
        Assert.Empty(Check(commercial));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    [InlineData(-1, false)]
    public void The_setting_is_kept_between_0_and_50(double percent, bool valid)
    {
        var options = new SurveyOptions { MinMixedExtentPercent = percent };
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
        Assert.Equal(valid, ok);
    }

    [Fact]
    public void Defaults_to_5_percent()
    {
        Assert.Equal(5, new SurveyOptions().MinMixedExtentPercent);
    }
}
