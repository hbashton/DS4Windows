/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Data;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class LogViewModel
    {
        internal const int MaximumLogItems = 1000;

        private readonly ReaderWriterLockSlim _logListLocker = new ReaderWriterLockSlim();
        private readonly ObservableCollection<LogItem> logItems = new ObservableCollection<LogItem>();

        public ObservableCollection<LogItem> LogItems => logItems;

        public ReaderWriterLockSlim LogListLocker => _logListLocker;

        // Tests can exercise the collection without starting controller discovery
        // or subscribing to the process-wide log publisher.
        internal LogViewModel()
        {
            BindingOperations.EnableCollectionSynchronization(logItems, _logListLocker, LogLockCallback);
        }

        public LogViewModel(DS4Windows.ControlService service) : this()
        {
            string version = DS4Windows.Global.exeDisplayVersion;
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"DS4Windows version {version}" });
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"DS4Windows Assembly Architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}" });
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"OS Version: {Environment.OSVersion}" });
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"OS Product Name: {DS4Windows.Util.GetOSProductName()}" });
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"OS Release ID: {DS4Windows.Util.GetOSReleaseId()}" });
            logItems.Add(new LogItem { Datetime = DateTime.Now, Message = $"System Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x32")}" });

            service.Debug += AddLogMessage;
            DS4Windows.AppLogger.GuiLog += AddLogMessage;
        }

        private void LogLockCallback(IEnumerable collection, object context, Action accessMethod, bool writeAccess)
        {
            // A UI-thread Clear raises Reset synchronously; WPF can read the
            // collection again while this thread still owns the write lock.
            if (_logListLocker.IsWriteLockHeld ||
                (!writeAccess && _logListLocker.IsReadLockHeld))
            {
                accessMethod?.Invoke();
                return;
            }

            if (writeAccess)
            {
                using (WriteLocker locker = new WriteLocker(_logListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
            else
            {
                using (ReadLocker locker = new ReadLocker(_logListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
        }

        internal void AddLogMessage(object sender, DS4Windows.DebugEventArgs e)
        {
            LogItem item = new LogItem { Datetime = e.Time, Message = e.Data, Warning = e.Warning };
            using WriteLocker locker = new WriteLocker(_logListLocker);
            // Bound the live UI, including temporary diagnostics. The independent
            // file logger continues receiving the original events unchanged.
            // Remove first so collection observers never see an oversized list.
            while (logItems.Count >= MaximumLogItems)
            {
                logItems.RemoveAt(0);
            }
            logItems.Add(item);
        }

        public void Clear()
        {
            using WriteLocker locker = new WriteLocker(_logListLocker);
            logItems.Clear();
        }

        public List<LogItem> Snapshot()
        {
            using ReadLocker locker = new ReadLocker(_logListLocker);
            return new List<LogItem>(logItems);
        }
    }
}
