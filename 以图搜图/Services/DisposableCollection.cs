using System.Collections;

namespace 以图搜图.Services;

/// <summary>
/// 可释放的集合：在 Dispose 时释放所有元素。用于集中管理多个 IDisposable 资源的生命周期。
/// </summary>
internal sealed class DisposableCollection<T>(IEnumerable<T> items) : IEnumerable<T>, IDisposable where T : IDisposable
{
    public List<T> Items { get; } = items.ToList();

    public void Dispose()
    {
        Exception? first = null;
        foreach (var item in Items)
        {
            try { item.Dispose(); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first != null)
        {
            System.Diagnostics.Debug.WriteLine($"DisposableCollection 部分元素释放失败: {first.Message}");
        }
    }

    /// <summary>Returns an enumerator that iterates through the collection.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        return Items.GetEnumerator();
    }

    /// <summary>Returns an enumerator that iterates through a collection.</summary>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
