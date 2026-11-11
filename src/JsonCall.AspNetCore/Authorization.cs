using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JsonCall.AspNetCore;

/// <summary>Applies [Authorize] and [AllowAnonymous] found on contracts and their methods.</summary>
internal sealed class JsonRpcAuthorizationFilter : IJsonRpcCallFilter
{
    public async ValueTask<JsonRpcError?> OnInvokingAsync(RegisteredMethod method, JsonRpcCallContext context)
    {
        var attributes = method.GetAttributes().ToList();
        var authorizeData = attributes.OfType<IAuthorizeData>().ToList();

        if (authorizeData.Count == 0 || attributes.OfType<IAllowAnonymous>().Any())
            return null;

        if (context.Items.TryGetValue(JsonRpcHttp.HttpContextKey, out var value) is false || value is not HttpContext http)
            return new JsonRpcError(JsonRpcErrorCodes.Unauthorized, "Authorization is not available for this call.");

        var provider = http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var evaluator = http.RequestServices.GetRequiredService<IPolicyEvaluator>();

        var policy = await AuthorizationPolicy.CombineAsync(provider, authorizeData).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("The authorization policy could not be resolved.");

        var authentication = await evaluator.AuthenticateAsync(policy, http).ConfigureAwait(false);
        var result = await evaluator.AuthorizeAsync(policy, authentication, http, null).ConfigureAwait(false);

        if (result.Succeeded)
            return null;

        return result.Challenged
            ? new JsonRpcError(JsonRpcErrorCodes.Unauthorized, "Unauthorized")
            : new JsonRpcError(JsonRpcErrorCodes.Forbidden, "Forbidden");
    }
}
