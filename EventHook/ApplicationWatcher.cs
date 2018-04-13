﻿using System;
using System.Collections.Generic;
using System.Linq;
using EventHook.Hooks;
using EventHook.Helpers;
using Nito.AsyncEx;
using System.Threading.Tasks;
using System.Threading;

namespace EventHook
{
    /// <summary>
    /// An enum for the type of application event
    /// </summary>
    public enum ApplicationEvents
    {
        Launched,
        Closed,
        Activated
    }

    /// <summary>
    /// An object that holds information on application event
    /// </summary>
    public class WindowData
    {
        public IntPtr HWnd;
        public int EventType;

        public string AppPath { get; set; }
        public string AppName { get; set; }
        public string AppTitle { get; set; }
    }

    /// <summary>
    /// An event argument object send to user
    /// </summary>
    public class ApplicationEventArgs : EventArgs
    {
        public WindowData ApplicationData { get; set; }
        public ApplicationEvents Event { get; set; }
    }

    /// <summary>
    /// A wrapper around shell hook to hook application window change events
    /// Uses a producer-consumer pattern to improve performance and to avoid operating system forcing unhook on delayed user callbacks
    /// </summary>
    public class ApplicationWatcher
    {
        /*Application history*/
        private object accesslock = new object();
        private bool isRunning;

        private AsyncCollection<object> appQueue;

        private List<WindowData> activeWindows;
        private DateTime prevTimeApp;

        public event EventHandler<ApplicationEventArgs> OnApplicationWindowChange;

        private SyncFactory factory;
        private WindowHook windowHook;

        internal ApplicationWatcher(SyncFactory factory)
        {
            this.factory = factory;
        }
        /// <summary>
        /// Start to watch
        /// </summary>
        public void Start()
        {
            lock (accesslock)
            {
                if (!isRunning)
                {
                    activeWindows = new List<WindowData> { };
                    prevTimeApp = DateTime.Now;
                   
                    appQueue = new AsyncCollection<object>();

                    //This needs to run on UI thread context
                    //So use task factory with the shared UI message pump thread
                    Task.Factory.StartNew(() =>
                    {
                        windowHook = new WindowHook(factory);
                        windowHook.WindowCreated += new GeneralShellHookEventHandler(WindowCreated);
                        windowHook.WindowDestroyed += new GeneralShellHookEventHandler(WindowDestroyed);
                        windowHook.WindowActivated += new GeneralShellHookEventHandler(WindowActivated);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    factory.GetTaskScheduler()).Wait();

                    lastEventWasLaunched = false;
                    lastHwndLaunched = IntPtr.Zero;

                    Task.Factory.StartNew(() => AppConsumer());
                    isRunning = true;
                }
            }

        }

        /// <summary>
        /// Quit watching
        /// </summary>
        public void Stop()
        {
            lock (accesslock)
            {
                if (isRunning)
                {

                    //This needs to run on UI thread context
                    //So use task factory with the shared UI message pump thread
                    Task.Factory.StartNew(() =>
                    {
                        windowHook.WindowCreated -= new GeneralShellHookEventHandler(WindowCreated);
                        windowHook.WindowDestroyed -= new GeneralShellHookEventHandler(WindowDestroyed);
                        windowHook.WindowActivated -= new GeneralShellHookEventHandler(WindowActivated);
                        windowHook.Destroy();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    factory.GetTaskScheduler()).Wait();

                    appQueue.Add(false);
                    isRunning = false;
                }
            }

        }

        /// <summary>
        /// A windows was created on desktop
        /// </summary>
        /// <param name="shellObject"></param>
        /// <param name="hWnd"></param>
        private void WindowCreated(ShellHook shellObject, IntPtr hWnd)
        {
            appQueue.Add(new WindowData() { HWnd = hWnd, EventType = 0 });
        }

        /// <summary>
        /// An existing desktop window was destroyed
        /// </summary>
        /// <param name="shellObject"></param>
        /// <param name="hWnd"></param>
        private void WindowDestroyed(ShellHook shellObject, IntPtr hWnd)
        {
            appQueue.Add(new WindowData() { HWnd = hWnd, EventType = 2 });
        }

        /// <summary>
        /// A windows was brought to foreground
        /// </summary>
        /// <param name="shellObject"></param>
        /// <param name="hWnd"></param>
        private void WindowActivated(ShellHook shellObject, IntPtr hWnd)
        {
            appQueue.Add(new WindowData() { HWnd = hWnd, EventType = 1 });
        }

        /// <summary>
        /// This is used to avoid blocking low level hooks
        /// Otherwise if user takes long time to return the message
        /// OS will unsubscribe the hook
        /// Producer-consumer
        /// </summary>
        /// <returns></returns>
        private async Task AppConsumer()
        {
            while (isRunning)
            {
                //blocking here until a key is added to the queue
                var item = await appQueue.TakeAsync();
                if (item is bool) break;

                var wnd = (WindowData)item;
                switch (wnd.EventType)
                {
                    case 0:
                        WindowCreated(wnd);
                        break;
                    case 1:
                        WindowActivated(wnd);
                        break;
                    case 2:
                        WindowDestroyed(wnd);
                        break;
                }

            }
        }

        /// <summary>
        /// A handle to keep track of last window launched
        /// </summary>
        private IntPtr lastHwndLaunched;

        /// <summary>
        /// A window got created
        /// </summary>
        /// <param name="wnd"></param>
        private void WindowCreated(WindowData wnd)
        {

            activeWindows.Add(wnd);
            ApplicationStatus(wnd, ApplicationEvents.Launched);

            lastEventWasLaunched = true;
            lastHwndLaunched = wnd.HWnd;

        }

        /// <summary>
        /// invoke user call back
        /// </summary>
        /// <param name="wnd"></param>
        /// <param name="appEvent"></param>
        private void ApplicationStatus(WindowData wnd, ApplicationEvents appEvent)
        {
            var timeStamp = DateTime.Now;

            wnd.AppTitle = appEvent == ApplicationEvents.Closed ? wnd.AppTitle : WindowHelper.GetWindowText(wnd.HWnd);
            wnd.AppPath = appEvent == ApplicationEvents.Closed ? wnd.AppPath : WindowHelper.GetAppPath(wnd.HWnd);
            wnd.AppName = appEvent == ApplicationEvents.Closed ? wnd.AppName : WindowHelper.GetAppDescription(wnd.AppPath);

            OnApplicationWindowChange?.Invoke(null, new ApplicationEventArgs() { ApplicationData = wnd, Event = appEvent });
        }

        /// <summary>
        /// Remove handle from active window collection
        /// </summary>
        /// <param name="wnd"></param>
        private void WindowDestroyed(WindowData wnd)
        {
            ApplicationStatus(activeWindows.FirstOrDefault(x => x.HWnd == wnd.HWnd) ?? wnd, ApplicationEvents.Closed);
            activeWindows.RemoveAll(x => x.HWnd == wnd.HWnd);
            lastEventWasLaunched = false;
        }

        /// <summary>
        /// Add window handle to active windows collection
        /// </summary>
        private bool lastEventWasLaunched;
        private void WindowActivated(WindowData wnd)
        {
            if (activeWindows.Any(x => x.HWnd == wnd.HWnd))
            {
                if ((!lastEventWasLaunched) && lastHwndLaunched != wnd.HWnd)
                {
                    ApplicationStatus(activeWindows.First(x => x.HWnd == wnd.HWnd), ApplicationEvents.Activated);
                }
            }
            lastEventWasLaunched = false;
        }

    }
}
