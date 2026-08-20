using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public static class ControlTokenAuthentication
{
    public static async Task InvokeAsync(
        HttpContext context,
        ControlTokenStore controlToken,
        RequestDelegate next)
    {
        var suppliedToken = context.Request.Headers[
            ProtocolConstants.ControlTokenHeader].ToString();
        if (!controlToken.IsMatch(suppliedToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid-control-token",
                message = "A valid local control token is required.",
            });
            return;
        }

        await next(context);
    }
}
