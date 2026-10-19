namespace FrameTrail.App;

// Static augmented tree: queries visit only intersecting branches, including
// long overlapping intervals that a start-time binary search alone would miss.
internal sealed class TimelineIntervalIndex<T>
{
    private readonly (long Start, long End, T Value)[] items;
    private readonly long[] maximum;
    internal TimelineIntervalIndex(IEnumerable<T> values, Func<T,long> start, Func<T,long> end)
    {
        items = values.Select(v => (start(v), end(v), v)).OrderBy(v => v.Item1).ToArray();
        maximum = new long[Math.Max(1, items.Length * 4)];
        if (items.Length > 0) Build(1, 0, items.Length);
    }
    private long Build(int node, int lo, int hi) => maximum[node] = hi-lo == 1 ? items[lo].End :
        Math.Max(Build(node*2,lo,(lo+hi)/2),Build(node*2+1,(lo+hi)/2,hi));
    internal IEnumerable<T> Query(long start, long end) => Query(1,0,items.Length,start,end);
    private IEnumerable<T> Query(int node,int lo,int hi,long start,long end)
    {
        if (lo >= hi || maximum[node] < start || items[lo].Start > end) yield break;
        if (hi-lo == 1) { yield return items[lo].Value; yield break; }
        foreach(var v in Query(node*2,lo,(lo+hi)/2,start,end))yield return v;
        foreach(var v in Query(node*2+1,(lo+hi)/2,hi,start,end))yield return v;
    }
}
