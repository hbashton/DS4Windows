using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class ViiperPayloadProviderTests
{
    private const string Name = "VIIPER-9.9.9-rc9.9-x64.exe";
    private const string Tag = "v9.9.9-rc9.9";
    private static readonly byte[] Payload = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
    private static string Hash => Convert.ToHexString(SHA256.HashData(Payload));

    [TestMethod]
    public async Task VerifiedLocalCopyWinsAndLeasePinsOnlyPrivateStage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Local, Payload);
        using var client = Client((_, _) => throw new AssertFailedException("No network for valid local payload."));
        var lease = fixture.Provider(client).AcquireAsync(fixture.Package);
        using (var acquired = await lease)
        {
            Assert.AreNotEqual(fixture.Local, acquired.Path);
            Assert.IsTrue(acquired.Path.StartsWith(fixture.Staging + Path.DirectorySeparatorChar));
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(acquired.Path));
            File.WriteAllText(fixture.Local, "Changed source after verification");
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(acquired.Path));
            Assert.ThrowsException<IOException>(() =>
            {
                using var denied = File.Open(acquired.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
            Assert.ThrowsException<IOException>(() => File.Delete(acquired.Path));
        }
        lease.Result.Dispose();
        fixture.AssertClean();
        Assert.AreEqual("Changed source after verification", File.ReadAllText(fixture.Local));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrInvalidLocalUsesOnlyPinnedOfficialUrl(bool corruptLocal)
    {
        using var fixture = new Fixture();
        if (corruptLocal) File.WriteAllText(fixture.Local, "not the compiled payload");
        int requests = 0;
        using var client = Client((request, _) =>
        {
            requests++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://github.com/hbashton/VIIPER/releases/download/" + Tag + "/" + Name,
                request.RequestUri.AbsoluteUri);
            return Task.FromResult(Ok());
        });
        using (var lease = await fixture.Provider(client).AcquireAsync(fixture.Package))
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(lease.Path));
        Assert.AreEqual(1, requests);
        if (corruptLocal) Assert.AreEqual("not the compiled payload", File.ReadAllText(fixture.Local));
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task IncorrectDownloadedHashIsRejectedAndPrivateCopyRemoved()
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) => Task.FromResult(Ok(new byte[] { 1, 2, 3 })));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Provider(client).AcquireAsync(fixture.Package));
        fixture.AssertClean();
    }

    [DataTestMethod]
    [DataRow(0L)]
    [DataRow(252L)]
    public async Task DeclaredInvalidSizeIsRejectedBeforeBodyRead(long size)
    {
        using var fixture = new Fixture();
        var stream = new CancellationStream();
        using var client = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = size;
            return Task.FromResult(response);
        });
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            fixture.Provider(client, Payload.Length).AcquireAsync(fixture.Package));
        Assert.AreEqual(0, stream.ReadCalls);
        fixture.AssertClean();
    }

    [DataTestMethod]
    [DataRow(250)]
    [DataRow(252)]
    public async Task DeclaredLengthMustExactlyMatchActualStream(int declaredLength)
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) =>
        {
            var response = Ok();
            response.Content.Headers.ContentLength = declaredLength;
            return Task.FromResult(response);
        });
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Provider(client).AcquireAsync(fixture.Package));
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task UnknownLengthStreamStillHasStrictByteLimit()
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(Payload))
        }));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            fixture.Provider(client, Payload.Length - 1).AcquireAsync(fixture.Package));
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task PreCancelledRequestDoesNotCreateStageOrSendRequest()
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) => throw new AssertFailedException("Cancelled request reached HTTP."));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            fixture.Provider(client).AcquireAsync(fixture.Package, cancel.Token));
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task CallerCancellationInterruptsStreamingAndCleansStage()
    {
        using var fixture = new Fixture();
        var stream = new CancellationStream();
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) }));
        using var cancel = new CancellationTokenSource();
        Task<ViiperPayloadLease> acquisition = fixture.Provider(client).AcquireAsync(fixture.Package, cancel.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await AssertCancelled(acquisition);
        Assert.IsTrue(stream.SawCancellation);
        fixture.AssertClean();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcquisitionDeadlineCoversHeadersAndBody(bool body)
    {
        using var fixture = new Fixture();
        var stream = new CancellationStream();
        using var client = Client(async (_, token) =>
        {
            if (!body) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        });
        await AssertCancelled(fixture.Provider(client, timeout: TimeSpan.FromMilliseconds(100)).AcquireAsync(fixture.Package));
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task OfficialReleaseAssetRedirectIsAllowedAndStillHashVerified()
    {
        using var fixture = new Fixture();
        int count = 0;
        using var client = Client((request, _) =>
        {
            count++;
            if (count == 1) return Task.FromResult(Redirect("https://release-assets.githubusercontent.com/example?signature=test"));
            Assert.AreEqual("release-assets.githubusercontent.com", request.RequestUri.Host);
            return Task.FromResult(Ok());
        });
        using (var lease = await fixture.Provider(client).AcquireAsync(fixture.Package))
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(lease.Path));
        Assert.AreEqual(2, count);
        fixture.AssertClean();
    }

    [DataTestMethod]
    [DataRow("http://github.com/unsafe")]
    [DataRow("https://evil.example/payload")]
    [DataRow("https://github.com.evil.example/payload")]
    [DataRow("https://user@github.com/payload")]
    [DataRow("https://github.com:444/payload")]
    public async Task RedirectCannotEscapeApprovedHttpsHosts(string location)
    {
        using var fixture = new Fixture();
        int count = 0;
        using var client = Client((_, _) => { count++; return Task.FromResult(Redirect(location)); });
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Provider(client).AcquireAsync(fixture.Package));
        Assert.AreEqual(1, count);
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task RedirectCyclesAreBounded()
    {
        using var fixture = new Fixture();
        int count = 0;
        using var client = Client((_, _) => { count++; return Task.FromResult(Redirect("https://github.com/cycle")); });
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Provider(client).AcquireAsync(fixture.Package));
        Assert.AreEqual(5, count);
        fixture.AssertClean();
    }

    [TestMethod]
    public async Task NonSuccessStatusNeverReturnsPayload()
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => fixture.Provider(client).AcquireAsync(fixture.Package));
        fixture.AssertClean();
    }

    [TestMethod]
    public void ProductionIdentityIsExactlyCompiledVersionNotLatest()
    {
        var provider = new ViiperPayloadProvider();
        Assert.AreEqual("https://github.com/hbashton/VIIPER/releases/download/" +
            ViiperSetupManager.SupportedViiperReleaseTag + "/" + ViiperSetupManager.BundledViiperName,
            provider.DownloadUri.AbsoluteUri);
        Assert.AreEqual(32L * 1024 * 1024, ViiperPayloadProvider.MaximumPayloadBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(30), ViiperPayloadProvider.AcquisitionTimeout);
    }

    [TestMethod]
    public void InvalidIdentityAndUnboundedOptionsAreRejected()
    {
        using var fixture = new Fixture();
        using var client = Client((_, _) => throw new AssertFailedException());
        Assert.ThrowsException<ArgumentException>(() => new ViiperPayloadProvider(client, fixture.Staging, "../" + Name, Tag, Hash));
        Assert.ThrowsException<ArgumentException>(() => new ViiperPayloadProvider(client, fixture.Staging, Name, "vother", Hash));
        Assert.ThrowsException<ArgumentException>(() => new ViiperPayloadProvider(client, fixture.Staging, Name, Tag, "short"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => fixture.Provider(client, ViiperPayloadProvider.MaximumPayloadBytes + 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => fixture.Provider(client, timeout: TimeSpan.FromMinutes(1)));
        Assert.ThrowsException<ArgumentException>(() => new ViiperPayloadProvider(client, "relative", Name, Tag, Hash));
    }

    [TestMethod]
    public void LeaseCleansOnceEvenWhenPinDisposalThrows()
    {
        int disposed = 0, cleaned = 0;
        var lease = new ViiperPayloadLease("test", new CallbackDisposable(() =>
        { disposed++; throw new IOException("fixture"); }), () => cleaned++);
        Assert.ThrowsException<IOException>(() => lease.Dispose());
        lease.Dispose();
        Assert.AreEqual(1, disposed);
        Assert.AreEqual(1, cleaned);
    }

    private static async Task AssertCancelled(Task<ViiperPayloadLease> task)
    {
        try
        {
            using var unexpected = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static HttpResponseMessage Ok(byte[] bytes = null) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Payload) };

    private static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(uri);
        return response;
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new Handler(handler)) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    private class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancellationStream : Stream
    {
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCalls;
        internal bool SawCancellation;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            Entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { SawCancellation = true; throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ViiperPayloadProviderTests-" + Guid.NewGuid().ToString("N"));
        internal string Package => Path.Combine(root, "package");
        internal string Staging => Path.Combine(root, "staging");
        internal string Local => Path.Combine(Package, "extras", Name);
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Package, "extras"));
            Directory.CreateDirectory(Staging);
            File.WriteAllText(Path.Combine(Staging, "unrelated.txt"), "preserve");
        }
        internal ViiperPayloadProvider Provider(HttpClient client, long maximum = ViiperPayloadProvider.MaximumPayloadBytes,
            TimeSpan? timeout = null) => new(client, Staging, Name, Tag, Hash, maximum, timeout);
        internal void AssertClean()
        {
            Assert.AreEqual(0, Directory.GetDirectories(Staging).Length);
            CollectionAssert.AreEqual(new[] { "unrelated.txt" }, Directory.GetFiles(Staging).Select(Path.GetFileName).ToArray());
            Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(Staging, "unrelated.txt")));
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("ViiperPayloadProviderTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup target changed.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
