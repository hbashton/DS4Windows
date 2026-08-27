using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseInputIsolationTests
    {
        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [TestMethod]
        public void UsbUnknownReportIdIsRejectedWithTelemetryOnly()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            int reportCallbacks = 0;
            device.Report += (_, _) => reportCallbacks++;
            byte[] normal = new byte[64];
            normal[0] = 0x01;
            byte[] unknown = new byte[64];
            unknown[0] = 0x05;

            Assert.IsTrue(device.TryAcceptUsbNormalInputFrame(normal));
            Assert.IsFalse(device.TryAcceptUsbNormalInputFrame(unknown));

            Assert.AreEqual(1L, device.UsbRejectedInputFrames);
            Assert.AreEqual(0x05, device.UsbLastRejectedInputReportId);
            Assert.AreEqual(0, reportCallbacks,
                "Reject telemetry must not invoke an input subscriber.");
        }

        [TestMethod]
        public void BlockingPhysicalOwnersDoNotBlockInputPublicationAndLifecycleOwnsJoin()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            using ManualResetEvent outputEntered = new(false);
            using ManualResetEvent microphoneEntered = new(false);
            using ManualResetEvent releaseOutput = new(false);
            using ManualResetEvent releaseMicrophone = new(false);
            Exception outputFailure = null;
            Exception microphoneFailure = null;

            device.PhysicalOutputWriteTestHook = () =>
            {
                outputEntered.Set();
                releaseOutput.WaitOne();
            };
            device.BluetoothMicrophoneOpusFrameReceived += (_, _) =>
            {
                microphoneEntered.Set();
                releaseMicrophone.WaitOne();
            };

            SetField(device, "physicalOutputGeneration", 41L);
            SetField(device, "physicalOutputStopRequested", 0);
            SetField(device, "bluetoothMicrophoneDispatchStopRequested", 0);
            SetField(device, "bluetoothMicrophoneStreamingRequested", 1);
            SetField(device, "bluetoothMicrophoneRequestGeneration", 7L);

            Thread outputWorker = new(() => InvokeWorker(device,
                "PhysicalOutputLoop", new object[] { 41L },
                ref outputFailure));
            Thread microphoneWorker = new(() => InvokeWorker(device,
                "BluetoothMicrophoneDispatchLoop", new object[] { 41L },
                ref microphoneFailure));
            SetField(device, "physicalOutputThread", outputWorker);
            SetField(device, "bluetoothMicrophoneDispatchThread",
                microphoneWorker);
            outputWorker.Start();
            microphoneWorker.Start();

            Invoke(device, "QueuePhysicalOutputUpdate");
            Assert.IsTrue(outputEntered.WaitOne(1000));
            byte[] microphoneReport = CreateMicrophoneReport();
            Invoke(device, "RecordBluetoothMicrophoneFrame",
                microphoneReport, Stopwatch.GetTimestamp());
            Assert.IsTrue(microphoneEntered.WaitOne(1000));

            ViiperInputScheduler scheduler = new();
            scheduler.Reset(3);
            scheduler.Publish(ViiperMappedInputState.Neutral, 1);
            ViiperMappedInputState pressed = ViiperMappedInputState.Neutral;
            pressed.R2 = 255;
            pressed.Buttons |= ViiperMappedInputState.R2ButtonMask;

            Stopwatch admission = Stopwatch.StartNew();
            Assert.IsTrue(scheduler.Publish(pressed, 2).Accepted);
            Invoke(device, "QueuePhysicalOutputUpdate");
            Invoke(device, "RecordBluetoothMicrophoneFrame",
                microphoneReport, Stopwatch.GetTimestamp());
            admission.Stop();
            Assert.IsTrue(admission.ElapsedMilliseconds < 250,
                $"Input-side bounded publication waited {admission.ElapsedMilliseconds}ms for a blocked owner.");

            using ManualResetEvent lifecycleComplete = new(false);
            Exception lifecycleFailure = null;
            Thread lifecycle = new(() =>
            {
                try
                {
                    Invoke(device, "StopPhysicalWorkersCore");
                }
                catch (Exception ex)
                {
                    lifecycleFailure = ex;
                }
                finally
                {
                    lifecycleComplete.Set();
                }
            });
            lifecycle.Start();
            Assert.IsFalse(lifecycleComplete.WaitOne(50),
                "The dedicated lifecycle path must own the worker retirement barrier.");
            releaseOutput.Set();
            releaseMicrophone.Set();
            Assert.IsTrue(lifecycleComplete.WaitOne(2000));
            lifecycle.Join();

            Assert.IsNull(outputFailure);
            Assert.IsNull(microphoneFailure);
            Assert.IsNull(lifecycleFailure);
            Assert.IsFalse(outputWorker.IsAlive);
            Assert.IsFalse(microphoneWorker.IsAlive);
        }

        [TestMethod]
        public void BlockingDeviceCommandRunsOffInputAdmissionAndCollectionLock()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            using ManualResetEvent commandEntered = new(false);
            using ManualResetEvent releaseCommand = new(false);
            using ManualResetEvent producerCompleted = new(false);
            Exception commandFailure = null;
            SetField(device, "physicalOutputGeneration", 57L);
            SetField(device, "deviceCommandStopRequested", 0);
            Thread commandWorker = new(() => InvokeWorker(device,
                "DeviceCommandLoop", new object[] { 57L },
                ref commandFailure));
            SetField(device, "deviceCommandThread", commandWorker);
            commandWorker.Start();

            device.queueEvent(() =>
            {
                commandEntered.Set();
                releaseCommand.WaitOne();
            });
            Thread inputAdmission = new(() => Invoke(device,
                "DrainQueuedInputEvents"))
            {
                IsBackground = true,
            };
            inputAdmission.Start();
            Assert.IsTrue(inputAdmission.Join(250),
                "The physical input admission path invoked a queued command.");
            Assert.IsTrue(commandEntered.WaitOne(1000));

            Thread producer = new(() =>
            {
                device.queueEvent(() => { });
                producerCompleted.Set();
            })
            {
                IsBackground = true,
            };
            producer.Start();
            Assert.IsTrue(producerCompleted.WaitOne(1000),
                "The command owner invoked a callback while eventQueueLock was held.");

            SetField(device, "deviceCommandStopRequested", 1);
            releaseCommand.Set();
            ((AutoResetEvent)GetField(device, "deviceCommandSignal")).Set();
            Assert.IsTrue(commandWorker.Join(1000));
            producer.Join();
            Assert.IsNull(commandFailure);
        }

        [TestMethod]
        public void BlockingBatterySubscriberRunsOnlyOnDeviceCommandOwner()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            using ManualResetEvent subscriberEntered = new(false);
            using ManualResetEvent releaseSubscriber = new(false);
            Exception commandFailure = null;
            SetField(device, "physicalOutputGeneration", 58L);
            SetField(device, "deviceCommandStopRequested", 0);
            Thread commandWorker = new(() => InvokeWorker(device,
                "DeviceCommandLoop", new object[] { 58L },
                ref commandFailure));
            SetField(device, "deviceCommandThread", commandWorker);
            commandWorker.Start();
            device.BatteryChanged += (_, _) =>
            {
                subscriberEntered.Set();
                releaseSubscriber.WaitOne();
            };
            SetField(device, "deviceStatusNotificationPending",
                GetConstant("DeviceStatusBatteryChanged"));

            Thread inputAdmission = new(() => Invoke(device,
                "DrainQueuedInputEvents"))
            {
                IsBackground = true,
            };
            inputAdmission.Start();
            Assert.IsTrue(inputAdmission.Join(250),
                "The input/report boundary invoked a battery subscriber.");
            Assert.IsTrue(subscriberEntered.WaitOne(1000));

            SetField(device, "deviceCommandStopRequested", 1);
            releaseSubscriber.Set();
            ((AutoResetEvent)GetField(device, "deviceCommandSignal")).Set();
            Assert.IsTrue(commandWorker.Join(1000));
            Assert.IsNull(commandFailure);
        }

        [TestMethod]
        public void StopBeforeFirstStartCannotRetireReplacementLifecycleOwner()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            int finalizeCount = 0;
            device.PhysicalOutputWriteTestHook = () => { };
            device.PhysicalOutputFinalizeTestHook = () =>
                Interlocked.Increment(ref finalizeCount);

            // Reproduce StopUpdate/StopOutput before the first StartUpdate.
            // There is no lifecycle consumer yet, so this leaves a permit in
            // the AutoResetEvent that the replacement generation must drain.
            Invoke(device, "RequestPhysicalLifecycleShutdown", false, false);
            Invoke(device, "StartPhysicalWorkers");

            Thread.Sleep(50);
            Thread lifecycle = (Thread)GetField(device,
                "physicalLifecycleThread");
            if (lifecycle?.IsAlive != true)
            {
                // Keep a failing test from leaking the worker generation.
                Invoke(device, "StopPhysicalWorkersCore");
            }
            Assert.IsTrue(lifecycle?.IsAlive == true,
                "The replacement lifecycle owner consumed a stale pre-start signal and exited.");

            Exception stopFailure = null;
            Thread stop = new(() =>
            {
                try
                {
                    Invoke(device, "RequestPhysicalLifecycleShutdown", true,
                        false);
                }
                catch (TargetInvocationException ex)
                {
                    stopFailure = ex.InnerException ?? ex;
                }
                catch (Exception ex)
                {
                    stopFailure = ex;
                }
            });
            stop.Start();
            Assert.IsTrue(stop.Join(2000),
                "A real shutdown had no lifecycle owner after start.");
            Assert.IsNull(stopFailure);
            Assert.AreEqual(1, Volatile.Read(ref finalizeCount));
            Assert.IsFalse(lifecycle.IsAlive);
            Assert.IsFalse(((Thread)GetField(device,
                "physicalOutputThread"))?.IsAlive == true);
        }

        [TestMethod]
        public void RecoverySleeperIsRetiredBeforeReplacementGenerationStarts()
        {
            DualSenseDevice device = CreateBluetoothDevice();
            using ManualResetEvent retryWaitEntered = new(false);
            using ManualResetEvent releaseOldRetry = new(false);
            using ManualResetEvent replacementAttempted = new(false);
            long oldGeneration = -1;
            long replacementGeneration = -1;
            int attempts = 0;

            device.BluetoothOutputRecoveryIterationTestHook = generation =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    Interlocked.Exchange(ref oldGeneration, generation);
                    return false;
                }

                Interlocked.Exchange(ref replacementGeneration, generation);
                replacementAttempted.Set();
                return true;
            };
            device.BluetoothOutputRecoveryBeforeWaitTestHook = _ =>
            {
                retryWaitEntered.Set();
                releaseOldRetry.WaitOne();
            };

            Invoke(device, "RequestUnifiedBluetoothOutputTransportRecovery");
            Assert.IsTrue(retryWaitEntered.WaitOne(1000),
                "The old recovery generation never reached its retry wait.");

            Exception retirementFailure = null;
            Thread retirement = new(() =>
            {
                try
                {
                    Invoke(device, "StopPhysicalWorkersCore");
                }
                catch (TargetInvocationException ex)
                {
                    retirementFailure = ex.InnerException ?? ex;
                }
                catch (Exception ex)
                {
                    retirementFailure = ex;
                }
            });
            retirement.Start();
            Assert.IsFalse(retirement.Join(50),
                "Lifecycle replacement did not wait for the admitted recovery owner.");
            releaseOldRetry.Set();
            Assert.IsTrue(retirement.Join(2000));
            Assert.IsNull(retirementFailure);

            device.BluetoothOutputRecoveryBeforeWaitTestHook = null;
            device.PhysicalOutputWriteTestHook = () => { };
            device.PhysicalOutputFinalizeTestHook = () => { };
            Invoke(device, "StartPhysicalWorkers");
            Invoke(device, "RequestUnifiedBluetoothOutputTransportRecovery");
            Assert.IsTrue(replacementAttempted.WaitOne(1000));
            Assert.IsTrue(((ManualResetEvent)GetField(device,
                "bluetoothAudioRecoveryWorkerIdle")).WaitOne(1000));
            Assert.AreEqual(2, Volatile.Read(ref attempts));
            Assert.AreNotEqual(Interlocked.Read(ref oldGeneration),
                Interlocked.Read(ref replacementGeneration));

            Invoke(device, "RequestPhysicalLifecycleShutdown", true, false);
        }

        private static DualSenseDevice CreateBluetoothDevice()
        {
            HidDevice hidDevice = (HidDevice)RuntimeHelpers.
                GetUninitializedObject(typeof(HidDevice));
            DualSenseDevice device = new(hidDevice,
                "input isolation ownership test");
            SetField(device, "conType", ConnectionType.BT,
                typeof(DS4Device));
            return device;
        }

        private static byte[] CreateMicrophoneReport()
        {
            int length = GetConstant("BT_INPUT_REPORT_LENGTH");
            int payloadOffset = GetConstant(
                "BluetoothMicrophonePayloadOffset");
            int payloadLength = GetConstant(
                "BluetoothMicrophonePayloadLength");
            byte[] report = new byte[length];
            report[2] = 9;
            for (int index = 0; index < payloadLength; index++)
            {
                report[payloadOffset + index] = (byte)index;
            }
            return report;
        }

        private static int GetConstant(string name) => (int)typeof(
            DualSenseDevice).GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();

        private static void InvokeWorker(object target, string method,
            object[] arguments, ref Exception failure)
        {
            try
            {
                typeof(DualSenseDevice).GetMethod(method, InstanceFlags).
                    Invoke(target, arguments);
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        private static object Invoke(object target, string method,
            params object[] arguments) => typeof(DualSenseDevice).
                GetMethod(method, InstanceFlags).Invoke(target, arguments);

        private static void SetField(object target, string name, object value,
            Type declaringType = null)
        {
            FieldInfo field = (declaringType ?? typeof(DualSenseDevice)).
                GetField(name, InstanceFlags);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name,
            Type declaringType = null)
        {
            FieldInfo field = (declaringType ?? typeof(DualSenseDevice)).
                GetField(name, InstanceFlags);
            Assert.IsNotNull(field, name);
            return field.GetValue(target);
        }
    }
}
