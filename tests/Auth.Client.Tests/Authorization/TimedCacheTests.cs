using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests.Authorization;

/// <summary>
/// Misses for the same key that arrive while a load is running wait for that load instead of asking AuthService
/// again (a role's operations are shared by many users, so an expiry would otherwise bring a burst of calls).
/// </summary>
public sealed class TimedCacheTests
{
    private readonly TimedCache<string, string> _cache = new(TimeSpan.FromMinutes(5), new FakeTimeProvider());
    private int _loads;

    [Fact]
    public async Task Misses_for_one_key_at_the_same_time_share_one_load()
    {
        var answer = new TaskCompletionSource<string>();

        var first = _cache.GetOrAddAsync("role", () => Load(answer));
        var second = _cache.GetOrAddAsync("role", () => Load(answer));
        answer.SetResult("operations");

        (await first).ShouldBe("operations");
        (await second).ShouldBe("operations");
        _loads.ShouldBe(1);
        (await _cache.GetOrAddAsync("role", () => Load(answer))).ShouldBe("operations", "stored");
        _loads.ShouldBe(1);
    }

    [Fact]
    public async Task A_miss_after_an_eviction_does_not_wait_for_a_load_started_before_it()
    {
        var oldAnswer = new TaskCompletionSource<string>();
        var newAnswer = new TaskCompletionSource<string>();

        var before = _cache.GetOrAddAsync("role", () => Load(oldAnswer));
        _cache.Evict("role");
        var after = _cache.GetOrAddAsync("role", () => Load(newAnswer));
        newAnswer.SetResult("new");
        oldAnswer.SetResult("old");

        (await before).ShouldBe("old");
        (await after).ShouldBe("new");
        _loads.ShouldBe(2);
        (await _cache.GetOrAddAsync("role", () => Load(oldAnswer))).ShouldBe("new", "the stale answer isn't stored");
    }

    [Fact]
    public async Task A_miss_after_clearing_does_not_wait_for_a_load_started_before_it()
    {
        var oldAnswer = new TaskCompletionSource<string>();
        var newAnswer = new TaskCompletionSource<string>();

        var before = _cache.GetOrAddAsync("role", () => Load(oldAnswer));
        _cache.Clear();
        var after = _cache.GetOrAddAsync("role", () => Load(newAnswer));
        newAnswer.SetResult("new");
        oldAnswer.SetResult("old");

        (await before).ShouldBe("old");
        (await after).ShouldBe("new");
        _loads.ShouldBe(2);
        (await _cache.GetOrAddAsync("role", () => Load(oldAnswer))).ShouldBe("new", "the stale answer isn't stored");
    }

    [Fact]
    public async Task A_failed_load_fails_everyone_waiting_for_it_and_the_next_miss_loads_again()
    {
        var failing = new TaskCompletionSource<string>();

        var first = _cache.GetOrAddAsync("role", () => Load(failing));
        var second = _cache.GetOrAddAsync("role", () => Load(failing));
        failing.SetException(new TimeoutException("AuthService didn't answer"));

        await Should.ThrowAsync<TimeoutException>(() => first);
        await Should.ThrowAsync<TimeoutException>(() => second);
        _loads.ShouldBe(1);
        (await _cache.GetOrAddAsync("role", () => Task.FromResult("operations"))).ShouldBe("operations");
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_load_for_the_others()
    {
        var answer = new TaskCompletionSource<string>();
        using var giveUp = new CancellationTokenSource();

        var impatient = _cache.GetOrAddAsync("role", () => Load(answer), giveUp.Token);
        var patient = _cache.GetOrAddAsync("role", () => Load(answer));
        await giveUp.CancelAsync();
        answer.SetResult("operations");

        await Should.ThrowAsync<OperationCanceledException>(() => impatient);
        (await patient).ShouldBe("operations");
        _loads.ShouldBe(1);
    }

    [Fact]
    public async Task A_load_that_fails_after_every_caller_gave_up_leaves_no_unobserved_exception()
    {
        // The host would see an unobserved exception as TaskScheduler.UnobservedTaskException, raised when the
        // failed task is garbage collected. Watching for this test's own exception keeps other tests out of it.
        var failure = new TimeoutException("AuthService didn't answer");
        var unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> watch = (_, e) => unobserved |= e.Exception.InnerExceptions.Contains(failure);
        TaskScheduler.UnobservedTaskException += watch;
        try
        {
            await FailALoadNobodyWaitsFor(failure);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            unobserved.ShouldBeFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= watch;
        }
    }

    // A method of its own, so nothing in the test above still refers to the load's task when it collects garbage.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task FailALoadNobodyWaitsFor(Exception failure)
    {
        var failing = new TaskCompletionSource<string>();
        using var giveUp = new CancellationTokenSource();
        var request = _cache.GetOrAddAsync("role", () => Load(failing), giveUp.Token);
        await giveUp.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => request);

        failing.SetException(failure);
    }

    private Task<string> Load(TaskCompletionSource<string> answer)
    {
        _loads++;
        return answer.Task;
    }
}
