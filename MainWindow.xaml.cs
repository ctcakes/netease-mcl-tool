using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MclLauncher;

public sealed partial class MainWindow : Window
{
    private const string LauncherHash = "de08144dddf4be9dda3e751922873e54";
    private const string CoreHash = "e59579cba0169dfcd453237d065f3c76";
    private const string DownloadUrl = "https://adl.netease.com/d/g/mc/c/pe?type=windows";
    private readonly string _launcherCacheFile = Path.Combine(AppContext.BaseDirectory, "launcher-paths.json");
    private string? _launcherPath;
    private Process? _injector;
    private Process? _launcher;
    private CancellationTokenSource? _monitorCts;
    private bool _injectorExitLogged;
    private readonly string _runtimeDir = Path.Combine(Path.GetTempPath(), "MclLauncher", "runtime");

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _monitorCts?.Cancel();
        Log("程序已启动，管理员权限已启用。\n");
        DispatcherQueue.TryEnqueue(async () => await RestoreSavedLaunchersAsync());
    }

    private void Log(string message)
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        LogScrollViewer.UpdateLayout();
        LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
    }

    private void LogSafe(string message)
    {
        if (!DispatcherQueue.HasThreadAccess) DispatcherQueue.TryEnqueue(() => Log(message));
        else Log(message);
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try
        {
            Log("开始并行扫描本机固定磁盘中的 WPFLauncher.exe…");
            var candidates = await Task.Run(FindLaunchersAsync);
            if (candidates.Count == 0)
            {
                LauncherPathText.Text = "未找到匹配版本";
                LauncherStatus.Text = "未找到可用启动器";
                PatchButton.IsEnabled = false;
                Log("扫描结束：没有 MD5 匹配的 WPFLauncher.exe。");
                return;
            }
            var selected = candidates[0];
            SaveLauncherPaths(candidates);
            _launcherPath = selected;
            LauncherPathText.Text = selected;
            LauncherStatus.Text = $"可用 · 找到 {candidates.Count} 个匹配项";
            Log($"已选择匹配启动器：{selected}");
            RefreshCoreState();
        }
        catch (Exception ex) { Log($"扫描失败：{ex.Message}"); }
        finally { ScanButton.IsEnabled = true; }
    }

    private async Task<List<string>> FindLaunchersAsync()
    {
        var everything = await FindWithEverythingAsync();
        if (everything is not null)
        {
            LogSafe($"Everything 返回 {everything.Count} 个候选文件，正在校验 MD5…");
            var indexedMatches = everything.Where(File.Exists).Where(file => { try { return HashFile(file) == LauncherHash; } catch { return false; } }).ToList();
            if (indexedMatches.Count > 0) return indexedMatches.OrderBy(x => x.Length).ToList();
            LogSafe("Everything 索引中没有匹配版本，回退到并行目录扫描。");
        }
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToArray();
        var files = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(roots.Select(root => Task.Run(() => EnumerateFast(root, files))));
        var matches = new List<string>();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (HashFile(file) == LauncherHash) matches.Add(file); }
            catch { }
        }
        return matches.OrderBy(x => x.Length).ToList();
    }

    private async Task<List<string>?> FindWithEverythingAsync()
    {
        var exe = FindEsExecutable();
        if (exe is null) return null;
        var export = Path.Combine(Path.GetTempPath(), $"mcl-everything-{Guid.NewGuid():N}.csv");
        try
        {
            var psi = new ProcessStartInfo(exe, $"-export-csv \"{export}\" WPFLauncher.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!, RedirectStandardError = true };
            using var process = Process.Start(psi);
            if (process is null) return null;
            await Task.Run(() => process.WaitForExit(6000));
            if (!process.HasExited || process.ExitCode != 0) return null;
            if (!File.Exists(export)) return null;
            var rows = File.ReadAllLines(export, Encoding.UTF8);
            if (rows.Length < 2) return new List<string>();
            var header = ParseCsvLine(rows[0]);
            var nameIndex = Array.FindIndex(header, x => x.Equals("Filename", StringComparison.OrdinalIgnoreCase) || x.Equals("Name", StringComparison.OrdinalIgnoreCase));
            var pathIndex = Array.FindIndex(header, x => x.Equals("Path", StringComparison.OrdinalIgnoreCase));
            if (pathIndex < 0 && nameIndex < 0) return null;
            var files = new List<string>();
            foreach (var row in rows.Skip(1))
            {
                var fields = ParseCsvLine(row);
                var path = pathIndex >= 0 && fields.Length > pathIndex ? fields[pathIndex] : (nameIndex >= 0 && fields.Length > nameIndex ? fields[nameIndex] : "");
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!Path.GetFileName(path).Equals("WPFLauncher.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(path)) files.Add(Path.Combine(path, "WPFLauncher.exe"));
                else if (File.Exists(path) && Path.GetFileName(path).Equals("WPFLauncher.exe", StringComparison.OrdinalIgnoreCase)) files.Add(path);
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) { LogSafe($"Everything 查询失败，回退文件扫描：{ex.Message}"); return null; }
        finally { try { if (File.Exists(export)) File.Delete(export); } catch { } }
    }

    private string? FindEsExecutable()
    {
        var candidates = new List<string>();
        candidates.AddRange(new[] { Path.Combine(AppContext.BaseDirectory, "es.exe"), @"D:\Everything-1.4.1.1032.x86\es.exe", @"D:\Everything\es.exe", @"C:\Program Files\Everything\es.exe", @"C:\Program Files (x86)\Everything\es.exe" });
        var installed = candidates.FirstOrDefault(File.Exists);
        if (installed is not null) return installed;
        var embedded = Path.Combine(_runtimeDir, "es.exe");
        try { ExtractResource("MclLauncher.Resources.es.exe", embedded); return embedded; } catch { return null; }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        fields.Add(value.ToString()); return fields.ToArray();
    }

    private static void EnumerateFast(string root, System.Collections.Concurrent.ConcurrentBag<string> output)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "WPFLauncher.exe", SearchOption.TopDirectoryOnly)) output.Add(file);
                foreach (var child in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(child);
                    if (name is "$Recycle.Bin" or "System Volume Information" or "WindowsApps") continue;
                    pending.Push(child);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr ReservedPtr;
        public int Reserved;
        public int FlagsEx;
    }

    private string? PickLauncherPath()
    {
        const int bufferChars = 32768;
        var fileBuffer = Marshal.AllocHGlobal(bufferChars * sizeof(char));
        var filter = Marshal.StringToCoTaskMemUni("WPFLauncher.exe (*.exe)\0*.exe\0所有文件 (*.*)\0*.*\0\0");
        var title = Marshal.StringToCoTaskMemUni("选择 WPFLauncher.exe");
        try
        {
            Marshal.WriteByte(fileBuffer, 0, 0);
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = WindowNative.GetWindowHandle(this),
                Filter = filter,
                FilterIndex = 1,
                File = fileBuffer,
                MaxFile = bufferChars,
                Title = title,
                Flags = 0x00001000 | 0x00000800 | 0x00080000 | 0x00000008
            };
            return GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(filter); Marshal.FreeCoTaskMem(title); Marshal.FreeHGlobal(fileBuffer);
        }
    }

    private async void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = PickLauncherPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Path.GetFileName(path).Equals("WPFLauncher.exe", StringComparison.OrdinalIgnoreCase) || HashFile(path) != LauncherHash)
            { await ShowMessage("文件不匹配", "指定的文件不是受支持的 WPFLauncher.exe 版本。", "确定"); return; }
            _launcherPath = path; LauncherPathText.Text = path; LauncherStatus.Text = "可用 · 手动指定"; RefreshCoreState();
            SaveLauncherPaths(new[] { path });
        }
        catch (Exception ex) { var detail = $"HRESULT/错误：0x{ex.HResult:X8}\n{ex.Message}"; Log($"手动选择失败：{detail}"); await ShowMessage("手动选择失败", detail, "确定"); }
    }

    private async Task RestoreSavedLaunchersAsync()
    {
        try
        {
            if (!File.Exists(_launcherCacheFile)) return;
            var saved = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(_launcherCacheFile)) ?? new List<string>();
            var valid = saved.Where(path => File.Exists(path) && Path.GetFileName(path).Equals("WPFLauncher.exe", StringComparison.OrdinalIgnoreCase))
                .Where(path => { try { return HashFile(path).Equals(LauncherHash, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveLauncherPaths(valid);
            if (valid.Count == 0) { Log("已读取启动器缓存，但路径已失效，需要重新检测。"); return; }
            _launcherPath = valid[0]; LauncherPathText.Text = _launcherPath; LauncherStatus.Text = $"可用 · 缓存恢复（{valid.Count} 个）"; RefreshCoreState(); Log($"已从缓存恢复并验证启动器：{_launcherPath}");
        }
        catch (Exception ex) { Log($"读取启动器缓存失败：{ex.Message}"); }
    }

    private void SaveLauncherPaths(IEnumerable<string> paths)
    {
        try { File.WriteAllText(_launcherCacheFile, JsonSerializer.Serialize(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8); }
        catch (Exception ex) { LogSafe($"保存启动器路径失败：{ex.Message}"); }
    }

    private void RefreshCoreState()
    {
        if (_launcherPath is null) return;
        StartButton.IsEnabled = false;
        var core = Path.Combine(Path.GetDirectoryName(_launcherPath)!, "Mcl.Core.dll");
        if (!File.Exists(core)) { PatchStatus.Text = "未找到 Mcl.Core.dll · 可执行补丁"; PatchButton.IsEnabled = true; return; }
        var ok = HashFile(core).Equals(CoreHash, StringComparison.OrdinalIgnoreCase);
        PatchStatus.Text = ok ? "已补丁 · MD5 校验通过" : "未补丁 · MD5 不匹配";
        PatchButton.IsEnabled = !ok;
        StartButton.IsEnabled = ok;
        Log(ok ? "Mcl.Core.dll 校验通过。" : "Mcl.Core.dll 需要补丁。");
    }

    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherPath is null) return;
        PatchButton.IsEnabled = false;
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(_launcherPath)!, "Mcl.Core.dll");
            Directory.CreateDirectory(_runtimeDir);
            var source = Path.Combine(_runtimeDir, "Mcl.Core.dll");
            ExtractResource("MclLauncher.Resources.Mcl.Core.dll", source);
            if (File.Exists(target)) File.Copy(target, target + ".original", true);
            var temp = target + ".mcltmp";
            File.Copy(source, temp, true);
            File.Move(temp, target, true);
            RefreshCoreState();
            if (!HashFile(target).Equals(CoreHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("替换后 MD5 校验失败");
            await ShowMessage("补丁成功", "Mcl.Core.dll 已替换并通过 MD5 校验。", "确定");
        }
        catch (Exception ex) { PatchStatus.Text = "补丁失败"; Log($"补丁失败：{ex.Message}"); PatchButton.IsEnabled = true; }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await Confirm("确认下载网易启动器", "将打开网易官方客户端下载页面。是否继续？")) return;
        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            await ShowMessage("下载启动器", "已打开默认浏览器。安装完成后，请点击“快速检测”。", "确定");
        }
        catch (Exception ex) { await ShowMessage("打开浏览器失败", ex.Message, "确定"); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherPath is null) return;
        Directory.CreateDirectory(_runtimeDir);
        var injectorPath = Path.Combine(_runtimeDir, "injector.exe"); var proxyPath = Path.Combine(_runtimeDir, "MinecraftProxy.dll"); var logPath = Path.Combine(_runtimeDir, "injector.log");
        ExtractResource("MclLauncher.Resources.injector.exe", injectorPath); ExtractResource("MclLauncher.Resources.MinecraftProxy.dll", proxyPath);
        var existingInjector = FindProcessAtPath(injectorPath);
        if (existingInjector is not null)
        {
            if (await Confirm("injector 已在运行", $"PID {existingInjector.Id} 的 injector.exe 已存在，是否重启 injector？", "重启")) { TryKill(existingInjector); existingInjector = null; } else return;
        }
        if (FindProcessAtPath(_launcherPath) is null)
        {
            try { _launcher = Process.Start(new ProcessStartInfo(_launcherPath) { WorkingDirectory = Path.GetDirectoryName(_launcherPath), UseShellExecute = true }); Log("已启动 WPFLauncher.exe。"); }
            catch (Exception ex) { Log($"启动 WPFLauncher 失败：{ex.Message}"); return; }
        }
        File.WriteAllText(logPath, "", Encoding.UTF8);
        var command = $"\"{injectorPath}\" \"{proxyPath}\" > \"{logPath}\" 2>&1";
        var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c \"" + command + "\"") { WorkingDirectory = _runtimeDir, UseShellExecute = false, CreateNoWindow = true });
        await Task.Delay(250);
        _injector = FindProcessAtPath(injectorPath) ?? shell;
        _injectorExitLogged = false;
        Log($"已启动 injector，PID {_injector?.Id}。已启动/复用 WPFLauncher。");
        StartButton.IsEnabled = false; StopButton.IsEnabled = true; StartMonitor(logPath);
        await ShowMessage("可以启动网易游戏端", "injector 和 WPFLauncher 已启动，可以启动网易游戏端了。", "确定");
    }

    private void StartMonitor(string logPath)
    {
        _monitorCts?.Cancel(); _monitorCts = new CancellationTokenSource(); var token = _monitorCts.Token;
        _ = Task.Run(async () => { while (!token.IsCancellationRequested) { await DispatcherQueue.EnqueueAsync(RefreshProcesses); if (!_injectorExitLogged && File.Exists(logPath) && !IsAlive(_injector)) { var text = File.ReadAllText(logPath); if (text.Contains("25565", StringComparison.OrdinalIgnoreCase)) { _injectorExitLogged = true; await DispatcherQueue.EnqueueAsync(() => Log("injector 已退出，状态：可以启动白端进入 127.0.0.1:25565 了")); } } await Task.Delay(700, token).ContinueWith(_ => { }); } }, token);
    }

    private void RefreshProcesses()
    {
        var wpfl = _launcherPath is null ? null : FindProcessAtPath(_launcherPath); var inj = FindProcessAtPath(Path.Combine(_runtimeDir, "injector.exe"));
        LauncherStatus.Text = wpfl is null ? "未运行" : $"运行中 · PID {wpfl.Id}"; InjectorStatus.Text = inj is null ? "未运行" : $"运行中 · PID {inj.Id}";
        StopButton.IsEnabled = wpfl is not null || inj is not null; StartButton.IsEnabled = _launcherPath is not null && PatchStatus.Text.Contains("通过") && inj is null;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) { _monitorCts?.Cancel(); TryKill(_launcher); TryKill(_injector); if (_launcherPath is not null) TryKill(FindProcessAtPath(_launcherPath)); Log("已请求退出 WPFLauncher 和 injector。"); await Task.Delay(250); RefreshProcesses(); }

    private static Process? FindProcess(string name) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).FirstOrDefault(p => IsAlive(p));
    private static Process? FindProcessAtPath(string path) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)).FirstOrDefault(p => { try { return IsAlive(p) && string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
    private static bool IsAlive(Process? p) { try { return p is not null && !p.HasExited; } catch { return false; } }
    private static void TryKill(Process? p) { try { if (IsAlive(p)) p!.Kill(true); } catch { } }
    private static string HashFile(string path) { using var md5 = MD5.Create(); using var stream = File.OpenRead(path); return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant(); }
    private static void ExtractResource(string name, string path) { using var input = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name); using var output = File.Create(path); input.CopyTo(output); }
    private async Task<bool> Confirm(string title, string content, string primaryText = "确定") { var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = "取消", XamlRoot = Content.XamlRoot }; return await dialog.ShowAsync() == ContentDialogResult.Primary; }
    private async Task ShowMessage(string title, string content, string close) { var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = close, XamlRoot = Content.XamlRoot }; await dialog.ShowAsync(); }
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    { var tcs = new TaskCompletionSource(); if (!queue.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) tcs.SetCanceled(); return tcs.Task; }
}
