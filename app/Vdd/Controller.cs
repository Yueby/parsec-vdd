﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ParsecVDisplay.Vdd
{
    internal static class Controller
    {
        static Thread UpdateThread;
        static Thread StatusThread;
        static CancellationTokenSource Cancellation;
        static IntPtr VddHandle = IntPtr.Zero;

        static Device.Status LastStatus;

        public static void Start()
        {
            Cancellation = new CancellationTokenSource();

            UpdateThread = new Thread(() => UpdateLoop(Cancellation.Token));
            UpdateThread.IsBackground = true;
            UpdateThread.Priority = ThreadPriority.Highest;

            StatusThread = new Thread(() => StatusLoop(Cancellation.Token));
            StatusThread.IsBackground = true;
            StatusThread.Priority = ThreadPriority.BelowNormal;

            UpdateThread.Start();
            StatusThread.Start();
        }

        public static void Stop()
        {
            Cancellation?.Cancel();
            UpdateThread?.Join();
            StatusThread?.Join();

            Device.CloseHandle(VddHandle);
        }

        static void UpdateLoop(CancellationToken cancellation)
        {
            while (true)
            {
                if (cancellation.IsCancellationRequested)
                    break;

                if (VddHandle.IsValidHandle() && LastStatus == Device.Status.OK)
                    Core.Update(VddHandle);

                Thread.Sleep(100);
            }
        }

        static void StatusLoop(CancellationToken cancellation)
        {
            bool first = true;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                if (cancellation.IsCancellationRequested)
                    break;

                if (first || sw.ElapsedMilliseconds >= 2000)
                {
                    first = false;

                    var status = QueryStatus(out var _);
                    unsafe
                    {
                        fixed (Device.Status* s = &LastStatus)
                        {
                            Interlocked.Exchange(ref *(int*)s, (int)status);
                        }
                    }

                    if (status == Device.Status.OK)
                    {
                        if (!VddHandle.IsValidHandle())
                        {
                            Device.OpenHandle(Core.ADAPTER_GUID, out var handle);
                            Interlocked.Exchange(ref VddHandle, handle);
                        }
                    }
                    else
                    {
                        var handle = VddHandle;
                        Interlocked.Exchange(ref VddHandle, IntPtr.Zero);
                        Device.CloseHandle(handle);
                    }

                    sw.Restart();
                }

                Thread.Sleep(50);
            }
        }

        public static Device.Status QueryStatus(out Version version)
        {
            return Device.QueryStatus(Core.CLASS_GUID, Core.HARDWARE_ID, out version);
        }

        public static Device.Status QueryStatus()
        {
            return QueryStatus(out var _);
        }

        public static void AddDisplay()
        {
            var status = QueryStatus();
            if (status != Device.Status.OK)
                throw new ErrorDriverStatus(status);

            int limit = Core.MAX_DISPLAYS;
            var displays = Core.GetDisplays();

            if (displays.Count >= limit)
            {
                throw new ErrorExceededLimit(limit);
            }
            else
            {
                if (!Core.AddDisplay(VddHandle, out var _))
                {
                    throw new ErrorOperationFailed(ErrorOperationFailed.Operation.AddDisplay);
                }

                // Refresh display configuration to activate the newly created virtual display
                // This eliminates the need for users to manually change display settings
                // Use Task to avoid blocking the main thread
                Task.Run(async () =>
                {
                    await Task.Delay(4000); // Small delay to ensure display is ready
                    Display.RefreshDisplayConfiguration();
                });
            }
        }

        public static void RemoveDisplay(int index)
        {
            var status = QueryStatus();
            if (status != Device.Status.OK)
                throw new ErrorDriverStatus(status);

            if (index >= 0)
            {
                if (!Core.RemoveDisplay(VddHandle, index))
                {
                    throw new ErrorOperationFailed(ErrorOperationFailed.Operation.RemoveDisplay);
                }
            }
        }

        public static void RemoveLastDisplay()
        {
            var displays = Core.GetDisplays();
            if (displays.Count > 0)
            {
                var last = displays[displays.Count - 1];
                RemoveDisplay(last.DisplayIndex);
            }
        }
    }
}