using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace monitoriMovil;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private string? _selectedDevice;
    private Process? _scrcpyProcess;
    
    private bool _isDown;
    private bool _longPressFired;
    private Point _downPoint;
    private Point _lastSentPoint;
    private DateTime _lastMoveSent = DateTime.MinValue;
    private DateTime _downTime = DateTime.MinValue;
    private CancellationTokenSource? _longPressCts;
    
    private int _delayMs = 33;
    private int _fpsMode = 0;
    
    private int _devW = 1080;
    private int _devH = 1920;
    
    private int _frameCount;
    private DateTime _lastFpsCheck = DateTime.Now;
    private bool _wakeLock = false;
    
    public MainWindow()
    {
        InitializeComponent();
        
        RefreshBtn.Click += (_, _) => RefreshDevices();
        ConnectBtn.Click += async (_, _) => await StartMirroring();
        StopBtn.Click += (_, _) => StopMirroring();
        FpsBtn.Click += (_, _) => CycleFps();
        
        BackBtn.Click += (_, _) => SendKey("4");
        HomeBtn.Click += (_, _) => SendKey("3");
        RecentsBtn.Click += (_, _) => SendKey("187");
        MenuBtn.Click += (_, _) => SendKey("82");
        PowerBtn.Click += (_, _) => SendKey("26");
        VolDownBtn.Click += (_, _) => SendKey("25");
        VolUpBtn.Click += (_, _) => SendKey("24");
        
        NotifBtn.Click += (_, _) => SwipeDown();
        QuickSettingsBtn.Click += (_, _) => SwipeUpQuick();
        WakeLockBtn.Click += (_, _) => ToggleWakeLock();
        InstallApkBtn.Click += async (_, _) => await InstallApk();
        
        TextBtn.Click += async (_, _) => await SendText();
        ScreenshotBtn.Click += async (_, _) => await TakeScreenshot();
        
        ScreenImage.PointerPressed += OnScreenPointerPressed;
        ScreenImage.PointerMoved += OnScreenPointerMoved;
        ScreenImage.PointerReleased += OnScreenPointerReleased;
        
        RefreshDevices();
    }
    
    private void SwipeDown()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        _ = Task.Run(() =>
        {
            var x = _devW / 2;
            RunAdbCommand($"-s {_selectedDevice} shell input swipe {x} 50 {x} 800 300");
        });
    }
    
    private void SwipeUpQuick()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        _ = Task.Run(() =>
        {
            var x = _devW / 2;
            RunAdbCommand($"-s {_selectedDevice} shell input swipe {x} 1500 {x} 300 300");
        });
    }
    
    private void ToggleWakeLock()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        _wakeLock = !_wakeLock;
        Dispatcher.UIThread.Post(() => WakeLockBtn.Content = _wakeLock ? "🔓 Mantener encendida: ON" : "🔒 Mantener encendida: OFF");
        _ = Task.Run(() =>
        {
            if (_wakeLock)
                RunAdbCommand($"-s {_selectedDevice} shell svc power stayon true");
            else
                RunAdbCommand($"-s {_selectedDevice} shell svc power stayon false");
        });
    }
    
    private async Task InstallApk()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Seleccionar APK",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("APK") { Patterns = new[] { "*.apk" } } }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            StatusText.Text = "Instalando APK...";
            _ = Task.Run(async () =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "adb",
                    Arguments = $" -s {_selectedDevice} install -r \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                var outp = await p.StandardOutput.ReadToEndAsync();
                var err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                Dispatcher.UIThread.Post(() => StatusText.Text = outp.Contains("Success") ? "APK instalado" : $"Resultado: {outp} {err}");
                await Task.Delay(3000);
                Dispatcher.UIThread.Post(() => StatusText.Text = "");
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }
    
    private void SendKey(string keycode)
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        _ = Task.Run(() => RunAdbCommand($"-s {_selectedDevice} shell input keyevent {keycode}"));
    }
    
    private async Task SendText()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        var dialog = new Window
        {
            Title = "Enviar texto al dispositivo",
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        var box = new TextBox { Watermark = "Escribe texto..." };
        var btn = new Button { Content = "Enviar", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        btn.Click += (_, _) =>
        {
            var text = box.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text)) { dialog.Close(); return; }
            // adb input text doesn't like spaces well, replace with %s
            var escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(" ", "%s");
            _ = Task.Run(() => RunAdbCommand($"-s {_selectedDevice} shell input text '{escaped.Replace("'", "'\\''")}'"));
            dialog.Close();
        };
        panel.Children.Add(box);
        panel.Children.Add(btn);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }
    
    private async Task TakeScreenshot()
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = $" -s {_selectedDevice} exec-out screencap -p",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            ms.Position = 0;
            await p.WaitForExitAsync();
            
            var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            await using var fs = File.Create(fileName);
            await ms.CopyToAsync(fs);
            StatusText.Text = $"Guardado: {fileName}";
            await Task.Delay(2000);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }
    
    private void CycleFps()
    {
        _fpsMode = (_fpsMode + 1) % 4;
        switch (_fpsMode)
        {
            case 0: _delayMs = 33; FpsBtn.Content = "FPS: ~30"; break;
            case 1: _delayMs = 66; FpsBtn.Content = "FPS: 15"; break;
            case 2: _delayMs = 33; FpsBtn.Content = "FPS: 30"; break;
            case 3: _delayMs = 16; FpsBtn.Content = "FPS: 60"; break;
        }
    }
    
    private void RefreshDevices()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = "devices",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            
            DeviceCombo.Items.Clear();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains('\t') && line.EndsWith("device"))
                {
                    var id = line.Split('\t')[0];
                    DeviceCombo.Items.Add(id);
                }
            }
            if (DeviceCombo.Items.Count > 0)
                DeviceCombo.SelectedIndex = 0;
            StatusText.Text = DeviceCombo.Items.Count == 0 ? "No hay dispositivos" : "Dispositivos encontrados";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }
    
    private async Task StartMirroring()
    {
        if (DeviceCombo.SelectedItem is not string device) return;
        _selectedDevice = device;
        StopMirroring();
        
        StatusText.Text = "Iniciando...";
        await GetDeviceSize();
        
        // Try using scrcpy first (best option)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "scrcpy",
                Arguments = $"--serial {device} --max-size=1080 --bit-rate=2M --no-audio",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _scrcpyProcess = Process.Start(psi);
            if (_scrcpyProcess != null)
            {
                StatusText.Text = "Usando scrcpy (ventana externa)";
                InfoText.Text = "scrcpy abrió una ventana. Para controlar desde aquí necesitamos otra opción.";
                await Task.Delay(1000);
                return;
            }
        }
        catch { }
        
        await StartAdbMirrorWithInput();
    }
    
    private async Task StartAdbMirrorWithInput()
    {
        _cts = new CancellationTokenSource();
        StatusText.Text = "Conectando vía ADB...";
        
        try
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "adb",
                            Arguments = $" -s {_selectedDevice} exec-out screencap -p",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var p = Process.Start(psi)!;
                        var ms = new MemoryStream(1024 * 1024);
                        var stream = p.StandardOutput.BaseStream;
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                        {
                            await ms.WriteAsync(buffer, 0, read, _cts.Token);
                        }
                        ms.Position = 0;
                        await p.WaitForExitAsync();
                        
                        if (ms.Length > 100 && !_cts.Token.IsCancellationRequested)
                        {
                            _frameCount++;
                            if ((DateTime.Now - _lastFpsCheck).TotalSeconds >= 1.0)
                            {
                                var fps = _frameCount;
                                _frameCount = 0;
                                _lastFpsCheck = DateTime.Now;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { RealFpsText.Text = $"{fps} FPS"; } catch { }
                                });
                            }
                            await Dispatcher.UIThread.InvokeAsync(() => 
                            {
                                try
                                {
                                    var bmp = new Bitmap(ms);
                                    ScreenImage.Source = bmp;
                                    StatusText.Text = "";
                                }
                                catch { }
                            });
                        }
                        await Task.Delay(_delayMs);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (!_cts.Token.IsCancellationRequested)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"Error: {ex.Message}");
                            break;
                        }
                    }
                }
            }, _cts.Token);
            
            InfoText.Text = "Toca, mantén (pulsación larga), arrastra, usa los botones.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }
    
    private async Task RunAdbCommand(string cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "adb",
            Arguments = cmd,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p != null) await p.WaitForExitAsync();
    }
    
    private (int x, int y) GetScaledCoords(Point point)
    {
        var bounds = ScreenImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return (0, 0);
        var scaleX = _devW / bounds.Width;
        var scaleY = _devH / bounds.Height;
        var x = (int)Math.Clamp(point.X * scaleX, 0, _devW);
        var y = (int)Math.Clamp(point.Y * scaleY, 0, _devH);
        return (x, y);
    }
    
    private async Task GetDeviceSize()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = $" -s {_selectedDevice} shell wm size",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            // Physical size: 1080x2400
            var idx = output.IndexOf(':');
            if (idx >= 0)
            {
                var rest = output[(idx+1)..].Trim();
                var parts = rest.Split('x', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var w) && int.TryParse(parts[1].Trim(), out var h))
                {
                    _devW = w; _devH = h;
                }
            }
        }
        catch { }
    }
    
    private void SendTouch(string action, int x, int y)
    {
        _ = Task.Run(() => RunAdbCommand($"-s {_selectedDevice} shell input motionevent {action} {x} {y}"));
    }
    
    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedDevice)) return;
        var point = e.GetPosition(ScreenImage);
        _downPoint = point;
        _lastSentPoint = point;
        _downTime = DateTime.Now;
        _lastMoveSent = DateTime.Now;
        _isDown = true;
        _longPressFired = false;
        _longPressCts?.Cancel();
        _longPressCts = new CancellationTokenSource();
        var cts = _longPressCts;
        var downPt = _downPoint;
        var dev = _selectedDevice;
        
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(550, cts.Token);
                if (_isDown && !_longPressFired && dist2(downPt, _lastSentPoint) < 5)
                {
                    _longPressFired = true;
                    var (x, y) = GetScaledCoords(downPt);
                    // Long press: tap and hold
                    await RunAdbCommand($"-s {dev} shell input swipe {x} {y} {x} {y} 800");
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
        
        var (x1, y1) = GetScaledCoords(point);
        SendTouch("DOWN", x1, y1);
    }
    
    private double dist2(Point a, Point b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    
    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDown || string.IsNullOrEmpty(_selectedDevice)) return;
        var now = DateTime.Now;
        if ((now - _lastMoveSent).TotalMilliseconds < 10) return;
        
        var point = e.GetPosition(ScreenImage);
        var dist = dist2(point, _lastSentPoint);
        if (dist < 2) return;
        if (dist2(point, _downPoint) > 6) _longPressCts?.Cancel();
        
        _lastSentPoint = point;
        _lastMoveSent = now;
        var (x, y) = GetScaledCoords(point);
        SendTouch("MOVE", x, y);
    }
    
    private void OnScreenPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDown || string.IsNullOrEmpty(_selectedDevice))
        {
            _isDown = false;
            _longPressCts?.Cancel();
            return;
        }
        _isDown = false;
        _longPressCts?.Cancel();
        
        if (_longPressFired) return;
        
        var endPoint = e.GetPosition(ScreenImage);
        var distTotal = dist2(endPoint, _downPoint);
        
        _ = Task.Run(() =>
        {
            if (distTotal < 6)
            {
                var (x, y) = GetScaledCoords(_downPoint);
                RunAdbCommand($"-s {_selectedDevice} shell input motionevent UP {x} {y}");
            }
            else
            {
                var (x2, y2) = GetScaledCoords(endPoint);
                RunAdbCommand($"-s {_selectedDevice} shell input motionevent UP {x2} {y2}");
            }
        });
    }
    
    private void StopMirroring()
    {
        _cts?.Cancel();
        _cts = null;
        _frameCount = 0;
        _lastFpsCheck = DateTime.Now;
        Dispatcher.UIThread.Post(() => { try { RealFpsText.Text = "0 FPS"; } catch { } });
        _wakeLock = false;
        if (!string.IsNullOrEmpty(_selectedDevice))
        {
            _ = Task.Run(() => RunAdbCommand($"-s {_selectedDevice} shell svc power stayon false"));
        }
        try { WakeLockBtn.Content = "🔒 Mantener encendida: OFF"; } catch { }
        try { _scrcpyProcess?.Kill(); _scrcpyProcess = null; } catch { }
        ScreenImage.Source = null;
        StatusText.Text = "Detenido";
    }
}