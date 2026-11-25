using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 收藏顺序存储全量用例（vc53 FavoriteStore 口径一比一）：
/// 移位、按屏挪、置顶、序列化恢复（坏段忽略）、取消复原、未入册兜底铺开。
/// </summary>
public class FavoriteStoreTests
{
    private static Channel MakeChannel(int number, string id) =>
        new() { Number = number, Name = $"频道-{id}", SourceId = id, SourceEndpoints = { new SourceEndpoint($"https://s/{id}", SourceKind.PureIp, 8) } };

    [Fact]
    public void Toggle_adds_in_order_and_remove_keeps_others()
    {
        var fav = new FavoriteStore();
        fav.Toggle(3); fav.Toggle(1); fav.Toggle(2);
        Assert.Equal(new[] { 3, 1, 2 }, fav.Numbers);
        fav.Toggle(1);
        Assert.Equal(new[] { 3, 2 }, fav.Numbers);
    }

    [Fact]
    public void MoveUp_and_MoveDown_swap_adjacent_only()
    {
        var fav = new FavoriteStore();
        fav.Restore("1,2,3");
        fav.MoveUp(1); // 已在顶：不动
        Assert.Equal(new[] { 1, 2, 3 }, fav.Numbers);
        fav.MoveUp(2);
        Assert.Equal(new[] { 2, 1, 3 }, fav.Numbers);
        fav.MoveDown(2); // 顶部往下移一位：回原位
        Assert.Equal(new[] { 1, 2, 3 }, fav.Numbers);
        fav.MoveDown(3); // 已在底：不动
        Assert.Equal(new[] { 1, 2, 3 }, fav.Numbers);
        fav.MoveDown(1); // 顶部再往下移一位：与后一位换
        Assert.Equal(new[] { 2, 1, 3 }, fav.Numbers);
    }

    [Fact]
    public void MoveBy_clamps_to_bounds()
    {
        var fav = new FavoriteStore();
        fav.Restore("1,2,3,4");
        fav.MoveBy(1, 10); // 越界收口到底
        Assert.Equal(new[] { 2, 3, 4, 1 }, fav.Numbers);
        fav.MoveBy(1, -99); // 再拉回顶
        Assert.Equal(new[] { 1, 2, 3, 4 }, fav.Numbers);
        fav.MoveBy(9, 5); // 不在册：无操作
        Assert.Equal(new[] { 1, 2, 3, 4 }, fav.Numbers);
    }

    [Fact]
    public void MoveToTop_moves_first_and_ignores_unknown()
    {
        var fav = new FavoriteStore();
        fav.Restore("1,2,3");
        fav.MoveToTop(3);
        Assert.Equal(new[] { 3, 1, 2 }, fav.Numbers);
        fav.MoveToTop(9); // 不在册：无操作
        Assert.Equal(new[] { 3, 1, 2 }, fav.Numbers);
    }

    [Fact]
    public void Serialize_restore_roundtrip_ignores_dirty_parts_and_duplicates()
    {
        var fav = new FavoriteStore();
        fav.Restore("1,2,3");
        var state = fav.Serialize();
        var fresh = new FavoriteStore();
        fresh.Restore(state);
        Assert.Equal(new[] { 1, 2, 3 }, fresh.Numbers);
        // 坏段忽略、重复段只留一份、空串=清空（容错优先，不因脏数据崩溃）
        fresh.Restore("5,abc, 5 ,x,2");
        Assert.Equal(new[] { 5, 2 }, fresh.Numbers);
        fresh.Restore("  ");
        Assert.Empty(fresh.Numbers);
        fresh.Restore(null);
        Assert.Empty(fresh.Numbers);
    }

    [Fact]
    public void Restore_snapshot_recovers_order_for_cancel()
    {
        var fav = new FavoriteStore();
        fav.Restore("1,2,3");
        var backup = fav.Numbers.ToList(); // 进入排序态前的复原快照（vc53 _sortBackup 口径）
        fav.MoveToTop(3);
        fav.MoveBy(1, 2);
        fav.Restore(string.Join(",", backup)); // 取消=复原
        Assert.Equal(new[] { 1, 2, 3 }, fav.Numbers);
    }

    [Fact]
    public void ListTabs_favorite_tab_spreads_in_order_then_unregistered_by_table()
    {
        var ch1 = MakeChannel(1, "c1");
        var ch2 = MakeChannel(2, "c2");
        var ch3 = MakeChannel(3, "c3");
        var channels = new List<Channel> { ch1, ch2, ch3 };
        bool IsFav(Channel c) => c.Number is 1 or 2 or 3;
        // 顺序 3,1：3 在前，1 次之，2 未入册按频道表序缀后
        var rows = ListTabs.Filter(ListTabs.Tab.Favorite, channels, IsFav, new List<int> { 3, 1 });
        Assert.Equal(new[] { 3, 1, 2 }, rows.Select(c => c.Number));
        // 无顺序账：全部按频道表序
        var plain = ListTabs.Filter(ListTabs.Tab.Favorite, channels, IsFav, null);
        Assert.Equal(new[] { 1, 2, 3 }, plain.Select(c => c.Number));
    }
}
