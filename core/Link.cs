using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Tribe
{
    // The core's side of the loopback connection to tribe.exe: one UTF-8 line per message, tab-separated fields.
    // Socket work happens on two background threads; the main thread only touches the queues.
    // The messages themselves are listed in Runner.cs.
    sealed class Link
    {
        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        readonly BlockingCollection<string> outbox = new BlockingCollection<string>(256);
        TcpClient client;
        volatile bool connected, closed;

        /// <summary>True once the exe has gone away (closed, crashed or told us to unload).</summary>
        public bool Closed { get { return closed; } }

        public Link(int port)
        {
            var t = new Thread(() => Run(port)) { IsBackground = true, Name = "tribe link" };
            t.Start();
        }

        public bool TryReceive(out string line) { return inbox.TryDequeue(out line); }

        // Drops messages instead of blocking the game if the exe stops reading.
        public void Send(string line)
        {
            if (closed) return;
            try { outbox.TryAdd(line); } catch { }
        }

        void Run(int port)
        {
            try
            {
                client = new TcpClient();
                client.NoDelay = true;
                client.Connect("127.0.0.1", port);
                connected = true;
                var stream = client.GetStream();
                var writer = new Thread(() => Write(stream)) { IsBackground = true, Name = "tribe link writer" };
                writer.Start();
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    string line;
                    while (!closed && (line = reader.ReadLine()) != null) inbox.Enqueue(line);
                }
            }
            catch (Exception) { }
            closed = true;
        }

        void Write(NetworkStream stream)
        {
            var enc = new UTF8Encoding(false);
            try
            {
                foreach (var line in outbox.GetConsumingEnumerable())
                {
                    var bytes = enc.GetBytes(line.Replace("\n", " ") + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception) { }
            closed = true;
        }

        /// <summary>Flushes what's queued (so "bye" gets out) and closes.</summary>
        public void Close()
        {
            if (!connected) { closed = true; return; }
            outbox.CompleteAdding();
            for (int i = 0; i < 20 && outbox.Count > 0; i++) Thread.Sleep(5);
            closed = true;
            try { client.Close(); } catch { }
        }
    }
}
