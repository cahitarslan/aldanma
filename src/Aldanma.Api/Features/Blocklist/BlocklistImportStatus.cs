namespace Aldanma.Api.Features.Blocklist;

/// <summary>What the public status shows about the block list download. No internals: no URLs, hosts or exception text.</summary>
public sealed record BlocklistImportSnapshot(
    string State,
    string? Mode,
    int PagesDone,
    int PagesTotal,
    int ItemsSoFar,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? SecondsRemaining,
    string? LastError);

/// <summary>
/// Progress of the running (or last) block list import, kept in memory so /api/meta can show it without a query.
/// Written by one background worker, read by any request.
/// </summary>
public sealed class BlocklistImportStatus(TimeProvider time)
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Full = "full";
    public const string Incremental = "incremental";

    private readonly Lock _gate = new();
    private string _state = Idle;
    private string? _mode;
    private int _pagesDone, _pagesTotal, _items, _resumedPages;
    private DateTimeOffset? _started, _finished;
    private string? _error;

    public void Start(string mode)
    {
        lock (_gate)
        {
            (_state, _mode, _pagesDone, _pagesTotal, _items, _resumedPages, _error) = (Running, mode, 0, 0, 0, 0, null);
            _started = time.GetUtcNow();
            _finished = null;
        }
    }

    /// <summary>Pages become known one list type at a time, so the total can grow during a run.</summary>
    /// <param name="alreadyDone">Pages of this type a resumed run read before it stopped.</param>
    public void AddPages(int pages, int alreadyDone = 0)
    {
        lock (_gate)
        {
            _pagesTotal += pages;
            _pagesDone += alreadyDone;
            _resumedPages += alreadyDone;
        }
    }

    public void PageDone(int items)
    {
        lock (_gate)
        {
            _pagesDone++;
            _items += items;
        }
    }

    public void Succeed()
    {
        lock (_gate)
        {
            _state = Idle;
            _finished = time.GetUtcNow();
        }
    }

    /// <param name="publicReason">A short, safe description, e.g. "download failed".</param>
    public void Fail(string publicReason)
    {
        lock (_gate)
        {
            _state = Failed;
            _error = publicReason;
            _finished = time.GetUtcNow();
        }
    }

    public BlocklistImportSnapshot Snapshot()
    {
        lock (_gate)
        {
            int? remaining = null;
            if (_state == Running && _started is { } s && _pagesDone > _resumedPages && _pagesTotal > _pagesDone)
            {
                // Pages a resumed run had read before do not count towards the speed of this one.
                var readNow = _pagesDone - _resumedPages;
                var perPage = readNow > 0 ? (time.GetUtcNow() - s).TotalSeconds / readNow : 0;
                remaining = (int)Math.Ceiling(perPage * (_pagesTotal - _pagesDone));
            }
            return new BlocklistImportSnapshot(_state, _mode, _pagesDone, _pagesTotal, _items, _started, _finished, remaining, _error);
        }
    }
}
