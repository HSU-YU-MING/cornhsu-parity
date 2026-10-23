namespace Parity.Tests;

/// <summary>
/// 登入序列化用的 KeyedGate(Parity.Server)。驗證者的 M21d 把 Leave 改成「從不回收 slot」,原有測試照樣綠——
/// 端點測試只看得到互斥,看不到 slot 越積越多(每個嘗試過的 email 一個 SemaphoreSlim,永不釋放)。
/// 這裡把驗證者的並發探針收成正式測試。
/// </summary>
public class KeyedGateTests
{
    [Fact]
    public async Task A_concurrent_storm_with_cancellations_never_double_holds_a_key_and_leaves_no_slot_behind()
    {
        var gate = new KeyedGate();
        const int keys = 200, entries = 4000;
        var holders = new int[keys];
        var maxHolders = new int[keys];
        var acquired = 0;
        var canceled = 0;

        async Task OneAsync(int i)
        {
            var key = i % keys;
            using var cts = new CancellationTokenSource();
            if (i % 7 == 0) cts.CancelAfter(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3))); // 1/7 在等待中途取消
            IDisposable lease;
            try
            {
                lease = await gate.EnterAsync($"KEY-{key}", cts.Token);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref canceled);
                return;
            }
            using (lease)
            {
                var now = Interlocked.Increment(ref holders[key]);
                InterlockedMax(ref maxHolders[key], now);
                Interlocked.Increment(ref acquired);
                await Task.Yield();
                if (i % 3 == 0) await Task.Delay(1);
                Interlocked.Decrement(ref holders[key]);
            }
        }

        var all = Task.WhenAll(Enumerable.Range(0, entries).Select(i => Task.Run(() => OneAsync(i))));
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(finished == all, "KeyedGate 卡住了(60 秒內沒跑完)");
        await all;

        Assert.All(maxHolders, m => Assert.True(m <= 1, "同一個 key 同時被兩個人持有"));
        Assert.Equal(entries, acquired + canceled);
        Assert.True(acquired > entries / 2, $"取得的太少({acquired}),測試沒有真的跑到並發");
        Assert.Equal(0, gate.Count); // 全部放掉之後,一個 slot 都不留
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
