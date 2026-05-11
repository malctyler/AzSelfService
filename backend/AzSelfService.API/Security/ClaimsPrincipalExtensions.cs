using System.Security.Claims;

namespace AzSelfService.API.Security;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetRequiredUserId(this ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var userId)
            ? userId
            : throw new UnauthorizedAccessException("User claim not found.");
    }

    public static Guid GetRequiredCustomerId(this ClaimsPrincipal principal)
    {
        var customerId = principal.FindFirstValue("customer_id");
        return Guid.TryParse(customerId, out var parsed)
            ? parsed
            : throw new UnauthorizedAccessException("Customer claim not found.");
    }
}