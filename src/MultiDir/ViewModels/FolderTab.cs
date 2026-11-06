using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using MultiDir.Models;
using MultiDir.Services;

namespace MultiDir.ViewModels;

/// <summary>One tab = one folder location with its own back/forward history.</summary>
public sealed class FolderTab : INotifyPropertyChanged
{
    /// <summary>Virtual path for the drive list ("本機").</summary>
    public const string ThisPc = "";

    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private CancellationTokenSource? _loadCts;
    private string _path = ThisPc;
    private ViewMode _viewMode = ViewMode.List;
    private string _filter = "";
    private string _status = "";

    public FolderTab(string path)
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => _filter.Length == 0 || Matches(((FileItem)o).Name, _filter);
        Navigate(path, addHistory: false);
    }

    public FileItemCollection Items { get; } = new();
    public ICollectionView View { get; }

    public string Path => _path;

    public string Title => _path == ThisPc ? "本機"
        : System.IO.Path.GetFileName(_path.TrimEnd('\\')) is { Length: > 0 } name ? name
        : _path.TrimEnd('\\');

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => _path != ThisPc;

    public SortKey SortKey => _sortKey;
    public bool SortDescending => _sortDescending;
    private SortKey _sortKey;
    private bool _sortDescending;

    /// <summary>
    /// Column header click: the same column flips the direction, a new column starts ascending
    /// (date and size start newest / largest first, like Explorer). Re-sorts in place; remembered per folder.
    /// </summary>
    public void SortBy(SortKey key)
    {
        bool desc = key == _sortKey ? !_sortDescending : key is SortKey.Modified or SortKey.Size;
        (_sortKey, _sortDescending) = (key, desc);
        SortStore.Set(_path, key, desc);
        var sorted = Items.ToList();
        sorted.Sort(Compare);
        Items.ReplaceAll(sorted);
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDescending));
    }

    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!Set(ref _viewMode, value)) return;
            ViewModeStore.Set(_path, value);
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? "")) return;
            View.Refresh();
            UpdateStatus();
        }
    }

    public event EventHandler? Navigated;

    /// <summary>Breadcrumb segments: (label, full path).</summary>
    public IReadOnlyList<(string Label, string Path)> Segments
    {
        get
        {
            var list = new List<(string, string)> { ("本機", ThisPc) };
            if (_path == ThisPc) return list;
            var root = System.IO.Path.GetPathRoot(_path)!;
            list.Add((root.TrimEnd('\\'), root));
            var rest = _path.Substring(root.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var acc = root;
            foreach (var part in rest)
            {
                acc = System.IO.Path.Combine(acc, part);
                list.Add((part, acc));
            }
            return list;
        }
    }

    public void Navigate(string path, bool addHistory = true)
    {
        path = NormalizePath(path);
        if (path != ThisPc && !Directory.Exists(path)) return;
        if (addHistory && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _back.Push(_path);
            _forward.Clear();
        }
        SetLocation(path);
    }

    /// <summary>Back / forward history, most recent first (session save).</summary>
    public string[] BackHistory => _back.ToArray();
    public string[] ForwardHistory => _forward.ToArray();

    /// <summary>Puts back a saved history (most recent first), e.g. when a session is restored.</summary>
    public void RestoreHistory(IEnumerable<string>? back, IEnumerable<string>? forward)
    {
        _back.Clear();
        _forward.Clear();
        foreach (var p in (back ?? []).Reverse()) _back.Push(p);
        foreach (var p in (forward ?? []).Reverse()) _forward.Push(p);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(_path);
        SetLocation(_back.Pop());
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(_path);
        SetLocation(_forward.Pop());
    }

    public void GoUp()
    {
        if (_path == ThisPc) return;
        var parent = Directory.GetParent(_path.TrimEnd('\\'));
        Navigate(parent?.FullName ?? ThisPc);
    }

    public void Refresh() => SetLocation(_path);

    private void SetLocation(string path)
    {
        _path = path;
        _filter = "";
        _viewMode = ViewModeStore.Get(path);
        (_sortKey, _sortDescending) = SortStore.Get(path);
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Segments));
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        Navigated?.Invoke(this, EventArgs.Empty);
        StartWatching(path);
        _ = LoadAsync(path);
    }

    /// <summary>
    /// Enumerates on a worker thread, then streams items in: the first batch appears immediately,
    /// the rest is added in chunks at background priority so the UI never freezes on huge folders.
    /// </summary>
    private async Task LoadAsync(string path)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;
        Items.Clear();
        IsLoaded = false;
        Status = "讀取中…";
        _loading = true;
        try { await LoadCoreAsync(path, cts, token); }
        finally { if (_loadCts == cts) _loading = false; }
    }

    private bool _loading;

    private async Task LoadCoreAsync(string path, CancellationTokenSource cts, CancellationToken token)
    {

        List<FileItem> entries;
        try
        {
            Comparison<FileItem> compare = Compare; // captures the current sort
            entries = await Task.Run(() => Enumerate(path, compare, token), token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Status = "無法讀取：" + ex.Message;
            return;
        }
        if (token.IsCancellationRequested) return;

        const int firstBatch = 200, chunk = 1000;
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == firstBatch || (i > firstBatch && (i - firstBatch) % chunk == 0))
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (token.IsCancellationRequested) return;
            }
            Items.Add(entries[i]);
        }
        UpdateStatus();
        IsLoaded = true;
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The current folder is fully listed (raised once per load; e.g. to select a file after /select).</summary>
    public event EventHandler? Loaded;
    public bool IsLoaded { get; private set; }

    private static List<FileItem> Enumerate(string path, Comparison<FileItem> compare, CancellationToken token)
    {
        var result = new List<FileItem>();
        if (path == ThisPc)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string label = d.Name.TrimEnd('\\');
                try { if (d.IsReady && d.VolumeLabel.Length > 0) label = $"{d.VolumeLabel} ({label})"; } catch { }
                result.Add(new FileItem { Name = label, FullPath = d.Name, IsDirectory = true, IsDrive = true });
            }
            return result;
        }

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", EnumOptions))
        {
            token.ThrowIfCancellationRequested();
            if ((info.Attributes & ProtectedOs) == ProtectedOs) continue;
            result.Add(FromInfo(info));
        }
        // Look up each new file type's name here, off the UI thread (a registry walk per extension; dozens of them
        // in e.g. C:\Windows made the first frame of a new tab noticeably late).
        foreach (var ext in result.Where(i => !i.IsDirectory).Select(i => i.Extension).Distinct(StringComparer.OrdinalIgnoreCase))
            ShellIcons.GetTypeName(ext);
        result.Sort(compare);
        return result;
    }

    // Show hidden items (.git, .vs, ...) and System-only folders (e.g. a Nextcloud sync root),
    // but keep Explorer's "protected OS files" (Hidden AND System: $RECYCLE.BIN, desktop.ini) out.
    private const FileAttributes ProtectedOs = FileAttributes.System | FileAttributes.Hidden;
    private static readonly EnumerationOptions EnumOptions = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    private static FileItem FromInfo(FileSystemInfo info)
    {
        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
        return new FileItem
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = isDir,
            Modified = info.LastWriteTime,
            Size = isDir ? null : ((FileInfo)info).Length,
        };
    }

    /// <summary>Folders always first; then the chosen column (ties by natural name order, like Explorer).</summary>
    private int Compare(FileItem a, FileItem b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        int c = _sortKey switch
        {
            SortKey.Modified => Nullable.Compare(a.Modified, b.Modified),
            SortKey.Size => Nullable.Compare(a.Size, b.Size),
            SortKey.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (c == 0) c = StrCmpLogicalW(a.Name, b.Name);
        return _sortDescending ? -c : c;
    }

    // ---------- auto refresh (no F5 needed) ----------

    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pendingNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingFullReload;
    private DispatcherTimer? _flushTimer;

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_pendingNames) { _pendingNames.Clear(); _pendingFullReload = false; }
        if (path == ThisPc) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Queue(e.Name);
            w.Deleted += (_, e) => Queue(e.Name);
            w.Changed += (_, e) => Queue(e.Name);
            w.Renamed += (_, e) => { Queue(e.OldName); Queue(e.Name); };
            w.Error += (_, _) => { lock (_pendingNames) _pendingFullReload = true; ScheduleFlush(); };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* e.g. network share without notifications: manual F5 still works */ }
    }

    private void Queue(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_pendingNames) _pendingNames.Add(name);
        ScheduleFlush();
    }

    /// <summary>Coalesces bursts (e.g. a 1,000-file copy) into one UI update every 300 ms.</summary>
    private void ScheduleFlush()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _flushTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Flush(),
                Application.Current.Dispatcher);
            if (!_flushTimer.IsEnabled) _flushTimer.Start();
        });
    }

    private void Flush()
    {
        _flushTimer?.Stop();
        string[] names;
        bool full;
        lock (_pendingNames)
        {
            names = _pendingNames.ToArray();
            full = _pendingFullReload;
            _pendingNames.Clear();
            _pendingFullReload = false;
        }
        if (_loading)
        {
            // Listing still streaming in: changes will be picked up by it or by the next flush.
            lock (_pendingNames) foreach (var n in names) _pendingNames.Add(n);
            _flushTimer?.Start();
            return;
        }
        if (full || names.Length > 2000) { _ = LoadAsync(_path); return; }

        foreach (var name in names)
        {
            var full_path = System.IO.Path.Combine(_path, name);
            int index = IndexOf(name);
            if (index >= 0) Items.RemoveAt(index);

            FileSystemInfo info = Directory.Exists(full_path) ? new DirectoryInfo(full_path) : new FileInfo(full_path);
            if (!info.Exists || (info.Attributes & ProtectedOs) == ProtectedOs) continue;
            var item = FromInfo(info);
            Items.Insert(InsertionIndex(item), item);
        }
        UpdateStatus();
    }

    private int IndexOf(string name)
    {
        for (int i = 0; i < Items.Count; i++)
            if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private int InsertionIndex(FileItem item)
    {
        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Compare(Items[mid], item) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private void UpdateStatus()
    {
        int total = Items.Count;
        if (_filter.Length == 0) { Status = $"{total} 個項目"; return; }
        int shown = View.Cast<object>().Count();
        Status = $"篩選「{_filter}」：{shown} / {total} 個項目";
    }

    /// <summary>Substring match, or wildcard match when the filter contains * or ?.</summary>
    private static bool Matches(string name, string filter)
    {
        if (filter.IndexOfAny(['*', '?']) < 0)
            return name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        return FileSystemName.MatchesSimpleExpression(filter, name, ignoreCase: true);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ThisPc;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            path = System.IO.Path.GetFullPath(path);
        }
        catch { return path; }
        // Keep "C:\" for roots, drop the trailing slash elsewhere.
        return System.IO.Path.GetPathRoot(path) == path ? path : path.TrimEnd('\\');
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>ObservableCollection that can swap its whole contents with a single Reset (fast re-sort of big folders).</summary>
public sealed class FileItemCollection : ObservableCollection<FileItem>
{
    public void ReplaceAll(IEnumerable<FileItem> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
