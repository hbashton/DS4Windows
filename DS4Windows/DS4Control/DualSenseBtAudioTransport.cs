using DS4Windows.InputDevices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows
{
    public sealed class DualSenseBtAudioTransport : IDisposable
    {
        private const int ReportLength = 142;
        private const byte ReportId = 0x32;
        private const int SampleRate = 3000;
        private const int Channels = 2;
        private const int SampleBytesPerReport = 64;
        private const int SampleOffset = 13;
        private const int PayloadLengthWithoutCrc = ReportLength - sizeof(uint);
        private static readonly byte[] CrcSeedHead = { 0xA2 };

        private readonly object syncRoot = new object();
        private readonly Queue<byte> pendingSamples = new Queue<byte>(SampleBytesPerReport * 8);
        private readonly byte[] report = new byte[ReportLength];
        private readonly DualSenseDevice device;
        private readonly Thread workerThread;
        private bool running = true;
        private byte sequence;
        private byte packetCounter;
        private int successfulReports;
        private int failedReports;
        private DateTime lastFailureLog = DateTime.MinValue;

        public DualSenseBtAudioTransport(DualSenseDevice device)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            BuildStaticReport();
            workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "DualSense BT audio transport",
            };
            workerThread.Start();
        }

        public static int TargetSampleRate => SampleRate;
        public static int TargetChannels => Channels;

        public void AddSamples(byte[] samples, int offset, int count)
        {
            if (samples == null || count <= 0)
            {
                return;
            }

            lock (syncRoot)
            {
                int maxQueuedBytes = SampleBytesPerReport * 24;
                while (pendingSamples.Count > maxQueuedBytes)
                {
                    pendingSamples.Dequeue();
                }

                int end = Math.Min(samples.Length, offset + count);
                for (int i = Math.Max(0, offset); i < end; i++)
                {
                    pendingSamples.Enqueue(samples[i]);
                }
            }
        }

        public void Dispose()
        {
            running = false;
            try
            {
                workerThread.Join(250);
            }
            catch { }
        }

        private void BuildStaticReport()
        {
            Array.Clear(report, 0, report.Length);
            report[0] = ReportId;
            report[1] = 0x00;
            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = 0xFE;
            report[5] = 0x00;
            report[6] = 0x00;
            report[7] = 0x00;
            report[8] = 0x00;
            report[9] = 0xFF;
            report[10] = 0x00;
            report[11] = 0x92;
            report[12] = SampleBytesPerReport;
        }

        private void WorkerLoop()
        {
            Stopwatch clock = Stopwatch.StartNew();
            long intervalTicks = Stopwatch.Frequency * SampleBytesPerReport /
                (long)(SampleRate * Channels);
            long nextTick = clock.ElapsedTicks + 1;

            while (running)
            {
                long now = clock.ElapsedTicks;
                if (now < nextTick)
                {
                    int sleepMs = (int)Math.Min(4,
                        Math.Max(0, (nextTick - now) * 1000 / Stopwatch.Frequency));
                    if (sleepMs > 0)
                    {
                        Thread.Sleep(sleepMs);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                    continue;
                }

                WriteNextReport();
                nextTick += intervalTicks;
                if (clock.ElapsedTicks - nextTick > intervalTicks * 4)
                {
                    nextTick = clock.ElapsedTicks + intervalTicks;
                }
            }
        }

        private void WriteNextReport()
        {
            report[1] = (byte)((sequence++ & 0x0F) << 4);
            report[10] = packetCounter++;

            lock (syncRoot)
            {
                for (int i = 0; i < SampleBytesPerReport; i++)
                {
                    report[SampleOffset + i] = pendingSamples.Count > 0 ? pendingSamples.Dequeue() : (byte)0x00;
                }
            }

            WriteCrc(report);

            bool success = device.TryWriteBtAudioReport(report);
            if (success)
            {
                successfulReports++;
                return;
            }

            failedReports++;
            DateTime now = DateTime.UtcNow;
            if ((now - lastFailureLog).TotalSeconds >= 2)
            {
                lastFailureLog = now;
                AppLogger.LogToGui(
                    $"DualSense BT audio HID write failed. Success={successfulReports} Fail={failedReports} ReportLength={ReportLength}",
                    true);
            }
        }

        private static void WriteCrc(byte[] buffer)
        {
            uint calcCrc32 = ~Crc32Algorithm.Compute(CrcSeedHead);
            int crcSize = PayloadLengthWithoutCrc;
            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref buffer, 0, crcSize);
            int crcOffset = ReportLength - sizeof(uint);
            buffer[crcOffset] = (byte)calcCrc32;
            buffer[crcOffset + 1] = (byte)(calcCrc32 >> 8);
            buffer[crcOffset + 2] = (byte)(calcCrc32 >> 16);
            buffer[crcOffset + 3] = (byte)(calcCrc32 >> 24);
        }
    }
}
