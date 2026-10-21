using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Operations;
using AuthService.Persistence;
using Grpc.Core;

namespace AuthService.Grpc;

public sealed class OperationRegistrationGrpcService(AuthDbContext db, OperationRegistrar registrar)
    : OperationRegistration.OperationRegistrationBase
{
    public override async Task<RegisterOperationsResponse> RegisterOperations(
        RegisterOperationsRequest request, ServerCallContext context)
    {
        // The application comes from the token, never from the request.
        var caller = await context.RequireActiveApplicationAsync(db);

        // AuthService's own operations (auth.Admin) are never registered through this API. "auth" is a client like
        // any other, so the admin UI's client secret gets a token for it; registering through it could make
        // auth.Admin obsolete, locking every admin out, or add new auth.* operations.
        if (caller.Key == AuthApplication.Key)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The operations of 'auth' belong to AuthService itself."));
        }
        var declarations = request.Operations
            .Select(operation => new OperationDeclaration(operation.Name, operation.Implies.ToList()))
            .ToList();

        RegistrationResult result;
        try
        {
            result = await registrar.RegisterAsync(caller.Id, declarations, context.CancellationToken);
        }
        catch (InvalidRegistrationException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }

        return new RegisterOperationsResponse
        {
            Added = { result.Added },
            Reactivated = { result.Reactivated },
            Obsoleted = { result.Obsoleted },
            Unchanged = result.Unchanged,
        };
    }
}
