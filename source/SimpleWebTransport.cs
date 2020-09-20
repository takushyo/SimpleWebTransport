using System;
using System.Collections.Generic;
using System.Net;
using UnityEngine;

namespace Mirror.SimpleWeb
{
    public class SimpleWebTransport : Transport
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        readonly bool isWebGL = true;
#else
        readonly bool isWebGL = false;
#endif

        public const string Scheme = "ws";

        [Tooltip("Protect against allocation attacks by keeping the max message size small. Otherwise an attacker might send multiple fake packets with 2GB headers, causing the server to run out of memory after allocating multiple large packets.")]
        public int maxMessageSize = 16 * 1024;
        public short port = 7776;

        [Tooltip("disables nagle algorithm. lowers CPU% and latency but increases bandwidth")]
        public bool noDelay = true;

        [Tooltip("Send would stall forever if the network is cut off during a send, so we need a timeout (in milliseconds)")]
        public int sendTimeout = 5000;

        private void OnValidate()
        {
            if (maxMessageSize > ushort.MaxValue)
            {
                Debug.LogWarning($"max supported value for maxMessageSize is {ushort.MaxValue}");
                maxMessageSize = ushort.MaxValue;
            }
        }

        SimpleWebClient client;
        SimpleWebServer server;

        public override bool Available()
        {
            return isWebGL;
        }
        public override int GetMaxPacketSize(int channelId = 0)
        {
            return maxMessageSize;
        }

        public override void Shutdown()
        {
            client?.Disconnect();
            server?.Stop();
        }

        private void LateUpdate()
        {
            server?.Update(this);
        }

        #region Client
        public override bool ClientConnected()
        {
            return client != null && client.IsConnected;
        }

        public override void ClientConnect(string address)
        {
            if (!isWebGL)
            {
                Debug.LogError("SimpleWebTransport client is only available on WebGL");
            }

            // connecting or connected
            if (client != null)
            {
                Debug.LogError("Already Connected");
            }

            UriBuilder builder = new UriBuilder
            {
                Scheme = Scheme,
                Host = address,
                Port = port
            };

            client = SimpleWebClient.Create();
            if (client == null) { return; }

            client.onConnect += OnClientConnected.Invoke;
            client.onDisconnect += OnClientDisconnected.Invoke;
            client.onData += (ArraySegment<byte> data) => OnClientDataReceived.Invoke(data, Channels.DefaultReliable);
            client.onError += () =>
            {
                ClientDisconnect();
                OnClientError.Invoke(new Exception("SimpleWebClient Error"));
            };


            // TODO can this just be builder.ToString()
            client.Connect(builder.Uri.ToString());
        }

        public override void ClientDisconnect()
        {
            if (client != null)
            {
                Debug.LogError("Not Connected");
            }

            client.Disconnect();
            client = null;
        }

        public override bool ClientSend(int channelId, ArraySegment<byte> segment)
        {
            if (!ClientConnected())
            {
                Debug.LogError("Already Connected");
                return false;
            }

            if (segment.Count > maxMessageSize)
            {
                Debug.LogError("Message greater than max size");
                return false;
            }

            client.Send(segment);
            return true;
        }
        #endregion

        #region Server
        public override bool ServerActive()
        {
            return server != null && server.Active;
        }

        public override void ServerStart()
        {
            if (isWebGL)
            {
                Debug.LogError("SimpleWebTransport server is only available on standalone");
            }

            if (ServerActive())
            {
                Debug.LogError("SimpleWebServer Already Started");
            }

            server = new SimpleWebServer(port, noDelay, sendTimeout);

            server.onConnect += OnServerConnected.Invoke;
            server.onDisconnect += OnServerDisconnected.Invoke;
            server.onData += (int connId, ArraySegment<byte> data) => OnServerDataReceived.Invoke(connId, data, Channels.DefaultReliable);
            server.onError += (connId) =>
            {
                OnServerError.Invoke(connId, new Exception("SimpleWebClient Error"));
            };

            server.Start();
        }

        public override void ServerStop()
        {
            if (!ServerActive())
            {
                Debug.LogError("SimpleWebServer Not Active");
            }

            server.Stop();
            server = null;
        }

        public override bool ServerDisconnect(int connectionId)
        {
            if (!ServerActive())
            {
                Debug.LogError(" SimpleWebServer Not Active");
                return false;
            }

            return server.KickClient(connectionId);
        }

        public override bool ServerSend(List<int> connectionIds, int channelId, ArraySegment<byte> segment)
        {
            if (!ServerActive())
            {
                Debug.LogError("SimpleWebServer Not Active");
                return false;
            }

            if (segment.Count > maxMessageSize)
            {
                Debug.LogError("Message greater than max size");
                return false;
            }

            server.SendAll(connectionIds, segment);
            return true;
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            return server.GetClientAddress(connectionId);
        }

        public override Uri ServerUri()
        {
            UriBuilder builder = new UriBuilder
            {
                Scheme = Scheme,
                Host = Dns.GetHostName(),
                Port = port
            };
            return builder.Uri;
        }
        #endregion

    }
}