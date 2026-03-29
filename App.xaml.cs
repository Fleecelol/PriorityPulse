using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace PriorityPulse
{
    public partial class App : Application
    {
        private Window? _window;
        private static Mutex? _mutex;

        public App() => InitializeComponent();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // single instance check
            _mutex = new Mutex(true, "PriorityPulse_SingleInstance", out bool isNew);
            if (!isNew)
            {
                // another instance is already running
                Environment.Exit(0);
                return;
            }

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
