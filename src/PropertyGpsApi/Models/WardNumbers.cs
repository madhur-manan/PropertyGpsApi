using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Models;

/// <summary>
/// Property GPS works by ULB + ward NUMBER only. The number is text everywhere - the master
/// keeps it as nvarchar, and nothing promises it is numeric - at most 20 characters, and two
/// numbers are the same ward when they match trimmed, ignoring case (the database's own
/// comparison). One place for that rule, so the mapping check, the live hub and the requests
/// cannot drift apart.
/// </summary>
public static class WardNumbers
{
    public const int MaxLength = 20;

    /// <summary>Trimmed; null when blank.</summary>
    public static string? Clean(string? wardNumber) =>
        string.IsNullOrWhiteSpace(wardNumber) ? null : wardNumber.Trim();

    /// <summary>1 to 20 characters once trimmed.</summary>
    public static bool IsValid(string? wardNumber) => Clean(wardNumber) is { Length: <= MaxLength };

    /// <summary>Equality for ward numbers that are already trimmed.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The same ward number; a blank one matches nothing.</summary>
    public static bool Same(string? a, string? b) =>
        Clean(a) is { } x && Clean(b) is { } y && Comparer.Equals(x, y);
}

/// <summary>A required ward number: 1 to 20 characters after trimming.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class WardNumberAttribute()
    : ValidationAttribute($"The ward number is required, at most {WardNumbers.MaxLength} characters.")
{
    public override bool IsValid(object? value) => value is string s && WardNumbers.IsValid(s);
}
