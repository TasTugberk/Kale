using System.Text.RegularExpressions;
using AuthService.Domain;
using AuthService.Outbox;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Operations;

/// <summary>
/// Stores an app's operations enum, sent on startup and every few minutes (DESIGN.md registration flow): adds new
/// operations, reactivates ones that came back, records when each listed one was last seen, marks the ones no
/// registration has listed for <see cref="OperationRegistrationSettings.ObsoleteAfter"/> obsolete (never deletes
/// them) and replaces the listed operations' implications. Changes to what roles hold queue RoleEdited events.
/// The application comes from the caller's token, never from the request.
/// </summary>
public sealed partial class OperationRegistrar(AuthDbContext db, TimeProvider clock, OperationRegistrationSettings settings)
{
    // Keeps "{key}.{name}" within operations.name (200): keys are at most 63 characters, plus the dot.
    private const int MaxMemberNameLength = 100;

    // An enum member name as C# allows it. The application prefix is added here, never sent.
    // \z, not $: in .NET, $ also matches just before a final newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
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
        // hashtext turns the id (sent as text) into the int this two-number lock form needs. A rare hash
        // collision only makes two applications take turns, which is harmless.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLocks.OperationRegistration}, hashtext({applicationId.ToString()}))",
            cancellationToken);

        var application = await db.Applications.SingleAsync(a => a.Id == applicationId, cancellationToken);
        var existing = await db.Operations
            .Where(o => o.ApplicationId == applicationId)
            .ToDictionaryAsync(o => o.Name, cancellationToken);
        var now = clock.GetUtcNow();

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

            // Through the tracked entity like every other change, so modified_at moves with it every few minutes
            // too; it then means "last touched by a registration", which is fine for operations.
            operation.LastRegisteredAt = now;
            byMemberName[declaration.Name] = operation;
        }

        // Not "whatever this request left out": another version of the app may be running at the same time
        // (rolling deployment, restart of an old instance, rollback) and still need its operations. Only what
        // no registration has listed for a while is obsolete (OperationRegistrationSettings explains the period).
        var obsoleted = new List<string>();
        var listedSince = now - settings.ObsoleteAfter;
        foreach (var operation in existing.Values)
        {
            if (!operation.IsObsolete && operation.LastRegisteredAt < listedSince)
            {
                operation.ObsoletedAt = now;
                obsoleted.Add(operation.Name);
            }
        }

        var implicationsChanged = await ReplaceImplicationsAsync(applicationId, declared, byMemberName, cancellationToken);

        // A new operation changes no role by itself (nothing grants it yet, and an implication pointing at it is
        // counted above). Obsolete or reactivated operations and changed implications change what roles hold.
        if (reactivated.Count > 0 || obsoleted.Count > 0 || implicationsChanged)
        {
            await QueueRoleEditedForEveryRoleAsync(applicationId, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RegistrationResult(added, reactivated, obsoleted, unchanged);
    }

    /// <summary>
    /// Makes the implications of every operation in the request exactly the declared ones. Implications of
    /// operations the request doesn't list are left alone: they belong to another version of the app that may
    /// still be running, so each operation's implications follow the latest registration that listed it.
    /// </summary>
    /// <returns>Whether any implication was added or removed.</returns>
    private async Task<bool> ReplaceImplicationsAsync(
        Guid applicationId,
        IReadOnlyCollection<OperationDeclaration> declared,
        Dictionary<string, Operation> byMemberName,
        CancellationToken cancellationToken)
    {
        var toAdd = new HashSet<(Guid OperationId, Guid ImpliedOperationId)>();
        foreach (var declaration in declared)
        {
            foreach (var implied in declaration.Implies)
            {
                toAdd.Add((byMemberName[declaration.Name].Id, byMemberName[implied].Id));
            }
        }

        var listedIds = byMemberName.Values.Select(o => o.Id).ToHashSet();
        var current = await db.OperationImplications
            .Where(i => i.ApplicationId == applicationId)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var implication in current)
        {
            if (!listedIds.Contains(implication.OperationId))
            {
                continue;
            }

            // Remove returns true when the pair is still declared: keep the row and don't add it again.
            if (!toAdd.Remove((implication.OperationId, implication.ImpliedOperationId)))
            {
                db.OperationImplications.Remove(implication);
                changed = true;
            }
        }

        foreach (var (operationId, impliedOperationId) in toAdd)
        {
            db.OperationImplications.Add(new OperationImplication
            {
                ApplicationId = applicationId, OperationId = operationId, ImpliedOperationId = impliedOperationId,
            });
            changed = true;
        }

        return changed;
    }

    // Every role of the application, not only the ones that grant a changed operation: implications chain
    // (A -> B -> C), so finding exactly the affected roles would mean expanding every role's grants. Such
    // changes come only with a new version of the app, so a few extra cache evictions cost nothing.
    private async Task QueueRoleEditedForEveryRoleAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var roleIds = await db.Roles
            .Where(r => r.ApplicationId == applicationId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var roleId in roleIds)
        {
            db.OutboxMessages.Add(OutboxEvents.RoleEdited(roleId, applicationId));
        }
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
            if (declaration.Name is null || declaration.Implies is null)
            {
                throw new InvalidRegistrationException("Every operation needs a name and a list of implied names (may be empty).");
            }

            if (!MemberName().IsMatch(declaration.Name))
            {
                throw new InvalidRegistrationException(
                    $"'{declaration.Name}' is not a valid operation name. Send the enum member name, without the application prefix.");
            }

            if (declaration.Name.Length > MaxMemberNameLength)
            {
                throw new InvalidRegistrationException(
                    $"'{declaration.Name}' is longer than {MaxMemberNameLength} characters.");
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

        var pairs = new List<(string Name, string Implied)>();
        foreach (var declaration in declared)
        {
            foreach (var implied in declaration.Implies)
            {
                pairs.Add((declaration.Name, implied));
            }
        }

        var implies = pairs.ToLookup(pair => pair.Name, pair => pair.Implied);
        var cycle = OperationImplications.FindCycle(implies);
        if (cycle is not null)
        {
            throw new InvalidRegistrationException($"The implications form a cycle: {string.Join(" -> ", cycle)}.");
        }
    }
}
