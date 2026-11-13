using System;
using System.Threading;
using System.Threading.Tasks;

namespace System.Runtime.InteropServices.WindowsRuntime
{
    public static class TaskExtensions
    {
        public static Task AsTask(this Task task, CancellationToken ct) { return task.WaitAsync(ct); }
        public static Task<T> AsTask<T>(this Task<T> task, CancellationToken ct) { return task.WaitAsync(ct); }
    }
}
namespace Windows.Storage.Streams
{
    public sealed class DataWriter : IDisposable
    {
        public DataWriter(object output) { }
        public void WriteBytes(byte[] data) { }
        public Task<uint> StoreAsync() { return Task.FromResult(0U); }
        public void DetachStream() { }
        public void Dispose() { }
    }
    public sealed class DataReader : IDisposable
    {
        private readonly byte[] _data;
        public DataReader(byte[] data) { _data = data; }
        public uint UnconsumedBufferLength { get { return (uint)_data.Length; } }
        public void ReadBytes(byte[] target) { Buffer.BlockCopy(_data, 0, target, 0, target.Length); }
        public void Dispose() { }
    }
}
namespace Windows.Networking.Sockets
{
    public enum SocketMessageType { Binary, Utf8 }
    public interface IWebSocket { }
    public sealed class WebSocketClosedEventArgs : EventArgs { }
    public sealed class MessageWebSocketMessageReceivedEventArgs : EventArgs
    {
        public SocketMessageType MessageType { get; set; }
        public byte[] Data { get; set; }
        public Windows.Storage.Streams.DataReader GetDataReader() { return new Windows.Storage.Streams.DataReader(Data); }
    }
    public sealed class MessageWebSocketControl
    { public SocketMessageType MessageType { get; set; } public uint MaxMessageSize { get; set; } }
    public sealed class MessageWebSocket : IWebSocket, IDisposable
    {
        public MessageWebSocketControl Control { get; } = new MessageWebSocketControl();
        public object OutputStream { get; } = new object();
        public event Action<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs> MessageReceived;
        public event Action<IWebSocket, WebSocketClosedEventArgs> Closed;
        public void SetRequestHeader(string name, string value) { }
        public Task ConnectAsync(Uri uri) { return Task.CompletedTask; }
        public void Close(ushort code, string reason) { Closed?.Invoke(this, new WebSocketClosedEventArgs()); }
        public void Dispose() { }
        public void Emit(byte[] data) { MessageReceived?.Invoke(this, new MessageWebSocketMessageReceivedEventArgs { Data = data }); }
    }
}
namespace BiliXbox.Services { public sealed class BilibiliClient { public const string UserAgent = "BiliXbox protocol offline tests"; } }
