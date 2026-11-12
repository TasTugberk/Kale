using System.Reflection;
using AuthService.Persistence;

namespace AuthService.Tests.Persistence;

public sealed class AdvisoryLocksTests
{
    [Fact]
    public void Every_advisory_lock_has_its_own_number()
    {
        // Reflection, so a lock added later is checked too without anyone remembering to list it here.
        var locks = typeof(AdvisoryLocks)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToList();

        locks.Count.ShouldBeGreaterThanOrEqualTo(3);
        var numbers = locks.Select(field => (int)field.GetRawConstantValue()!).ToList();
        numbers.ShouldBeUnique();
    }
}
