using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;

namespace PropertyGpsApi.Interfaces;

public interface IOtpService
{
    Task<SendOtpResponse> SendAsync(SendOtpRequest request, CancellationToken ct);
    Task<VerifyOtpResponse> VerifyAsync(VerifyOtpRequest request, string? clientIp, CancellationToken ct);
}
