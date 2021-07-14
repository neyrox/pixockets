﻿using System;
using System.Net;
using System.Net.Sockets;
using Pixockets.DebugTools;
using Pixockets.Pools;

namespace Pixockets
{
    public class BareSock : SockBase
    {
        public Socket SysSock;

        public override IPEndPoint LocalEndPoint
        {
            get { return (IPEndPoint)SysSock.LocalEndPoint; }
        }

        public override IPEndPoint RemoteEndPoint
        {
            get { return _remoteEndPoint; }
        }

        private readonly BufferPoolBase _buffersPool;
        private readonly ILogger _logger;

        private IPEndPoint _remoteEndPoint;
        private bool _connectedMode;

        public BareSock(BufferPoolBase buffersPool, AddressFamily addressFamily, ILogger logger)
        {
            SysSock = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            SysSock.Blocking = false;
            _buffersPool = buffersPool;
            _logger = logger;
        }

        public override void Connect(IPAddress address, int port)
        {
            _remoteEndPoint = new IPEndPoint(address, port);
            AddressFamily addressFamily = SysSock.AddressFamily;
            if (SysSock.IsBound)
            {
                SysSock = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
                SysSock.Blocking = false;
            }

            SysSock.Connect(_remoteEndPoint);
            _connectedMode = true;
        }

        public override void Listen(int port)
        {
            _remoteEndPoint = new IPEndPoint(AnyAddress(SysSock.AddressFamily), port);
            SysSock.Bind(_remoteEndPoint);
            _connectedMode = false;
        }

        public override void Send(IPEndPoint endPoint, byte[] buffer, int offset, int length, bool putBufferToPool)
        {
            ValidateLength(length);

            try
            {
                // Some implementations throw exception if sendto is called on a connected SOCK_DGRAM socket.
                if (!_connectedMode)
                    SysSock.SendTo(buffer, offset, length, SocketFlags.None, endPoint);
                else
                    SysSock.Send(buffer, offset, length, SocketFlags.None);
            }
            catch (SocketException se)
            {
                // Ignore harmless errors
                if (!HarmlessErrors.Contains(se.SocketErrorCode))
                {
                    _logger.Exception(se);
                    throw;
                }
            }
            finally
            {
                if (putBufferToPool)
                    _buffersPool.Put(buffer);
            }
        }

        public override void Send(byte[] buffer, int offset, int length, bool putBufferToPool)
        {
            try
            {
                // It uses Send instead of SendTo because SendTo seems not implemented in Unity on iOS
                SysSock.Send(buffer, offset, length, SocketFlags.None);
            }
            catch (SocketException se)
            {
                // Ignore harmless errors
                if (!HarmlessErrors.Contains(se.SocketErrorCode))
                {
                    _logger.Exception(se);
                    throw;
                }
            }
            finally
            {
                if (putBufferToPool)
                    _buffersPool.Put(buffer);
            }
        }

        public override bool Receive(ref ReceivedPacket packet)
        {
            try
            {
                if (SysSock.Available == 0)
                {
                    return false;
                }
            }
            catch (SocketException se)
            {
                // Ignore harmless errors
                if (!HarmlessErrors.Contains(se.SocketErrorCode))
                {
                    _logger.Exception(se);
                    throw;
                }
            }

            var buffer = _buffersPool.Get(MTUSafe);
            var bufferInUse = false;
            EndPoint remoteEP = _remoteEndPoint;
            try
            {
                int bytesReceived = 0;
                if (_connectedMode)
                    bytesReceived = SysSock.Receive(buffer, MTUSafe, SocketFlags.None);
                else
                    bytesReceived = SysSock.ReceiveFrom(buffer, MTUSafe, SocketFlags.None, ref remoteEP);

                //ntrf: On windows we will get EMSGSIZE error if message was truncated, but Mono on Unix will fill up the
                //      whole buffer silently. We detect this case by allowing buffer to be slightly larger, than our typical
                //      packet, and dropping any packet, that did fill the whole thing.
                if (bytesReceived > 0 && bytesReceived <= MTU)
                {
                    packet.Buffer = buffer;
                    bufferInUse = true;
                    packet.Offset = 0;
                    packet.Length = bytesReceived;
                    packet.EndPoint = (IPEndPoint) remoteEP;

                    return true;
                }
            }
            catch (SocketException se)
            {
                // Ignore harmless errors
                if (!HarmlessErrors.Contains(se.SocketErrorCode))
                {
                    _logger.Exception(se);
                    throw;
                }
            }
            finally
            {
                // We don't return buffer here if it is to be processed by client
                if (!bufferInUse)
                    _buffersPool.Put(buffer);
            }

            return false;
        }

        public override void Close()
        {
            if (SysSock != null)
            {
                SysSock.Close();
                SysSock = null;
            }

            _remoteEndPoint = null;
        }
    }
}
