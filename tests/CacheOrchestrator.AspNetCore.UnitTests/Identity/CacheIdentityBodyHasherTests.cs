using CacheOrchestrator.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace CacheOrchestrator.AspNetCore.UnitTests.Identity;

public class CacheIdentityBodyHasherTests
{
    [Fact]
    public async Task HashAsync_OversizedNonSeekableBody_BypassesIdentity_ButLeavesBodyReadable()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghij"); // 20 bytes
        DefaultHttpContext http = new();
        http.Request.Method = "POST";
        http.Request.Path = "/search";
        http.Request.Body = new NonSeekableStream(payload);
        http.Request.ContentLength = null;

        CacheIdentityMaterial? material = await CacheIdentityBodyHasher.HashAsync(
            http.Request,
            maxBodyBytes: 8,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        material.Should().BeNull();

        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        body.Should().Be("0123456789abcdefghij");
    }

    [Fact]
    public async Task HashAsync_WithinLimit_ReturnsHash_AndRewindsBody()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello");
        DefaultHttpContext http = new();
        http.Request.Method = "POST";
        http.Request.Path = "/search";
        http.Request.Body = new MemoryStream(payload);
        http.Request.ContentLength = payload.Length;

        CacheIdentityMaterial? material = await CacheIdentityBodyHasher.HashAsync(
            http.Request,
            maxBodyBytes: 64,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        material.Should().NotBeNull();
        material!.Values.Should().ContainKey("body-hash");
        http.Request.Body.Position.Should().Be(0);

        byte[] roundTrip = new byte[payload.Length];
        int read = await http.Request.Body.ReadAsync(roundTrip, TestContext.Current.CancellationToken);
        read.Should().Be(payload.Length);
        roundTrip.Should().Equal(payload);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] payload)
        {
            _inner = new MemoryStream(payload);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
