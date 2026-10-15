using Auth.Contracts.V1;
using AuthService.Operations;
using AuthService.Persistence;
using Grpc.Core;

namespace AuthService.Grpc;

public sealed class RoleGrpcService(AuthDbContext db, EffectiveOperations effectiveOperations) : RoleService.RoleServiceBase
{
    public override async Task<GetEffectiveOperationsResponse> GetEffectiveOperations(
        GetEffectiveOperationsRequest request, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var roleId = AppCaller.ParseId(request.RoleId, "role_id");

        var operations = await effectiveOperations.ForRoleAsync(caller.Id, roleId, context.CancellationToken)
            ?? throw AppCaller.NotFound("Role");

        return new GetEffectiveOperationsResponse { Operations = { operations.Order(StringComparer.Ordinal) } };
    }
}
