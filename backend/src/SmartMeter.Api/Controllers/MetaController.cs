using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartMeter.Api.Infrastructure;
using SmartMeter.Application.Meta;

namespace SmartMeter.Api.Controllers;

[Route("api/v1")]
public sealed class MetaController(IOptions<AuthOptions> auth, IHostEnvironment env) : ApiControllerBase
{
    /// <summary>Analytics window, data version, dimensions (zones / NSPs / manufacturers), grid levels, colour bands.</summary>
    [HttpGet("meta")]
    public async Task<ActionResult<MetaDto>> Meta(CancellationToken ct) => await Sender.Send(new GetMetaQuery(), ct);

    /// <summary>Authentication settings for the SPA (public).</summary>
    [AllowAnonymous]
    [HttpGet("auth/config")]
    public IActionResult AuthConfig()
    {
        var o = auth.Value;
        return Ok(new { enabled = o.Enabled, mode = o.Enabled ? o.Mode : "None", authority = o.Authority, clientId = o.ClientId, scope = o.Scope, audience = o.Audience });
    }

    /// <summary>Issues a short-lived signed token (Auth:Mode = Dev only; never available in production).</summary>
    [AllowAnonymous]
    [HttpPost("auth/dev-token")]
    public IActionResult DevToken([FromBody] DevTokenRequest request)
    {
        var o = auth.Value;
        if (!o.Enabled || !o.IsDev || env.IsProduction()) return NotFound();
        var roles = request.Roles is { Length: > 0 } r ? r : ["viewer", "ingestion-operator"];
        var claims = new List<Claim> { new(ClaimTypes.Name, string.IsNullOrWhiteSpace(request.User) ? "dev.user" : request.User!) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.DevIssuer,
            Audience = o.DevAudience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(8),
            SigningCredentials = new SigningCredentials(o.DevKey(), SecurityAlgorithms.HmacSha256),
        });
        return Ok(new { accessToken = token, expiresInSeconds = 8 * 3600, roles });
    }

    public sealed record DevTokenRequest(string? User, string[]? Roles);
}
