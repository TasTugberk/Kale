using Auth.Contracts.V1;
using Google.Protobuf.Reflection;

namespace Auth.Client.Tests.Contracts;

/// <summary>
/// Field numbers are the wire format: changing one silently breaks every deployed client app.
/// These tests make such a change a deliberate, visible edit.
/// </summary>
public sealed class ContractTests
{
    [Fact]
    public void OperationRegistration_service_shape()
    {
        var method = FindMethod(RegistrationReflection.Descriptor, "OperationRegistration", "RegisterOperations");

        method.InputType.Name.ShouldBe(nameof(RegisterOperationsRequest));
        method.OutputType.Name.ShouldBe(nameof(RegisterOperationsResponse));
    }

    [Fact]
    public void Registration_field_numbers()
    {
        RegisterOperationsRequest.OperationsFieldNumber.ShouldBe(1);
        OperationDefinition.NameFieldNumber.ShouldBe(1);
        OperationDefinition.ImpliesFieldNumber.ShouldBe(2);
        RegisterOperationsResponse.AddedFieldNumber.ShouldBe(1);
        RegisterOperationsResponse.ReactivatedFieldNumber.ShouldBe(2);
        RegisterOperationsResponse.ObsoletedFieldNumber.ShouldBe(3);
        RegisterOperationsResponse.UnchangedFieldNumber.ShouldBe(4);
    }

    [Fact]
    public void SessionService_shape()
    {
        var method = FindMethod(SessionsReflection.Descriptor, "SessionService", "GetSession");

        method.InputType.Name.ShouldBe(nameof(GetSessionRequest));
        method.OutputType.Name.ShouldBe(nameof(GetSessionResponse));
    }

    [Fact]
    public void Session_field_numbers_and_statuses()
    {
        GetSessionRequest.SessionIdFieldNumber.ShouldBe(1);
        GetSessionResponse.StatusFieldNumber.ShouldBe(1);
        GetSessionResponse.RoleIdFieldNumber.ShouldBe(2);
        GetSessionResponse.UserIdFieldNumber.ShouldBe(3);
        GetSessionResponse.ExpiresAtFieldNumber.ShouldBe(4);

        ((int)SessionStatus.Unspecified).ShouldBe(0);
        ((int)SessionStatus.Active).ShouldBe(1);
        ((int)SessionStatus.Ended).ShouldBe(2);
    }

    [Fact]
    public void RoleService_shape()
    {
        var method = FindMethod(RolesReflection.Descriptor, "RoleService", "GetEffectiveOperations");

        method.InputType.Name.ShouldBe(nameof(GetEffectiveOperationsRequest));
        method.OutputType.Name.ShouldBe(nameof(GetEffectiveOperationsResponse));
    }

    [Fact]
    public void Role_field_numbers()
    {
        GetEffectiveOperationsRequest.RoleIdFieldNumber.ShouldBe(1);
        GetEffectiveOperationsResponse.OperationsFieldNumber.ShouldBe(1);
    }

    [Fact]
    public void All_contracts_share_one_versioned_package()
    {
        RegistrationReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
        SessionsReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
        RolesReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
    }

    private static MethodDescriptor FindMethod(FileDescriptor file, string service, string method)
    {
        var serviceDescriptor = file.Services.SingleOrDefault(s => s.Name == service);
        serviceDescriptor.ShouldNotBeNull($"service {service} in {file.Name}");

        var methodDescriptor = serviceDescriptor.FindMethodByName(method);
        methodDescriptor.ShouldNotBeNull($"method {service}.{method}");
        return methodDescriptor;
    }
}
