using Auth.Contracts.V1;
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
