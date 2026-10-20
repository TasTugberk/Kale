using System.Reflection;

namespace Auth.Client;

/// <summary>One enum member as sent to AuthService on startup: its name and the names it implies.</summary>
public sealed record OperationDeclaration(string Name, IReadOnlyList<string> Implies);

/// <summary>
/// The application's operations: its enum, read once at startup. Mistakes in the enum fail the app's startup,
/// not a request later.
/// </summary>
public sealed class OperationCatalog
{
    private readonly HashSet<string> _members;

    private OperationCatalog(Type enumType, string applicationKey, IReadOnlyList<OperationDeclaration> declarations)
    {
        EnumType = enumType;
        ApplicationKey = applicationKey;
        Declarations = declarations;
        _members = declarations.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
    }

    public Type EnumType { get; }

    public string ApplicationKey { get; }

    public IReadOnlyList<OperationDeclaration> Declarations { get; }

    public static OperationCatalog For<TOperations>(string applicationKey)
        where TOperations : struct, Enum
    {
        var enumType = typeof(TOperations);

        // Two names for one value would make one of them unreachable: Enum.ToString returns only one name.
        var duplicate = Enum.GetValues<TOperations>()
            .GroupBy(value => value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var names = enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => Equals(field.GetValue(null), duplicate.Key))
                .Select(field => field.Name);
            throw new InvalidOperationException(
                $"{enumType.Name} has several members with the same value ({string.Join(", ", names)}). Give each operation its own value.");
        }

        var declarations = enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => new OperationDeclaration(field.Name, ImpliedNames(enumType, field)))
            .ToList();
        return new OperationCatalog(enumType, applicationKey, declarations);
    }

    /// <summary>"InvoiceRead" → "billing.InvoiceRead", the name AuthService stores and returns.</summary>
    public string QualifiedName(string member) =>
        _members.Contains(member)
            ? $"{ApplicationKey}.{member}"
            : throw new InvalidOperationException($"'{member}' is not a member of {EnumType.Name}.");

    private static List<string> ImpliedNames(Type enumType, FieldInfo field)
    {
        var implied = field.GetCustomAttribute<ImpliesAttribute>()?.Operations ?? [];
        foreach (var operation in implied)
        {
            if (operation.GetType() != enumType || !Enum.IsDefined(enumType, operation))
            {
                throw new InvalidOperationException(
                    $"{enumType.Name}.{field.Name} implies {operation}, which isn't a member of {enumType.Name}.");
            }
        }

        return implied.Select(operation => operation.ToString()!).ToList();
    }
}
