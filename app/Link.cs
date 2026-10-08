using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Tribe.App
{
    // tribe.exe's end of the loopback connection to the injected core (protocol in core\Link.cs). The exe listens
    // on an ephemeral 127.0.0.1 port and passes the port to Entry.Inject; the core connects back. One core at a time:
    // a new connection replaces the old one. Lines arrive on a background thread via Received.
    sealed class Link : IDisposable
    {
        readonly TcpListener listener;
        readonly object gate = new object();
        TcpClient client;
        StreamWriter writer;
        volatile bool disposed;
        int generation;

        public Action<string> Received;          // background thread
        public Action Connected, Disconnected;   // background thread

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public bool IsConnected { get { lock (gate) return client != null; } }

        public Link()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            new Thread(Accept) { IsBackground = true, Name = "link accept" }.Start();
        }

        void Accept()
        {
            while (!disposed)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (disposed) return; Thread.Sleep(200); continue; }
                c.NoDelay = true;
                int mine;
                lock (gate)
                {
                    Drop();
                    client = c;
                    writer = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    mine = ++generation;
                }
                if (Connected != null) Connected();
                new Thread(() => Read(c, mine)) { IsBackground = true, Name = "link read" }.Start();
            }
        }

        void Read(TcpClient c, int mine)
        {
            try
            {
                using (var reader = new StreamReader(c.GetStream(), new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (Received != null) try { Received(line); } catch (Exception e) { Log.Write("link handler failed: " + e.Message); }
                }
            }
            catch { }
            bool current;
            lock (gate)
            {
                current = mine == generation && client == c;
                if (current) Drop();
            }
            if (current && Disconnected != null && !disposed) Disconnected();
        }

        void Drop()
        {
            if (client == null) return;
            try { client.Close(); } catch { }
            client = null; writer = null;
        }

        /// <summary>Hangs up on whoever is connected (a client that didn't prove itself).</summary>
        public void DropClient() { lock (gate) Drop(); }

        public bool Send(string line)
        {
            lock (gate)
            {
                if (writer == null) return false;
                try { writer.WriteLine(line); return true; }
                catch { return false; }   // the read thread notices the dead socket and reports the disconnect
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (gate) Drop();
            try { listener.Stop(); } catch { }
        }
    }
}
