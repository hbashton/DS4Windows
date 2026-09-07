using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using Switch2BluetoothAudioProbe;

try { return await InspectAsync(); }
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Error = error.Message, Type = error.GetType().Name }));
    return 1;
}

async Task<int> InspectAsync()
{
    // No bond, firmware, GATT command/output/CCCD, or audio-device writes.
    // --wake-inspect additionally publishes the bounded, targeted wake payload.
    if (args.Length == 0 || args[0] is not ("--list" or "--inspect" or "--inspect-known" or "--wake-inspect" or "--wake-only" or "--await-inspect") ||
        (args[0] == "--list" ? args.Length != 1 : args.Length != 2))
    {
        Console.Error.WriteLine("Use --list, or --inspect|--inspect-known|--wake-inspect|--wake-only|--await-inspect <exact ProController2 Windows ID>. See README for side effects and deadlines.");
        return 2;
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(args[0] == "--await-inspect" ? 50 : 20));
    var devices = await DeviceInformation.FindAllAsync(
        BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint)
        .AsTask(timeout.Token);
    bool IsProName(string name) => name is "ProController2" or "Nintendo Switch 2 Pro Controller";
    var candidates = devices.Where(d => IsProName(d.Name)).ToArray();
    if (args[0] == "--list")
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ConnectedBleCount = devices.Count,
            ConnectedProControllers = candidates.Select(d => new { d.Id, d.Name })
        }));
        return 0;
    }
    if (args[0] == "--inspect" && candidates.Count(d => d.Id == args[1]) != 1)
        throw new InvalidOperationException("The selected ProController2 is not uniquely present in the requested Windows inventory.");
    using var device = await BluetoothLEDevice.FromIdAsync(args[1]).AsTask(timeout.Token)
        ?? throw new InvalidOperationException("Windows did not open the selected device.");
    if ((args[0] == "--inspect" && device.ConnectionStatus != BluetoothConnectionStatus.Connected) || !IsProName(device.Name))
        throw new InvalidOperationException("The selected device changed identity or connection state.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        device.Name,
        Connection = device.ConnectionStatus.ToString(),
        Mode = args[0] == "--wake-only" ? "Targeted wake only; no GATT service open" :
            args[0] == "--wake-inspect" ? "Targeted wake then read-only inventory" : "Read-only inventory"
    }));
    if (args[0] == "--await-inspect" && device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        await AwaitAdvertisementAsync(device.BluetoothAddress, timeout.Token);
    if (args[0] is "--wake-inspect" or "--wake-only") await WakeAsync(device, timeout.Token);
    // Safe alongside the application's connection owner: publisher only, no
    // GATT service discovery/open, characteristic reads or connection takeover.
    if (args[0] == "--wake-only") return 0;
    var services = await device.GetGattServicesForUuidAsync(
        ProbeProtocol.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(timeout.Token);
    if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1)
    {
        foreach (var item in services.Services) item.Dispose();
        throw new InvalidOperationException($"Expected one Nintendo service: {services.Status}, count={services.Services.Count}.");
    }
    using var service = services.Services[0];
    var sharing = await service.OpenAsync(GattSharingMode.SharedReadAndWrite).AsTask(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Sharing = sharing.ToString() }));
    if (sharing is not (GattOpenStatus.Success or GattOpenStatus.AlreadyOpened))
        throw new InvalidOperationException($"Shared service open failed: {sharing}.");
    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
    if (characteristics.Status != GattCommunicationStatus.Success)
        throw new InvalidOperationException($"Characteristic discovery failed: {characteristics.Status}.");
    using var session = service.Session;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Service = service.Uuid,
        MaxPduSize = session.MaxPduSize,
        MaximumSingleAttValueBytes = Math.Max(0, session.MaxPduSize - 3),
        // Characteristic presence is a capability observation, not delivery.
        AudioOutputCharacteristicPresent = characteristics.Characteristics.Any(c => c.Uuid == ProbeProtocol.HeadsetOutputUuid),
        BluetoothPlaybackConfirmed = false,
        Characteristics = characteristics.Characteristics.Select(c => new
        { c.Uuid, c.AttributeHandle, Properties = c.CharacteristicProperties.ToString() }),
    }));
    // Do not read a microphone stream. Ordinary Pro input is a different report;
    // its offset 13 must not be mislabeled as the headset report's jack state.
    return 0;
}

static async Task WakeAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
{
    // Observed controller-specific wake payload, not pairing/key commands:
    // ndeadly/switch2_controller_research d1c5a7f, bluetooth_interface.md.
    // Windows reserves GAP Flags; only the evidenced manufacturer section is
    // reproduced here. Success is NOT proof that the controller has woken.
    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken);
    if (adapter == null || !adapter.IsPeripheralRoleSupported)
        throw new InvalidOperationException("This Bluetooth adapter cannot publish the bounded wake advertisement.");
    byte[] payload = ProbeProtocol.BuildWakeManufacturerValue(device.BluetoothAddress);
    using var writer = new DataWriter();
    writer.WriteBytes(payload);
    var advertisement = new BluetoothLEAdvertisement();
    advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(0x0553, writer.DetachBuffer()));
    var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
    try
    {
        publisher.Start();
        await Task.Delay(2000, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            WakeAdvertisement = publisher.Status.ToString(),
            DurationMs = 2000,
            GapFlagsControlledByWindows = true,
            ControllerWakeConfirmed = false
        }));
    }
    finally
    {
        publisher.Stop();
        for (int i = 0; i < 20 && publisher.Status is not (BluetoothLEAdvertisementPublisherStatus.Stopped or BluetoothLEAdvertisementPublisherStatus.Aborted); i++)
            await Task.Delay(50);
        Console.WriteLine(JsonSerializer.Serialize(new { WakeAdvertisementFinal = publisher.Status.ToString() }));
    }
}

static async Task AwaitAdvertisementAsync(ulong address, CancellationToken cancellationToken)
{
    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    wait.CancelAfter(TimeSpan.FromSeconds(30));
    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var watcher = new BluetoothLEAdvertisementWatcher
    {
        ScanningMode = BluetoothLEScanningMode.Active,
        AllowExtendedAdvertisements = true
    };
    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs report)
    {
        if (report.BluetoothAddress != address) return;
        var sections = report.Advertisement.ManufacturerData.Where(s => s.CompanyId == 0x0553).ToArray();
        if (sections.Length != 1 || sections[0].Data.Length != 24) return;
        using var reader = DataReader.FromBuffer(sections[0].Data);
        byte[] value = new byte[24];
        reader.ReadBytes(value);
        if (ProbeProtocol.IsProAdvertisement(value)) received.TrySetResult(true);
        // Neither nearby identities nor remembered-host bytes are logged.
    }
    watcher.Received += OnReceived;
    try
    {
        watcher.Start();
        Console.WriteLine("{\"WaitingForExactControllerAdvertisementSeconds\":30}");
        await received.Task.WaitAsync(wait.Token);
        Console.WriteLine("{\"ExactProAdvertisementObserved\":true}");
    }
    finally { watcher.Stop(); watcher.Received -= OnReceived; }
}
