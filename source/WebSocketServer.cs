#define SIMPLE_WEB_INFO_LOG
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Mirror.SimpleWeb
{
    public class WebSocketServer
    {
        [System.Diagnostics.Conditional("SIMPLE_WEB_INFO_LOG")]
        static void Info(string msg) => Debug.Log($"<color=blue>{msg}</color>");

        class Connection
        {
            public int connId;
            public TcpClient client;
            public Thread receiveThread;
            public Thread sendThread;

            public ManualResetEvent sendPending = new ManualResetEvent(false);
            public ConcurrentQueue<ArraySegment<byte>> sendQueue = new ConcurrentQueue<ArraySegment<byte>>();
        }
        public struct Message
        {
            public int connId;
            public EventType type;
            public ArraySegment<byte> data;
        }
        public enum EventType
        {
            Connected,
            Data,
            Disconnected
        }
        struct Mask
        {
            byte a;
            byte b;
            byte c;
            byte d;

            public Mask(byte[] buffer, int offset)
            {
                a = buffer[offset];
                b = buffer[offset + 1];
                c = buffer[offset + 2];
                d = buffer[offset + 3];
            }

            public byte getMaskByte(int index)
            {
                switch (index % 4)
                {
                    default:
                    case 0: return a;
                    case 1: return b;
                    case 2: return c;
                    case 3: return d;
                }

            }
        }

        public readonly ConcurrentQueue<Message> receiveQueue = new ConcurrentQueue<Message>();


        readonly bool noDelay;
        readonly int sendTimeout;
        readonly int recieveLoopSleepTime;

        TcpListener listener;
        Thread acceptThread;
        readonly ConcurrentDictionary<int, Connection> connections = new ConcurrentDictionary<int, Connection>();
        int _previousId = 0;
        int GetNextId()
        {
            _previousId++;
            return _previousId;
        }

        byte[] _buffer = new byte[10000];

        public WebSocketServer(bool noDelay, int sendTimeout, int recieveLoopSleepTime)
        {
            this.noDelay = noDelay;
            this.sendTimeout = sendTimeout;
            this.recieveLoopSleepTime = recieveLoopSleepTime;
        }

        public void Listen(short port)
        {
            listener = TcpListener.Create(port);
            listener.Server.NoDelay = noDelay;
            listener.Server.SendTimeout = sendTimeout;
            listener.Start();

            Debug.Log($"Server has started on port {port}.\nWaiting for a connection...");

            acceptThread = new Thread(acceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Start();
        }
        public void Stop()
        {
            listener?.Stop();
            acceptThread?.Interrupt();
            acceptThread = null;

            Connection[] connections = this.connections.Values.ToArray();
            foreach (Connection conn in connections)
            {
                conn.client.Close();
                conn.receiveThread.Interrupt();
                conn.sendThread.Interrupt();
            }

            this.connections.Clear();
        }

        void acceptLoop()
        {
            try
            {
                try
                {


                    while (true)
                    {
                        // TODO check this is blocking?
                        TcpClient client = listener.AcceptTcpClient();

                        Debug.Log("A client connected.");

                        // only start receieve thread till Handshake is complete
                        Connection conn = new Connection
                        {
                            connId = GetNextId(),
                            client = client,
                        };
                        Thread receiveThread = new Thread(() => HandshakeAndReceive(conn));

                        conn.receiveThread = receiveThread;

                        receiveThread.IsBackground = true;
                        receiveThread.Start();

                        connections.TryAdd(conn.connId, conn);
                    }
                }
                catch (SocketException e)
                {
                    // check for Interrupted/Abort
                    Thread.Sleep(1);

                    Debug.LogException(e);
                }
            }
            catch (ThreadInterruptedException) { Info("Accept ThreadInterrupted"); return; }
            catch (ThreadAbortException) { Info("Accept ThreadAbort"); return; }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void HandshakeAndReceive(Connection conn)
        {
            bool success = Handshake(conn);

            if (!success)
            {
                CloseConnection(conn);
                return;
            }

            receiveQueue.Enqueue(new Message { connId = conn.connId, type = EventType.Connected });

            Thread sendThread = new Thread(() => SendLoop(conn));

            conn.sendThread = sendThread;

            sendThread.IsBackground = true;
            sendThread.Start();

            ReceiveLoop(conn);
        }

        bool Handshake(Connection conn)
        {
            NetworkStream stream = conn.client.GetStream();
            byte[] buffer = new byte[1000];

            // blocking
            stream.Read(buffer, 0, 3);

            string getText = Encoding.UTF8.GetString(buffer, 0, 3);

            if (getText != "GET")
            {
                Debug.LogError($"Did not recieve Handshake, instead got {getText}");
                return false;
            }

            int length = conn.client.Available;
            stream.Read(buffer, 3, length);
            string msg = Encoding.UTF8.GetString(buffer, 0, length);

            AcceptHandshake(stream, msg);
            return true;
        }

        static void AcceptHandshake(NetworkStream stream, string msg)
        {
            const string eol = "\r\n"; // HTTP/1.1 defines the sequence CR LF as the end-of-line marker

            string key = new Regex("Sec-WebSocket-Key: (.*)").Match(msg).Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] keyBuffer = Encoding.UTF8.GetBytes(key);
            byte[] keyHash = SHA1.Create().ComputeHash(keyBuffer);

            string keyHashString = Convert.ToBase64String(keyHash);

            string message = "HTTP/1.1 101 Switching Protocols" + eol
                + "Connection: Upgrade" + eol
                + "Upgrade: websocket" + eol
                + "Sec-WebSocket-Accept: " + keyHashString + eol
                + eol;

            byte[] response = Encoding.UTF8.GetBytes(message);

            stream.Write(response, 0, response.Length);
            Debug.Log("Sent Handshake");
        }

        void ReceiveLoop(Connection conn)
        {
            try
            {
                TcpClient client = conn.client;
                NetworkStream stream = client.GetStream();

                while (true)
                {
                    // we expect atleast 6 bytes
                    // 1 for bit fields
                    // 1+ for length (length can be be 1, 2, or 4)
                    // 4 for mask
                    WaitForData(client, 6, recieveLoopSleepTime);
                    if (!client.Connected) { return; }

                    // TODO dont allocate new buffer
                    int length = client.Available;
                    byte[] buffer = new byte[length];
                    stream.Read(buffer, 0, length);

                    ProcessMessages(conn, buffer, length);

                    Thread.Sleep(recieveLoopSleepTime);
                }
            }
            catch (ThreadInterruptedException) { Info($"Receive {conn.connId} ThreadInterrupted"); return; }
            catch (ThreadAbortException) { Info($"Receive {conn.connId} ThreadAbort"); return; }
            catch (Exception e)
            {
                Debug.LogException(e);

                CloseConnection(conn);
            }
        }

        static void WaitForData(TcpClient client, int minCount, int sleepTime)
        {
            while (client.Connected && client.Available < minCount)
            {
                Thread.Sleep(sleepTime);
            }
        }

        void ProcessMessages(Connection conn, byte[] buffer, int length)
        {
            int bytesProcessed = ProcessMessage(conn, buffer, length);

            while (bytesProcessed < length)
            {
                // TODO dont allocate new buffer
                int newLength = length - bytesProcessed;
                byte[] newBuffer = new byte[newLength];

                bytesProcessed = ProcessMessage(conn, newBuffer, newLength);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="buffer"></param>
        /// <param name="length"></param>
        /// <returns>bytes processed</returns>
        int ProcessMessage(Connection conn, byte[] buffer, int length)
        {
            bool finished = (buffer[0] & 0b10000000) != 0; // has full message been sent
            bool hasMask = (buffer[1] & 0b10000000) != 0; // must be true, "All messages from the client to the server have this bit set"

            int opcode = buffer[0] & 0b00001111; // expecting 1 - text message
            byte lenByte = (byte)(buffer[1] - 128); // & 0111 1111

            throwIfNotFinished(finished);
            throwIfNoMask(hasMask);
            throwIfBadOpCode(opcode);

            (int msglen, int offset) = GetMessageLength(buffer, lenByte);

            throwIfLengthZero(msglen);


            //if (offset + msglen != length)
            //{
            //    throw new InvalidDataException("Message length was not equal to buffer length");
            //}


            byte[] decoded = new byte[msglen];
            Mask mask = new Mask(buffer, offset);
            offset += 4;

            for (int i = 0; i < msglen; i++)
                decoded[i] = (byte)(buffer[offset + i] ^ mask.getMaskByte(i));

            if (opcode == 2)
            {
                ArraySegment<byte> data = new ArraySegment<byte>(decoded, 0, decoded.Length);

                receiveQueue.Enqueue(new Message
                {
                    connId = conn.connId,
                    type = EventType.Data,
                    data = data,
                });

            }
            else if (opcode == 8)
            {
                Info($"Close: {decoded[0] << 8 | decoded[1]} message:{Encoding.UTF8.GetString(decoded, 2, msglen - 2)}");
                CloseConnection(conn);
            }

            return offset + msglen;
        }


        /// <exception cref="InvalidDataException"></exception>
        static void throwIfNotFinished(bool finished)
        {
            if (!finished)
            {
                // TODO check if we need to deal with this
                throw new InvalidDataException("Full message should have been sent, if the full message wasn't sent it wasn't sent from this trasnport");
            }
        }

        /// <exception cref="InvalidDataException"></exception>
        static void throwIfNoMask(bool hasMask)
        {
            if (!hasMask)
            {
                throw new InvalidDataException("Message from client should have mask set to true");
            }
        }

        /// <exception cref="InvalidDataException"></exception>
        static void throwIfBadOpCode(int opcode)
        {
            // 2 = binary
            // 8 = close
            if (opcode != 2 && opcode != 8)
            {
                throw new InvalidDataException("Expected opcode to be binary or close");
            }
        }

        /// <exception cref="InvalidDataException"></exception>
        static void throwIfLengthZero(int msglen)
        {
            if (msglen == 0)
            {
                throw new InvalidDataException("Message length was zero");
            }
        }

        static (int length, int offset) GetMessageLength(byte[] buffer, byte lenByte)
        {
            if (lenByte == 126)
            {
                ushort value = 0;
                value |= buffer[2];
                value |= (ushort)(buffer[3] << 8);

                return (value, 4);
            }
            else if (lenByte == 127)
            {
                throw new Exception("Max length is longer than allowed in a single message");

                //ulong value = 0;
                //value |= buffer[2];
                //value |= (ulong)buffer[3] << 8;
                //value |= (ulong)buffer[4] << 16;
                //value |= (ulong)buffer[5] << 24;
                //value |= (ulong)buffer[6] << 32;
                //value |= (ulong)buffer[7] << 40;
                //value |= (ulong)buffer[8] << 48;
                //value |= (ulong)buffer[9] << 56;

                //return (value, 10);
            }
            else // is less than 126
            {
                return (lenByte, 2);
            }
        }

        void SendLoop(Connection conn)
        {
            try
            {
                TcpClient client = conn.client;
                NetworkStream stream = client.GetStream();
                while (client.Connected)
                {
                    // wait for message
                    conn.sendPending.WaitOne();
                    conn.sendPending.Reset();

                    while (conn.sendQueue.TryDequeue(out ArraySegment<byte> msg))
                    {
                        SendMessageToClient(stream, msg);

                        // check if connection was closed after sending message
                        if (!client.Connected) { return; }
                    }
                }
            }
            catch (ThreadInterruptedException) { Info($"Send {conn.connId} ThreadInterrupted"); return; }
            catch (ThreadAbortException) { Info($"Send {conn.connId} ThreadAbort"); return; }
            catch (Exception e)
            {
                Debug.LogException(e);

                CloseConnection(conn);
            }
        }

        void CloseConnection(Connection conn)
        {
            conn.client.GetStream().Close();
            conn.client.Close();
            conn.receiveThread.Interrupt();
            conn.sendThread.Interrupt();
            receiveQueue.Enqueue(new Message { connId = conn.connId, type = EventType.Disconnected });
            connections.TryRemove(conn.connId, out Connection _);
        }

        static void SendMessageToClient(NetworkStream stream, ArraySegment<byte> msg)
        {
            int msgLength = msg.Count;
            byte[] buffer = new byte[4 + msgLength];
            int sendLength = 0;

            byte finished = 128;
            byte byteOpCode = 2;

            buffer[0] = (byte)(finished | byteOpCode);
            sendLength++;

            if (msgLength < 125)
            {
                buffer[1] = (byte)msgLength;
                sendLength++;
            }
            else if (msgLength < ushort.MaxValue)
            {
                buffer[1] = 126;
                buffer[2] = (byte)msgLength;
                buffer[3] = (byte)(msgLength >> 8);
                sendLength += 3;
            }
            else
            {
                throw new InvalidDataException($"Trying to send a message larger than {ushort.MaxValue} bytes");
            }

            Array.Copy(msg.Array, msg.Offset, buffer, sendLength, msgLength);
            sendLength += msgLength;

            stream.Write(buffer, 0, sendLength);

            Debug.Log("Sent message to client");
        }

        public void Send(int id, ArraySegment<byte> segment)
        {
            if (connections.TryGetValue(id, out Connection conn))
            {
                conn.sendQueue.Enqueue(segment);
                conn.sendPending.Set();
            }
            else
            {
                Debug.LogError($"Cant send message to {id} because connection was not found in dictionary");
            }
        }

        public bool CloseConnection(int id)
        {
            if (connections.TryGetValue(id, out Connection conn))
            {
                CloseConnection(conn);
                return true;
            }
            else
            {
                Debug.LogError($"Cant close connection to {id} because connection was not found in dictionary");
                return false;
            }
        }

        public string GetClientAddress(int id)
        {
            if (connections.TryGetValue(id, out Connection conn))
            {
                return conn.client.Client.RemoteEndPoint.ToString();
            }
            else
            {
                Debug.LogError($"Cant close connection to {id} because connection was not found in dictionary");
                return null;
            }
        }
    }
}