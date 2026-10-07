using System.Text.RegularExpressions;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Operations;

/// <summary>
/// Stores an app's operations enum (DESIGN.md registration flow): adds new operations, reactivates ones that
/// came back, marks missing ones obsolete (never deletes them) and replaces the implications.
/// The application comes from the caller's token, never from the request.
/// </summary>
public sealed partial class OperationRegistrar(AuthDbContext db, TimeProvider clock)
{
    // An enum member name as C# allows it. The application prefix is added here, never sent.
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex MemberName();

    public async Task<RegistrationResult> RegisterAsync(
        Guid applicationId, IReadOnlyCollection<OperationDeclaration> declared, CancellationToken cancellationToken = default)
    {
        // Before touching the database, so a bad request changes nothing.
        Validate(declared);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Several instances of an app register at the same moment on startup. This PostgreSQL lock, held until
        // the transaction ends, makes registrations of one application take turns: the second instance then
        // finds the first one's rows instead of inserting duplicates. Other applications aren't blocked.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({applicationId.ToString()}, 0))", cancellationToken);

        var application = await db.Applications.SingleAsync(a => a.Id == applicationId, cancellationToken);
        var existing = await db.Operations
            .Where(o => o.ApplicationId == applicationId)
            .ToDictionaryAsync(o => o.Name, cancellationToken);

        var added = new List<string>();
        var reactivated = new List<string>();
        var unchanged = 0;
        var byMemberName = new Dictionary<string, Operation>();

        foreach (var declaration in declared)
        {
            var name = $"{application.Key}.{declaration.Name}";
            if (!existing.TryGetValue(name, out var operation))
            {
                operation = new Operation { ApplicationId = applicationId, ApplicationKey = application.Key, Name = name };
                db.Operations.Add(operation);
                added.Add(name);
            }
            else if (operation.IsObsolete)
            {
                operation.ObsoletedAt = null;
                reactivated.Add(name);
            }
            else
            {
                unchanged++;
            }

            byMemberName[declaration.Name] = operation;
        }

        var declaredNames = byMemberName.Values.Select(o => o.Name).ToHashSet();
        var obsoleted = new List<string>();
        var now = clock.GetUtcNow();
        foreach (var operation in existing.Values)
        {
            if (!operation.IsObsolete && !declaredNames.Contains(operation.Name))
            {
                operation.ObsoletedAt = now;
                obsoleted.Add(operation.Name);
            }
        }

        // Make the application's implications exactly the declared ones: keep matches, remove the rest, add new.
        var toAdd = declared
            .SelectMany(declaration => declaration.Implies, (declaration, implied) =>
                (OperationId: byMemberName[declaration.Name].Id, ImpliedOperationId: byMemberName[implied].Id))
            .ToHashSet();
        var current = await db.OperationImplications
            .Where(i => i.ApplicationId == applicationId)
            .ToListAsync(cancellationToken);
        foreach (var implication in current)
        {
            // Remove returns true when the pair is still declared: keep the row and don't add it again.
            if (!toAdd.Remove((implication.OperationId, implication.ImpliedOperationId)))
            {
                db.OperationImplications.Remove(implication);
            }
        }

        foreach (var (operationId, impliedOperationId) in toAdd)
        {
            db.OperationImplications.Add(new OperationImplication
            {
                ApplicationId = applicationId, OperationId = operationId, ImpliedOperationId = impliedOperationId,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RegistrationResult(added, reactivated, obsoleted, unchanged);
    }

    private static void Validate(IReadOnlyCollection<OperationDeclaration> declared)
    {
        if (declared.Count == 0)
        {
            throw new InvalidRegistrationException(
                "The operations list is empty. That would make every operation obsolete and remove every permission in the application.");
        }

        var names = new HashSet<string>();
        foreach (var declaration in declared)
        {
            if (!MemberName().IsMatch(declaration.Name))
            {
                throw new InvalidRegistrationException(
                    $"'{declaration.Name}' is not a valid operation name. Send the enum member name, without the application prefix.");
            }

            if (!names.Add(declaration.Name))
            {
                throw new InvalidRegistrationException($"Operation '{declaration.Name}' is declared twice.");
            }
        }

        foreach (var declaration in declared)
        {
            foreach (var implied in declaration.Implies)
            {
                if (!names.Contains(implied))
                {
                    throw new InvalidRegistrationException(
                        $"'{declaration.Name}' implies '{implied}', which is not in the request.");
                }
            }
        }

        var implies = declared
            .SelectMany(declaration => declaration.Implies, (declaration, implied) => (declaration.Name, Implied: implied))
            .ToLookup(pair => pair.Name, pair => pair.Implied);
        var cycle = OperationImplications.FindCycle(implies);
        if (cycle is not null)
        {
            throw new InvalidRegistrationException($"The implications form a cycle: {string.Join(" -> ", cycle)}.");
        }
    }
}
