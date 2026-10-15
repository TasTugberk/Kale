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
    public void Registration_fields()
    {
        ShouldHaveField(RegisterOperationsRequest.Descriptor, "operations", 1, FieldType.Message, repeated: true);
        ShouldHaveField(OperationDefinition.Descriptor, "name", 1, FieldType.String);
        ShouldHaveField(OperationDefinition.Descriptor, "implies", 2, FieldType.String, repeated: true);
        ShouldHaveField(RegisterOperationsResponse.Descriptor, "added", 1, FieldType.String, repeated: true);
        ShouldHaveField(RegisterOperationsResponse.Descriptor, "reactivated", 2, FieldType.String, repeated: true);
        ShouldHaveField(RegisterOperationsResponse.Descriptor, "obsoleted", 3, FieldType.String, repeated: true);
        ShouldHaveField(RegisterOperationsResponse.Descriptor, "unchanged", 4, FieldType.Int32);
    }

    [Fact]
    public void SessionService_shape()
    {
        var method = FindMethod(SessionsReflection.Descriptor, "SessionService", "GetSession");

        method.InputType.Name.ShouldBe(nameof(GetSessionRequest));
        method.OutputType.Name.ShouldBe(nameof(GetSessionResponse));
    }

    [Fact]
    public void Session_fields_and_statuses()
    {
        ShouldHaveField(GetSessionRequest.Descriptor, "session_id", 1, FieldType.String);
        ShouldHaveField(GetSessionResponse.Descriptor, "status", 1, FieldType.Enum);
        ShouldHaveField(GetSessionResponse.Descriptor, "role_id", 2, FieldType.String);
        ShouldHaveField(GetSessionResponse.Descriptor, "user_id", 3, FieldType.String);
        ShouldHaveField(GetSessionResponse.Descriptor, "expires_at", 4, FieldType.Message);

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
    public void Role_fields()
    {
        ShouldHaveField(GetEffectiveOperationsRequest.Descriptor, "role_id", 1, FieldType.String);
        ShouldHaveField(GetEffectiveOperationsResponse.Descriptor, "operations", 1, FieldType.String, repeated: true);
    }

    [Fact]
    public void All_contracts_share_one_versioned_package()
    {
        RegistrationReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
        SessionsReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
        RolesReflection.Descriptor.Package.ShouldBe("kale.auth.v1");
    }

    // Number, type and repeated-ness together are what goes on the wire.
    private static void ShouldHaveField(
        MessageDescriptor message, string name, int number, FieldType type, bool repeated = false)
    {
        var field = message.FindFieldByName(name);
        field.ShouldNotBeNull($"{message.Name}.{name}");
        field.FieldNumber.ShouldBe(number, $"{message.Name}.{name} number");
        field.FieldType.ShouldBe(type, $"{message.Name}.{name} type");
        field.IsRepeated.ShouldBe(repeated, $"{message.Name}.{name} repeated");
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
