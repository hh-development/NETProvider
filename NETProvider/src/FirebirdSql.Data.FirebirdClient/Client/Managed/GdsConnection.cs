﻿/*
 *	Firebird ADO.NET Data provider for .NET and Mono
 *
 *	   The contents of this file are subject to the Initial
 *	   Developer's Public License Version 1.0 (the "License");
 *	   you may not use this file except in compliance with the
 *	   License. You may obtain a copy of the License at
 *	   http://www.firebirdsql.org/index.php?op=doc&id=idpl
 *
 *	   Software distributed under the License is distributed on
 *	   an "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either
 *	   express or implied. See the License for the specific
 *	   language governing rights and limitations under the License.
 *
 *	Copyright (c) 2002 - 2007 Carlos Guzman Alvarez
 *	Copyright (c) 2007 - 2012 Jiri Cincura (jiri@cincura.net)
 *	All Rights Reserved.
 */

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

using FirebirdSql.Data.Common;

namespace FirebirdSql.Data.Client.Managed
{
	internal class GdsConnection
	{
		const ulong KeepAliveTime = 1800000; //30min
		const ulong KeepAliveInterval = 1800000; //30min

		#region Fields

		private Socket _socket;
		private NetworkStream _networkStream;
		private string _userID;
		private string _password;
		private string _dataSource;
		private int _portNumber;
		private int _packetSize;
		private Charset _characterSet;
		private int _protocolVersion;
		private int _protocolArchitecture;
		private int _protocolMinimunType;
		private SrpClient _srpClient;
		private byte[] _authData;

		#endregion

		#region Properties

		public bool IsConnected
		{
			get { return _socket?.Connected ?? false; }
		}

		public int ProtocolVersion
		{
			get { return _protocolVersion; }
		}

		public int ProtocolArchitecture
		{
			get { return _protocolArchitecture; }
		}

		public int ProtocolMinimunType
		{
			get { return _protocolMinimunType; }
		}

		public bool DataAvailable
		{
			get { return _networkStream.DataAvailable; }
		}

		public string UserID
		{
			get { return _userID; }
		}

		public string Password
		{
			get { return _password; }
		}

		public byte[] AuthData
		{
			get { return _authData; }
		}

		internal IPAddress IPAddress { get; private set; }

		#endregion

		#region Constructors

		public GdsConnection(string dataSource, int port)
			: this(null, null, dataSource, port, 8192, Charset.DefaultCharset)
		{
		}

		public GdsConnection(string userID, string password, string dataSource, int portNumber, int packetSize, Charset characterSet)
		{
			_userID = userID;
			_password = password;
			_dataSource = dataSource;
			_portNumber = portNumber;
			_packetSize = packetSize;
			_characterSet = characterSet;
			_srpClient = new SrpClient();
		}

		#endregion

		#region Methods

		public virtual void Connect()
		{
			try
			{
				IPAddress = GetIPAddress(_dataSource, AddressFamily.InterNetwork);
				var endPoint = new IPEndPoint(IPAddress, _portNumber);

				_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

				_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, _packetSize);
				_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, _packetSize);
				_socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
				_socket.SetKeepAlive(KeepAliveTime, KeepAliveInterval);

				_socket.Connect(endPoint);
				_networkStream = new NetworkStream(_socket, true);
			}
			catch (SocketException ex)
			{
				throw IscException.ForTypeErrorCodeStrParam(IscCodes.isc_arg_gds, IscCodes.isc_network_error, _dataSource, ex);
			}
		}

		public virtual void Identify(string database)
		{
			using (var xdrStream = CreateXdrStream())
			{
				try
				{
					xdrStream.Write(IscCodes.op_connect);
					xdrStream.Write(IscCodes.op_attach);
					xdrStream.Write(IscCodes.CONNECT_VERSION3);
					xdrStream.Write(IscCodes.GenericAchitectureClient);

					xdrStream.Write(database);
					xdrStream.Write(3);                         // Protocol versions understood
					xdrStream.WriteBuffer(UserIdentificationStuff());

#warning Refactoring? Wrapping?
					xdrStream.Write(IscCodes.PROTOCOL_VERSION10);
					xdrStream.Write(IscCodes.GenericAchitectureClient);
					xdrStream.Write(IscCodes.ptype_rpc);
					xdrStream.Write(IscCodes.ptype_batch_send);
					xdrStream.Write(0);                              // Preference weight

					xdrStream.Write(IscCodes.PROTOCOL_VERSION11);
					xdrStream.Write(IscCodes.GenericAchitectureClient);
					xdrStream.Write(IscCodes.ptype_rpc);
					xdrStream.Write(IscCodes.ptype_lazy_send);
					xdrStream.Write(1);                              // Preference weight

					xdrStream.Write(IscCodes.PROTOCOL_VERSION12);
					xdrStream.Write(IscCodes.GenericAchitectureClient);
					xdrStream.Write(IscCodes.ptype_rpc);
					xdrStream.Write(IscCodes.ptype_lazy_send);
					xdrStream.Write(2);                              // Preference weight

					xdrStream.Flush();

					var operation = xdrStream.ReadOperation();
					if (operation == IscCodes.op_accept || operation == IscCodes.op_cond_accept || operation == IscCodes.op_accept_data)
					{
						_protocolVersion = xdrStream.ReadInt32(); // Protocol version
						_protocolArchitecture = xdrStream.ReadInt32();    // Architecture for protocol
						_protocolMinimunType = xdrStream.ReadInt32();   // Minimum type

						if (_protocolVersion < 0)
						{
							_protocolVersion = (ushort)(_protocolVersion & IscCodes.FB_PROTOCOL_MASK) | IscCodes.FB_PROTOCOL_FLAG;
						}
					}
					else
					{
						try
						{
							Disconnect();
						}
						catch
						{ }
						finally
						{
							throw IscException.ForErrorCode(IscCodes.isc_connect_reject);
						}
					}

					if (operation == IscCodes.op_cond_accept || operation == IscCodes.op_accept_data)
					{
						var data = xdrStream.ReadBuffer();
						var acceptPluginName = xdrStream.ReadString();
						var isAuthenticated = xdrStream.ReadInt32();
						var keys = xdrStream.ReadString();
						if (isAuthenticated == 0)
						{
							_authData = _srpClient.ClientProof(_userID, _password, data);
						}
					}
				}
				catch (IOException ex)
				{
					throw IscException.ForErrorCode(IscCodes.isc_network_error, ex);
				}
			}
		}

		public XdrStream CreateXdrStream()
		{
			return new XdrStream(new BufferedStream(_networkStream, 32 * 1024), _characterSet, false);
		}

		public virtual void Disconnect()
		{
			// socket is owned by network stream, so it'll be closed automatically
			_networkStream?.Close();
			_networkStream = null;
			_socket = null;
		}

		#endregion

		#region Private Methods

		private IPAddress GetIPAddress(string dataSource, AddressFamily addressFamily)
		{
			IPAddress ipaddress = null;

			if (IPAddress.TryParse(dataSource, out ipaddress))
			{
				return ipaddress;
			}

			IPAddress[] addresses = Dns.GetHostEntry(dataSource).AddressList;

			// Try to avoid problems with IPV6 addresses
			foreach (IPAddress address in addresses)
			{
				if (address.AddressFamily == addressFamily)
				{
					return address;
				}
			}

			return addresses[0];
		}

		private byte[] UserIdentificationStuff()
		{
			using (var result = new MemoryStream())
			{
				if (_userID != null)
				{
#warning Charset
					var login = Encoding.Default.GetBytes(_userID);
#warning Plugin name constant
					var plugin_name = Encoding.Default.GetBytes("Srp");

#warning Magic constants
					// Login
					result.WriteByte(9);
					result.WriteByte((byte)login.Length);
					result.Write(login, 0, login.Length);

					// Plugin Name
					result.WriteByte(8);
					result.WriteByte((byte)plugin_name.Length);
					result.Write(plugin_name, 0, plugin_name.Length);

					// Plugin List
					result.WriteByte(10);
					result.WriteByte((byte)plugin_name.Length);
					result.Write(plugin_name, 0, plugin_name.Length);

					// Specific Data
					var specific_data = Encoding.Default.GetBytes(_srpClient.PublicKeyHex);
					var remaining = specific_data.Length;
					var position = 0;
					var step = 0;
					while (remaining > 0)
					{
						result.WriteByte(7);
						int toWrite = Math.Min(remaining, 254);
						result.WriteByte((byte)(toWrite + 1));
						result.WriteByte((byte)step++);
						result.Write(specific_data, position, toWrite);
						remaining -= toWrite;
						position += toWrite;
					}

					// Client Crypt (Not Encrypt)
					result.WriteByte(11);
					result.WriteByte(4);
					result.WriteByte(0);
					result.WriteByte(0);
					result.WriteByte(0);
					result.WriteByte(0);
				}

				// User	Name
				var user = Encoding.Default.GetBytes(Environment.UserName);
				result.WriteByte(1);
				result.WriteByte((byte)user.Length);
				result.Write(user, 0, user.Length);

				// Host	name
				var host = Encoding.Default.GetBytes(Dns.GetHostName());
				result.WriteByte(4);
				result.WriteByte((byte)host.Length);
				result.Write(host, 0, host.Length);

				// Attach/create using this connection will use user verification
				result.WriteByte(6);
				result.WriteByte(0);

				return result.ToArray();
			}
		}

		#endregion
	}
}