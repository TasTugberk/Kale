using AuthService.Keys;

namespace AuthService.Tests.Keys;

/// <summary>
/// Lifecycle of one kind of key (signing or encryption), with the defaults: a new key every 30 days, published
/// 1 day before it's used, and an old key kept 1 day after it stops being used.
/// </summary>
public sealed class KeyRotationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly KeyRotationOptions Options = new();

    [Fact]
    public void With_no_keys_a_key_must_be_created()
    {
        var plan = KeyRotationPolicy.Plan([], Now, Options);

        plan.CreateNewKey.ShouldBeTrue();
        plan.SigningKeyId.ShouldBeNull();
        plan.KeysToDelete.ShouldBeEmpty();
    }

    [Fact]
    public void A_brand_new_first_key_is_used_right_away()
    {
        // On first start there is nothing older to use, so pre-publication can't apply.
        var first = KeyCreated(daysAgo: 0);

        var plan = KeyRotationPolicy.Plan([first], Now, Options);

        plan.SigningKeyId.ShouldBe(first.Id);
        plan.CreateNewKey.ShouldBeFalse();
    }

    [Fact]
    public void When_no_key_is_old_enough_the_one_published_longest_is_used()
    {
        // Rare (e.g. keys deleted by hand), but then the oldest key is the one most likely known everywhere.
        var older = KeyCreated(daysAgo: 0.8);
        var newer = KeyCreated(daysAgo: 0.1);

        var plan = KeyRotationPolicy.Plan([newer, older], Now, Options);

        plan.SigningKeyId.ShouldBe(older.Id);
    }

    [Fact]
    public void A_key_younger_than_30_days_needs_no_successor()
    {
        var plan = KeyRotationPolicy.Plan([KeyCreated(daysAgo: 29.9)], Now, Options);

        plan.CreateNewKey.ShouldBeFalse();
    }

    [Fact]
    public void The_successor_is_created_after_30_days_and_not_used_yet()
    {
        var current = KeyCreated(daysAgo: 30);

        var plan = KeyRotationPolicy.Plan([current], Now, Options);

        plan.CreateNewKey.ShouldBeTrue();
        plan.SigningKeyId.ShouldBe(current.Id);
    }

    [Fact]
    public void A_successor_younger_than_one_day_is_published_but_not_used_for_signing()
    {
        var current = KeyCreated(daysAgo: 30.5);
        var successor = KeyCreated(daysAgo: 0.5);

        var plan = KeyRotationPolicy.Plan([current, successor], Now, Options);

        plan.SigningKeyId.ShouldBe(current.Id);
        plan.CreateNewKey.ShouldBeFalse();
        plan.KeysToDelete.ShouldBeEmpty();
    }

    [Fact]
    public void After_one_day_the_successor_takes_over()
    {
        var old = KeyCreated(daysAgo: 31);
        var successor = KeyCreated(daysAgo: 1);

        var plan = KeyRotationPolicy.Plan([old, successor], Now, Options);

        plan.SigningKeyId.ShouldBe(successor.Id);
    }

    [Fact]
    public void An_old_key_stays_published_for_one_day_after_it_stopped_signing()
    {
        // The successor took over half a day ago.
        var plan = KeyRotationPolicy.Plan([KeyCreated(daysAgo: 31.5), KeyCreated(daysAgo: 1.5)], Now, Options);

        plan.KeysToDelete.ShouldBeEmpty();
    }

    [Fact]
    public void Retention_counts_from_when_the_old_key_stopped_being_used_not_from_its_age()
    {
        // The refresh that should have created the successor on day 30 ran 3 days late. The old key is 33.5 days
        // old, but it was still in use until 0.5 days ago, so tokens it made must still be accepted.
        var old = KeyCreated(daysAgo: 33.5);
        var successor = KeyCreated(daysAgo: 1.5);

        var plan = KeyRotationPolicy.Plan([old, successor], Now, Options);

        plan.SigningKeyId.ShouldBe(successor.Id);
        plan.KeysToDelete.ShouldBeEmpty();
    }

    [Fact]
    public void An_old_key_is_deleted_once_its_retention_has_passed()
    {
        // The successor took over a day ago.
        var old = KeyCreated(daysAgo: 32);

        var plan = KeyRotationPolicy.Plan([old, KeyCreated(daysAgo: 2)], Now, Options);

        plan.KeysToDelete.ShouldBe([old.Id]);
    }

    [Fact]
    public void The_key_in_use_is_never_deleted_even_if_rotation_stalled()
    {
        // e.g. the service was down for months: keep signing with what we have and create a successor.
        var only = KeyCreated(daysAgo: 100);

        var plan = KeyRotationPolicy.Plan([only], Now, Options);

        plan.SigningKeyId.ShouldBe(only.Id);
        plan.KeysToDelete.ShouldBeEmpty();
        plan.CreateNewKey.ShouldBeTrue();
    }

    [Fact]
    public void The_newest_key_that_is_old_enough_signs()
    {
        var oldest = KeyCreated(daysAgo: 31.5);
        var middle = KeyCreated(daysAgo: 1.5);
        var newest = KeyCreated(daysAgo: 0.2);

        var plan = KeyRotationPolicy.Plan([newest, oldest, middle], Now, Options);

        plan.SigningKeyId.ShouldBe(middle.Id);
    }

    private static KeyAge KeyCreated(double daysAgo) => new(Guid.CreateVersion7(), Now.AddDays(-daysAgo));
}
