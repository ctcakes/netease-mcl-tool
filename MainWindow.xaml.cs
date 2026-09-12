using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace MclLauncher;

public sealed partial class MainWindow : Window
{
    private const string LauncherHash = "de08144dddf4be9dda3e751922873e54";
    private const string CoreHash = "eef5e9f11feee4d366444dd1cbafc2cb";
    private const string DownloadUrl = "https://adl.netease.com/d/g/mc/c/pe?type=windows";
    private readonly string _launcherCacheFile = Path.Combine(AppContext.BaseDirectory, "launcher-paths.json");
    private string? _launcherPath;
    private Process? _injector;
    private Process? _launcher;
    private CancellationTokenSource? _monitorCts;
    private bool _injectorExitLogged;
    private string _proxyGameState = "未启动";
    private readonly string _runtimeDir = Path.Combine(Path.GetTempPath(), "MclLauncher", "runtime");
    private string Account4399File => Path.Combine(AppContext.BaseDirectory, "4399-output", "accs.txt");

    public MainWindow()
    {
        InitializeComponent();
        SetProxyGameStatus("未启动", ProxyVisualState.Neutral);
        Closed += (_, _) => _monitorCts?.Cancel();
        Log("程序已启动，管理员权限已启用。\n");
        DispatcherQueue.TryEnqueue(async () => await RestoreSavedLaunchersAsync());
    }

    private void Log(string message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Log(message));
            return;
        }
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
        StartButton.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(_runtimeDir);
            var injectorPath = Path.Combine(_runtimeDir, "injector.exe"); var proxyPath = Path.Combine(_runtimeDir, "MinecraftProxy.dll"); var logPath = Path.Combine(_runtimeDir, "injector.log");
            ExtractResource("MclLauncher.Resources.injector.exe", injectorPath); ExtractResource("MclLauncher.Resources.MinecraftProxy.dll", proxyPath);
            var existingInjector = FindProcess("injector.exe");
            if (existingInjector is not null)
            {
                if (await Confirm("injector 已在运行", $"PID {existingInjector.Id} 的 injector.exe 已存在，是否重启 injector？", "重启")) TryKill(existingInjector); else { StartButton.IsEnabled = true; return; }
            }
            if (FindProcess("WPFLauncher.exe") is null)
            {
                _launcher = Process.Start(new ProcessStartInfo(_launcherPath) { WorkingDirectory = Path.GetDirectoryName(_launcherPath), UseShellExecute = true });
                Log("已启动 WPFLauncher.exe。");
            }
            else Log("已复用正在运行的 WPFLauncher.exe。");
            File.WriteAllText(logPath, "", Encoding.UTF8);
            var command = $"\"{injectorPath}\" \"{proxyPath}\" > \"{logPath}\" 2>&1";
            var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c \"" + command + "\"") { WorkingDirectory = _runtimeDir, UseShellExecute = false, CreateNoWindow = true });
            await Task.Delay(250);
            _injector = FindProcess("injector.exe") ?? shell;
            _injectorExitLogged = false;
            Log($"已启动 injector，PID {_injector?.Id}。已启动/复用 WPFLauncher。");
            SetProxyGameStatus("等待 proxy 游戏端连接 127.0.0.1:25565", ProxyVisualState.Warning);
            StartButton.IsEnabled = false; StopButton.IsEnabled = true; StartMonitor(logPath);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            Log($"一键启动失败：{ex}");
            await ShowMessage("一键启动失败", ex.ToString(), "确定");
        }
    }

    private void StartMonitor(string logPath)
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _ = Task.Run(async () =>
        {
            var lastLogLength = 0;
            while (!token.IsCancellationRequested)
            {
                await DispatcherQueue.EnqueueAsync(RefreshProcesses);
                if (File.Exists(logPath))
                {
                    string text = "";
                    try { text = File.ReadAllText(logPath); } catch { }
                    if (!_injectorExitLogged && !IsAlive(_injector) && text.Contains("25565", StringComparison.OrdinalIgnoreCase))
                    {
                        _injectorExitLogged = true;
                        await DispatcherQueue.EnqueueAsync(() => Log("injector 已退出，proxy 监听已就绪；请启动/重连 proxy 游戏端到 127.0.0.1:25565。"));
                    }
                    await DispatcherQueue.EnqueueAsync(() => RefreshProxyGameStatusFromLog(text));
                    lastLogLength = text.Length;
                }
                await Task.Delay(700, token).ContinueWith(_ => { });
            }
        }, token);
    }


    private enum ProxyVisualState { Neutral, Good, Warning, Bad }

    private void SetProxyGameStatus(string text, ProxyVisualState state)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetProxyGameStatus(text, state));
            return;
        }
        _proxyGameState = text;
        ProxyGameStatus.Text = text;
        ProxyGameStatus.Foreground = state switch
        {
            ProxyVisualState.Good => new SolidColorBrush(Colors.LimeGreen),
            ProxyVisualState.Warning => new SolidColorBrush(Colors.Orange),
            ProxyVisualState.Bad => new SolidColorBrush(Colors.Red),
            _ => Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush
        };
    }

    private void RefreshProxyGameStatusFromLog(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText)) return;
        if (logText.Contains("BServer: B channelInactive", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("B gone", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("channelInactive", StringComparison.OrdinalIgnoreCase))
        {
            var lastPlay = logText.LastIndexOf("B in PLAY", StringComparison.OrdinalIgnoreCase);
            var lastGone = Math.Max(logText.LastIndexOf("channelInactive", StringComparison.OrdinalIgnoreCase), logText.LastIndexOf("B gone", StringComparison.OrdinalIgnoreCase));
            if (lastGone > lastPlay)
            {
                SetProxyGameStatus("proxy 游戏端已断开：请在白端重新连接后，再在 proxy 端重新连接直到正常", ProxyVisualState.Bad);
                return;
            }
        }
        if (logText.Contains("B in PLAY", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("B reached PLAY", StringComparison.OrdinalIgnoreCase))
        {
            SetProxyGameStatus("正常：proxy 游戏端已进入游戏", ProxyVisualState.Good);
            return;
        }
        if (logText.Contains("intention", StringComparison.OrdinalIgnoreCase) && logText.Contains("LOGIN", StringComparison.OrdinalIgnoreCase))
        {
            SetProxyGameStatus("proxy 游戏端正在登录/进服，等待进入游戏", ProxyVisualState.Warning);
            return;
        }
        if (logText.Contains("B channel captured", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("BServer: B channelActive", StringComparison.OrdinalIgnoreCase))
        {
            SetProxyGameStatus("proxy 游戏端已连接但未进入游戏", ProxyVisualState.Warning);
            return;
        }
        if (logText.Contains("proxy listener ready", StringComparison.OrdinalIgnoreCase) ||
            logText.Contains("25565", StringComparison.OrdinalIgnoreCase))
        {
            SetProxyGameStatus("等待 proxy 游戏端连接 127.0.0.1:25565", ProxyVisualState.Warning);
        }
    }

    private void RefreshProcesses()
    {
        var wpfl = FindProcess("WPFLauncher.exe"); var inj = FindProcess("injector.exe");
        LauncherStatus.Text = wpfl is null ? "未运行" : $"运行中 · PID {wpfl.Id}"; InjectorStatus.Text = inj is null ? "未运行" : $"运行中 · PID {inj.Id}";
        StopButton.IsEnabled = wpfl is not null || inj is not null; StartButton.IsEnabled = _launcherPath is not null && PatchStatus.Text.Contains("通过") && inj is null;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) { _monitorCts?.Cancel(); TryKill(_launcher); TryKill(_injector); TryKill(FindProcess("WPFLauncher.exe")); TryKill(FindProcess("injector.exe")); Log("已请求退出 WPFLauncher 和 injector。"); SetProxyGameStatus("未启动", ProxyVisualState.Neutral); await Task.Delay(250); RefreshProcesses(); }

    private static Process? FindProcess(string name) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).FirstOrDefault(IsAlive);
    private static bool IsAlive(Process? p) { try { return p is not null && !p.HasExited; } catch { return false; } }
    private static void TryKill(Process? p) { try { if (IsAlive(p)) p!.Kill(true); } catch { } }
    private static string HashFile(string path) { using var md5 = MD5.Create(); using var stream = File.OpenRead(path); return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant(); }
    private static void ExtractResource(string name, string path) { using var input = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name); using var output = File.Create(path); input.CopyTo(output); }
    private async Task<bool> Confirm(string title, string content, string primaryText = "确定") { var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = "取消", XamlRoot = Content.XamlRoot }; return await dialog.ShowAsync() == ContentDialogResult.Primary; }
    private async Task ShowMessage(string title, string content, string close) { var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = close, XamlRoot = Content.XamlRoot }; await dialog.ShowAsync(); }


    private async void Account4399Button_Click(object sender, RoutedEventArgs e)
    {
        var accounts = Load4399Accounts();
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            MinWidth = 720,
            MaxHeight = 460,
            ItemsSource = accounts
        };
        list.ItemTemplate = BuildAccountRowTemplate();
        var pathText = new TextBlock
        {
            Text = $"结果文件：{Account4399File}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(pathText);
        panel.Children.Add(new TextBlock { Text = "格式：账号----密码----身份证。可按 Ctrl/Shift 多选，点复制会复制选中行的账号和密码。" });
        panel.Children.Add(BuildAccountHeader());
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            Title = $"4399 账号管理（{accounts.Count} 条）",
            Content = panel,
            PrimaryButtonText = "复制选中账号/密码",
            SecondaryButtonText = "刷新",
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var selected = list.SelectedItems.Cast<Account4399Row>().ToList();
            if (selected.Count == 0) { Log("4399账号管理：未选中任何账号"); return; }
            var text = string.Join(Environment.NewLine, selected.Select(x => $"{x.Username}----{x.Password}"));
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            Log($"4399账号管理：已复制 {selected.Count} 条账号/密码");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Account4399Button_Click(sender, e);
        }
    }

    private List<Account4399Row> Load4399Accounts()
    {
        if (!File.Exists(Account4399File)) return new List<Account4399Row>();
        var rows = new List<Account4399Row>();
        var index = 1;
        foreach (var line in File.ReadLines(Account4399File, Encoding.UTF8))
        {
            var parts = line.Split("----", 3, StringSplitOptions.None);
            if (parts.Length >= 3) rows.Add(new Account4399Row(index++, parts[0], parts[1], parts[2]));
        }
        return rows;
    }

    private static Grid BuildAccountHeader()
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Add(string text, int col)
        {
            var tb = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        Add("序号", 0); Add("账号", 1); Add("密码", 2); Add("身份证", 3);
        return grid;
    }

    private static DataTemplate BuildAccountRowTemplate()
    {
        const string xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Grid ColumnSpacing="12" Padding="8,6,8,6">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="56"/>
      <ColumnDefinition Width="180"/>
      <ColumnDefinition Width="200"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="{Binding Index}"/>
    <TextBlock Grid.Column="1" Text="{Binding Username}" TextTrimming="CharacterEllipsis"/>
    <TextBlock Grid.Column="2" Text="{Binding Password}" TextTrimming="CharacterEllipsis"/>
    <TextBlock Grid.Column="3" Text="{Binding IdCard}" TextTrimming="CharacterEllipsis"/>
  </Grid>
</DataTemplate>
""";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private sealed record Account4399Row(int Index, string Username, string Password, string IdCard);


    private async void Reg4399Button_Click(object sender, RoutedEventArgs e)
    {
        var countBox = new TextBox { Text = "1", Header = "注册数量" };
        var threadBox = new TextBox { Text = "1", Header = "线程数量" };
        var nameBox = new TextBox { Text = "李艳娟", Header = "真实姓名" };
        var inputBox = new TextBox { Text = Path.Combine(AppContext.BaseDirectory, "4399_demo.txt"), Header = "身份证/demo 数据文件，每行一条", MinWidth = 520 };
        var proxyBox = new CheckBox { Content = "生成请求使用代理（127.0.0.1:7897）", IsChecked = false };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "已复刻 Python 逻辑到软件内部：随机账号密码、读取输入、多线程、AES 加密、HTTP 提交均由 C# 执行。默认不使用代理。" });
        panel.Children.Add(countBox);
        panel.Children.Add(threadBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(inputBox);
        panel.Children.Add(proxyBox);
        var dialog = new ContentDialog { Title = "4399 批量工具", Content = panel, PrimaryButtonText = "开始", CloseButtonText = "取消", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!int.TryParse(countBox.Text, out var count) || count <= 0) { Log("4399: 注册数量无效"); return; }
        if (!int.TryParse(threadBox.Text, out var threads) || threads <= 0) { Log("4399: 线程数量无效"); return; }
        if (!File.Exists(inputBox.Text)) { Log($"4399: 输入文件不存在：{inputBox.Text}"); return; }
        await Run4399BatchNativeAsync(count, Math.Min(threads, 64), nameBox.Text, inputBox.Text, proxyBox.IsChecked == true);
    }

    private async Task Run4399BatchNativeAsync(int count, int threads, string realName, string inputFile, bool useProxy)
    {
        var demos = (await File.ReadAllLinesAsync(inputFile, Encoding.UTF8)).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (demos.Length == 0) { Log("4399: 输入文件没有有效内容"); return; }
        var outDir = Path.Combine(AppContext.BaseDirectory, "4399-output");
        Directory.CreateDirectory(outDir);
        var outFile = Account4399File;
        Log($"4399: 读取 {demos.Length} 条数据，开始内部批量任务 count={count}, threads={threads}, 代理={(useProxy ? "启用" : "关闭")}");
        var ok = 0; var fail = 0;
        using var gate = new SemaphoreSlim(threads);
        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await gate.WaitAsync();
            try
            {
                var username = RandomUsername();
                var password = RandomPassword();
                var idcard = demos[RandomNumberGenerator.GetInt32(demos.Length)];
                var result = await Register4399NativeAsync(username, password, realName, idcard, useProxy);
                if (result.Ok && result.StatusCode == 200)
                {
                    Interlocked.Increment(ref ok);
                    await File.AppendAllTextAsync(outFile, $"{username}----{password}----{idcard}{Environment.NewLine}", Encoding.UTF8);
                    LogSafe($"4399: [成功] {username} {result.Message}");
                }
                else
                {
                    Interlocked.Increment(ref fail);
                    LogSafe($"4399: [失败] {username} HTTP {result.StatusCode} {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref fail);
                LogSafe($"4399: [异常] {ex.Message}");
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        Log($"4399: 完成，成功 {ok}，失败 {fail}，结果文件：{outFile}");
    }

    private static async Task<RegResult> Register4399NativeAsync(string username, string password, string realName, string idcard, bool useProxy)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All };
        if (useProxy)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy("http://127.0.0.1:7897");
        }
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://my.4399.com/account/login");
        var frameUrl = "https://ptlogin.4399.com/ptlogin/regFrame.do?regMode=reg_normal&postLoginHandler=refreshParent&displayMode=embed&appId=my&externalLogin=qq&regIdcard=true&autoLogin=false&includeFcmInfo=false&expandFcmInput=true&fcmFakeValidate=false&mainDivId=popup_reg_div&iframeId=popup_reg_frame&v=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var html = await http.GetStringAsync(frameUrl);
        var payload = ParseHiddenInputs(html);
        var sec = payload.TryGetValue("sec", out var secVal) ? secVal : "1";
        payload["username"] = username;
        payload["email"] = "";
        payload["reg_eula_agree"] = "on";
        payload["password"] = sec == "1" ? EncryptCryptoJsAes(password) : password;
        payload["passwordveri"] = sec == "1" ? EncryptCryptoJsAes(password) : password;
        payload["realname"] = sec == "1" ? EncryptCryptoJsAes(realName) : realName;
        payload["idcard"] = sec == "1" ? EncryptCryptoJsAes(idcard) : idcard;
        using var content = new FormUrlEncodedContent(payload);
        var resp = await http.PostAsync("https://ptlogin.4399.com/ptlogin/register.do", content);
        var body = await resp.Content.ReadAsStringAsync();
        var parsed = Parse4399Result(body);
        return new RegResult(parsed.ok, (int)resp.StatusCode, parsed.message);
    }

    private static Dictionary<string, string> ParseHiddenInputs(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var name = Regex.Match(tag, "name=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            var value = Regex.Match(tag, "value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            if (name.Success) fields[name.Groups[1].Value] = WebUtility.HtmlDecode(value.Success ? value.Groups[1].Value : "");
        }
        return fields;
    }

    private static (bool ok, string message) Parse4399Result(string html)
    {
        string Clean(string x) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(x), "<[^>]+>|&nbsp;", ""), "\\s+", " ").Trim();
        var cleanHtml = Clean(html);
        if (cleanHtml.Contains("验证码错误", StringComparison.OrdinalIgnoreCase))
            return (false, "验证码错误，请更换代理节点");
        var m = Regex.Match(html, "<div class=\"login_error\">\\s*<strong>(.*?)</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (m.Success) return (false, Clean(m.Groups[1].Value));
        m = Regex.Match(html, "<div id=\"Msg\"[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (m.Success && Clean(m.Groups[1].Value).Length > 0) return (false, Clean(m.Groups[1].Value));
        if (html.Contains("login_comfirm", StringComparison.OrdinalIgnoreCase) || html.Contains("reg_success", StringComparison.OrdinalIgnoreCase)) return (true, "注册成功");
        return (false, $"未识别响应 len={html.Length}");
    }

    private static string EncryptCryptoJsAes(string plain)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var pass = Encoding.UTF8.GetBytes("lzYW5qaXVqa");
        var keyiv = EvpBytesToKey(pass, salt, 48);
        using var aes = Aes.Create();
        aes.KeySize = 256; aes.BlockSize = 128; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyiv.Take(32).ToArray(); aes.IV = keyiv.Skip(32).Take(16).ToArray();
        using var enc = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(Encoding.ASCII.GetBytes("Salted__").Concat(salt).Concat(cipher).ToArray());
    }

    private static byte[] EvpBytesToKey(byte[] pass, byte[] salt, int needed)
    {
        using var md5 = MD5.Create();
        var result = new List<byte>();
        byte[] prev = Array.Empty<byte>();
        while (result.Count < needed)
        {
            prev = md5.ComputeHash(prev.Concat(pass).Concat(salt).ToArray());
            result.AddRange(prev);
        }
        return result.Take(needed).ToArray();
    }

    private static string RandomUsername()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var len = RandomNumberGenerator.GetInt32(6, 11);
        return new string(Enumerable.Range(0, len).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    private static string RandomPassword()
    {
        const string all = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        while (true)
        {
            var s = new string(Enumerable.Range(0, 13).Select(_ => all[RandomNumberGenerator.GetInt32(all.Length)]).ToArray());
            if (s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(char.IsDigit) && s.Any(c => "!@#$%^&*".Contains(c))) return s;
        }
    }

    private readonly record struct RegResult(bool Ok, int StatusCode, string Message);

}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    { var tcs = new TaskCompletionSource(); if (!queue.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) tcs.SetCanceled(); return tcs.Task; }
}


