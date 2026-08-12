using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WorkMonitorSwitcher
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize(); // <-- Keep this if it's already part of your project

            using var singleInstance = new Mutex(
                initiallyOwned: true,
                name: @"Local\WorkMonitorSwitcher.SingleInstance",
                createdNew: out var createdNew);

            if (!createdNew)
            {
                SingleInstanceMessenger.NotifyExistingInstance();
                return;
            }

            Application.Run(new Form1());
        }
    }

    internal static class SingleInstanceMessenger
    {
        internal const string ShowExistingWindowEventName = @"Local\WorkMonitorSwitcher.ShowExistingWindowEvent";

        private static readonly IntPtr HwndBroadcast = new(0xffff);

        internal static readonly int ShowExistingWindowMessage =
            RegisterWindowMessage("WorkMonitorSwitcher.ShowExistingWindow");

        internal static void NotifyExistingInstance()
        {
            try
            {
                using var signal = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: ShowExistingWindowEventName);
                signal.Set();
            }
            catch
            {
                // Fall back to the window message below if the event is not available yet.
            }

            if (ShowExistingWindowMessage == 0)
                return;

            PostMessage(HwndBroadcast, ShowExistingWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
