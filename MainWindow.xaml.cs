using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace WpfEtwDemo
{
    public partial class MainWindow : Window
    {
        // Queue 存純資料（字串 + 顏色）
        private readonly ConcurrentQueue<(string Message, Brush Color)> _eventQueue = new();

        // Live session 相關
        private TraceEventSession? _liveSession;
        private Task? _liveTask;

        // ETL 讀取相關
        private Task? _etlTask;
        private CancellationTokenSource? _etlCts;

        // 狀態旗標
        private volatile bool _isLiveRunning = false;
        private volatile bool _isEtlRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            comboMode.SelectionChanged += ComboMode_SelectionChanged;
            UpdateUiState();
        }

        // 切換模式時更新 UI 可用按鈕
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
                // 建立唯一名稱以避免衝突（若需要固定名稱可改回固定字串）
                string sessionName = "WpfEtwDemoLive_" + Guid.NewGuid();

                _liveTask = Task.Run(() =>
                {
                    try
                    {
                        using (var session = new TraceEventSession(sessionName))
                        {
                            _liveSession = session;
                            session.StopOnDispose = true;

                            // 啟用 kernel process provider
                            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

                            session.Source.Kernel.ProcessStart += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string message = $"[{timestamp}] START: {data.ProcessName} (PID={data.ProcessID})";
                                _eventQueue.Enqueue((message, Brushes.Green));
                                Dispatcher.BeginInvoke(new Action(ProcessQueue));
                            };

                            session.Source.Kernel.ProcessStop += data =>
                            {
                                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                                string message = $"[{timestamp}] STOP: {data.ProcessName} (PID={data.ProcessID})";
                                _eventQueue.Enqueue((message, Brushes.Red));
                                Dispatcher.BeginInvoke(new Action(ProcessQueue));
                            };

                            // 更新狀態
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _isLiveRunning = true;
                                txtStatus.Text = "Live session running";
                                UpdateUiState();
                            }));

                            // 這會阻塞直到 session 結束或被 Dispose
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
                        // 清理與狀態回復
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _isLiveRunning = false;
                            _liveSession = null;
                            txtStatus.Text = "Live session stopped";
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
                // Dispose session 會讓 session.Source.Process() 返回
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
                        UpdateUiState();
                    }));

                    // 根據 TraceEvent 版本，這裡使用 ETWTraceEventSource
                    using (var source = new ETWTraceEventSource(etlPath))
                    {
                        var kernel = new KernelTraceEventParser(source);

                        kernel.ProcessStart += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] START: {data.ProcessName} (PID={data.ProcessID})";
                            _eventQueue.Enqueue((message, Brushes.Green));
                            Dispatcher.BeginInvoke(new Action(ProcessQueue));
                        };

                        kernel.ProcessStop += data =>
                        {
                            string timestamp = data.TimeStamp.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] STOP: {data.ProcessName} (PID={data.ProcessID})";
                            _eventQueue.Enqueue((message, Brushes.Red));
                            Dispatcher.BeginInvoke(new Action(ProcessQueue));
                        };

                        // 同步處理 ETL，直到結束或取消
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
                // ETWTraceEventSource 的 Process() 是同步的，Cancel 可能無法立即中斷，
                // 若需要更強的中斷，可考慮使用 ETLX 或外部控制流程。
                txtStatus.Text = "Stopping ETL read...";
                UpdateUiState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止 ETL 發生錯誤：{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Queue 處理

        // 在 UI thread 執行，建立 ListBoxItem 並顯示
        private void ProcessQueue()
        {
            while (_eventQueue.TryDequeue(out var evt))
            {
                var item = new ListBoxItem
                {
                    Content = evt.Message,
                    Foreground = evt.Color
                };
                listBoxEvents.Items.Add(item);
                listBoxEvents.ScrollIntoView(item);
            }
        }

        #endregion

        // 視窗關閉時確保清理
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                StopLiveSession();
                StopReadingEtl();
            }
            catch { }
        }
    }
}