using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace WpfEtwDemo
{
    public partial class MainWindow : Window
    {
        // Queue 存純資料（字串 + 顏色）
        private readonly ConcurrentQueue<(string Message, Brush Color)> _eventQueue = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // 啟動 ETW consumer 在背景 thread
            Task.Run(() =>
            {
                using (var session = new TraceEventSession("WpfEtwDemoSession"))
                {
                    session.StopOnDispose = true;
                    session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

                    session.Source.Kernel.ProcessStart += data =>
                    {
                        string timestamp = DateTime.Now.ToString("HH:mm:ss");
                        string message = $"[{timestamp}] START: {data.ProcessName} (PID={data.ProcessID})";

                        _eventQueue.Enqueue((message, Brushes.Green));

                        // 事件驅動：通知 UI 消費 queue
                        Dispatcher.BeginInvoke(new Action(ProcessQueue));
                    };

                    session.Source.Kernel.ProcessStop += data =>
                    {
                        string timestamp = DateTime.Now.ToString("HH:mm:ss");
                        string message = $"[{timestamp}] STOP: {data.ProcessName} (PID={data.ProcessID})";

                        _eventQueue.Enqueue((message, Brushes.Red));

                        Dispatcher.BeginInvoke(new Action(ProcessQueue));
                    };

                    session.Source.Process();
                }
            });
        }

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
    }
}