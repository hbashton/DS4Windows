using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DS4Windows;

namespace DS4WindowsTests
{
    [TestClass]
    public class ViiperInputSchedulingTests
    {
        [TestMethod]
        public void AnalogUpdatesCoalesceWithoutErasingDiscreteEdges()
        {
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(2, 4);
            byte[] neutral = { 1, 0 };
            byte[] newerNeutral = { 2, 0 };
            byte[] pressed = { 3, 1 };
            byte[] held = { 4, 1 };
            byte[] released = { 5, 0 };

            AssertEnqueued(queue, neutral, 1, 0, false);
            AssertEnqueued(queue, newerNeutral, 2, 0, true);
            AssertEnqueued(queue, pressed, 3, 1, false);
            AssertEnqueued(queue, held, 4, 1, true);
            AssertEnqueued(queue, released, 5, 0, false);
            Assert.AreEqual(3, queue.Count);

            AssertDequeued(queue, newerNeutral, 2);
            AssertDequeued(queue, held, 4);
            AssertDequeued(queue, released, 5);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void FailedPacketRetryPrecedesACompletelyFullNewerRing()
        {
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(1, 2);
            AssertEnqueued(queue, new byte[] { 2 }, 2, 2, false);
            AssertEnqueued(queue, new byte[] { 3 }, 3, 3, false);
            Assert.IsTrue(queue.TryQueueRetry(new byte[] { 1 }, 1));
            Assert.AreEqual(3, queue.Count);

            AssertDequeued(queue, new byte[] { 1 }, 1);
            AssertDequeued(queue, new byte[] { 2 }, 2);
            AssertDequeued(queue, new byte[] { 3 }, 3);
        }

        [TestMethod]
        public void FullDistinctEdgeQueueFailsClosedInsteadOfDroppingAnEdge()
        {
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(1, 2);
            AssertEnqueued(queue, new byte[] { 1 }, 1, 1, false);
            AssertEnqueued(queue, new byte[] { 2 }, 2, 2, false);

            Assert.IsFalse(queue.TryEnqueue(new byte[] { 3 }, 3, 3,
                out bool coalesced));
            Assert.IsFalse(coalesced);
            Assert.AreEqual(2, queue.Count);
            AssertDequeued(queue, new byte[] { 1 }, 1);
            AssertDequeued(queue, new byte[] { 2 }, 2);
        }

        [TestMethod]
        public void LatestStateCanBeReplayedWithoutAllocatingAnotherSlot()
        {
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(2, 2);
            byte[] latest = { 8, 9 };
            AssertEnqueued(queue, latest, 10, 7, false);
            AssertDequeued(queue, latest, 10);

            Assert.IsTrue(queue.EnsureLatestQueued(20));
            AssertDequeued(queue, latest, 20);
        }

        [TestMethod]
        public void QueueSteadyStateUsesOnlyPreallocatedStorage()
        {
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(33, 4);
            byte[] input = new byte[33];
            byte[] output = new byte[33];
            Assert.IsTrue(queue.TryEnqueue(input, 1, 0, out _));
            Assert.IsTrue(queue.TryDequeue(output, out _));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                input[0] = (byte)i;
                Assert.IsTrue(queue.TryEnqueue(input, i + 2, 0, out _));
                Assert.IsTrue(queue.TryDequeue(output, out _));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated);
        }

        [TestMethod]
        public void WaitingInputPassesQueuedMediaThenMediaMakesProgress()
        {
            ViiperPriorityWriteScheduler scheduler =
                new ViiperPriorityWriteScheduler();
            ConcurrentQueue<string> order = new ConcurrentQueue<string>();
            ManualResetEventSlim inputEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim releaseInput = new ManualResetEventSlim(false);
            ManualResetEventSlim mediaEntered = new ManualResetEventSlim(false);

            scheduler.EnterMedia();
            Thread media = new Thread(() =>
            {
                scheduler.EnterMedia();
                order.Enqueue("media");
                mediaEntered.Set();
                scheduler.Exit();
            });
            Thread input = new Thread(() =>
            {
                scheduler.EnterInput();
                order.Enqueue("input");
                inputEntered.Set();
                releaseInput.Wait();
                scheduler.Exit();
            });
            media.Start();
            input.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                scheduler.WaitingInput == 1 && scheduler.WaitingMedia == 1,
                2000));

            scheduler.Exit();
            Assert.IsTrue(inputEntered.Wait(2000));
            Assert.IsFalse(mediaEntered.IsSet,
                "Queued microphone/media work passed a waiting input report.");
            releaseInput.Set();
            Assert.IsTrue(mediaEntered.Wait(2000));
            Assert.IsTrue(input.Join(2000));
            Assert.IsTrue(media.Join(2000));
            CollectionAssert.AreEqual(new[] { "input", "media" },
                order.ToArray());
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void ImmediateMappedSubmissionMicrobenchmarkIsAllocationFree()
        {
            const int iterations = 50000;
            ViiperVirtualDeviceType type = ViiperVirtualDeviceType.DualSense;
            ViiperInputPacketQueue queue = new ViiperInputPacketQueue(
                ViiperStatePacketBuilder.GetPacketLength(type), 4);
            DS4State state = ViiperStatePacketBuilder.CreateNeutralState();
            byte[] mapped = new byte[queue.PacketLength];
            byte[] submitted = new byte[queue.PacketLength];
            long[] samples = new long[iterations];

            for (int i = 0; i < 1000; i++)
            {
                state.LX = (byte)i;
                ViiperStatePacketBuilder.BuildInto(type, state, -1, mapped);
                ulong signature =
                    ViiperStatePacketBuilder.GetEdgeSignature(type, mapped);
                queue.TryEnqueue(mapped, i, signature, out _);
                queue.TryDequeue(submitted, out _);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                state.LX = (byte)i;
                long started = Stopwatch.GetTimestamp();
                ViiperStatePacketBuilder.BuildInto(type, state, -1, mapped);
                ulong signature =
                    ViiperStatePacketBuilder.GetEdgeSignature(type, mapped);
                if (!queue.TryEnqueue(mapped, started, signature, out _) ||
                    !queue.TryDequeue(submitted, out _))
                {
                    Assert.Fail("The immediate submission queue stopped making progress.");
                }
                samples[i] = Stopwatch.GetTimestamp() - started;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() -
                allocatedBefore;

            Array.Sort(samples);
            double tickNanoseconds = 1_000_000_000.0 /
                Stopwatch.Frequency;
            double p50 = samples[iterations / 2] * tickNanoseconds;
            double p95 = samples[iterations * 95 / 100] * tickNanoseconds;
            double p99 = samples[iterations * 99 / 100] * tickNanoseconds;
            Console.WriteLine(
                $"VIIPER immediate mapped submission ({iterations} samples): p50={p50:F0}ns p95={p95:F0}ns p99={p99:F0}ns allocations={allocated}B");

            Assert.AreEqual(0L, allocated);
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void SharedMemoryAndLoopbackTcpTransportBenchmark()
        {
            const int iterations = 10000;
            byte[] payload = new byte[33];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 17 + 3);
            }

            long[] tcp = BenchmarkLoopbackTcp(iterations, payload);
            long[] sharedMemory = BenchmarkSharedMemory(iterations, payload);
            string tcpSummary = FormatLatencySummary(tcp);
            string sharedMemorySummary = FormatLatencySummary(sharedMemory);

            Console.WriteLine(
                $"VIIPER 33-byte persistent transport round trip ({iterations} samples): loopback-tcp {tcpSummary}; shared-memory {sharedMemorySummary}");
            Assert.AreEqual(iterations, tcp.Length);
            Assert.AreEqual(iterations, sharedMemory.Length);
        }

        private static long[] BenchmarkLoopbackTcp(int iterations,
            byte[] payload)
        {
            const int warmup = 1000;
            int total = warmup + iterations;
            using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            Exception workerError = null;
            Thread worker = new Thread(() =>
            {
                try
                {
                    using TcpClient accepted = listener.AcceptTcpClient();
                    accepted.NoDelay = true;
                    using NetworkStream stream = accepted.GetStream();
                    byte[] request = new byte[4 + payload.Length];
                    byte[] response = new byte[4];
                    for (int i = 0; i < total; i++)
                    {
                        ReadExactly(stream, request);
                        Buffer.BlockCopy(request, 0, response, 0, 4);
                        stream.Write(response, 0, response.Length);
                    }
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "VIIPER loopback benchmark responder",
            };
            worker.Start();

            long[] samples = new long[iterations];
            using (TcpClient client = new TcpClient())
            {
                client.NoDelay = true;
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                using NetworkStream stream = client.GetStream();
                byte[] request = new byte[4 + payload.Length];
                byte[] response = new byte[4];
                Buffer.BlockCopy(payload, 0, request, 4, payload.Length);
                for (int i = 0; i < total; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    BinaryPrimitives.WriteInt32LittleEndian(
                        request.AsSpan(0, 4), i);
                    stream.Write(request, 0, request.Length);
                    ReadExactly(stream, response);
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    Assert.AreEqual(i,
                        BinaryPrimitives.ReadInt32LittleEndian(response));
                    if (i >= warmup)
                    {
                        samples[i - warmup] = elapsed;
                    }
                }
            }

            listener.Stop();
            Assert.IsTrue(worker.Join(5000));
            if (workerError != null)
            {
                Assert.Fail(workerError.ToString());
            }
            return samples;
        }

        private static long[] BenchmarkSharedMemory(int iterations,
            byte[] payload)
        {
            const int warmup = 1000;
            int total = warmup + iterations;
            using MemoryMappedFile mapping = MemoryMappedFile.CreateNew(
                null, 8 + payload.Length, MemoryMappedFileAccess.ReadWrite);
            using MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
                0, 8 + payload.Length, MemoryMappedFileAccess.ReadWrite);
            using AutoResetEvent request = new AutoResetEvent(false);
            using AutoResetEvent response = new AutoResetEvent(false);
            Exception workerError = null;
            Thread worker = new Thread(() =>
            {
                try
                {
                    byte[] received = new byte[payload.Length];
                    for (int i = 0; i < total; i++)
                    {
                        request.WaitOne();
                        int sequence = view.ReadInt32(0);
                        view.ReadArray(8, received, 0, received.Length);
                        view.Write(4, sequence);
                        response.Set();
                    }
                }
                catch (Exception ex)
                {
                    workerError = ex;
                    response.Set();
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "VIIPER shared-memory benchmark responder",
            };
            worker.Start();

            long[] samples = new long[iterations];
            for (int i = 0; i < total; i++)
            {
                long started = Stopwatch.GetTimestamp();
                view.Write(0, i);
                view.WriteArray(8, payload, 0, payload.Length);
                request.Set();
                Assert.IsTrue(response.WaitOne(5000));
                long elapsed = Stopwatch.GetTimestamp() - started;
                if (workerError != null)
                {
                    Assert.Fail(workerError.ToString());
                }
                Assert.AreEqual(i, view.ReadInt32(4));
                if (i >= warmup)
                {
                    samples[i - warmup] = elapsed;
                }
            }

            Assert.IsTrue(worker.Join(5000));
            return samples;
        }

        private static string FormatLatencySummary(long[] samples)
        {
            Array.Sort(samples);
            double tickMicroseconds = 1_000_000.0 / Stopwatch.Frequency;
            double p50 = samples[samples.Length / 2] * tickMicroseconds;
            double p95 = samples[samples.Length * 95 / 100] * tickMicroseconds;
            double p99 = samples[samples.Length * 99 / 100] * tickMicroseconds;
            double maximum = samples[^1] * tickMicroseconds;
            return $"p50={p50:F1}us p95={p95:F1}us p99={p99:F1}us max={maximum:F1}us";
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
            }
        }

        private static void AssertEnqueued(ViiperInputPacketQueue queue,
            byte[] packet, long queuedAt, ulong signature, bool expectedCoalesced)
        {
            Assert.IsTrue(queue.TryEnqueue(packet, queuedAt, signature,
                out bool coalesced));
            Assert.AreEqual(expectedCoalesced, coalesced);
        }

        private static void AssertDequeued(ViiperInputPacketQueue queue,
            byte[] expected, long expectedQueuedAt)
        {
            byte[] actual = new byte[queue.PacketLength];
            Assert.IsTrue(queue.TryDequeue(actual, out long queuedAt));
            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(expectedQueuedAt, queuedAt);
        }
    }
}
