using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace WpfEtwDemo
{
    public partial class MainWindow : Window
    {
        // Queue save data（message + color）
        private readonly ConcurrentQueue<(string Message, Brush Color)> _eventQueue = new();

        // Batches queued events into the UI on a fixed interval instead of
        // dispatching once per ETW event, which floods the UI thread under
        // high-frequency providers like ThreadStart/Stop.
        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        // Upper bound for listBoxEvents so long-running sessions don't grow it unbounded.
        private const int MaxDisplayedEvents = 2000;

        // Live session
        private TraceEventSession? _liveSession;
        private Task? _liveTask;

        // ETL
        private Task? _etlTask;
        private CancellationTokenSource? _etlCts;

        // State flag
        private volatile bool _isLiveRunning = false;
        private volatile bool _isEtlRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            comboMode.SelectionChanged += ComboMode_SelectionChanged;
            _uiTimer.Tick += (s, e) => ProcessQueue();
            UpdateUiState();
        }

        // Update UI when switching modes
        private void ComboMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateUiState();
        }

        private void UpdateUiState()
        {
            bool isLiveMode = (comboMode.SelectedIndex == 0);

            btnStartLive.IsEnabled = isLiveMode && !_isLiveRunning && !_isEtlRunning;
            btnStopLive.IsEnabled = isLiveMode && _isLiveRunning;

            btnOpenEtl.IsEnabled = !isLiveMode && !_isEtlRunning && !_isLiveRunning;
            btnStopEtl.IsEnabled = !isLiveMode && _isEtlRunning;
        }

        #region Live Session

        private void BtnStartLive_Click(object sender, RoutedEventArgs e)
        {
            if (_isEtlRunning)
            {
                MessageBox.Show("目前正在處理 ETL，請先停止 ETL。", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StartLiveSession();
        }

        private void BtnStopLive_Click(object sender, RoutedEventArgs e)
        {
            StopLiveSession();
        }

        private void StartLiveSession()
        {
            if (_isLiveRunning) return;

            try
            {
                //Establish a unique name to avoid conflicts
                string sessionName = "WpfEtwDemoLive_" + Guid.NewGuid();

                _liveTask = Task.Run(() =>
                {
                    try
                    {
                        using (var session = new TraceEventSession(sessionName))
                        {
                            _liveSession = session;
                            session.StopOnDispose = true;

                            // Enable kernel process provider
                            session.EnableKernelProvider(
                                KernelTraceEventParser.Keywords.Process |
                                KernelTraceEventParser.Keywords.Thread |
                                KernelTraceEventParser.Keywords.ImageLoad);

                            session.Source.Kernel.ProcessStart += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string processName = data.ProcessName;
                                
                                //Precisely capture the "IPC intelligent linkage process" we just designed.
                                if (processName.Contains("WmiQueryTool", StringComparison.OrdinalIgnoreCase))
                                {
                                    string message = $"🔥 [{timestamp}] 【AI 智慧聯動】Ollama 成功驅動 WmiQueryTool 啟動！(PID={data.ProcessID})";
                                    _eventQueue.Enqueue((message, Brushes.Magenta));
                                }
                                else if (processName.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                                {
                                    string message = $"🚀 [{timestamp}] 【地端模型載入】Ollama 推理進程啟動 (PID={data.ProcessID})";
                                    _eventQueue.Enqueue((message, Brushes.Crimson));
                                }
                                else
                                {
                                    // Regular system background events maintain your original logic.
                                    string message = $"[{timestamp}] START: {processName} (PID={data.ProcessID})";
                                    _eventQueue.Enqueue((message, Brushes.Green));
                                }
                            };

                            session.Source.Kernel.ProcessStop += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string processName = data.ProcessName;

                                if (processName.Contains("WmiQueryTool", StringComparison.OrdinalIgnoreCase))
                                {
                                    string message = $"🏁 [{timestamp}] 【IPC 乾淨收尾】WmiQueryTool 執行 WMI 成功，進程已安全釋放。";
                                    _eventQueue.Enqueue((message, Brushes.DarkCyan));
                                }
                                else
                                {
                                    string message = $"[{timestamp}] STOP: {processName} (PID={data.ProcessID})";
                                    _eventQueue.Enqueue((message, Brushes.Red));
                                }
                            };

                            session.Source.Kernel.ThreadStart += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string message = $"[{timestamp}] THREAD START: PID={data.ProcessID}, TID={data.ThreadID}";
                                _eventQueue.Enqueue((message, Brushes.Blue));
                            };
                            
                            session.Source.Kernel.ThreadStop += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string message = $"[{timestamp}] THREAD STOP: PID={data.ProcessID}, TID={data.ThreadID}";
                                _eventQueue.Enqueue((message, Brushes.Purple));
                            };
                            
                            session.Source.Kernel.ImageLoad += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                // Observe whether the ground-side inference module loads the Windows core color management library (such as mscms.dll / icm). 
                                // Alternatively, Ollama can load GPU computing libraries (such as nvcuda.dll / opencl.dll).
                                string fileName = data.FileName;
                                bool isAiTarget = fileName.Contains("mscms", StringComparison.OrdinalIgnoreCase) || 
                                                 fileName.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
                                                 fileName.Contains("WmiQueryTool", StringComparison.OrdinalIgnoreCase);

                                if (isAiTarget)
                                {
                                    string message = $"⚡ [{timestamp}] 【AI 核心模組載入】IMAGE LOAD: {Path.GetFileName(fileName)} (PID={data.ProcessID}, BaseAddr=0x{data.ImageBase:x})";
                                    _eventQueue.Enqueue((message, Brushes.Gold));
                                }
                                else
                                {
                                    string message = $"[{timestamp}] IMAGE LOAD: {fileName} (PID={data.ProcessID}, BaseAddr=0x{data.ImageBase:x})";
                                    _eventQueue.Enqueue((message, Brushes.DarkOrange));
                                }
                            };

                            // Update UI
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _isLiveRunning = true;
                                txtStatus.Text = "Live session running";
                                _uiTimer.Start();
                                UpdateUiState();
                            }));

                            // This will block until the session ends or is disposed of.
                            session.Source.Process();
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show($"Live session 發生錯誤：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }));
                    }
                    finally
                    {
                        // Cleanup and status restore
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _isLiveRunning = false;
                            _liveSession = null;
                            txtStatus.Text = "Live session stopped";
                            ProcessQueue();
                            _uiTimer.Stop();
                            UpdateUiState();
                        }));
                    }
                });

                txtStatus.Text = "Starting live session...";
                UpdateUiState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"無法啟動 Live session：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopLiveSession()
        {
            if (!_isLiveRunning && _liveSession == null) return;

            try
            {
                // Disposing of a session will cause session.Source.Process() to return a value.
                _liveSession?.Dispose();
                _liveSession = null;
                txtStatus.Text = "Stopping live session...";
                UpdateUiState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止 Live session 發生錯誤：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region ETL Read

        private void BtnOpenEtl_Click(object sender, RoutedEventArgs e)
        {
            if (_isLiveRunning)
            {
                MessageBox.Show("目前正在執行 Live session，請先停止 Live。", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Filter = "ETL files (*.etl)|*.etl|All files (*.*)|*.*",
                Title = "Select ETL file"
            };

            if (dlg.ShowDialog() == true)
            {
                string path = dlg.FileName;
                listBoxEvents.Items.Clear();
                txtEventCount.Text = "Events: 0 | Pending: 0";
                StartReadingEtl(path);
            }
        }

        private void BtnStopEtl_Click(object sender, RoutedEventArgs e)
        {
            StopReadingEtl();
        }

        private void StartReadingEtl(string etlPath)
        {
            if (_isEtlRunning) return;

            _etlCts = new CancellationTokenSource();
            CancellationToken ct = _etlCts.Token;

            _etlTask = Task.Run(() =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isEtlRunning = true;
                        txtStatus.Text = $"Reading ETL: {System.IO.Path.GetFileName(etlPath)}";
                        _uiTimer.Start();
                        UpdateUiState();
                    }));

                    // Use ETWTraceEventSource
                    using (var source = new ETWTraceEventSource(etlPath))
                    {
                        var kernel = new KernelTraceEventParser(source);

                        kernel.ProcessStart += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] START: {data.ProcessName} (PID={data.ProcessID}, TID={data.ThreadID})";
                            _eventQueue.Enqueue((message, Brushes.Green));
                        };

                        kernel.ProcessStop += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] STOP: {data.ProcessName} (PID={data.ProcessID}, TID={data.ThreadID})";
                            _eventQueue.Enqueue((message, Brushes.Red));
                        };

                        kernel.ThreadStart += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] THREAD START: PID={data.ProcessID}, TID={data.ThreadID}";
                            _eventQueue.Enqueue((message, Brushes.Blue));
                        };
                        
                        kernel.ThreadStop += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] THREAD STOP: PID={data.ProcessID}, TID={data.ThreadID}";
                            _eventQueue.Enqueue((message, Brushes.Purple));
                        };
                        
                        kernel.ImageLoad += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] IMAGE LOAD: {data.FileName} (PID={data.ProcessID}, BaseAddr=0x{data.ImageBase:x})";
                            _eventQueue.Enqueue((message, Brushes.DarkOrange));
                        };

                        // Synchronous processing of ETL until completion or cancellation
                        source.Process();
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        txtStatus.Text = "Finished reading ETL";
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show($"讀取 ETL 發生錯誤：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        txtStatus.Text = "Error reading ETL";
                    }));
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isEtlRunning = false;
                        _etlCts?.Dispose();
                        _etlCts = null;
                        ProcessQueue();
                        _uiTimer.Stop();
                        UpdateUiState();
                    }));
                }
            }, ct);
        }

        private void StopReadingEtl()
        {
            if (!_isEtlRunning) return;

            try
            {
                _etlCts?.Cancel();
                // The Process() method of ETWTraceEventSource is synchronous, and Cancel may not interrupt immediately.
                // If a stronger interrupt is required, consider using ETLX or an external control flow.
                txtStatus.Text = "Stopping ETL read...";
                UpdateUiState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止 ETL 發生錯誤：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Queue

        // Executed on the UI thread by _uiTimer: batch-drains the queue and
        // updates the ListBox once per tick instead of once per ETW event.
        private void ProcessQueue()
        {
            ListBoxItem? lastItem = null;

            while (_eventQueue.TryDequeue(out var evt))
            {
                lastItem = new ListBoxItem
                {
                    Content = evt.Message,
                    Foreground = evt.Color
                };
                listBoxEvents.Items.Add(lastItem);
            }

            if (lastItem == null) return;

            // Keep listBoxEvents as a fixed-size ring buffer so long-running sessions don't grow it unbounded.
            while (listBoxEvents.Items.Count > MaxDisplayedEvents)
            {
                listBoxEvents.Items.RemoveAt(0);
            }

            txtEventCount.Text = $"Events: {listBoxEvents.Items.Count} | Pending: {_eventQueue.Count}";
            listBoxEvents.ScrollIntoView(lastItem);
        }

        #endregion

        // Make sure to clean up when the window is closed.
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                StopLiveSession();
                StopReadingEtl();
                _uiTimer.Stop();
            }
            catch { }
        }
    }
}