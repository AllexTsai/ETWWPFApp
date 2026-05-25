using System;
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
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                using (var session = new TraceEventSession("WpfEtwDemoSession"))
                {
                    session.StopOnDispose = true;
                    session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

                    session.Source.Kernel.ProcessStart += data =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            string timestamp = DateTime.Now.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] START: {data.ProcessName} (PID={data.ProcessID})";

                            var item = new ListBoxItem
                            {
                                Content = message,
                                Foreground = Brushes.Green
                            };
                            listBoxEvents.Items.Add(item);
                            listBoxEvents.ScrollIntoView(item);
                        });
                    };

                    session.Source.Kernel.ProcessStop += data =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            string timestamp = DateTime.Now.ToString("HH:mm:ss");
                            string message = $"[{timestamp}] STOP: {data.ProcessName} (PID={data.ProcessID})";

                            var item = new ListBoxItem
                            {
                                Content = message,
                                Foreground = Brushes.Red
                            };
                            listBoxEvents.Items.Add(item);
                            listBoxEvents.ScrollIntoView(item);
                        });
                    };

                    session.Source.Process();
                }
            });
        }
    }
}