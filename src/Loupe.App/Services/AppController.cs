using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Loupe.App.Bridge;
using Loupe.Core.Editing;
using Loupe.Core.Files;
using Loupe.Core.Imaging;

namespace Loupe.App.Services;

public sealed record FileFailure(string Path, string Error);

public sealed record EditItem(string Path, EditSettings Edit);

/// <summary>
/// The page's commands (see wwwroot/js/app.js). Long jobs (applying an edit to many files)
/// run on the thread pool and report progress with "job.progress" events.
/// </summary>
public sealed class AppController
{
    private readonly IWindowHost _host;
    private readonly BridgeHost _bridge;
    private readonly AppSettings _settings = AppSettings.Load();
    private CancellationTokenSource? _job;
    private readonly RescanList _rescan = new(AppSettings.RescanFile);

    public SmokeTest? Smoke { get; init; }
    public List<string> JsErrors { get; } = [];
    public Action<JsonElement>? OnSelfTestResult { get; set; }

    public AppSettings Settings => _settings;
    public ImageRenderer Renderer { get; private set; }
    public ImageEditor Editor { get; private set; }

    public AppController(IWindowHost host, BridgeHost bridge)
    {
        _host = host;
        _bridge = bridge;
        Loc.Load(_settings.Language);
        Renderer = new ImageRenderer(_settings.ThumbRoot);
        Editor = new ImageEditor(new BackupStore(_settings.BackupRoot));
        Register();
    }

    public bool ConfirmClose()
    {
        if (_job is null) return true;
        var r = MessageBox.Show(_host.Window, Loc.T("Files are still being saved. Stop and close Loupe?"), "Loupe",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return false;
        _job?.Cancel();
        return true;
    }

    public void Shutdown() => _settings.Save();

    private BackupStore Backups => Editor.Backups;

    private void Register()
    {
        // ---- app
        _bridge.On("i18n.get", _ => new { lang = Loc.Language, dict = Loc.Dictionary, theme = _settings.Theme });
        _bridge.On("app.setLanguage", args =>
        {
            Loc.Load(BridgeHost.Arg(args, "lang", "en"));
            _settings.Language = Loc.Language;
            _settings.Save();
            return null;
        });
        _bridge.On("i18n.openFolder", _ => { string f = Loc.PrepareUserFolder(); Shell.OpenFolder(f); return f; });
        _bridge.On("app.init", _ => new
        {
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
            turboJpeg = TurboJpeg.Status,
            toneMethods = Enumerable.Range(0, ToneMethod.Count).Select(ToneAdjust.MethodName).ToArray(),
            settings = _settings,
            backupRoot = _settings.BackupRoot,
            thumbRoot = _settings.ThumbRoot,
            undo = LastUndo()?.Title,
            smokeFolder = Smoke?.SampleFolder,
        });
        _bridge.On("app.theme", args =>
        {
            _settings.Theme = BridgeHost.Arg(args, "theme", _settings.Theme);
            string bg = BridgeHost.Arg(args, "background", "#0e0f12");
            System.Drawing.Color color;
            try { color = System.Drawing.ColorTranslator.FromHtml(bg); } catch (Exception) { color = System.Drawing.Color.FromArgb(14, 15, 18); }
            _host.ApplyTheme(BridgeHost.Arg(args, "dark", true), color);
            return null;
        });
        _bridge.On("app.jsError", args => { JsErrors.Add(BridgeHost.Arg(args, "message", "")); return null; });
        _bridge.On("app.selfTestResult", args => { OnSelfTestResult?.Invoke(args.Clone()); return null; });
        _bridge.On("settings.set", args =>
        {
            var s = _settings;
            s.ThumbSize = Math.Clamp(BridgeHost.Arg(args, "thumbSize", s.ThumbSize), 80, 520);
            s.GridColumns = Math.Clamp(BridgeHost.Arg(args, "gridColumns", s.GridColumns), 0, 12);
            s.ShowNames = BridgeHost.Arg(args, "showNames", s.ShowNames);
            string bg = BridgeHost.Arg(args, "viewBackground", s.ViewBackground);
            if (bg == "theme" || System.Text.RegularExpressions.Regex.IsMatch(bg, "^#[0-9a-fA-F]{6}$")) s.ViewBackground = bg;
            s.TreeWidth = Math.Clamp(BridgeHost.Arg(args, "treeWidth", s.TreeWidth), 160, 700);
            s.SaveQuality = Math.Clamp(BridgeHost.Arg(args, "saveQuality", s.SaveQuality), 0, 100);
            s.LosslessRotate = BridgeHost.Arg(args, "losslessRotate", s.LosslessRotate);
            s.Workers = Math.Clamp(BridgeHost.Arg(args, "workers", s.Workers), 1, 16);
            s.DeleteEmptyParts = BridgeHost.Arg(args, "deleteEmptyParts", s.DeleteEmptyParts);
            s.ConfirmDeleteParts = BridgeHost.Arg(args, "confirmDeleteParts", s.ConfirmDeleteParts);
            s.Toolbar = BridgeHost.Arg(args, "toolbar", s.Toolbar).Where(x => x.Length is > 0 and < 40).Take(80).ToList();
            s.Save();
            return null;
        });

        // Back to the defaults; pinned folders, language, last folder and the folders for
        // originals / thumbnails stay (moving those has its own rules).
        _bridge.On("settings.reset", _ =>
        {
            var fresh = new AppSettings
            {
                Language = _settings.Language, Favorites = _settings.Favorites, LastFolder = _settings.LastFolder,
                BackupFolder = _settings.BackupFolder, ThumbFolder = _settings.ThumbFolder,
            };
            foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(_settings, p.GetValue(fresh));
            _settings.Save();
            return _settings;
        });

        // ---- pop-out viewer (second monitor). The main page sends what to show; the pop-out
        // page sends its key presses back as actions. Both go through events to every page.
        _bridge.On("popup.open", _ => { _host.OpenPopup(); return null; });
        _bridge.On("popup.close", _ => { _host.ClosePopup(); return null; });
        _bridge.On("popup.isOpen", _ => _host.Popup is not null);
        _bridge.On("popup.update", args => { _bridge.Emit("popup.image", args.Clone()); return null; });
        _bridge.On("popup.themeSync", args => { _bridge.Emit("popup.theme", args.Clone()); return null; });
        _bridge.On("popup.action", args => { _bridge.Emit("popup.action", args.Clone()); return null; });
        _bridge.On("popup.fullscreen", args => _host.Popup?.SetFullscreen(BridgeHost.Arg<bool?>(args, "on", null)) ?? false);
        _bridge.On("popup.topmost", args =>
        {
            _settings.PopupTopmost = BridgeHost.Arg(args, "on", !_settings.PopupTopmost);
            if (_host.Popup is { } p) p.Topmost = _settings.PopupTopmost;
            return _settings.PopupTopmost;
        });
        _bridge.On("popup.ready", _ =>
        {
            _bridge.Emit("popup.wanted", null);   // the main page answers with popup.update
            return new { fullscreen = _host.Popup?.IsFullscreen ?? false, topmost = _settings.PopupTopmost, viewBackground = _settings.ViewBackground, theme = _settings.Theme };
        });

        // ---- browsing
        _bridge.On("fs.roots", _ => Roots());
        _bridge.OnAsync("fs.list", async args =>
        {
            string path = BridgeHost.Arg(args, "path", "");
            var listing = await Task.Run(() => FolderScanner.List(path));
            _settings.LastFolder = listing.Path;
            return new
            {
                path = listing.Path,
                parent = Path.GetDirectoryName(listing.Path),
                folders = listing.Folders,
                images = listing.Images.Select(i => new
                {
                    i.Name, i.Path, i.Size, modified = i.Modified.Ticks, edited = Backups.Has(i.Path), rescan = _rescan.Contains(i.Path),
                }),
                otherFiles = listing.OtherFiles,
                part = PartMerger.ParsePart(Path.GetFileName(listing.Path)) is not null,
            };
        });
        _bridge.OnAsync("fs.subfolders", async args =>
        {
            string path = BridgeHost.Arg(args, "path", "");
            return await Task.Run(() => FolderScanner.Subfolders(path));
        });
        _bridge.On("fs.favoriteAdd", args =>
        {
            string path = BridgeHost.Arg(args, "path", "").Trim();
            if (path.Length == 0) return Roots();
            if (!Directory.Exists(path)) throw new BridgeException(Loc.T("Folder not found: {path}", ("path", path)));
            path = Path.GetFullPath(path);
            if (!_settings.Favorites.Contains(path, StringComparer.OrdinalIgnoreCase)) _settings.Favorites.Add(path);
            _settings.Save();
            return Roots();
        });
        _bridge.On("fs.favoriteRemove", args =>
        {
            string path = BridgeHost.Arg(args, "path", "");
            _settings.Favorites.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            return Roots();
        });
        _bridge.On("fs.pickFolder", args =>
        {
            var dlg = new OpenFolderDialog { Title = BridgeHost.Arg(args, "title", Loc.T("Choose a folder")) };
            string start = BridgeHost.Arg(args, "start", _settings.LastFolder);
            if (Directory.Exists(start)) dlg.InitialDirectory = start;
            return dlg.ShowDialog(_host.Window) == true ? dlg.FolderName : null;
        });
        _bridge.On("fs.show", args =>
        {
            string path = BridgeHost.Arg(args, "path", "");
            if (Directory.Exists(path)) Shell.OpenFolder(path); else Shell.ShowFile(path);
            return null;
        });
        _bridge.OnAsync("img.info", async args =>
        {
            string path = BridgeHost.Arg(args, "path", "");
            return await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(path);
                var info = JpegMeta.Read(bytes);
                var fi = new FileInfo(path);
                return new
                {
                    path, name = fi.Name, info.Width, info.Height, info.IsGray, info.Quality, info.DpiX, info.DpiY,
                    size = fi.Length, modified = fi.LastWriteTimeUtc.Ticks, modifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    edited = Backups.Has(path),
                };
            });
        });

        // ---- editing
        _bridge.OnAsync("edit.apply", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            var edit = BridgeHost.Arg(args, "edit", new EditSettings());
            var save = new SaveOptions { Quality = _settings.SaveQuality, LosslessWhenPossible = _settings.LosslessRotate };
            return await RunJob(Loc.T("Saving"), paths, p => Editor.Apply(p, edit, save) is var r && r.Ok ? null : r.Error ?? "?");
        });
        // Global + own corrections from the page: every file with its own settings.
        _bridge.OnAsync("edit.applyMany", async args =>
        {
            var items = BridgeHost.Arg(args, "items", new List<EditItem>());
            var byPath = items.Where(i => i.Path.Length > 0).GroupBy(i => i.Path).ToDictionary(g => g.Key, g => g.Last().Edit);
            var save = new SaveOptions { Quality = _settings.SaveQuality, LosslessWhenPossible = _settings.LosslessRotate };
            return await RunJob(Loc.T("Saving"), byPath.Keys.ToList(),
                p => Editor.Apply(p, byPath[p], save) is var r && r.Ok ? null : r.Error ?? "?");
        });
        _bridge.OnAsync("edit.restore", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            return await RunJob(Loc.T("Restoring originals"), paths,
                p => Backups.Restore(p) ? null : Loc.T("No original is kept for this file."));
        });
        _bridge.On("job.cancel", _ => { _job?.Cancel(); return null; });

        // ---- rename
        _bridge.OnAsync("rename.plan", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            var rules = BridgeHost.Arg(args, "rules", new RenameRules());
            return await Task.Run(() => RenamePlanner.Plan(paths, rules));
        });
        _bridge.OnAsync("rename.run", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            var rules = BridgeHost.Arg(args, "rules", new RenameRules());
            _settings.RenameRules = rules;
            _settings.Save();
            return await Task.Run(() =>
            {
                var plan = RenamePlanner.Plan(paths, rules);
                if (!plan.CanRun) throw new BridgeException(plan.Error ?? Loc.T("The new names have problems; nothing was renamed."));
                var moves = RenamePlanner.Moves(plan);
                SafeMover.Run(moves, Backups);
                _rescan.Follow(moves);
                var log = new UndoLog { Kind = "rename", Title = Loc.T("Rename of {n} files", ("n", moves.Count)), Moves = moves };
                SaveUndo(log);
                return new { count = moves.Count, undo = log.Title, renamed = moves };
            });
        });

        // ---- merge parts
        _bridge.OnAsync("merge.plan", async args =>
        {
            var folders = BridgeHost.Arg(args, "folders", new List<string>());
            var plan = await Task.Run(() => PartMerger.Plan(folders));
            return MergeDto(plan);
        });
        _bridge.OnAsync("merge.run", async args =>
        {
            var folders = BridgeHost.Arg(args, "folders", new List<string>());
            bool deleteEmpty = BridgeHost.Arg(args, "deleteEmpty", _settings.DeleteEmptyParts);
            return await Task.Run(() =>
            {
                var plan = PartMerger.Plan(folders);
                if (!plan.CanRun) throw new BridgeException(plan.Error ?? string.Join(" ", plan.Problems));
                var log = PartMerger.Run(plan, deleteEmpty, Backups);
                _rescan.Follow(log.Moves);
                SaveUndo(log);
                return new { target = plan.Target, count = plan.Moves.Count, deleted = log.DeletedFolders, undo = log.Title };
            });
        });

        // ---- undo (last rename / merge)
        _bridge.On("undo.info", _ => new { title = LastUndo()?.Title });
        _bridge.OnAsync("undo.run", async _ =>
        {
            var (log, file) = LastUndoWithFile();
            if (log is null || file is null) throw new BridgeException(Loc.T("Nothing to undo."));
            await Task.Run(() => SafeMover.Undo(log, Backups));
            _rescan.Follow(log.Moves.Select(m => new MovePair(m.To, m.From)));
            File.Delete(file);
            return new { title = log.Title, next = LastUndo()?.Title, folder = log.Moves.Count > 0 ? Path.GetDirectoryName(log.Moves[0].From) : null };
        });

        // ---- slice (cut into pieces, renumber the folder)
        _bridge.OnAsync("slice.plan", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            var s = BridgeHost.Arg(args, "settings", new SliceSettings());
            return SliceDto(await Task.Run(() => Slicer.Plan(paths, s)));
        });
        _bridge.OnAsync("slice.run", async args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            var s = BridgeHost.Arg(args, "settings", new SliceSettings());
            if (_job is not null) throw new BridgeException(Loc.T("Another job is still running."));
            return await Task.Run(() =>
            {
                var plan = Slicer.Plan(paths, s);
                if (!plan.CanRun) throw new BridgeException(plan.Error ?? string.Join(" ", plan.Problems));
                string keep = Path.Combine(AppSettings.UndoRoot, $"slice-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
                var log = Slicer.Run(plan, s, keep, Backups, _settings.SaveQuality) with
                {
                    Title = Loc.T("Slice of {n} images", ("n", plan.Sources.Count)),
                };
                _rescan.Follow(log.Moves.Where(m => !Path.GetFileName(m.From).StartsWith(".loupe-"))
                    .Concat(plan.Sources.Select(src => new MovePair(src, plan.Outputs.First(o => o.Source == src).Target))));
                SaveUndo(log);
                return new { outputs = plan.Outputs.Count, undo = log.Title };
            });
        });

        // ---- rescan list (images beyond repair)
        _bridge.On("rescan.list", _ => RescanDto());
        _bridge.On("rescan.set", args =>
        {
            var paths = BridgeHost.Arg(args, "paths", new List<string>());
            _rescan.Set(paths, BridgeHost.Arg(args, "on", true), BridgeHost.Arg(args, "reason", ""));
            return RescanDto();
        });
        _bridge.On("rescan.update", args =>
        {
            _rescan.Update(BridgeHost.Arg(args, "path", ""), BridgeHost.Arg<string?>(args, "reason", null), BridgeHost.Arg<string?>(args, "microfilm", null));
            return RescanDto();
        });
        _bridge.On("rescan.clear", _ => { _rescan.Clear(); return RescanDto(); });
        _bridge.On("rescan.copy", args =>
        {
            bool headers = BridgeHost.Arg(args, "headers", _settings.RescanHeaders);
            _settings.RescanHeaders = headers;
            var rows = _rescan.Rows(RescanRoots());
            Clipboard.SetText(RescanList.ToTsv(rows, headers ? RescanHeader() : null));
            return rows.Count;
        });
        _bridge.On("rescan.saveCsv", _ =>
        {
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Save the rescan list"), Filter = Loc.T("CSV for Excel") + " (*.csv)|*.csv",
                FileName = $"rescan-{DateTime.Now:yyyy-MM-dd}.csv",
            };
            if (dlg.ShowDialog(_host.Window) != true) return null;
            File.WriteAllBytes(dlg.FileName, RescanList.ToCsv(_rescan.Rows(RescanRoots()), RescanHeader()));
            return dlg.FileName;
        });

        // ---- storage
        _bridge.OnAsync("storage.info", async _ => await Task.Run(() =>
        {
            var (bf, bb) = Backups.Usage();
            var (tf, tb) = Renderer.Usage();
            return new { backupRoot = Backups.Root, backupFiles = bf, backupBytes = bb, thumbRoot = Renderer.ThumbRoot, thumbFiles = tf, thumbBytes = tb };
        }));
        _bridge.OnAsync("storage.clearThumbs", async _ => { await Task.Run(Renderer.Clear); return null; });
        _bridge.On("storage.openBackups", _ => { Directory.CreateDirectory(Backups.Root); Shell.OpenFolder(Backups.Root); return null; });
        _bridge.On("storage.pickBackupFolder", _ =>
        {
            var dlg = new OpenFolderDialog { Title = Loc.T("Folder for the originals of edited files") };
            if (dlg.ShowDialog(_host.Window) != true) return null;
            if (Backups.Usage().Files > 0)
                throw new BridgeException(Loc.T("Originals are still kept in the current folder. Restore or clear them first, then change the folder."));
            _settings.BackupFolder = dlg.FolderName;
            _settings.Save();
            Editor = new ImageEditor(new BackupStore(_settings.BackupRoot));
            return _settings.BackupRoot;
        });
        _bridge.OnAsync("storage.clearBackups", async _ => { await Task.Run(Backups.Clear); return null; });
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<string> RescanRoots() =>
        _settings.Favorites.Concat(FolderScanner.Drives().Select(d => d.Path));

    private static string[] RescanHeader() =>
        [Loc.T("Microfilm number"), Loc.T("Folder"), Loc.T("File"), Loc.T("Reason"), Loc.T("Marked"), Loc.T("Full path")];

    private object RescanDto()
    {
        var roots = RescanRoots().ToList();
        return new
        {
            headers = _settings.RescanHeaders,
            entries = _rescan.Entries
                .OrderBy(e => Path.GetDirectoryName(e.Path), NaturalComparer.Instance).ThenBy(e => Path.GetFileName(e.Path), NaturalComparer.Instance)
                .Select(e => new
                {
                    e.Path, e.Reason, e.Microfilm, marked = e.Marked.ToString("yyyy-MM-dd HH:mm"),
                    folder = RescanList.RelativeFolder(e.Path, roots), file = Path.GetFileName(e.Path), exists = File.Exists(e.Path),
                }),
        };
    }

    private object Roots() => new
    {
        drives = FolderScanner.Drives(),
        favorites = _settings.Favorites.Select(f => new { name = Path.GetFileName(f.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : f, path = f, exists = Directory.Exists(f) }),
    };

    private static object SliceDto(SlicePlan p)
    {
        if (p.Error is not null) return new { error = p.Error, canRun = false };
        var rows = new List<object>();
        var pieces = p.Outputs.Select(o => new { from = Path.GetFileName(o.Source) + $" [{o.Part + 1}]", to = Path.GetFileName(o.Target), piece = true });
        var renamed = p.Renames.Select(m => new { from = Path.GetFileName(m.From), to = Path.GetFileName(m.To), piece = false });
        var all = pieces.Concat(renamed).OrderBy(r => r.to, NaturalComparer.Instance).ToList();
        foreach (var r in all.Count <= 14 ? all : all.Take(10).Append(new { from = "…", to = "…", piece = false }).Concat(all.TakeLast(3))) rows.Add(r);
        return new
        {
            error = (string?)null, canRun = p.CanRun, p.Problems, sources = p.Sources.Count, outputs = p.Outputs.Count,
            renames = p.Renames.Count, files = p.Files, after = p.Files + p.Outputs.Count - p.Sources.Count, sample = rows,
        };
    }

    private static object MergeDto(MergePlan plan) => new
    {
        plan.Target, plan.BaseName, plan.Parts, plan.Problems, plan.Error, plan.CanRun,
        count = plan.Moves.Count,
        sample = plan.Moves.Count == 0 ? [] : SampleMoves(plan.Moves),
    };

    /// <summary>The first and last files of each part (the whole list can be thousands of rows).</summary>
    private static List<object> SampleMoves(List<MovePair> moves)
    {
        var rows = new List<object>();
        foreach (var g in moves.GroupBy(m => Path.GetDirectoryName(m.From)))
        {
            var list = g.ToList();
            var pick = list.Count <= 4 ? list : [list[0], list[1], list[^2], list[^1]];
            for (int i = 0; i < pick.Count; i++)
            {
                if (list.Count > 4 && i == 2) rows.Add(new { from = "…", to = "…", folder = Path.GetFileName(g.Key) });
                rows.Add(new { from = Path.GetFileName(pick[i].From), to = Path.GetFileName(pick[i].To), folder = Path.GetFileName(g.Key) });
            }
        }
        return rows;
    }

    private void SaveUndo(UndoLog log) =>
        log.Save(Path.Combine(AppSettings.UndoRoot, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{log.Kind}.json"));

    private static (UndoLog? Log, string? File) LastUndoWithFile()
    {
        if (!Directory.Exists(AppSettings.UndoRoot)) return (null, null);
        foreach (var f in Directory.GetFiles(AppSettings.UndoRoot, "*.json").OrderByDescending(x => x, StringComparer.Ordinal))
            if (UndoLog.Load(f) is { } log) return (log, f);
        return (null, null);
    }

    private static UndoLog? LastUndo() => LastUndoWithFile().Log;

    /// <summary>
    /// Runs <paramref name="work"/> over the files on a few workers (I/O-bound on external disks
    /// and shares). A worker returns null on success or an error message.
    /// </summary>
    private async Task<object> RunJob(string title, List<string> paths, Func<string, string?> work)
    {
        if (_job is not null) throw new BridgeException(Loc.T("Another job is still running."));
        var cts = new CancellationTokenSource();
        _job = cts;
        var failures = new List<FileFailure>();
        int done = 0, total = paths.Count;
        var started = DateTime.UtcNow;
        _bridge.Emit("job.progress", new { title, done, total, running = true });
        try
        {
            await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = _settings.Workers, CancellationToken = cts.Token },
                (p, _) =>
                {
                    string? error;
                    try { error = work(p); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TurboJpegException) { error = ex.Message; }
                    if (error is not null) lock (failures) failures.Add(new FileFailure(p, error));
                    int d = Interlocked.Increment(ref done);
                    _bridge.Emit("job.progress", new { title, done = d, total, running = true, current = Path.GetFileName(p) });
                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _job = null;
            _bridge.Emit("job.progress", new { title, done, total, running = false });
        }
        return new { done, total, cancelled = cts.IsCancellationRequested, failures, seconds = (DateTime.UtcNow - started).TotalSeconds };
    }
}
